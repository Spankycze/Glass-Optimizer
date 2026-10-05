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

    public static IReadOnlyDictionary<string, TaskRuntimeState> GetRuntimeStates(
        IReadOnlyList<string> paths,
        CancellationToken token)
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
                catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException)
                {
                    // Missing tasks are normal across Windows editions/builds.
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

    public static void SetEnabled(string path, bool enabled, CancellationToken token)
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
            bool original = task.Enabled;
            if (original == enabled) return;
            try
            {
                task.Enabled = enabled;
                if ((bool)task.Enabled != enabled)
                    throw new InvalidOperationException("Task Scheduler nepotvrdil požadovaný stav.");
            }
            catch
            {
                try { task.Enabled = original; } catch { }
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
        catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException) { return null; }
        finally
        {
            Release(taskObj);
            Release(folderObj);
            Release(serviceObj);
        }
    }

    public static bool DeleteIfExists(string path)
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
            try { folder.GetTask(taskName); }
            catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException) { return false; }
            folder.DeleteTask(taskName, 0);
            return true;
        }
        catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException) { return false; }
        finally
        {
            Release(folderObj);
            Release(serviceObj);
        }
    }

    public static string[] SetEnabledBatch(IReadOnlyList<(string Path, bool Enabled)> changes, CancellationToken token)
    {
        var errors = new List<string>();
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
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                object? taskObj = null;
                try
                {
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
                    try
                    {
                        task.Enabled = change.Enabled;
                        if ((bool)task.Enabled != change.Enabled)
                            throw new InvalidOperationException("Task Scheduler nepotvrdil požadovaný stav.");
                    }
                    catch
                    {
                        try { task.Enabled = original; } catch { }
                        throw;
                    }
                }
                catch (Exception ex) { errors.Add("Task " + change.Path + ": " + ex.Message); }
                finally { Release(taskObj); }
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
            WalkFolder(rootObj, result, token, includeStartupTrigger);
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            return result;
        }
        finally
        {
            Release(rootObj);
            Release(serviceObj);
        }
    }

    private static void WalkFolder(object folderObj, List<TaskInfo> result, CancellationToken token,
        bool includeStartupTrigger)
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
                    string path = task.Path?.ToString() ?? string.Empty;
                    bool enabled = task.Enabled;
                    if (path.Length != 0)
                    {
                        int slash = path.LastIndexOf('\\');
                        string name = slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
                        bool startupTrigger = false;
                        string? executable = null;
                        if (includeStartupTrigger)
                        {
                            string xml = task.Xml?.ToString() ?? string.Empty;
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
                    WalkFolder(childObj, result, token, includeStartupTrigger);
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
