using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record PowerPlanGuardStatus(
    string? ConfiguredGuid,
    bool LogonConfigured,
    bool BootConfigured,
    string LastResult,
    bool LastRunOk);

public static class PowerPlanGuardService
{
    public const string TaskName = @"\SpankyhoOptimizer-PowerPlanGuard";
    public const string BootTaskName = @"\SpankyhoOptimizer-PowerPlanGuard-Boot";
    public static string GuardScriptPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer",
        "power-plan-guard.cmd");

    public static string GuardStatusPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer",
        "power-plan-guard-status.log");

    private static string LegacyGuardScriptPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer",
        "power-plan-guard.ps1");

    private static readonly Regex GuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    internal static string BuildCreateArguments(string guid) =>
        BuildCreateArguments(guid, boot: false);

    internal static string BuildCreateArguments(string guid, bool boot)
    {
        if (!Guid.TryParse(guid, out var parsed))
            throw new ArgumentException("Power plan GUID není platný.", nameof(guid));

        var adaptive = PerformanceProfilesService.GetAdaptivePowerProfile();
        string normalized = parsed.ToString("D");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        string taskCommand =
            $"\"{cmd}\" /D /S /C \"\"{GuardScriptPath}\" {normalized} " +
            $"{adaptive.Epp} {adaptive.Epp:x8} " +
            $"{adaptive.CoreMin} {adaptive.CoreMin:x8} " +
            $"{adaptive.IncThreshold} {adaptive.IncThreshold:x8} " +
            $"{adaptive.DecThreshold} {adaptive.DecThreshold:x8} " +
            $"{adaptive.PerfCheckMs} {adaptive.PerfCheckMs:x8}\"";

        string taskName = boot ? BootTaskName : TaskName;
        string trigger = boot ? "/SC ONSTART /DELAY 0000:15" : "/SC ONLOGON /DELAY 0000:30";
        return $"/Create /F /TN \"{taskName}\" {trigger} /RU SYSTEM /RL HIGHEST /TR \"{taskCommand.Replace("\"", "\\\"")}\"";
    }

    internal static string BuildGuardScript()
    {
        return """
@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "GUID=%~1"
set "EPP=%~2"
set "EPPHEX=%~3"
set "COREMIN=%~4"
set "COREMINHEX=%~5"
set "INCTH=%~6"
set "INCTHHEX=%~7"
set "DECTH=%~8"
set "DECTHHEX=%~9"
if not defined GUID exit /b 1
if not defined EPP set "EPP=20"
if not defined EPPHEX set "EPPHEX=00000014"
if not defined COREMIN set "COREMIN=25"
if not defined COREMINHEX set "COREMINHEX=00000019"
if not defined INCTH set "INCTH=10"
if not defined INCTHHEX set "INCTHHEX=0000000a"
if not defined DECTH set "DECTH=8"
if not defined DECTHHEX set "DECTHHEX=00000008"

set "POWERCFG=%SystemRoot%\System32\powercfg.exe"
set "FINDSTR=%SystemRoot%\System32\findstr.exe"
set "STATUS=%~dp0power-plan-guard-status.log"
set "REPAIRED=0"

"%POWERCFG%" /list | "%FINDSTR%" /I /C:"%GUID%" >nul
if errorlevel 1 (
  >>"%STATUS%" echo [%date% %time%] FAIL guid=%GUID% reason=plan-missing
  exit /b 2
)

call :ensure SUB_PROCESSOR PROCTHROTTLEMIN 5 00000005 cpu-min
if errorlevel 1 exit /b 3
call :ensure SUB_PROCESSOR PROCTHROTTLEMAX 100 00000064 cpu-max
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 be337238-0d82-4146-a960-4f3749d470c7 4 00000004 boost
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 36687f9e-e3a5-4dbf-b1dc-15eb381c6863 %EPP% %EPPHEX% epp
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 8baa4a8a-14c6-4451-8e8b-14bdbd197537 1 00000001 cppc
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 5d76a2ca-e8c0-402f-a133-2158492d58ad 0 00000000 cpu-idle
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 94d3a615-a899-4ac5-ae2b-e4d8f634367f 1 00000001 cooling
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 0cc5b647-c1df-4637-891a-dec35c318583 %COREMIN% %COREMINHEX% core-min
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 ea062031-0e34-4ff1-9b6d-eb1059334028 100 00000064 core-max
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 465e1f50-b610-473a-ab58-00d1077dc418 2 00000002 perf-inc-policy
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 40fbefc7-2e9d-4d25-a185-0cfd8574bac6 0 00000000 perf-dec-policy
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 06cadf0e-64ed-448a-8927-ce7bf90eb35d %INCTH% %INCTHHEX% perf-inc-threshold
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 12a0ab44-fe28-4fa9-b3bd-4b64f44960a6 %DECTH% %DECTHHEX% perf-dec-threshold
if errorlevel 1 exit /b 3
call :ensure 54533251-82be-4824-96c1-47b60b740d00 4d2b0152-7d5c-498b-88e2-34345392a2c5 15 0000000f perf-check
if errorlevel 1 exit /b 3
call :ensure SUB_DISK DISKIDLE 0 00000000 disk
if errorlevel 1 exit /b 3
call :ensure SUB_VIDEO VIDEOIDLE 0 00000000 display
if errorlevel 1 exit /b 3
call :ensure SUB_SLEEP STANDBYIDLE 0 00000000 sleep
if errorlevel 1 exit /b 3
call :ensure SUB_SLEEP HIBERNATEIDLE 0 00000000 hibernate
if errorlevel 1 exit /b 3

set "ACTIVEFILE=%TEMP%\SpankyPowerGuard-%RANDOM%-%RANDOM%.txt"
for /L %%I in (1,1,3) do (
  "%POWERCFG%" /setactive %GUID% >nul
  if not errorlevel 1 (
    "%POWERCFG%" /getactivescheme >"%ACTIVEFILE%" 2>nul
    "%FINDSTR%" /I /C:"%GUID%" "%ACTIVEFILE%" >nul
    if not errorlevel 1 (
      del /q "%ACTIVEFILE%" >nul 2>&1
      >>"%STATUS%" echo [%date% %time%] OK guid=%GUID% repaired=!REPAIRED! attempt=%%I
      exit /b 0
    )
  )
  >nul 2>&1 %SystemRoot%\System32\timeout.exe /t 2 /nobreak
)
del /q "%ACTIVEFILE%" >nul 2>&1

>>"%STATUS%" echo [%date% %time%] FAIL guid=%GUID% reason=active-readback-mismatch
exit /b 4

:ensure
"%POWERCFG%" /qh %GUID% %~1 %~2 | "%FINDSTR%" /I /C:"Current AC Power Setting Index: 0x%~4" >nul
if not errorlevel 1 exit /b 0
"%POWERCFG%" /setacvalueindex %GUID% %~1 %~2 %~3 >nul
if errorlevel 1 (
  >>"%STATUS%" echo [%date% %time%] FAIL guid=%GUID% setting=%~5 desired=%~3
  exit /b 1
)
set /a REPAIRED+=1
>>"%STATUS%" echo [%date% %time%] REPAIR guid=%GUID% setting=%~5 value=%~3
exit /b 0
""";
    }

    private static async Task EnsureGuardScriptAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(GuardScriptPath)
            ?? throw new InvalidOperationException("Power Plan guard script path není platná.");
        Directory.CreateDirectory(directory);

        string desired = BuildGuardScript().Replace("\r\n", "\n");
        string? current = null;
        try
        {
            if (File.Exists(GuardScriptPath))
                current = (await File.ReadAllTextAsync(GuardScriptPath, token).ConfigureAwait(false)).Replace("\r\n", "\n");
        }
        catch (Exception ex) { LogService.Error("power", "Power plan guard script read failed", ex); }
        if (string.Equals(current, desired, StringComparison.Ordinal))
        {
            try { if (File.Exists(LegacyGuardScriptPath)) File.Delete(LegacyGuardScriptPath); }
            catch (Exception ex) { LogService.Error("power", "Legacy PowerShell guard cleanup failed", ex); }
            return;
        }

        string temp = GuardScriptPath + ".new";
        await File.WriteAllTextAsync(temp, desired, new UTF8Encoding(false), token).ConfigureAwait(false);
        File.Move(temp, GuardScriptPath, true);
        try { if (File.Exists(LegacyGuardScriptPath)) File.Delete(LegacyGuardScriptPath); }
        catch (Exception ex) { LogService.Error("power", "Legacy PowerShell guard cleanup failed", ex); }
        LogService.Info("power", $"Power plan guard script refreshed path={GuardScriptPath}");
    }

    public static async Task EnsureAsync(string guid, CancellationToken token = default)
    {
        if (!Guid.TryParse(guid, out var parsed))
            throw new ArgumentException("Power plan GUID není platný.", nameof(guid));

        string normalized = parsed.ToString("D");
        await EnsureGuardScriptAsync(token).ConfigureAwait(false);

        var logonCreate = await ProcessRunner.RunAsync(
            "schtasks.exe",
            BuildCreateArguments(normalized, boot: false),
            TimeSpan.FromSeconds(20),
            token).ConfigureAwait(false);
        if (!logonCreate.Success)
            throw new InvalidOperationException("Nelze vytvořit Power Plan Logon guard: " + (logonCreate.StdErr + logonCreate.StdOut).Trim());

        var bootCreate = await ProcessRunner.RunAsync(
            "schtasks.exe",
            BuildCreateArguments(normalized, boot: true),
            TimeSpan.FromSeconds(20),
            token).ConfigureAwait(false);
        if (!bootCreate.Success)
            throw new InvalidOperationException("Nelze vytvořit Power Plan Boot guard: " + (bootCreate.StdErr + bootCreate.StdOut).Trim());

        string? logonXml = await Task.Run(() => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(() => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);
        if (!TaskXmlMatches(logonXml, normalized) || !TaskXmlMatches(bootXml, normalized))
            throw new InvalidOperationException("Power Plan Boot/Logon guard nebyl po vytvoření ověřen.");

        LogService.Info("power", $"Power plan guard configured guid={normalized} triggers=ONSTART+ONLOGON delays=15s/30s criticalNever=4");
    }

    public static async Task RunNowAsync(CancellationToken token = default)
    {
        var run = await ProcessRunner.RunAsync(
            "schtasks.exe",
            $"/Run /TN \"{TaskName}\"",
            TimeSpan.FromSeconds(15),
            token).ConfigureAwait(false);
        if (!run.Success)
            throw new InvalidOperationException("Power Plan guard nelze spustit: " + (run.StdErr + run.StdOut).Trim());
    }

    public static async Task DisableAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        bool removedLogon = await Task.Run(() => TaskSchedulerComService.DeleteIfExists(TaskName), token).ConfigureAwait(false);
        bool removedBoot = await Task.Run(() => TaskSchedulerComService.DeleteIfExists(BootTaskName), token).ConfigureAwait(false);
        if (removedLogon || removedBoot) LogService.Info("power", "Power plan Boot/Logon guard removed.");
        try { if (File.Exists(GuardScriptPath)) File.Delete(GuardScriptPath); }
        catch (Exception ex) { LogService.Error("power", "Power plan guard script cleanup failed", ex); }
        try { if (File.Exists(LegacyGuardScriptPath)) File.Delete(LegacyGuardScriptPath); }
        catch (Exception ex) { LogService.Error("power", "Legacy PowerShell guard cleanup failed", ex); }
    }

    public static async Task<string?> TryGetConfiguredGuidAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string? xml = await Task.Run(() => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var match = GuidRegex.Match(xml);
        return match.Success ? match.Value : null;
    }

    public static async Task<PowerPlanGuardStatus> ReadStatusAsync(
        string? expectedGuid = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        string? logonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);

        string? configuredGuid = null;
        if (!string.IsNullOrWhiteSpace(logonXml))
        {
            var match = GuidRegex.Match(logonXml);
            if (match.Success) configuredGuid = match.Value;
        }
        if (configuredGuid == null && !string.IsNullOrWhiteSpace(bootXml))
        {
            var match = GuidRegex.Match(bootXml);
            if (match.Success) configuredGuid = match.Value;
        }

        string normalizedExpected = Guid.TryParse(expectedGuid, out var parsed)
            ? parsed.ToString("D")
            : configuredGuid ?? string.Empty;

        bool logonOk = TaskXmlMatches(logonXml, normalizedExpected);
        bool bootOk = TaskXmlMatches(bootXml, normalizedExpected);

        string last = "Guard zatím nemá uložený výsledek.";
        try
        {
            if (File.Exists(GuardStatusPath))
            {
                string[] lines = await File.ReadAllLinesAsync(GuardStatusPath, token).ConfigureAwait(false);
                last = lines.LastOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? last;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("power", "Power plan guard status read failed", ex);
        }

        bool lastOk =
            last.Contains(" OK ", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(normalizedExpected) ||
             last.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase));

        return new PowerPlanGuardStatus(configuredGuid, logonOk, bootOk, last, lastOk);
    }

    private static bool TaskXmlMatches(string? xml, string guid)
    {
        if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(guid)) return false;
        return xml.Contains(guid, StringComparison.OrdinalIgnoreCase) &&
               xml.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) &&
               xml.Contains("power-plan-guard.cmd", StringComparison.OrdinalIgnoreCase);
    }

    internal static void RunArgumentSmoke()
    {
        const string guid = "11111111-2222-3333-4444-555555555555";
        string args = BuildCreateArguments(guid, boot: false);
        string bootArgs = BuildCreateArguments(guid, boot: true);
        string script = BuildGuardScript();

        var missing = new System.Collections.Generic.List<string>();
        if (!args.Contains(TaskName, StringComparison.Ordinal)) missing.Add("task-name");
        if (!args.Contains("/SC ONLOGON", StringComparison.OrdinalIgnoreCase)) missing.Add("onlogon");
        if (!args.Contains("/DELAY 0000:30", StringComparison.OrdinalIgnoreCase)) missing.Add("logon-delay");
        if (!bootArgs.Contains(BootTaskName, StringComparison.Ordinal)) missing.Add("boot-task-name");
        if (!bootArgs.Contains("/SC ONSTART", StringComparison.OrdinalIgnoreCase)) missing.Add("onstart");
        if (!bootArgs.Contains("/DELAY 0000:15", StringComparison.OrdinalIgnoreCase)) missing.Add("boot-delay");
        if (!args.Contains("/RU SYSTEM", StringComparison.OrdinalIgnoreCase)) missing.Add("run-as-system");
        if (!args.Contains("/RL HIGHEST", StringComparison.OrdinalIgnoreCase)) missing.Add("highest");
        if (!args.Contains("power-plan-guard.cmd", StringComparison.OrdinalIgnoreCase)) missing.Add("cmd-file");
        if (!args.Contains(guid, StringComparison.OrdinalIgnoreCase)) missing.Add("guid");
        if (!args.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)) missing.Add("cmd-exe");
        if (!script.Contains("SUB_DISK", StringComparison.Ordinal)) missing.Add("disk");
        if (!script.Contains("VIDEOIDLE", StringComparison.Ordinal)) missing.Add("display");
        if (!script.Contains("STANDBYIDLE", StringComparison.Ordinal)) missing.Add("sleep");
        if (!script.Contains("HIBERNATEIDLE", StringComparison.Ordinal)) missing.Add("hibernate");
        if (!script.Contains("/setactive %GUID%", StringComparison.OrdinalIgnoreCase)) missing.Add("setactive");
        if (!script.Contains("/getactivescheme", StringComparison.OrdinalIgnoreCase)) missing.Add("active-readback");
        if (!script.Contains("for /L %%I in (1,1,3)", StringComparison.OrdinalIgnoreCase)) missing.Add("retry");
        if (!script.Contains("power-plan-guard-status.log", StringComparison.OrdinalIgnoreCase)) missing.Add("status-log");

        if (missing.Count != 0)
            throw new InvalidOperationException(
                "Power Plan guard argument/script smoke failed: " + string.Join(",", missing) +
                " | args=" + args.Replace(Environment.NewLine, " ") +
                " | script=" + script.Replace(Environment.NewLine, " | "));

        LogService.Info("POWER_PLAN_GUARD_ARGUMENT_SMOKE_OK criticalNever=4 backend=cmd");
    }
}
