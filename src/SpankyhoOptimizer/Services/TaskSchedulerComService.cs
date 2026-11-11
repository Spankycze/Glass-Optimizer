using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpankyhoOptimizer.Services;

internal readonly record struct TaskRuntimeState(
    bool Enabled,
    bool Running,
    DateTime? NextRunUtc,
    DateTime? LastRunUtc);

internal static class TaskSchedulerComService
{
    private const int TaskEnumHidden = 1;

    // Scheduler HRESULTs for an actual absent task/folder. Access-denied or
    // corrupted task definitions are NOT evidence of an uninstalled task.
    internal static bool IsMissingTaskError(int hresult) =>
        hresult == unchecked((int)0x80070002) || // ERROR_FILE_NOT_FOUND
        hresult == unchecked((int)0x80070003) || // ERROR_PATH_NOT_FOUND
        hresult == unchecked((int)0x8004130F);   // SCHED_E_UNKNOWN_OBJECT

    // Access denied, malformed XML, RPC failure and COM service errors must
    // NEVER masquerade as an absent task. In particular, PowerPlanGuard
    // must not delete its script if the schedule could not be removed.
    internal static bool IsKnownAbsentTaskException(Exception error) =>
        error is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException ||
        error is COMException com && IsMissingTaskError(com.HResult);

    // Separate lookup, mutation and resource-release from live COM so the
    // absence/failure distinction is verifiable without touching Windows.
    internal static bool DeleteRegisteredTaskIfPresent(
        Func<object> lookupTask, Action deleteTask, Action<object> releaseTask,
        Func<object, bool>? authorizeTask = null)
    {
        ArgumentNullException.ThrowIfNull(lookupTask);
        ArgumentNullException.ThrowIfNull(deleteTask);
        ArgumentNullException.ThrowIfNull(releaseTask);
        object? task = null;
        try
        {
            try { task = lookupTask(); }
            catch (Exception ex) when (IsKnownAbsentTaskException(ex)) { return false; }
            if (task is null)
                throw new InvalidOperationException(
                    "Task Scheduler returned a null registered task object.");
            // Names are not ownership evidence. Power Plan guard may share
            // a name with a user-replaced task: check the ACTUAL retrieved
            // IRegisteredTask XML before any destructive COM operation.
            if (authorizeTask is not null && !authorizeTask(task))
                throw new InvalidOperationException(
                    "Naplánovaná úloha má cizí definici; její odstranění bylo zablokováno.");
            try { deleteTask(); return true; }
            catch (Exception ex) when (IsKnownAbsentTaskException(ex)) { return false; }
        }
        finally
        {
            if (task is not null) releaseTask(task);
        }
    }

    // Authenticate and run through the SAME retrieved IRegisteredTask
    // handle. A separate schtasks.exe /Run /TN would re-resolve the name
    // after the authorization read and could launch a replacement task.
    // COM itself does not promise transactional isolation from other writers.
    // All delegates can be synthetic: unit tests never connect to Scheduler.
    internal static void RunRegisteredTaskIfAuthorized(
        Func<object> lookupTask,
        Func<object, string?> readTaskXml,
        Action<string?> validateTaskXml,
        Func<object, object?> runTask,
        Action<object> releaseObject,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(lookupTask);
        ArgumentNullException.ThrowIfNull(readTaskXml);
        ArgumentNullException.ThrowIfNull(validateTaskXml);
        ArgumentNullException.ThrowIfNull(runTask);
        ArgumentNullException.ThrowIfNull(releaseObject);
        token.ThrowIfCancellationRequested();
        object? task = null;
        object? running = null;
        try
        {
            task = lookupTask() ?? throw new InvalidOperationException(
                "Task Scheduler vrátil prázdný objekt naplánované úlohy.");
            token.ThrowIfCancellationRequested();
            validateTaskXml(readTaskXml(task));
            token.ThrowIfCancellationRequested();
            running = runTask(task) ?? throw new InvalidOperationException(
                "Task Scheduler nepotvrdil spuštění naplánované úlohy.");
        }
        finally
        {
            if (running is not null && !ReferenceEquals(running, task))
            {
                try { releaseObject(running); }
                finally { if (task is not null) releaseObject(task); }
            }
            else if (task is not null)
            {
                releaseObject(task);
            }
        }
    }

    // This direct COM Run() uses the same task RCW for XML admission and
    // launch. Missing/unreadable tasks and ACL failures propagate fail-closed.
    public static void RunAuthorizedTask(
        string path, Action<string?> validateTaskXml, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(validateTaskXml);
        token.ThrowIfCancellationRequested();
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
            RunRegisteredTaskIfAuthorized(
                () => (object)folder.GetTask(taskName),
                registered => ((dynamic)registered).Xml?.ToString(),
                validateTaskXml,
                registered => (object?)((dynamic)registered).Run(null),
                Release,
                token);
        }
        finally
        {
            Release(folderObj);
            Release(serviceObj);
        }
    }

    // No Schedule.Service connections: compiled safe policy regression.
    internal static bool RunTaskMissingVsUnreadable190Smoke()
    {
        int released = 0, deletes = 0;
        object existing = new();
        if (DeleteRegisteredTaskIfPresent(
                () => throw new System.IO.FileNotFoundException("synthetic absent task"),
                () => deletes++, _ => released++) ||
            deletes != 0 || released != 0)
            return false;
        bool denied = false;
        try
        {
            _ = DeleteRegisteredTaskIfPresent(
                () => throw new UnauthorizedAccessException("synthetic task ACL denial"),
                () => deletes++, _ => released++);
        }
        catch (UnauthorizedAccessException) { denied = true; }
        if (!denied || deletes != 0 || released != 0)
            return false;
        if (!DeleteRegisteredTaskIfPresent(
                () => existing, () => deletes++, _ => released++) ||
            deletes != 1 || released != 1)
            return false;
        // Real COM HRESULT classification is separately tested with
        // synthetic COMException objects in CoreTests, not in Release code.
        return !IsMissingTaskError(unchecked((int)0x80070005)) &&
               IsMissingTaskError(unchecked((int)0x8004130F)) &&
               IsKnownAbsentTaskException(new System.IO.FileNotFoundException()) &&
               !IsKnownAbsentTaskException(new UnauthorizedAccessException());
    }

    public static IReadOnlyDictionary<string, TaskRuntimeState> GetRuntimeStates(
        IReadOnlyList<string> paths,
        CancellationToken token,
        ISet<string>? unreadablePaths = null)
    {
        var result = new Dictionary<string, TaskRuntimeState>(StringComparer.OrdinalIgnoreCase);
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

            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                object? taskObj = null;
                try
                {
                    int split = path.LastIndexOf('\\');
                    string folderPath = split <= 0 ? "\\" : path[..split];
                    string taskName = path[(split + 1)..];

                    if (!folders.TryGetValue(folderPath, out var folderObj))
                    {
                        folderObj = service.GetFolder(folderPath);
                        folders[folderPath] = folderObj;
                    }

                    dynamic folder = folderObj;
                    taskObj = folder.GetTask(taskName);
                    dynamic task = taskObj;

                    DateTime next = task.NextRunTime;
                    DateTime last = task.LastRunTime;
                    DateTime? nextUtc = next <= DateTime.MinValue.AddDays(1)
                        ? null
                        : next.ToUniversalTime();
                    DateTime? lastUtc = last <= DateTime.MinValue.AddDays(1)
                        ? null
                        : last.ToUniversalTime();
                    result[path] = new TaskRuntimeState(
                        (bool)task.Enabled,
                        Convert.ToInt32(task.State) == 4,
                        nextUtc,
                        lastUtc);
                }
                catch (System.IO.FileNotFoundException)
                {
                    // A missing optional Windows task is a normal inventory fact.
                }
                catch (System.IO.DirectoryNotFoundException)
                {
                    // A missing task folder is not a failed AI action.
                }
                catch (COMException ex) when (IsMissingTaskError(ex.HResult))
                {
                    // Only known Scheduler not-found HRESULTs mean absent.
                }
                catch (COMException)
                {
                    // E_ACCESSDENIED, malformed definitions and COM failures
                    // must NEVER be silently converted into "not installed".
                    if (unreadablePaths == null) throw;
                    unreadablePaths.Add(path);
                }
                finally
                {
                    Release(taskObj);
                }
            }

            return result;
        }
        finally
        {
            foreach (var folder in folders.Values) Release(folder);
            Release(serviceObj);
        }
    }

    public static TaskRuntimeState GetRuntimeState(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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

            DateTime next = task.NextRunTime;
            DateTime last = task.LastRunTime;
            DateTime? nextUtc = next <= DateTime.MinValue.AddDays(1) ? null : next.ToUniversalTime();
            DateTime? lastUtc = last <= DateTime.MinValue.AddDays(1) ? null : last.ToUniversalTime();
            return new TaskRuntimeState(
                (bool)task.Enabled,
                Convert.ToInt32(task.State) == 4,
                nextUtc,
                lastUtc);
        }
        finally
        {
            Release(taskObj);
            Release(folderObj);
            Release(serviceObj);
        }
    }

    public static bool GetEnabled(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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
            return task.Enabled;
        }
        finally
        {
            Release(taskObj);
            Release(folderObj);
            Release(serviceObj);
        }
    }

    public static bool IsRunning(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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
            return Convert.ToInt32(task.State) == 4;
        }
        finally
        {
            Release(taskObj);
            Release(folderObj);
            Release(serviceObj);
        }
    }

    public static DateTime? TryGetNextRunTimeUtc(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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
            DateTime next = task.NextRunTime;
            return next <= DateTime.MinValue.AddDays(1) ? null : next.ToUniversalTime();
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(taskObj);
            Release(folderObj);
            Release(serviceObj);
        }
    }

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
