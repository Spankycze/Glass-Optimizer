using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record AiTweakPerformanceHistory(DateTime Utc, string Id, string Verdict, string Evidence,
    double ImprovementPercent, AiSystemPerformanceSample[] Baseline,
    AiSystemPerformanceSample[] Candidate, AiSystemPerformanceSample[] Reference);

public sealed partial class AiTweakRuntimeService
{
    private sealed class PerformanceState
    {
        public MeasuredTrial? Trial { get; set; }
        public List<AiTweakPerformanceHistory> History { get; set; } = new();
        public string LastEvidence { get; set; } = "No measured gains established";
    }
    private sealed class MeasuredTrial
    {
        public string Id { get; set; } = "";
        public string Phase { get; set; } = "Baseline A";
        public DateTime StartedUtc { get; set; }
        public List<AiSystemPerformanceSample> Baseline { get; set; } = new();
        public List<AiSystemPerformanceSample> Candidate { get; set; } = new();
        public List<AiSystemPerformanceSample> Reference { get; set; } = new();
    }
    private readonly bool _feedbackRequired;
    private readonly Func<DateTime> _utcNow;
    private readonly AiSystemPerformanceProbe _probe = new();
    private PerformanceState _performance = new();
    private AiSystemPerformanceSample? _latestSample;
    private bool _performanceInitialized;
    private readonly object _queueGate = new();
    private Task? _worker;
    private CancellationTokenSource? _workerCancellation;
    private DateTime _lastQueuedUtc;
    private volatile bool _productionRegistryAutomationRetired;
    private string PerformancePath => _path + ".performance.json";

    private async Task<FileStream> AcquireOwnershipLeaseAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
    // No benchmark, registry I/O or engine wait runs on the fan caller.
    public void QueueTick(AiSystemRuntimeConfig config, bool realIdle, double load, bool gameActive, CancellationToken token)
    {
        lock (_queueGate)
        {
            if (_path == DefaultPath)
            {
                if (_productionRegistryAutomationRetired)
                    return;
                if (_worker != null && !_worker.IsCompleted)
                    return;

                _workerCancellation?.Dispose();
                _workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                _workerCancellation.CancelAfter(TimeSpan.FromSeconds(30));
                var retirementToken = _workerCancellation.Token;
                _worker = Task.Run(async () =>
                {
                    try
                    {
                        await RestoreAllAsync(retirementToken).ConfigureAwait(false);
                        _productionRegistryAutomationRetired = true;
                        _lastAction = "Production registry-tweak AI retired; protected sections stay manual";
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _lastError = ex.GetBaseException().Message;
                        LogService.Error("ai-performance", "Registry-tweak retirement failed", ex);
                    }
                }, CancellationToken.None);
                return;
            }

            if (_worker != null && !_worker.IsCompleted)
            {
                if (!config.Enabled || !config.TweaksEnabled || gameActive || load > 35) _workerCancellation?.Cancel();
                return;
            }
            if (DateTime.UtcNow - _lastQueuedUtc < TimeSpan.FromSeconds(5)) return;
            _lastQueuedUtc = DateTime.UtcNow;
            _workerCancellation?.Dispose();
            _workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _workerCancellation.CancelAfter(TimeSpan.FromSeconds(25));
            var ct = _workerCancellation.Token;
            _worker = Task.Run(async () =>
            {
                try { await TickAsync(config, realIdle, load, gameActive, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _lastError = ex.GetBaseException().Message; LogService.Error("ai-performance", "System performance worker failed", ex); }
            }, CancellationToken.None);
        }
    }

    public static IReadOnlyList<AiTweakPerformanceHistory> ReadPerformanceHistory()
    {
        try
        {
            var state = JsonSerializer.Deserialize<PerformanceState>(File.ReadAllText(DefaultPath + ".performance.json"));
            return state?.History.ToArray() ?? Array.Empty<AiTweakPerformanceHistory>();
        }
        catch { return Array.Empty<AiTweakPerformanceHistory>(); }
    }
    private Task SavePerformanceAsync(CancellationToken token) =>
        AtomicFileService.WriteTextAsync(PerformancePath, JsonSerializer.Serialize(_performance), token);
    private async Task LoadPerformanceAsync(CancellationToken token)
    {
        if (File.Exists(PerformancePath))
            _performance = JsonSerializer.Deserialize<PerformanceState>(await File.ReadAllTextAsync(PerformancePath, token).ConfigureAwait(false))
                ?? throw new InvalidOperationException("Invalid system performance trial snapshot");
    }

    private async Task EvaluateMeasuredSafeAsync(AiSystemRuntimeConfig config, TweakDefinition[] defs, bool realIdle,
        double load, bool gameActive, AiSystemPerformanceSample? supplied, CancellationToken token)
    {
        try { await EvaluateMeasuredAsync(config, defs, realIdle, load, gameActive, supplied, token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _lastError = ex.GetBaseException().Message;
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await EndMeasuredTrialAsync(defs, "Failed/interrupted trial: " + _lastError, recovery.Token).ConfigureAwait(false); }
            catch (Exception rollback)
            {
                _lastError += "; recovery pending: " + rollback.GetBaseException().Message;
                if (_performance.Trial != null) _performance.Trial.Phase = "Recovery";
                _performance.LastEvidence = _lastError;
                try { await SavePerformanceAsync(recovery.Token).ConfigureAwait(false); } catch { }
            }
            finally { if (_performance.Trial?.Phase == "Recovery" || _performance.Trial == null) AiPerformanceExperimentCoordinator.Release("system-tweaks"); }
            if (ex is OperationCanceledException) throw;
        }
    }
    private async Task EvaluateMeasuredAsync(AiSystemRuntimeConfig config, TweakDefinition[] defs, bool realIdle,
        double load, bool gameActive, AiSystemPerformanceSample? supplied, CancellationToken token)
    {
        await LoadPerformanceAsync(token).ConfigureAwait(false);
        if (_performance.Trial == null) AiPerformanceExperimentCoordinator.Release("system-tweaks");
        if (_performance.Trial != null)
        {
            var activeTarget = defs.FirstOrDefault(t => t.Id == _performance.Trial.Id);
            if (activeTarget != null && AiAutomaticMutationGuard.IsProtectedManualSection(activeTarget))
            {
                if (!AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks")) return;
                await EndMeasuredTrialAsync(defs, "Protected manual section; trial retired", token).ConfigureAwait(false);
                return;
            }
        }
        if (!_performanceInitialized)
        {
            _performanceInitialized = true;
            if (_performance.Trial != null)
            {
                if (!AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks")) return;
                await EndMeasuredTrialAsync(defs, "Interrupted trial recovered after restart; original restored", token).ConfigureAwait(false);
                return;
            }
        }
        if (_performance.Trial?.Phase == "Recovery")
        {
            if (!AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks")) return;
            await EndMeasuredTrialAsync(defs, "Pending recovery restored original", token).ConfigureAwait(false);
            return;
        }
        // User-protected sections are manual-only. Retire any previously accepted AI ownership.
        foreach (var previous in _owned.Values.Where(x => x.Managed).ToArray())
        {
            var protectedTarget = defs.FirstOrDefault(t => t.Id == previous.Id && Target(t) == previous.Target);
            if (protectedTarget == null || !AiAutomaticMutationGuard.IsProtectedManualSection(protectedTarget))
                continue;
            await RestoreOwnedAsync(protectedTarget, previous, token).ConfigureAwait(false);
            _performance.LastEvidence = "Protected manual section restored: " + protectedTarget.Id;
            await SavePerformanceAsync(token).ConfigureAwait(false);
            return;
        }

        // Upgrade from613: policy changes without measured evidence are rolled back before experiments.
        var unproven = _owned.Values.FirstOrDefault(x => x.Managed && !x.Reason.StartsWith("Measured accepted", StringComparison.Ordinal)
            && (_performance.Trial == null || _performance.Trial.Id != x.Id));
        if (unproven != null)
        {
            var t = defs.FirstOrDefault(x => x.Id == unproven.Id && Target(x) == unproven.Target);
            if (t == null) { _lastError = "Unproven original target missing: " + unproven.Id; return; }
            await RestoreOwnedAsync(t, unproven, token).ConfigureAwait(false);
            _performance.LastEvidence = "Unmeasured613 policy restored: " + t.Id;
            await SavePerformanceAsync(token).ConfigureAwait(false);
            return;
        }
        foreach (var accepted in _owned.Values.Where(x => x.Managed && x.Reason.StartsWith("Measured accepted", StringComparison.Ordinal)).Take(4).ToArray())
        {
            var target = defs.FirstOrDefault(t => t.Id == accepted.Id && Target(t) == accepted.Target);
            if (target != null && await _backend.CaptureAsync(target, token).ConfigureAwait(false) != accepted.Applied)
                await PreserveOverrideAsync(accepted, token).ConfigureAwait(false);
        }
        var sample = supplied ?? await _probe.SampleAsync(token).ConfigureAwait(false);
        _latestSample = sample;
        string blocker = Math.Abs((_utcNow() - sample.Utc).TotalSeconds) > 15 ? "Stale/future sample" :
            gameActive ? "Game guard" : load > 35 ? "High-load guard" : AiSystemPerformancePolicy.SampleBlocker(sample);
        if (blocker.Length != 0)
        {
            if (_performance.Trial != null) await EndMeasuredTrialAsync(defs, "Inconclusive: " + blocker, token).ConfigureAwait(false);
            else _performance.LastEvidence = blocker;
            return;
        }
        var trial = _performance.Trial;
        if (trial == null)
        {
            if (DateTime.UtcNow < _cooldown) return;
            var next = defs.FirstOrDefault(t => !AiAutomaticMutationGuard.IsProtectedManualSection(t) &&
                AiTweakRuntimePolicy.DeferredReason(t).Length == 0 &&
                _backend.DeferredReason(t).Length == 0 && AiSystemPerformancePolicy.CandidateDeferredReason(t, sample.ActiveComponents).Length == 0 &&
                (!_owned.TryGetValue(t.Id, out var own) || (!own.Managed && !own.Suppressed)) &&
                !_performance.History.Any(h => h.Id == t.Id));
            if (next == null) { _performance.LastEvidence = "No applicable observable performance candidate"; return; }
            if (await _backend.IsEnabledAsync(next, token).ConfigureAwait(false)) return;
            if (!_observed.TryGetValue(next.Id, out var since)) _observed[next.Id] = since = DateTime.UtcNow;
            if (DateTime.UtcNow - since < TimeSpan.FromSeconds(Math.Clamp(config.ObservationSeconds, 0, 3600))) return;
            if (!AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks")) return;
            trial = _performance.Trial = new() { Id = next.Id, StartedUtc = sample.Utc };
            _lastAction = "Baseline A: " + next.Id;
            await SavePerformanceAsync(token).ConfigureAwait(false);
        }
        else if (!AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks")) return;
        var priorSamples = trial.Baseline.Concat(trial.Candidate).Concat(trial.Reference).ToArray();
        if (priorSamples.Length != 0 && sample.Utc <= priorSamples.Max(s => s.Utc))
        { await EndMeasuredTrialAsync(defs, "Inconclusive: duplicate/out-of-order sample", token).ConfigureAwait(false); return; }
        var tweak = defs.FirstOrDefault(t => t.Id == trial.Id);
        if (tweak == null) throw new InvalidOperationException("Trial catalog target missing");
        if (sample.Utc - trial.StartedUtc > TimeSpan.FromMinutes(10))
        { await EndMeasuredTrialAsync(defs, "Inconclusive: trial timed out", token).ConfigureAwait(false); return; }
        if (trial.Baseline.Count > 0 && trial.Baseline[0].WorkloadKey != sample.WorkloadKey)
        { await EndMeasuredTrialAsync(defs, "Inconclusive: foreground workload changed", token).ConfigureAwait(false); return; }
        if (_owned.TryGetValue(trial.Id, out var owned) && trial.Phase != "Baseline A")
        {
            string expected = trial.Phase == "Candidate B" ? owned.Applied : owned.Original;
            if (await _backend.CaptureAsync(tweak, token).ConfigureAwait(false) != expected)
            {
                await PreserveOverrideAsync(owned, token).ConfigureAwait(false);
                await EndMeasuredTrialAsync(defs, "Manual override preserved during experiment", token).ConfigureAwait(false);
                return;
            }
        }
        var samples = trial.Phase == "Baseline A" ? trial.Baseline : trial.Phase == "Candidate B" ? trial.Candidate : trial.Reference;
        if (samples.Count != 0 && sample.Utc - samples[^1].Utc < TimeSpan.FromSeconds(5)) return;
        samples.Add(sample);
        await SavePerformanceAsync(token).ConfigureAwait(false);
        if (samples.Count < AiSystemPerformancePolicy.SamplesPerWindow) return;
        try
        {
            if (trial.Phase == "Baseline A")
            {
                // A noisy baseline is rejected before any system change.
                string stability = AiSystemPerformancePolicy.WindowBlocker(trial.Baseline);
                if (stability.Length != 0)
                { await EndMeasuredTrialAsync(defs, "Inconclusive baseline: " + stability, token).ConfigureAwait(false); return; }
                if (await _backend.IsEnabledAsync(tweak, token).ConfigureAwait(false))
                { await EndMeasuredTrialAsync(defs, "Manual activation during baseline preserved", token).ConfigureAwait(false); return; }
                string original = await _backend.CaptureAsync(tweak, token).ConfigureAwait(false);
                owned = new(tweak.Id, Target(tweak), "ai." + tweak.Id + "." + Guid.NewGuid().ToString("N"),
                    original, _backend.ExpectedSignature(tweak), true, false, false, DateTime.UtcNow, "Measured trial pending");
                _owned[tweak.Id] = owned;
                await PersistAsync(token).ConfigureAwait(false);
                trial.Phase = "Applying B";
                await SavePerformanceAsync(token).ConfigureAwait(false);
                await ApplyMeasuredOwnedAsync(tweak, owned, token).ConfigureAwait(false);
                trial.Phase = "Candidate B"; _lastAction = "Candidate B: " + tweak.Id;
            }
            else if (trial.Phase == "Candidate B")
            {
                await RestoreOwnedAsync(tweak, owned!, token).ConfigureAwait(false);
                trial.Phase = "Reference A"; _lastAction = "Original restored; reference A: " + tweak.Id;
            }
            else
            {
                var verdict = AiSystemPerformancePolicy.Compare(trial.Baseline, trial.Candidate, trial.Reference);
                if (verdict.Accepted)
                {
                    owned = _owned[tweak.Id] with { Managed = true, Reason = "Measured accepted: " + verdict.Reason };
                    _owned[tweak.Id] = owned;
                    await PersistAsync(token).ConfigureAwait(false);
                    trial.Phase = "Accepting B";
                    await SavePerformanceAsync(token).ConfigureAwait(false);
                    await ApplyMeasuredOwnedAsync(tweak, owned, token).ConfigureAwait(false);
                    _lastAction = "Measured accepted: " + tweak.Id;
                    CompleteMeasuredTrial("Accepted", verdict.Reason, verdict.WorkImprovementPercent);
                    await SavePerformanceAsync(token).ConfigureAwait(false);
                }
                else await EndMeasuredTrialAsync(defs, verdict.Reason, token).ConfigureAwait(false);
                _cooldown = DateTime.UtcNow.AddSeconds(Math.Clamp(config.ActionCooldownSeconds, 0, 3600));
                return;
            }
            await SavePerformanceAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastError = ex.GetBaseException().Message;
            using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await EndMeasuredTrialAsync(defs, "Failed/cancelled trial: " + _lastError, rollback.Token).ConfigureAwait(false);
            if (ex is OperationCanceledException) throw;
        }
    }

    private async Task ApplyMeasuredOwnedAsync(TweakDefinition tweak, Ownership owned, CancellationToken token)
    {
        if (await _backend.CaptureAsync(tweak, token).ConfigureAwait(false) != owned.Original)
            throw new InvalidOperationException("Original changed before candidate apply");
        await _backend.ApplyAsync(tweak, owned.BackupId, token).ConfigureAwait(false);
        if (await _backend.CaptureAsync(tweak, token).ConfigureAwait(false) != owned.Applied)
            throw new InvalidOperationException("Measured tweak read-back failed");
    }

    private async Task EndMeasuredTrialAsync(TweakDefinition[] defs, string evidence, CancellationToken token)
    {
        await LoadPerformanceAsync(token).ConfigureAwait(false);
        var trial = _performance.Trial;
        if (trial == null) return;
        if (!AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks"))
            throw new InvalidOperationException("Experiment recovery waits for network experiment");
        if (_owned.TryGetValue(trial.Id, out var owned) && owned.Managed)
        {
            var tweak = defs.FirstOrDefault(t => t.Id == owned.Id && Target(t) == owned.Target);
            if (tweak == null) throw new InvalidOperationException("Recovery target missing: " + owned.Id);
            await RestoreOwnedAsync(tweak, owned, token).ConfigureAwait(false);
        }
        CompleteMeasuredTrial("Rejected/inconclusive", evidence, 0);
        _lastAction = "Trial restored: " + trial.Id;
        await SavePerformanceAsync(token).ConfigureAwait(false);
    }
    private void CompleteMeasuredTrial(string verdict, string evidence, double improvement)
    {
        var trial = _performance.Trial!;
        _performance.History.Add(new(DateTime.UtcNow, trial.Id, verdict, evidence, improvement,
            trial.Baseline.ToArray(), trial.Candidate.ToArray(), trial.Reference.ToArray()));
        if (_performance.History.Count > 32) _performance.History.RemoveAt(0);
        _performance.LastEvidence = evidence;
        _performance.Trial = null;
        AiPerformanceExperimentCoordinator.Release("system-tweaks");
    }
}
