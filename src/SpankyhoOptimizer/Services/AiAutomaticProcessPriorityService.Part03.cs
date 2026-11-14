using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

internal sealed partial class AiAutomaticProcessPriorityService
{
    private void TryLowerBackgroundProcess(
        string processName, AdaptivePowerRuntimeStatus power,
        AiAutomaticPriorityTrialPolicy.CpuWindow baseline,
        double baselineLoadPercent, int expectedTargetPid,
        AiWorkloadClass workload, DateTime nowUtc)
    {
        // At most one exact-name candidate, examined only after two stable
        // observations. No all-process polling loop, wildcard or process kill.
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(processName); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            ReportIneligible("nativni overeni kandidata selhalo", nowUtc);
            return;
        }
        try
        {
            if (candidates.Length != 1)
            {
                ReportIneligible("cilovy proces nema prave jednu instanci", nowUtc);
                return;
            }
            Process process = candidates[0];
            var foreground = ForegroundProcessProbe.GetWindowIdentity();
            int liveForegroundPid = foreground.ProcessId;
            if (!AiAutomaticPriorityTrialPolicy.MayStartNativePriorityMutation(
                    power, nowUtc, liveForegroundPid, foreground.Handle.ToInt64()))
            {
                ReportIneligible("CPU/aktivni okno: neaktualni nebo zmenena identita", nowUtc);
                return;
            }
            if (process.HasExited ||
                process.Id != expectedTargetPid ||
                power.ForegroundProcessId <= 0 ||
                liveForegroundPid != power.ForegroundProcessId ||
                process.Id == Environment.ProcessId ||
                process.Id == liveForegroundPid)
            {
                ReportIneligible("zmena identity ciloveho nebo aktivniho procesu", nowUtc);
                return;
            }
            if (process.PriorityClass != ProcessPriorityClass.Normal)
            {
                // Respect third-party/user priority changes. Never overwrite.
                ReportIneligible("cilovy proces uz nema prioritu Normal", nowUtc);
                return;
            }

            DateTime startUtc = process.StartTime.ToUniversalTime();
            string? image = process.MainModule?.FileName;
            if (!AiAutomaticProcessPriorityPolicy.TrustedExecutable(processName, image))
            {
                ReportIneligible("neoverena instalacni cesta procesu", nowUtc);
                return;
            }
            if (AiAutomaticProcessPriorityPolicy.IsWindowsSearchIndexer(processName))
            {
                int livePid = liveForegroundPid;
                string liveName = AiPriorityTrialMemoryPolicy.CanReuseForegroundName(
                    power.ForegroundProcessId, livePid, power.ForegroundProcess)
                    ? power.ForegroundProcess : ForegroundProcessProbe.GetName();
                if (AiPriorityTrialMemoryPolicy.SearchUserActive(processName,
                    power.ForegroundProcess, liveName))
                {
                    ReportIneligible("aktivni vyhledavani Windows ma prednost", nowUtc);
                    return;
                }
            }

            // Native process/window state can change while retrieving the
            // trusted executable and checking Windows Search. Recheck just
            // before writing the ownership journal, not only at method entry.
            // This prevents applying a stale trial after the user switches
            // tasks or another tool edits this process priority.
            var beforeCommitForeground = ForegroundProcessProbe.GetWindowIdentity();
            if (!AiAutomaticPriorityTrialPolicy.MayStartNativePriorityMutation(
                    power, DateTime.UtcNow, beforeCommitForeground.ProcessId,
                    beforeCommitForeground.Handle.ToInt64()) ||
                process.HasExited ||
                process.Id != expectedTargetPid ||
                process.StartTime.ToUniversalTime().Ticks != startUtc.Ticks ||
                !string.Equals(process.MainModule?.FileName, image,
                    StringComparison.OrdinalIgnoreCase) ||
                process.PriorityClass != ProcessPriorityClass.Normal)
            {
                ReportIneligible("zmeneny proces nebo okno pred zapisem priority", DateTime.UtcNow);
                return;
            }

            var proposed = new PriorityOwnership(
                process.Id, startUtc.Ticks, processName, image!, nowUtc,
                power.ForegroundProcess, baseline.Average,
                baselineLoadPercent, baseline.Count, power.ForegroundProcessId,
                workload);
            // Durable ownership BEFORE touching a non-owned Windows process.
            AtomicFileService.WriteText(JournalPath, JsonSerializer.Serialize(proposed));
            _owned = proposed;
            _candidateCpuWindow = default;
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                process.Refresh();
                if (process.PriorityClass != ProcessPriorityClass.BelowNormal)
                    throw new InvalidOperationException("Native priority readback did not match.");
                _successfulPriorityApplies++;
                Status = "Procesy: dočasně snížená priorita " + processName;
                LogService.Info("ai-system",
                    $"AI_PROCESS_PRIORITY_APPLY name={processName} pid={process.Id} from=Normal to=BelowNormal");
            }
            catch
            {
                RestoreOwned(nowUtc, "nepotvrzený zápis priority");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            Win32Exception or UnauthorizedAccessException or IOException or
            NotSupportedException or ArgumentException)
        {
            // Never hammer native protected Windows process APIs on failure.
            // No unchecked escalation, service permissions or UAC bypass.
            // If journal persistence failed, no priority was modified.
            // If it succeeded, next tick will try to restore the owner.
            _journalFaulted = _owned == null && File.Exists(JournalPath);
            _nextEligibleUtc = nowUtc.AddMinutes(15);
            Status = "Procesy: změna nepovolena či neověřena; 15min ochranná prodleva";
        }
        finally
        {
            foreach (Process candidate in candidates) candidate.Dispose();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            // File.Exists can hide permission errors. Read directly and treat
            // only a definitely absent journal as an empty ownership state.
            // Decode persisted ownership directly from UTF-8 bytes while
            // retaining the original Unicode BOM fallback and all fail-closed
            // trust checks before any process-priority change.
            _owned = ObservedJsonSnapshotCache<PriorityOwnership>.ReadSnapshotFromFile(JournalPath) ??
                throw new InvalidDataException("Empty priority ownership journal");
            if (_owned.Pid <= 0 || _owned.StartUtcTicks <= 0 ||
                !AiAutomaticProcessPriorityPolicy.TrustedExecutable(
                    _owned.Name, _owned.ImagePath))
                throw new InvalidDataException("Untrusted priority ownership journal");
            _recoveryPending = true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException)
        {
            _journalFaulted = true;
            LogService.Error("ai-system", "AI process priority journal cannot be safely recovered", ex);
        }

        // Only one compact result and cooldown; no high-frequency disk logs.
        // A malformed/unreadable checkpoint does NOT prevent restoration of
        // our existing process priority, but disables NEW scheduling changes.
        try
        {
            string json = File.ReadAllText(FeedbackPath);
            DateTime now = DateTime.UtcNow;
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid AI priority feedback object");
            if (document.RootElement.TryGetProperty("Items", out _))
            {
                var parsed = JsonSerializer.Deserialize<AiPriorityTrialMemoryPolicy.History>(json);
                if (!AiPriorityTrialMemoryPolicy.ValidateHistory(parsed, now))
                    throw new InvalidDataException("Invalid AI priority feedback history");
                _feedbackHistory = parsed!;
            }
            else
            {
                // Backward-compatible one-entry 1075–1079 JSON format.
                var legacy = JsonSerializer.Deserialize<AiPriorityTrialMemoryPolicy.Checkpoint>(json);
                if (!AiPriorityTrialMemoryPolicy.Accept(legacy, now) &&
                    !AiPriorityTrialMemoryPolicy.IsExpired(legacy, now))
                    throw new InvalidDataException("Invalid legacy AI priority feedback");
                _feedbackHistory = new AiPriorityTrialMemoryPolicy.History(
                    legacy is null ? Array.Empty<AiPriorityTrialMemoryPolicy.Checkpoint>() :
                    new[] { legacy });
            }
            _lastCheckpoint = AiPriorityTrialMemoryPolicy.Latest(_feedbackHistory);
            if (_lastCheckpoint != null &&
                AiPriorityTrialMemoryPolicy.Accept(_lastCheckpoint, now))
                LastTrialResult = _lastCheckpoint.Result;
            // Old checkpoints may expire without disabling subsequent safe
            // experiments. Only bad/unreadable JSON must fail closed.
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException)
        {
            _feedbackFaulted = true;
            LogService.Error("ai-system",
                "AI process priority last-trial feedback unavailable; new trials blocked", ex);
        }
    }

    private void PersistFeedback(string target, string message, DateTime nowUtc)
    {
        if (_feedbackFaulted || !AiAutomaticProcessPriorityPolicy.IsAllowlisted(target))
            return;
        try
        {
            var updated = AiPriorityTrialMemoryPolicy.Remember(
                _feedbackHistory, target, message, nowUtc);
            // Write first. Only then expose new cooldown in memory; fail-closed
            // on I/O errors instead of pretending the checkpoint was saved.
            AtomicFileService.WriteText(FeedbackPath, JsonSerializer.Serialize(updated));
            _feedbackHistory = updated;
            _lastCheckpoint = AiPriorityTrialMemoryPolicy.Latest(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException)
        {
            _feedbackFaulted = true;
            LogService.Error("ai-system",
                "Could not persist AI priority trial memory; new trials blocked", ex);
        }
    }

    private bool RestoreOwned(DateTime nowUtc, string reason, bool prepareReference = false)
    {
        if (_owned == null) return true;
        PriorityOwnership old = _owned;
        bool actuallyRestored = false;
        try
        {
            using Process process = Process.GetProcessById(old.Pid);
            if (!process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == old.StartUtcTicks &&
                string.Equals(process.MainModule?.FileName, old.ImagePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                // User/external changes always win. Only return OUR exact
                // BelowNormal mutation to the original Normal priority.
                if (process.PriorityClass == ProcessPriorityClass.BelowNormal)
                {
                    process.PriorityClass = ProcessPriorityClass.Normal;
                    process.Refresh();
                    if (process.PriorityClass != ProcessPriorityClass.Normal)
                        return false;
                    actuallyRestored = true;
                    _successfulPriorityRestores++;
                }
            }
        }
        catch (ArgumentException) { /* Exited or PID recycled: never touch. */ }
        catch (InvalidOperationException) { /* Process exited. */ }
        catch (Exception ex) when (ex is Win32Exception or
            UnauthorizedAccessException or NotSupportedException)
        {
            Status = "Procesy: obnovení priority čeká na ověření vlastníka";
            return false;
        }
        try
        {
            File.Delete(JournalPath);
            _owned = null;
            if (actuallyRestored && prepareReference &&
                _candidateCpuWindow.Count >=
                    AiAutomaticPriorityTrialPolicy.RequiredCandidateSnapshots &&
                old.BaselineTopCpuPercent >=
                    AiAutomaticProcessPriorityPolicy.MinimumBackgroundCpuPercent)
            {
                _restoredReference = new RestoredReference(
                    old.Name, old.ForegroundProcess, old.BaselineTopCpuPercent,
                    old.BaselineSystemCpuPercent, _candidateCpuWindow, nowUtc, default,
                    old.Pid, old.ForegroundProcessId,
                    _baselineResponseWindow, _candidateResponseWindow, default,
                    _baselineMemoryWindow, _candidateMemoryWindow, default,
                    old.Workload,
                    nowUtc >= old.AppliedUtc
                        ? (long)(nowUtc - old.AppliedUtc).TotalMilliseconds
                        : null, old.BaselineSamples);
                Status = "Procesy: původní priorita obnovena; ověřuji referenční měření";
            }
            else
            {
                _restoredReference = null;
                LastTrialResult = old.Name + ": " + reason + "; výkonový přínos neprokázán";
                PersistFeedback(old.Name, LastTrialResult, nowUtc);
                Status = "Procesy: obnoven původní stav (" + reason +
                    "); výkonový přínos zatím neprokázán";
            }
            _candidateCpuWindow = default;
            _baselineMemoryWindow = default;
            _candidateMemoryWindow = default;
            _baselineResponseWindow = default;
            _candidateResponseWindow = default;
            LogService.Info("ai-system",
                $"AI_PROCESS_PRIORITY_RESTORE name={old.Name} pid={old.Pid} " +
                $"actuallyRestored={actuallyRestored} referencePending={_restoredReference != null} " +
                $"holdMs={(nowUtc >= old.AppliedUtc ? ((long)(nowUtc - old.AppliedUtc).TotalMilliseconds).ToString() : "unavailable")} " +
                $"reason={reason}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Procesy: nelze potvrdit zrušení záznamu, další změny blokovány";
            _journalFaulted = true;
            LogService.Error("ai-system", "AI process priority journal removal failed", ex);
            return false;
        }
    }

    // Passive explanation of a failed eligibility gate. At most one line
    // per ten minutes, even if the target ranking changes every sampler tick.
    // No extra process scans, registry writes, timers or priority operations.
    private void ReportIneligible(string reason, DateTime nowUtc)
    {
        Status = "Procesy: bez zásahu (" + reason + ")";
        if (_lastEligibilityLogUtc != DateTime.MinValue &&
            (nowUtc < _lastEligibilityLogUtc ||
             nowUtc - _lastEligibilityLogUtc < TimeSpan.FromMinutes(10)))
            return;
        _lastEligibilityLogUtc = nowUtc;
        LogService.Info("ai-system", "AI_PROCESS_PRIORITY_GATE reason=" + reason);
    }

    private void ResetCandidate()
    {
        _candidate = string.Empty;
        _candidateFirstUtc = DateTime.MinValue;
        _candidateLastUtc = DateTime.MinValue;
        _candidateObservations = 0;
        _candidateForeground = string.Empty;
        _candidateWorkload = AiWorkloadClass.Unknown;
        _candidateTargetPid = 0;
        _candidateForegroundPid = 0;
        _candidateBaselineLoadPercent = 0;
        _baselineCpuWindow = default;
        _baselineMemoryWindow = default;
        _candidateMemoryWindow = default;
        _baselineResponseWindow = default;
        _candidateResponseWindow = default;
    }
}
