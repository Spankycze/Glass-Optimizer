using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record AiNetworkProbeResult(DateTime CapturedUtc, string Context, bool Valid, int BytesPerSample,
    double[] TransferMs, double[] ConnectMs, double LossPercent, double CpuPercent, double AmbientBytesPerSecond, string Error);
public sealed record AiNetworkRuntimeHealth(DateTime UpdatedUtc, bool Enabled, string Phase, string Candidate, bool Managed,
    int ManualOverrides, string LastAction, string LastError, AiNetworkProbeResult? Before, AiNetworkProbeResult? CandidateResult,
    AiNetworkProbeResult? Reference, string[] Deferred);

public interface IAiNetworkRuntimeBackend
{
    Task<string> ReadSettingAsync(CancellationToken token);
    Task ApplySettingAsync(string value, CancellationToken token);
    Task<AiNetworkProbeResult> MeasureAsync(CancellationToken token);
}

public static class AiNetworkRuntimePolicy
{
    public static readonly TimeSpan AcceptedRetestDelay = TimeSpan.FromHours(6);
    public static readonly TimeSpan RejectedRetestDelay = TimeSpan.FromHours(12);
    public static readonly TimeSpan NoisyRetryDelay = TimeSpan.FromMinutes(45);

    public static bool Usable(AiNetworkProbeResult p, DateTime now) =>
        p.Valid && !string.IsNullOrWhiteSpace(p.Context) && p.CpuPercent >= 0 && p.AmbientBytesPerSecond >= 0 && p.CapturedUtc <= now.AddSeconds(2) && now - p.CapturedUtc < TimeSpan.FromSeconds(45) &&
        p.BytesPerSample == 262144 && p.TransferMs.Length == 5 && p.ConnectMs.Length == 5 &&
        p.TransferMs.All(x => double.IsFinite(x) && x > 0) && p.ConnectMs.All(x => double.IsFinite(x) && x > 0) &&
        p.LossPercent == 0 && double.IsFinite(p.CpuPercent) && p.CpuPercent <= 20 &&
        double.IsFinite(p.AmbientBytesPerSecond) && p.AmbientBytesPerSecond <= 49152;

    public static double Median(double[] values) => values.OrderBy(x => x).ElementAt(values.Length / 2);

    public static bool StableBenefit(AiNetworkProbeResult a, AiNetworkProbeResult b, AiNetworkProbeResult reference, DateTime now)
    {
        if (!Usable(a, now) || !Usable(b, now) || !Usable(reference, now) ||
            a.Context != b.Context || a.Context != reference.Context) return false;
        double first = Median(a.TransferMs), second = Median(reference.TransferMs), candidate = Median(b.TransferMs);
        if (Math.Abs(first - second) > Math.Max(5, Math.Min(first, second) * .10)) return false;
        if (first - candidate < Math.Max(20, first * .20) || second - candidate < Math.Max(20, second * .20)) return false;
        // Small transfers give weak evidence: refuse noisy tails, changed connect path or CPU/traffic load.
        if (b.TransferMs.Max() > Math.Min(a.TransferMs.Max(), reference.TransferMs.Max()) * .90) return false;
        double connect = Median(a.ConnectMs);
        if (Math.Abs(connect - Median(b.ConnectMs)) > Math.Max(5, connect * .20) ||
            Math.Abs(connect - Median(reference.ConnectMs)) > Math.Max(5, connect * .20)) return false;

        double ambientMax = Math.Max(a.AmbientBytesPerSecond,
            Math.Max(b.AmbientBytesPerSecond, reference.AmbientBytesPerSecond));
        double ambientMin = Math.Min(a.AmbientBytesPerSecond,
            Math.Min(b.AmbientBytesPerSecond, reference.AmbientBytesPerSecond));
        if (ambientMax - ambientMin > 16384) return false;

        return Math.Max(a.CpuPercent, Math.Max(b.CpuPercent, reference.CpuPercent)) -
            Math.Min(a.CpuPercent, Math.Min(b.CpuPercent, reference.CpuPercent)) <= 10;
    }
}

public sealed class AiNetworkRuntimeService
{
    private sealed record Ownership(string Original, string Applied, string Phase, bool Managed,
        bool Suppressed, int ManualOverrides, DateTime NextRunUtc);
    private static readonly string DefaultPath = Path.Combine(LogService.Root, "ai-network-ownership.json");
    private static readonly string HealthPath = Path.Combine(LogService.Root, "ai-network-health.json");
    private static readonly SemaphoreSlim DefaultGate = new(1, 1);
    private static AiNetworkRuntimeHealth? _cachedHealth;
    private readonly IAiNetworkRuntimeBackend _backend;
    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private readonly object _queueSync = new();
    private Task? _pending;
    private DateTime _lastQueuedUtc;
    private CancellationTokenSource? _workCts;
    private volatile bool _liveEnabled, _liveIdle, _liveGame;
    private volatile AiWorkloadClass _liveWorkload = AiWorkloadClass.Unknown;
    private double _liveLoad;
    private Ownership? _owned;
    private DateTime _nextAudit;
    private AiNetworkProbeResult? _before, _candidate, _reference;
    private string _action = "Observing; requires quiet network window", _error = "";
    private static readonly string[] Deferred =
    {
        "DNS: preserves filtered/custom resolvers; no unmeasured resolver replacement",
        "MTU/IP/gateway/adapter restart/NIC driver properties: deferred during active connections",
        "RSC/ECN: no matching supported performance evidence; unchanged"
    };
    public AiNetworkRuntimeHealth Health { get; private set; } =
        new(DateTime.MinValue, false, "Observing", "Internet autotuning", false, 0, "", "", null, null, null, Deferred);

    public AiNetworkRuntimeService() : this(new AiNetworkWindowsBackend(), DefaultPath) { }
    public AiNetworkRuntimeService(IAiNetworkRuntimeBackend backend, string path)
    { _backend = backend; _path = path; _gate = path == DefaultPath ? DefaultGate : new SemaphoreSlim(1, 1); }

    public static AiNetworkRuntimeHealth? ReadHealth()
    {
        try { if (File.Exists(HealthPath)) _cachedHealth = JsonSerializer.Deserialize<AiNetworkRuntimeHealth>(File.ReadAllText(HealthPath)); }
        catch { }
        return _cachedHealth;
    }

    // The fan loop only updates context and schedules; no probing or process wait runs on its thread.
    public void QueueTick(bool enabled, bool realIdle, double load, bool gameActive, CancellationToken token) =>
        QueueTick(enabled, realIdle, load, gameActive, AiWorkloadClass.Unknown, token);

    public void QueueTick(bool enabled, bool realIdle, double load, bool gameActive, AiWorkloadClass workload, CancellationToken token)
    {
        bool wasEnabled = _liveEnabled;
        UpdateContext(enabled, realIdle, load, gameActive, workload);
        lock (_queueSync)
        {
            if (wasEnabled &&
                (!enabled || gameActive || !realIdle || load > 15 ||
                 workload is AiWorkloadClass.Interactive or AiWorkloadClass.Game))
                _workCts?.Cancel();
            if (_pending is { IsCompleted: false }) return;
            bool restoreNeeded = !enabled && (wasEnabled || Health.Managed || Health.Phase is "Pending" or "Testing" or "Reference");
            if (!restoreNeeded && DateTime.UtcNow - _lastQueuedUtc < TimeSpan.FromSeconds(15)) return;
            _lastQueuedUtc = DateTime.UtcNow;
            _workCts?.Dispose();
            _workCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken workerToken = _workCts.Token;
            _pending = Task.Run(async () =>
            {
                try { await TickAsync(enabled, realIdle, load, gameActive, workload, workerToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { LogService.Error("network", "AI network worker failed", ex); }
            }, CancellationToken.None);
        }
    }

    private void UpdateContext(bool enabled, bool idle, double load, bool game, AiWorkloadClass workload)
    {
        _liveEnabled = enabled;
        _liveIdle = idle;
        _liveGame = game;
        _liveWorkload = workload;
        Volatile.Write(ref _liveLoad, load);
    }

    private bool Quiet()
    {
        if (!_liveEnabled || !_liveIdle || _liveGame ||
            _liveWorkload is AiWorkloadClass.Interactive or AiWorkloadClass.Game ||
            !double.IsFinite(Volatile.Read(ref _liveLoad)) ||
            Volatile.Read(ref _liveLoad) > 15)
            return false;

        // Isolated/test backends depend only on supplied context.
        if (_path != DefaultPath)
            return true;

        // Production experiments require a fresh, classified workload context.
        // Unknown usually means the shared power/workload status is stale or unavailable.
        if (!AiSharedWorkloadPolicy.AllowsBackgroundAutomation(_liveWorkload))
        {
            _action = "Deferred: workload context unavailable/stale";
            return false;
        }

        var config = AiSystemResourceManagerService.LoadConfig();
        return config.Enabled && config.NetworkEnabled;
    }
    private void RequireQuiet() { if (!Quiet()) throw new OperationCanceledException("Network context changed"); }

    public Task RestoreAllAsync(CancellationToken token = default) =>
        TickAsync(false, false, 0, false, AiWorkloadClass.Unknown, token);

    public Task TickAsync(bool enabled, bool realIdle, double load, bool gameActive, CancellationToken token) =>
        TickAsync(enabled, realIdle, load, gameActive, AiWorkloadClass.Unknown, token);

    public async Task TickAsync(bool enabled, bool realIdle, double load, bool gameActive, AiWorkloadClass workload, CancellationToken token)
    {
        UpdateContext(enabled, realIdle, load, gameActive, workload);
        if (!await _gate.WaitAsync(enabled ? TimeSpan.Zero : TimeSpan.FromSeconds(25), token).ConfigureAwait(false))
            throw new TimeoutException("Network restore waiting for active worker");
        bool coordinated = false;
        FileStream? processLease = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            DateTime leaseDeadline = DateTime.UtcNow.AddSeconds(enabled ? 1 : 25);
            while (processLease == null)
            {
                try { processLease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < leaseDeadline) { await Task.Delay(100, token).ConfigureAwait(false); }
            }
            if (!AiPerformanceExperimentCoordinator.TryAcquire("network"))
            { _action = "Deferred: another performance trial owns measurements"; return; }
            coordinated = true;
            if (File.Exists(_path))
                _owned = JsonSerializer.Deserialize<Ownership>(await File.ReadAllTextAsync(_path, token).ConfigureAwait(false))
                    ?? throw new InvalidDataException("Network ownership state unavailable");
            if (_owned is { Phase: "Pending" or "Testing" or "Reference" })
            {
                await RestoreOwnedAsync(CancellationToken.None).ConfigureAwait(false);
                _action = "Recovered interrupted network transaction";
                return;
            }
            if (!enabled)
            {
                if (_owned?.Managed == true) await RestoreOwnedAsync(token).ConfigureAwait(false);
                _action = "Network control disabled; owned setting restored";
                return;
            }
            if (!Quiet() || _owned?.Suppressed == true || DateTime.UtcNow < (_owned?.NextRunUtc ?? DateTime.MinValue)) return;
            if (_owned?.Managed == true)
            {
                if (DateTime.UtcNow < _nextAudit) return;
                _nextAudit = DateTime.UtcNow.AddMinutes(5);
                string current = await _backend.ReadSettingAsync(token).ConfigureAwait(false);
                if (current != _owned.Applied) { await PreserveManualAsync().ConfigureAwait(false); return; }
                await RestoreOwnedAsync(token).ConfigureAwait(false);
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(90));
            CancellationToken trialToken = budget.Token;
            string original = await _backend.ReadSettingAsync(trialToken).ConfigureAwait(false);
            if (original is not ("normal" or "restricted"))
            { _action = "Unsupported/custom Internet autotuning; unchanged"; _nextAudit = DateTime.UtcNow.AddHours(6); return; }
            _before = await _backend.MeasureAsync(trialToken).ConfigureAwait(false);
            if (!AiNetworkRuntimePolicy.Usable(_before, DateTime.UtcNow))
            {
                _action = "Baseline unavailable/noisy; unchanged"; _error = _before.Error;
                _owned = new(original, original, "Deferred", false, false, 0, DateTime.UtcNow.Add(AiNetworkRuntimePolicy.NoisyRetryDelay));
                await PersistAsync(token).ConfigureAwait(false); return;
            }
            RequireQuiet();
            string candidate = original == "normal" ? "restricted" : "normal";
            _owned = new(original, candidate, "Pending", true, false, 0, DateTime.MinValue);
            await PersistAsync(trialToken).ConfigureAwait(false); // exact original BEFORE any write
            if (await _backend.ReadSettingAsync(trialToken).ConfigureAwait(false) != original)
            { await PreserveManualAsync().ConfigureAwait(false); return; }
            await _backend.ApplySettingAsync(candidate, trialToken).ConfigureAwait(false);
            if (await _backend.ReadSettingAsync(trialToken).ConfigureAwait(false) != candidate)
                throw new InvalidOperationException("Network apply read-back failed");
            _owned = _owned with { Phase = "Testing" };
            await PersistAsync(trialToken).ConfigureAwait(false);
            _candidate = await _backend.MeasureAsync(trialToken).ConfigureAwait(false);
            RequireQuiet();
            if (await _backend.ReadSettingAsync(trialToken).ConfigureAwait(false) != candidate)
            { await PreserveManualAsync().ConfigureAwait(false); return; }
            await _backend.ApplySettingAsync(original, trialToken).ConfigureAwait(false);
            if (await _backend.ReadSettingAsync(trialToken).ConfigureAwait(false) != original)
                throw new InvalidOperationException("Network reference restore read-back failed");
            _owned = _owned with { Phase = "Reference" };
            await PersistAsync(trialToken).ConfigureAwait(false);
            _reference = await _backend.MeasureAsync(trialToken).ConfigureAwait(false);
            RequireQuiet();
            bool accept = AiNetworkRuntimePolicy.StableBenefit(_before, _candidate, _reference, DateTime.UtcNow);
            if (await _backend.ReadSettingAsync(trialToken).ConfigureAwait(false) != original)
            { await PreserveManualAsync().ConfigureAwait(false); return; }
            if (accept)
            {
                _owned = _owned with { Phase = "Pending" };
                await PersistAsync(trialToken).ConfigureAwait(false);
                await _backend.ApplySettingAsync(candidate, trialToken).ConfigureAwait(false);
                if (await _backend.ReadSettingAsync(trialToken).ConfigureAwait(false) != candidate)
                    throw new InvalidOperationException("Network accept read-back failed");
            }
            _owned = _owned with
            {
                Phase = accept ? "Managed" : "Rejected",
                Managed = accept,
                NextRunUtc = DateTime.UtcNow.Add(accept
                    ? AiNetworkRuntimePolicy.AcceptedRetestDelay
                    : AiNetworkRuntimePolicy.RejectedRetestDelay)
            };
            _action = accept ? "Retained measured stable TCP transfer improvement" : "No stable TCP transfer benefit; original restored";
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _error = ex.GetBaseException().Message;
            if (_owned is { Managed: true, Suppressed: false })
            {
                try { await RestoreOwnedAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollback) { _error += "; rollback pending: " + rollback.GetBaseException().Message; }
            }
            if (ex is OperationCanceledException && token.IsCancellationRequested) throw;
        }
        finally
        {
            if (coordinated) AiPerformanceExperimentCoordinator.Release("network");
            processLease?.Dispose();
            try { await PublishAsync(enabled).ConfigureAwait(false); } finally { _gate.Release(); }
        }
    }

    private async Task RestoreOwnedAsync(CancellationToken token)
    {
        if (_owned == null) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        string current = await _backend.ReadSettingAsync(budget.Token).ConfigureAwait(false);
        if (current != _owned.Applied && current != _owned.Original)
        { await PreserveManualAsync().ConfigureAwait(false); return; }
        if (current != _owned.Original)
            await _backend.ApplySettingAsync(_owned.Original, budget.Token).ConfigureAwait(false);
        if (await _backend.ReadSettingAsync(budget.Token).ConfigureAwait(false) != _owned.Original)
            throw new InvalidOperationException("Network rollback read-back failed");
        _owned = _owned with { Phase = "Restored", Managed = false, NextRunUtc = DateTime.UtcNow.AddMinutes(30) };
        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
    }
    private async Task PreserveManualAsync()
    {
        _owned = _owned! with { Managed = false, Suppressed = true, Phase = "ManualOverride", ManualOverrides = _owned!.ManualOverrides + 1 };
        _action = "External manual TCP setting preserved; automatic control suppressed";
        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
    }
    private Task PersistAsync(CancellationToken token) => AtomicFileService.WriteTextAsync(_path, JsonSerializer.Serialize(_owned), token);
    private async Task PublishAsync(bool enabled)
    {
        Health = new(DateTime.UtcNow, enabled, _owned?.Phase ?? "Observing", "Internet autotuning", _owned?.Managed == true,
            _owned?.ManualOverrides ?? 0, _action, _error, _before, _candidate, _reference, Deferred);
        if (_path == DefaultPath)
        {
            _cachedHealth = Health;
            await AtomicFileService.WriteTextAsync(HealthPath, JsonSerializer.Serialize(Health), CancellationToken.None).ConfigureAwait(false);
        }
    }
}
