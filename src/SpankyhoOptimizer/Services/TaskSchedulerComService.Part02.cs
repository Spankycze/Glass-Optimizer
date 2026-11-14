using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpankyhoOptimizer.Services;

internal static partial class TaskSchedulerComService
{
    // A failed native COM property setter does not prove that our task
    // state change completed. Blindly restoring the earlier snapshot can
    // overwrite an independent user/task-scheduler change. Roll back only
    // after a completed own setter AND a fresh readback still equals the
    // state we requested. Diagnostic/restore failures never mask the
    // original operation error. Synthetic delegates avoid COM in tests.
    internal static bool TryRestoreTaskStateAfterFailure(
        bool writeCompleted, bool originalEnabled, bool requestedEnabled,
        Func<bool> getCurrentEnabled, Action<bool> restoreEnabled)
    {
        ArgumentNullException.ThrowIfNull(getCurrentEnabled);
        ArgumentNullException.ThrowIfNull(restoreEnabled);
        if (!writeCompleted || originalEnabled == requestedEnabled)
            return false;
        try
        {
            if (getCurrentEnabled() != requestedEnabled)
                return false;
            restoreEnabled(originalEnabled);
            return true;
        }
        catch
        {
            // The first failure is authoritative. Never blindly retry.
            return false;
        }
    }

    // Safe synthetic regression for the real COM rollback predicate.
    // Never creates Schedule.Service or touches an installed task.
    internal static bool RunTaskRollbackOwnership188Smoke()
    {
        bool current = false;
        int reads = 0, writes = 0;
        bool Restore(bool confirmedWrite, bool original, bool requested)
        {
            return TryRestoreTaskStateAfterFailure(
                confirmedWrite, original, requested,
                () => { reads++; return current; },
                restored => { writes++; current = restored; });
        }
        // Setter threw; another task owner set the same target value.
        if (Restore(false, true, false) || reads != 0 || writes != 0)
            return false;
        // Setter succeeded, but another actor changed task state later.
        current = true;
        if (Restore(true, true, false) || reads != 1 || writes != 0)
            return false;
        // Only a confirmed own write still at our desired state can be
        // rolled back to the original after downstream failure.
        current = false;
        if (!Restore(true, true, false) || !current ||
            reads != 2 || writes != 1)
            return false;
        // A failed readback must not trigger a blind restoration write.
        int attemptedRestore = 0;
        if (TryRestoreTaskStateAfterFailure(
                true, true, false,
                () => throw new InvalidOperationException("synthetic COM read failure"),
                _ => attemptedRestore++) || attemptedRestore != 0)
            return false;
        return true;
    }

    public static void SetEnabled(string path, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Final boundary: privileged callers must not bypass the same task
        // safety policy enforced by the GUI / inventory.
        TaskSchedulerService.EnsureTaskMutationAllowed(path, enabled);
        int split = path.LastIndexOf('\\');
        string folderPath = split <= 0 ? "\\" : path[..split];
        string taskName = path[(split + 1)..];
        object? serviceObj = null;
        object? folderObj = null;
        object? taskObj = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                ?? throw new PlatformNotSupportedException("Task Scheduler COM není dostupný.");
            serviceObj = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Task Scheduler COM nelze vytvořit.");
            dynamic service = serviceObj;
            service.Connect();
            folderObj = service.GetFolder(folderPath);
            dynamic folder = folderObj;
            taskObj = folder.GetTask(taskName);
            dynamic task = taskObj;
            bool original = task.Enabled;
            if (original == enabled) return;
            bool ownWriteCompleted = false;
            try
            {
                task.Enabled = enabled;
                ownWriteCompleted = true;
                if ((bool)task.Enabled != enabled)
                    throw new InvalidOperationException("Task Scheduler nepotvrdil požadovaný stav.");
            }
            catch
            {
                _ = TryRestoreTaskStateAfterFailure(
                    ownWriteCompleted, original, enabled,
                    () => (bool)task.Enabled,
                    restore => task.Enabled = restore);
                throw;
            }
        }
        finally
        {
            Release(taskObj);
            Release(folderObj);
            Release(serviceObj);
        }
    }

    public static string? TryGetTaskXml(string path)
    {
        object? serviceObj = null;
        object? folderObj = null;
        object? taskObj = null;
        try
        {
            int split = path.LastIndexOf('\\');
            string folderPath = split <= 0 ? "\\" : path[..split];
            string taskName = path[(split + 1)..];
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                ?? throw new PlatformNotSupportedException("Task Scheduler COM není dostupný.");
            serviceObj = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Task Scheduler COM nelze vytvořit.");
            dynamic service = serviceObj;
            service.Connect();
            folderObj = service.GetFolder(folderPath);
            dynamic folder = folderObj;
            taskObj = folder.GetTask(taskName);
            dynamic task = taskObj;
            return task.Xml?.ToString();
        }
        catch (Exception ex) when (IsKnownAbsentTaskException(ex)) { return null; }
        finally
        {
            Release(taskObj);
            Release(folderObj);
            Release(serviceObj);
        }
    }

    public static bool DeleteIfExists(string path, Func<string?, bool>? authorizeXml = null)
    {
        object? serviceObj = null;
        object? folderObj = null;
        try
        {
            int split = path.LastIndexOf('\\');
            string folderPath = split <= 0 ? "\\" : path[..split];
            string taskName = path[(split + 1)..];
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                ?? throw new PlatformNotSupportedException("Task Scheduler COM není dostupný.");
            serviceObj = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Task Scheduler COM nelze vytvořit.");
            dynamic service = serviceObj;
            service.Connect();
            folderObj = service.GetFolder(folderPath);
            dynamic folder = folderObj;
            return DeleteRegisteredTaskIfPresent(
                () => (object)folder.GetTask(taskName),
                () => { folder.DeleteTask(taskName, 0); },
                Release,
                authorizeXml is null ? null :
                    registered => authorizeXml(
                        ((dynamic)registered).Xml?.ToString()));
        }
        catch (Exception ex) when (IsKnownAbsentTaskException(ex)) { return false; }
        finally
        {
            Release(folderObj);
            Release(serviceObj);
        }
    }

    public static string[] SetEnabledBatch(IReadOnlyList<(string Path, bool Enabled)> changes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Every caller, including direct privileged COM access, receives a
        // frozen validated batch. Reject contradictory duplicates before
        // changing the first task or even connecting to Schedule.Service.
        var approved = TaskSchedulerService.PrepareTaskMutationBatch(changes);
        if (approved.Length == 0) return Array.Empty<string>();
        var errors = new List<string>();
        var changed = new List<(string Path, bool Original, bool Requested)>();
        object? serviceObj = null;
        var folders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                ?? throw new PlatformNotSupportedException("Task Scheduler COM není dostupný.");
            serviceObj = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Task Scheduler COM nelze vytvořit.");
            dynamic service = serviceObj;
            service.Connect();
            foreach (var change in approved)
            {
                object? taskObj = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    int split = change.Path.LastIndexOf('\\');
                    string folderPath = split <= 0 ? "\\" : change.Path[..split];
                    string taskName = change.Path[(split + 1)..];
                    if (!folders.TryGetValue(folderPath, out var folderObj))
                    {
                        folderObj = service.GetFolder(folderPath);
                        folders[folderPath] = folderObj;
                    }
                    dynamic folder = folderObj;
                    taskObj = folder.GetTask(taskName);
                    dynamic task = taskObj;
                    bool original = task.Enabled;
                    if (original == change.Enabled) continue;
                    bool ownWriteCompleted = false;
                    try
                    {
                        task.Enabled = change.Enabled;
                        ownWriteCompleted = true;
                        if ((bool)task.Enabled != change.Enabled)
                            throw new InvalidOperationException("Task Scheduler nepotvrdil požadovaný stav.");
                        changed.Add((change.Path, original, change.Enabled));
                    }
                    catch
                    {
                        _ = TryRestoreTaskStateAfterFailure(
                            ownWriteCompleted, original, change.Enabled,
                            () => (bool)task.Enabled,
                            restore => task.Enabled = restore);
                        throw;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add("Task " + change.Path + ": " + ex.Message);
                    break;
                }
                finally { Release(taskObj); }
            }
            if (errors.Count != 0)
            {
                for (int index = changed.Count - 1; index >= 0; index--)
                {
                    var prior = changed[index];
                    object? rollbackTaskObj = null;
                    try
                    {
                        int split = prior.Path.LastIndexOf('\\');
                        string folderPath = split <= 0 ? "\\" : prior.Path[..split];
                        string taskName = prior.Path[(split + 1)..];
                        if (!folders.TryGetValue(folderPath, out var folderObj))
                        {
                            folderObj = service.GetFolder(folderPath);
                            folders[folderPath] = folderObj;
                        }
                        dynamic folder = folderObj;
                        rollbackTaskObj = folder.GetTask(taskName);
                        dynamic task = rollbackTaskObj;
                        _ = TryRestoreTaskStateAfterFailure(
                            true, prior.Original, prior.Requested,
                            () => (bool)task.Enabled,
                            restore => task.Enabled = restore);
                    }
                    catch (Exception ex)
                    {
                        errors.Add("Task " + prior.Path + ": rollback selhal: " + ex.Message);
                    }
                    finally { Release(rollbackTaskObj); }
                }
            }
            return errors.ToArray();
        }
        finally
        {
            foreach (var folder in folders.Values) Release(folder);
            Release(serviceObj);
        }
    }

    public static IReadOnlyList<TaskInfo> GetAll(CancellationToken token, bool includeStartupTrigger = true)
    {
        object? serviceObj = null;
        object? rootObj = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                ?? throw new PlatformNotSupportedException("Task Scheduler COM není dostupný.");
            serviceObj = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Task Scheduler COM nelze vytvořit.");
            dynamic service = serviceObj;
            service.Connect();
            rootObj = service.GetFolder("\\");
            var result = new List<TaskInfo>(512);
            long walkStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            long xmlTicks = 0;
            int xmlReads = 0;
            WalkFolder(rootObj, result, token, includeStartupTrigger, ref xmlTicks, ref xmlReads);
            double walkMs = System.Diagnostics.Stopwatch.GetElapsedTime(walkStarted).TotalMilliseconds;
            double xmlMs = xmlTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LogService.Info("tasks", $"TASK_COM_WALK_DIAG count={result.Count} enrich={includeStartupTrigger} walkMs={walkMs:0.0} xmlReads={xmlReads} xmlMs={xmlMs:0.0}");
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            // Publish a compact, fixed-size snapshot; the service can reuse this
            // same array for memory cache and persistent serialization.
            return result.ToArray();
        }
        finally
        {
            Release(rootObj);
            Release(serviceObj);
        }
    }

    private static void WalkFolder(object folderObj, List<TaskInfo> result, CancellationToken token,
        bool includeStartupTrigger, ref long xmlTicks, ref int xmlReads)
    {
        token.ThrowIfCancellationRequested();
        dynamic folder = folderObj;
        object? tasksObj = null;
        object? foldersObj = null;
        try
        {
            tasksObj = folder.GetTasks(TaskEnumHidden);
            dynamic tasks = tasksObj;
            int taskCount = tasks.Count;
            for (int i = 1; i <= taskCount; i++)
            {
                token.ThrowIfCancellationRequested();
                object? taskObj = null;
                try
                {
                    taskObj = tasks.Item(i);
                    dynamic task = taskObj;
                    // COM returns BSTR for Path. Avoid an extra dynamic ToString dispatch
                    // on every task in this hot enumeration loop.
                    string path = (string?)task.Path ?? string.Empty;
                    bool enabled = task.Enabled;
                    if (path.Length != 0)
                    {
                        int slash = path.LastIndexOf('\\');
                        string name = slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
                        bool startupTrigger = false;
                        string? executable = null;
                        if (includeStartupTrigger)
                        {
                            long xmlStart = System.Diagnostics.Stopwatch.GetTimestamp();
                            // Xml is already a COM BSTR/string; do not invoke ToString dynamically.
                            string xml = (string?)task.Xml ?? string.Empty;
                            xmlTicks += System.Diagnostics.Stopwatch.GetTimestamp() - xmlStart;
                            xmlReads++;
                            startupTrigger = xml.Contains("<BootTrigger", StringComparison.OrdinalIgnoreCase) ||
                                             xml.Contains("<LogonTrigger", StringComparison.OrdinalIgnoreCase);
                            if (startupTrigger)
                                executable = TryExtractCommand(xml);
                        }
                        result.Add(new TaskInfo(path, name, enabled, startupTrigger, executable));
                    }
                }
                finally { Release(taskObj); }
            }
            foldersObj = folder.GetFolders(0);
            dynamic folders = foldersObj;
            int folderCount = folders.Count;
            for (int i = 1; i <= folderCount; i++)
            {
                token.ThrowIfCancellationRequested();
                object? childObj = null;
                try
                {
                    childObj = folders.Item(i);
                    WalkFolder(childObj, result, token, includeStartupTrigger, ref xmlTicks, ref xmlReads);
                }
                finally { Release(childObj); }
            }
        }
        finally
        {
            Release(tasksObj);
            Release(foldersObj);
        }
    }

    private static string? TryExtractCommand(string xml)
    {
        int start = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += 9;
        int end = xml.IndexOf("</Command>", start, StringComparison.OrdinalIgnoreCase);
        if (end <= start) return null;
        string decoded = System.Net.WebUtility.HtmlDecode(xml[start..end]).Trim();
        return decoded.Length == 0 ? null : Environment.ExpandEnvironmentVariables(decoded);
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
