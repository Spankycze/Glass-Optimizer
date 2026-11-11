using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace SpankyhoOptimizer.Services;

// One exact Windows Search maintenance task. The task is not a generic
// optional telemetry candidate: disabling it only makes sense while the
// user's AI-owned WSearch service is stopped/disabled. No new UI or timer.
internal sealed class AiWindowsSearchMaintenanceTaskService
{
    private const string TaskPath = @"\Microsoft\Windows\Shell\IndexerAutomaticMaintenance";
    private sealed record Ownership(bool Applied, bool Suppressed);
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "ai-wsearch-maintenance-task-ownership.json");

    private long _lastCheckStamp;
    internal bool IsManaged { get; private set; }
    internal string LastError { get; private set; } = "";

    internal string? Tick(bool enabled, CancellationToken token)
    {
        long stamp = Stopwatch.GetTimestamp();
        if (enabled && _lastCheckStamp != 0 &&
            Stopwatch.GetElapsedTime(_lastCheckStamp, stamp) < TimeSpan.FromSeconds(45))
            return null;
        _lastCheckStamp = stamp;
        try
        {
            if (!enabled && !File.Exists(JournalPath))
            {
                IsManaged = false;
                LastError = "";
                return null;
            }
            Ownership? owner = File.Exists(JournalPath)
                ? JsonSerializer.Deserialize<Ownership>(File.ReadAllText(JournalPath))
                    ?? throw new InvalidDataException("Windows Search task journal is invalid.")
                : null;
            token.ThrowIfCancellationRequested();
            if (owner is { Suppressed: true })
            {
                if (!enabled)
                {
                    File.Delete(JournalPath);
                    IsManaged = false;
                }
                LastError = "";
                return null;
            }
            var state = TaskSchedulerComService.GetRuntimeState(TaskPath, token);
            if (!enabled)
            {
                // If interrupted after the intent write, the disabled task is
                // still ours to restore, regardless of Applied=false.
                bool changed = !state.Enabled;
                if (changed)
                {
                    TaskSchedulerComService.SetEnabled(TaskPath, true, token);
                    if (!TaskSchedulerComService.GetRuntimeState(TaskPath, token).Enabled)
                        throw new IOException("Windows Search maintenance task restore read-back failed.");
                }
                File.Delete(JournalPath);
                IsManaged = false;
                LastError = "";
                return changed ? "Windows Search maintenance task restored: " + TaskPath
                    : "Windows Search maintenance task already restored externally";
            }

            if (owner is { Applied: true })
            {
                if (state.Enabled)
                {
                    // Manual/OS re-enable takes precedence over AI. Do not
                    // fight it on each subsequent FanAgent tick.
                    AtomicFileService.WriteText(JournalPath,
                        JsonSerializer.Serialize(owner with { Suppressed = true }));
                    IsManaged = false;
                    LastError = "";
                    return "Windows Search maintenance task manually re-enabled; AI yielded";
                }
                IsManaged = true;
                LastError = "";
                return null;
            }

            if (!state.Enabled)
            {
                if (owner is { Applied: false })
                {
                    // Interrupted after disabling but before the final WAL
                    // commit. Recognize the live result without toggling.
                    AtomicFileService.WriteText(JournalPath,
                        JsonSerializer.Serialize(owner with { Applied = true }));
                    IsManaged = true;
                    LastError = "";
                    return "Windows Search maintenance task ownership recovered";
                }
                IsManaged = false; // Task was already disabled independently.
                LastError = "";
                return null;
            }

            if (state.Running)
                return null; // No interruption of a task already executing.
            if (!TaskSafetyService.CanDisable(TaskPath))
                throw new InvalidOperationException("Windows Search maintenance task is protected.");
            if (owner is null)
            {
                // Save original enabled state before the Task Scheduler write.
                owner = new Ownership(Applied: false, Suppressed: false);
                AtomicFileService.WriteText(JournalPath, JsonSerializer.Serialize(owner));
            }
            TaskSchedulerComService.SetEnabled(TaskPath, false, token);
            if (TaskSchedulerComService.GetRuntimeState(TaskPath, token).Enabled)
                throw new IOException("Windows Search maintenance task disable read-back failed.");
            AtomicFileService.WriteText(JournalPath,
                JsonSerializer.Serialize(owner with { Applied = true }));
            IsManaged = true;
            LastError = "";
            return "Windows Search maintenance task disabled: " + TaskPath;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            IsManaged = false;
            LastError = ex.GetBaseException().Message;
            LogService.Error("ai-system", "Windows Search maintenance task optimization failed", ex);
            return null;
        }
    }
}
