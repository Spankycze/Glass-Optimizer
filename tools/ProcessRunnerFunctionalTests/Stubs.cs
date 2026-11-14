namespace SpankyhoOptimizer.Services;
internal static class LogService { public static string Root => Path.GetTempPath(); public static void Error(string category, string message, Exception? ex = null) {} public static void Info(string category, string message) {} }
internal static class FaultInjectionService { public static Task InjectAsync(string name, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
internal static class DiagnosticsTelemetry { public static System.Diagnostics.Activity? Start(string name, params (string, object)[] tags) => null; public static void RecordDuration(string name, double ms, params (string, object)[] tags) {} }
internal static class ContextMenuService
{
 public static Dictionary<string,bool> ReadOurEntryStates() => new();
 public static void SetTerminalHere(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetCmdHere(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetPowerShellHere(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetAdminPowerShellHere(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetCopyPath(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetSha256(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetNewEmptyFile(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetVsCode(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetAdvancedStartup(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetRestartExplorer(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetAdminTerminalHere(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetTaskManager(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void SetDeviceManager(bool value) => throw new InvalidOperationException("Forbidden fixture mutation");
 public static void OptimizeContextMenuExperience() => throw new InvalidOperationException("Forbidden fixture mutation");
}
internal sealed record TaskInfo(string Name, string Path, bool Enabled, bool StartupTrigger=false);
internal sealed class TaskSchedulerService
{
 public Task<IReadOnlyList<TaskInfo>> GetBasicAsync(bool forceRefresh=false,CancellationToken token=default) => throw new InvalidOperationException("Forbidden fixture mutation");
 public Task SetEnabledAsync(string path,bool enabled,CancellationToken token) => throw new InvalidOperationException("Forbidden fixture mutation");
}

internal static class WorkerExecutionContext { public static bool IsWorker => true; }
internal static class WorkerPrivilegePolicy { public static bool IsAdministrator() => true; }
internal sealed class WorkerAck { }
internal sealed class ModernizerAdaptiveRequest { public AdaptiveScanReport Report { get; } public ModernizerAdaptiveRequest(AdaptiveScanReport report) { Report = report; } }
internal static class WorkerPayloads { public static string Encode<T>(T value) => "fixture"; }
internal static class WorkerClientService
{
 public static Task<T> RunAsync<T>(string action, string? arg = null, TimeSpan? timeout = null, CancellationToken token = default) => throw new InvalidOperationException("Forbidden fixture broker call: " + action);
}
