using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace SpankyhoOptimizer.Services;

// Optional Windows maintenance/telemetry tasks are a separate automatic scope.
// Only the existing AI allowlist is eligible for writes; all installed tasks
// are observed by AiSystemInventoryCoverageService. Never touch update/security
// reconciliation or user/third-party tasks merely because they exist.
internal sealed class AiOptionalTaskOptimizationService
{
    private sealed record Ownership(bool Applied, bool Suppressed);
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "ai-optional-task-ownership.json");
    private readonly Dictionary<string, Ownership> _owned = new(StringComparer.OrdinalIgnoreCase);
    private long _lastStamp;
    private bool _loaded;
    private long _missingPreferenceSinceStamp;
    internal int ManagedCount => _owned.Count(x => x.Value.Applied && !x.Value.Suppressed);
    internal string LastError { get; private set; } = "";

    internal (bool KeepOwned, bool AllowNewWrites) ReadUserPreference()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\SpankyhoOptimizer\AI");
            object? preference = key?.GetValue("DisableOptionalBackgroundTasks");
            if (preference is int value)
            {
                // DWORD 0 is an immediate opt-out, not a transient absence.
                _missingPreferenceSinceStamp = 0;
                return (value == 1, value == 1);
            }
            // Missing value is temporarily tolerated ONLY for owned tasks.
            // Do not start any new mutations without explicit DWORD 1.
            if (preference is null && File.Exists(JournalPath))
            {
                long stamp = Stopwatch.GetTimestamp();
                if (_missingPreferenceSinceStamp == 0)
                    _missingPreferenceSinceStamp = stamp;
                bool grace = Stopwatch.GetElapsedTime(
                    _missingPreferenceSinceStamp, stamp) < TimeSpan.FromMinutes(2);
                return (grace, false);
            }
            _missingPreferenceSinceStamp = 0;
        }
        catch { _missingPreferenceSinceStamp = 0; }
        return (false, false);
    }

    internal string? Tick(bool enabled, IReadOnlyList<string> allowed, CancellationToken token)
    {
        long now = Stopwatch.GetTimestamp();
        // Recovery is urgent: an explicit AI OFF/opt-out must not wait for
        // the ordinary 45-second optimization cadence.
        if (enabled && _lastStamp != 0 &&
            Stopwatch.GetElapsedTime(_lastStamp, now) < TimeSpan.FromSeconds(45))
            return null;
        _lastStamp = now;
        try
        {
            if (!_loaded)
            {
                // Unreadable ownership is NOT empty permission to begin writes.
                if (File.Exists(JournalPath))
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, Ownership>>(
                        File.ReadAllText(JournalPath))
                        ?? throw new InvalidDataException("Task ownership journal is invalid.");
                    foreach (var pair in saved)
                        _owned.Add(pair.Key, pair.Value);
                }
                _loaded = true;
            }
            token.ThrowIfCancellationRequested();
            // Include owned tasks in recovery even if allowlist evolves.
            string[] candidates = (enabled ? allowed : Array.Empty<string>())
                .Concat(_owned.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 0) return null;
            var states = TaskSchedulerComService.GetRuntimeStates(candidates, token);
            DateTime utcNow = DateTime.UtcNow;

            // Restore user tasks on AI OFF or when the user clears opt-in.
            if (!enabled)
            {
                int restored = 0;
                string? firstRestored = null;
                // Restore ALL owned tasks in one explicit opt-out pass. The
                // journal is committed after EACH read-back, so a partial
                // failure still retains evidence for every remaining item.
                foreach (var pair in _owned.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (pair.Value.Suppressed)
                    {
                        _owned.Remove(pair.Key);
                        SaveJournal();
                        continue;
                    }
                    // An unapplied write-ahead intent can mean the agent
                    // crashed AFTER disabling the task but BEFORE marking
                    // Applied=true. Inspect the live state and restore it;
                    // never discard the only recovery journal prematurely.
                    if (!states.TryGetValue(pair.Key, out var live))
                        continue; // Unreadable: retain original-state evidence.
                    if (live.Enabled)
                    {
                        _owned.Remove(pair.Key); // user or Windows already restored.
                        SaveJournal();
                        continue;
                    }
                    TaskSchedulerComService.SetEnabled(pair.Key, true, token);
                    if (!TaskSchedulerComService.GetRuntimeState(pair.Key, token).Enabled)
                        throw new IOException("Task Scheduler restore did not confirm Enabled=true");
                    _owned.Remove(pair.Key);
                    SaveJournal();
                    restored++;
                    firstRestored ??= pair.Key;
                }
                LastError = "";
                return restored > 0
                    ? $"Scheduled tasks restored: {restored}; first: {firstRestored}"
                    : null;
            }

            // Only reversible, optional, originally enabled Windows tasks
            // with no current or imminent scheduled activity may be stopped.
            // One mutation per pass; do not swamp Task Scheduler at startup.
            foreach (string path in allowed)
            {
                token.ThrowIfCancellationRequested();
                if (!path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) ||
                    !TaskSafetyService.CanDisable(path))
                    continue;
                if (!states.TryGetValue(path, out var live))
                    continue;
                if (_owned.TryGetValue(path, out var prior))
                {
                    if (prior.Suppressed)
                        continue;
                    if (!prior.Applied)
                    {
                        // Interrupted after the durable intent, before write:
                        // if still enabled retry; if disabled confirm ownership.
                        if (!live.Enabled)
                        {
                            _owned[path] = prior with { Applied = true };
                            SaveJournal();
                            return "Scheduled task state recovered: " + path;
                        }
                    }
                    else if (live.Enabled)
                    {
                        // External re-enable is demand: never fight the user.
                        _owned[path] = prior with { Suppressed = true };
                        SaveJournal();
                        continue;
                    }
                    else continue;
                }
                if (!live.Enabled || live.Running ||
                    (live.NextRunUtc is DateTime next &&
                     next >= utcNow.AddMinutes(-1) &&
                     next <= utcNow.AddMinutes(45)))
                    continue;
                if (AiPerformanceExperimentCoordinator.IsActive)
                    break;
                // This is a durable ownership write BEFORE Task Scheduler COM.
                if (!_owned.ContainsKey(path))
                {
                    _owned[path] = new Ownership(Applied: false, Suppressed: false);
                    SaveJournal();
                }
                TaskSchedulerComService.SetEnabled(path, false, token);
                if (TaskSchedulerComService.GetRuntimeState(path, token).Enabled)
                    throw new IOException("Task Scheduler opt-out read-back failed");
                _owned[path] = new Ownership(Applied: true, Suppressed: false);
                SaveJournal();
                LastError = "";
                return "Optional scheduled task disabled: " + path;
            }
            LastError = "";
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
            LogService.Error("ai-system", "Automatic optional task optimization failed", ex);
            return null;
        }
    }

    private void SaveJournal()
    {
        AtomicFileService.WriteAllBytesAtomic(JournalPath,
            JsonSerializer.SerializeToUtf8Bytes(_owned));
    }
}
