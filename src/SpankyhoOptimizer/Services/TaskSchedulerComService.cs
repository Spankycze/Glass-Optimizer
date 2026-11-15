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

internal static partial class TaskSchedulerComService
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
        using var session = TaskComSession.Open(path, out dynamic folder);
        RunRegisteredTaskIfAuthorized(
            () => (object)folder.GetTask(session.TaskName),
            registered => ((dynamic)registered).Xml?.ToString(),
            validateTaskXml,
            registered => (object?)((dynamic)registered).Run(null),
            Release,
            token);
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
        using var session = TaskComSession.Open(path, out dynamic folder);
        object? taskObj = null;
        try
        {
            taskObj = folder.GetTask(session.TaskName);
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
        finally { Release(taskObj); }
    }

    public static bool GetEnabled(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var session = TaskComSession.Open(path, out dynamic folder);
        object? taskObj = null;
        try
        {
            taskObj = folder.GetTask(session.TaskName);
            dynamic task = taskObj;
            return (bool)task.Enabled;
        }
        finally { Release(taskObj); }
    }

    public static bool IsRunning(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var session = TaskComSession.Open(path, out dynamic folder);
        object? taskObj = null;
        try
        {
            taskObj = folder.GetTask(session.TaskName);
            dynamic task = taskObj;
            return Convert.ToInt32(task.State) == 4;
        }
        finally { Release(taskObj); }
    }

    public static DateTime? TryGetNextRunTimeUtc(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var session = TaskComSession.Open(path, out dynamic folder);
        object? taskObj = null;
        try
        {
            taskObj = folder.GetTask(session.TaskName);
            dynamic task = taskObj;
            DateTime next = task.NextRunTime;
            return next <= DateTime.MinValue.AddDays(1) ? null : next.ToUniversalTime();
        }
        catch
        {
            return null;
        }
        finally { Release(taskObj); }
    }
}
