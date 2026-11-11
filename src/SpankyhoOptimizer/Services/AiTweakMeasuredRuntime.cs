using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private bool _trustedPerformanceJournalKnown;
    private bool _previouslyObservedPerformanceJournal;
    private byte[]? _trustedPerformanceJournalDigest;
    private AiSystemPerformanceSample? _latestSample;
    private bool _performanceInitialized;
    private readonly object _queueGate = new();
    private Task? _worker;
    private CancellationTokenSource? _workerCancellation;
    private long _lastQueuedStamp;
    private volatile bool _productionRegistryAutomationRetired;
    private string PerformancePath => _path + ".performance.json";

    private async Task<FileStream> AcquireOwnershipLeaseAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        long startedStamp = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (!AiTweakMonotonicTimingPolicy.HasElapsed(
                startedStamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                TimeSpan.FromSeconds(30)))
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }
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
                if (!config.Enabled || !config.TweaksEnabled || gameActive || load > 35 ||
                    !AiSharedDecisionContextService.AllowsMutation())
                    _workerCancellation?.Cancel();
                return;
            }
            if (!AiSharedDecisionContextService.AllowsMutation()) return;
            long queueStamp = Stopwatch.GetTimestamp();
            if (!AiTweakMonotonicTimingPolicy.MayQueue(
                    _lastQueuedStamp, queueStamp, Stopwatch.Frequency)) return;
            _lastQueuedStamp = queueStamp;
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
            // The UI history read must share the same bounded-admission
            // policy as recovery. No unbounded File.ReadAllText allocation
            // from a damaged or externally replaced performance journal.
            using var stream = new FileStream(DefaultPath + ".performance.json",
                FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxPerformanceJournalBytes)
                return Array.Empty<AiTweakPerformanceHistory>();
            using var buffer = new MemoryStream((int)stream.Length);
            byte[] chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) != 0)
            {
                if (buffer.Length + read > MaxPerformanceJournalBytes)
                    return Array.Empty<AiTweakPerformanceHistory>();
                buffer.Write(chunk, 0, read);
            }
            var state = JsonSerializer.Deserialize<PerformanceState>(buffer.ToArray());
            return state?.History?.ToArray() ?? Array.Empty<AiTweakPerformanceHistory>();
        }
        catch (IOException) { return Array.Empty<AiTweakPerformanceHistory>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<AiTweakPerformanceHistory>(); }
        catch (JsonException) { return Array.Empty<AiTweakPerformanceHistory>(); }
        catch (System.Security.SecurityException) { return Array.Empty<AiTweakPerformanceHistory>(); }
    }
    private const int MaxPerformanceJournalBytes = 1024 * 1024;

    private async Task<byte[]?> ReadPerformanceJournalBytesAsync(CancellationToken token)
    {
        try
        {
            // Bound the SAME bytes later validated and hashed. The .lease
            // serializes cooperating runtimes; this guard also detects
            // external writers which disregard that lease.
            await using var stream = new FileStream(PerformancePath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                8192, FileOptions.Asynchronous);
            if (stream.Length > MaxPerformanceJournalBytes)
                throw new InvalidDataException("AI measured trial journal exceeds safe size limit.");
            using var buffer = new MemoryStream((int)stream.Length);
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaxPerformanceJournalBytes)
                    throw new InvalidDataException("AI measured trial journal grew beyond safe size limit.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task EnsurePerformanceJournalUnchangedAsync(CancellationToken token)
    {
        if (!_trustedPerformanceJournalKnown)
            throw new IOException("AI measured trial journal has no validated snapshot.");
        byte[]? current = await ReadPerformanceJournalBytesAsync(token).ConfigureAwait(false);
        bool unchanged = _trustedPerformanceJournalDigest is null
            ? current is null
            : current is not null &&
              CryptographicOperations.FixedTimeEquals(
                  _trustedPerformanceJournalDigest, SHA256.HashData(current));
        if (!unchanged)
            throw new IOException(
                "AI measured trial journal changed since validated lease read; refusing stale write.");
    }

    private async Task SavePerformanceAsync(CancellationToken token)
    {
        // An optimistic conflict check, NOT an atomic filesystem CAS.
        await EnsurePerformanceJournalUnchangedAsync(token).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(_performance);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxPerformanceJournalBytes)
            throw new InvalidDataException("AI measured trial journal would exceed safe size limit.");
        await AtomicFileService.WriteTextAsync(PerformancePath, json, token).ConfigureAwait(false);
        _trustedPerformanceJournalDigest = SHA256.HashData(bytes);
        _trustedPerformanceJournalKnown = true;
        _previouslyObservedPerformanceJournal = true;
    }

    private async Task LoadPerformanceAsync(CancellationToken token)
    {
        byte[]? bytes = await ReadPerformanceJournalBytesAsync(token).ConfigureAwait(false);
        // Do not silently re-trust an unrelated performance journal inside
        // the SAME recovery transaction after a collision or deletion.
        if (_trustedPerformanceJournalKnown)
        {
            bool unchanged = _trustedPerformanceJournalDigest is null
                ? bytes is null
                : bytes is not null &&
                  CryptographicOperations.FixedTimeEquals(
                      _trustedPerformanceJournalDigest, SHA256.HashData(bytes));
            if (!unchanged)
                throw new IOException("AI measured trial journal changed during active recovery.");
        }
        if (bytes is null)
        {
            // New installations legitimately start without a history file.
            // After observing a durable journal, however, its disappearance
            // is NOT evidence of a completed measured rollback. Never discard
            // known history and start a fresh experiment on that basis.
            if (_previouslyObservedPerformanceJournal)
                throw new InvalidDataException(
                    "Previously trusted AI measured trial journal disappeared.");
            _performance = new PerformanceState();
            _trustedPerformanceJournalDigest = null;
        }
        else
        {
            RejectDuplicateJournalJsonProperties(bytes);
            var restored = JsonSerializer.Deserialize<PerformanceState>(bytes)
                ?? throw new InvalidDataException("Invalid system performance trial snapshot");
            ValidatePerformanceState(restored, bytes);
            _performance = restored;
            _trustedPerformanceJournalDigest = SHA256.HashData(bytes);
            _previouslyObservedPerformanceJournal = true;
        }
        _trustedPerformanceJournalKnown = true;
    }

    private static void ValidatePerformanceState(PerformanceState state, byte[] journalBytes)
    {
        using var document = JsonDocument.Parse(journalBytes);
        var root = document.RootElement;
        // Default-initialized properties could otherwise disguise loss of
        // durable history or a missing trial phase/sample set after a crash.
        if (!root.TryGetProperty("History", out var storedHistory) ||
            storedHistory.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("LastEvidence", out var storedEvidence) ||
            storedEvidence.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("AI measured history lacks durable required fields.");

        if (root.TryGetProperty("Trial", out var storedTrial) &&
            storedTrial.ValueKind == JsonValueKind.Object)
        {
            foreach (string property in new[]
                { "Id", "Phase", "StartedUtc", "Baseline", "Candidate", "Reference" })
            {
                if (!storedTrial.TryGetProperty(property, out _))
                    throw new InvalidDataException(
                        "AI measured trial is missing required field: " + property);
            }
        }

        if (state.History is null || state.LastEvidence is null ||
            state.History.Any(h => h is null ||
                string.IsNullOrWhiteSpace(h.Id) ||
                h.Baseline is null || h.Candidate is null || h.Reference is null))
            throw new InvalidDataException("AI measured performance history has invalid required fields.");

        if (state.Trial is not { } trial) return;
        if (string.IsNullOrWhiteSpace(trial.Id) ||
            trial.StartedUtc == default ||
            trial.Baseline is null || trial.Candidate is null || trial.Reference is null ||
            trial.Phase is not ("Baseline A" or "Applying B" or
                               "Candidate B" or "Reference A" or
                               "Accepting B" or "Recovery"))
            throw new InvalidDataException("AI measured trial has invalid phase or required fields.");
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
            var activeTarget = defs.FirstOrDefault(t => SameTweakId(t.Id, _performance.Trial.Id));
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
            var protectedTarget = defs.FirstOrDefault(t => SameTweakId(t.Id, previous.Id) && SameOwnedTarget(t, previous.Target));
            if (protectedTarget == null || !AiAutomaticMutationGuard.IsProtectedManualSection(protectedTarget))
                continue;
            await RestoreOwnedAsync(protectedTarget, previous, token).ConfigureAwait(false);
            _performance.LastEvidence = "Protected manual section restored: " + protectedTarget.Id;
            await SavePerformanceAsync(token).ConfigureAwait(false);
            return;
        }

        // Upgrade from613: policy changes without measured evidence are rolled back before experiments.
        var unproven = _owned.Values.FirstOrDefault(x => x.Managed && !x.Reason.StartsWith("Measured accepted", StringComparison.Ordinal)
            && (_performance.Trial == null || !SameTweakId(_performance.Trial.Id, x.Id)));
        if (unproven != null)
        {
            var t = defs.FirstOrDefault(x => SameTweakId(x.Id, unproven.Id) && SameOwnedTarget(x, unproven.Target));
            if (t == null) { _lastError = "Unproven original target missing: " + unproven.Id; return; }
            await RestoreOwnedAsync(t, unproven, token).ConfigureAwait(false);
            _performance.LastEvidence = "Unmeasured613 policy restored: " + t.Id;
            await SavePerformanceAsync(token).ConfigureAwait(false);
            return;
        }
        foreach (var accepted in _owned.Values.Where(x => x.Managed && x.Reason.StartsWith("Measured accepted", StringComparison.Ordinal)).Take(4).ToArray())
        {
            var target = defs.FirstOrDefault(t => SameTweakId(t.Id, accepted.Id) && SameOwnedTarget(t, accepted.Target));
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
                !_performance.History.Any(h => SameTweakId(h.Id, t.Id)));
            if (next == null) { _performance.LastEvidence = "No applicable observable performance candidate"; return; }
            if (await _backend.IsEnabledAsync(next, token).ConfigureAwait(false)) return;
            long observationStamp = Stopwatch.GetTimestamp();
            if (!_observed.TryGetValue(next.Id, out var since))
                _observed[next.Id] = since = observationStamp;
            if (!AiTweakMonotonicTimingPolicy.HasElapsed(since, observationStamp,
                Stopwatch.Frequency, TimeSpan.FromSeconds(Math.Clamp(
                    config.ObservationSeconds > 0 ? config.ObservationSeconds : 180, 0, 3600))))
                return;
            if (!AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks")) return;
            trial = _performance.Trial = new() { Id = next.Id, StartedUtc = sample.Utc };
            _lastAction = "Baseline A: " + next.Id;
            await SavePerformanceAsync(token).ConfigureAwait(false);
        }
        else if (!AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks")) return;
        var priorSamples = trial.Baseline.Concat(trial.Candidate).Concat(trial.Reference).ToArray();
        if (priorSamples.Length != 0 && sample.Utc <= priorSamples.Max(s => s.Utc))
        { await EndMeasuredTrialAsync(defs, "Inconclusive: duplicate/out-of-order sample", token).ConfigureAwait(false); return; }
        var tweak = defs.FirstOrDefault(t => SameTweakId(t.Id, trial.Id));
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
        // Baseline/observation can span minutes; a user may disable the AI
        // scope long after the trial was admitted. Re-read the live scope at
        // the final native-write boundary, never before ownership recovery.
        token.ThrowIfCancellationRequested();
        RequireLiveTweakWriteScope();
        await EnsureOwnershipJournalUnchangedAsync(token).ConfigureAwait(false);
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
        if (!_owned.TryGetValue(trial.Id, out var owned))
        {
            // Baseline A has not made a native change. Every subsequent
            // phase must have durable owner evidence, including the reference
            // phase where an earlier restore may have been interrupted. A
            // missing journal row is not proof that Windows was restored.
            if (trial.Phase != "Baseline A")
                throw new InvalidDataException(
                    "Measured AI trial has no durable tweak owner; restore cannot be confirmed: " + trial.Id);
        }
        else
        {
            var tweak = defs.FirstOrDefault(t => SameTweakId(t.Id, owned.Id) && SameOwnedTarget(t, owned.Target));
            if (tweak == null) throw new InvalidOperationException("Recovery target missing: " + owned.Id);
            if (owned.Managed)
                await RestoreOwnedAsync(tweak, owned, token).ConfigureAwait(false);
            else if (trial.Phase != "Baseline A")
            {
                // A prior restore may have finished its native write and
                // journal update before the phase snapshot was saved.
                // Verify Windows rather than treating Managed=false as
                // unconditional proof of successful rollback.
                string current = await _backend.CaptureAsync(tweak, token).ConfigureAwait(false);
                if (current == owned.Applied && current != owned.Original)
                    throw new InvalidDataException(
                        "Measured AI trial still has applied value but no active restore owner: " + trial.Id);
                if (current != owned.Original)
                    await PreserveOverrideAsync(owned, token).ConfigureAwait(false);
            }
        }
        CompleteMeasuredTrial("Rejected/inconclusive", evidence, 0);
        _lastAction = "Trial closed after ownership read-back: " + trial.Id;
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
