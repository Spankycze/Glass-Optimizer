using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record PowerPlanGuardStatus(
    string? ConfiguredGuid,
    bool LogonConfigured,
    bool BootConfigured,
    string LastResult,
    bool LastRunOk);

public static partial class PowerPlanGuardService
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

    // Read Task Scheduler XML from the actual Exec action, never metadata.
    // These are switches emitted by BuildCreateArguments, not substring hints.
    private static readonly Regex GuardCmdSwitches = new(
        @"\A[ \t]*/D[ \t]+/S[ \t]+/C[ \t]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The CMD guard appends indefinitely. A UI status refresh must NOT read
    // all prior history or trust a partial final record as a successful run.
    internal const int GuardStatusTailLimitBytes = 16 * 1024;
    private const int GuardStatusLineDisplayLimitBytes = 1024;
    private static readonly Regex GuardSuccessLineRegex = new(
        @"\A\[[^\]\r\n]{1,100}\] OK guid=(?<guid>[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}) repaired=[0-9]{1,10} attempt=[1-3]\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
rem CMD expands %%~10 as argument 1 plus literal 0. Shift only
rem positions 8-9 so we can read positional arguments 10 and 11.
shift /8
set "PERFCHECK=%~9"
shift /8
set "PERFCHECKHEX=%~9"
if not defined GUID exit /b 1
if not defined EPP set "EPP=20"
if not defined EPPHEX set "EPPHEX=00000014"
if not defined COREMIN set "COREMIN=25"
if not defined COREMINHEX set "COREMINHEX=00000019"
if not defined INCTH set "INCTH=10"
if not defined INCTHHEX set "INCTHHEX=0000000a"
if not defined DECTH set "DECTH=8"
if not defined DECTHHEX set "DECTHHEX=00000008"
if not defined PERFCHECK set "PERFCHECK=15"
if not defined PERFCHECKHEX set "PERFCHECKHEX=0000000f"

set "POWERCFG=%SystemRoot%\System32\powercfg.exe"
set "FINDSTR=%SystemRoot%\System32\findstr.exe"
set "REG=%SystemRoot%\System32\reg.exe"
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
call :ensure 54533251-82be-4824-96c1-47b60b740d00 4d2b0152-7d5c-498b-88e2-34345392a2c5 %PERFCHECK% %PERFCHECKHEX% perf-check
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
rem Registry value names are locale-independent. Resolve the few aliases
rem used by this guard to their GUIDs so ACSettingIndex can be read directly.
set "SUB=%~1"
set "SETTING=%~2"
if /I "%~1"=="SUB_PROCESSOR" set "SUB=54533251-82be-4824-96c1-47b60b740d00"
if /I "%~2"=="PROCTHROTTLEMIN" set "SETTING=893dee8e-2bef-41e0-89c6-b55d0929964c"
if /I "%~2"=="PROCTHROTTLEMAX" set "SETTING=bc5038f7-23e0-4960-96da-33abaf5935ec"
if /I "%~1"=="SUB_DISK" set "SUB=0012ee47-9041-4b5d-9b77-535fba8b1442"
if /I "%~2"=="DISKIDLE" set "SETTING=6738e2c4-e8a5-4a42-b16a-e040e769756e"
if /I "%~1"=="SUB_VIDEO" set "SUB=7516b95f-f776-4464-8c53-06167f40cc99"
if /I "%~2"=="VIDEOIDLE" set "SETTING=3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"
if /I "%~1"=="SUB_SLEEP" set "SUB=238c9fa8-0aad-41ed-83f4-97be242c8f20"
if /I "%~2"=="STANDBYIDLE" set "SETTING=29f6c1db-86da-48c5-9fdb-f2b67b1f44da"
if /I "%~2"=="HIBERNATEIDLE" set "SETTING=9d7815a6-7ee4-497e-8888-515a05f02364"
set "CURRENT="
for /f "tokens=2,*" %%A in ('%REG% query "HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\%GUID%\%SUB%\%SETTING%" /v ACSettingIndex 2^>nul') do (
  if /I "%%A"=="REG_DWORD" set "CURRENT=%%B"
)
if defined CURRENT (
  set "HEXVAL=!CURRENT:0x=!"
  set "DECVAL="
  set /a DECVAL=0x!HEXVAL! 2>nul
  if defined DECVAL if !DECVAL! EQU %~3 exit /b 0
)
rem Fallback for a setting that has no explicit registry override yet.
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

    // Automatic ONLOGON/ONSTART tasks run as SYSTEM. A reparse-point
    // folder or CMD file must never be treated as our owned guard path.
    // Attribute checks narrow risk but cannot make concurrent filesystem
    // modification by other principals transactional.
    internal static void EnsureSafeGuardScriptAttributes(
        FileAttributes directoryAttributes, FileAttributes? scriptAttributes)
    {
        if ((directoryAttributes & FileAttributes.Directory) == 0 ||
            (directoryAttributes & FileAttributes.ReparsePoint) != 0 ||
            (scriptAttributes is FileAttributes file &&
             (file & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
            throw new InvalidOperationException(
                "Power Plan guard script používá přesměrovanou nebo neplatnou cestu; registrace byla zablokována.");
    }

    private static FileAttributes? TryGetExistingGuardScriptAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
    }

    private static void EnsureSafeGuardScriptDestination(string directory)
    {
        var attributes = TryGetExistingGuardScriptAttributes(GuardScriptPath);
        // File.Exists alone is false for dangling symbolic links. Check the
        // link target too, including when the destination does not exist.
        if (new FileInfo(GuardScriptPath).LinkTarget is not null)
            throw new InvalidOperationException(
                "Power Plan guard script je symbolický odkaz; registrace byla zablokována.");
        EnsureSafeGuardScriptAttributes(File.GetAttributes(directory), attributes);
    }

    internal static string BuildGuardScriptStagePath(string directory, string randomName)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(randomName) ||
            randomName != Path.GetFileName(randomName) ||
            randomName is "." or ".." ||
            randomName.Contains(':') || randomName.Contains('\\') ||
            randomName.Contains('/'))
            throw new ArgumentException("Neplatný pomocný název guard skriptu.", nameof(randomName));
        return Path.Combine(directory, "power-plan-guard-" + randomName + ".tmp");
    }

    private static async Task EnsureGuardScriptAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(GuardScriptPath)
            ?? throw new InvalidOperationException("Power Plan guard script path není platná.");
        if (Directory.Exists(directory))
            EnsureSafeGuardScriptDestination(directory);
        Directory.CreateDirectory(directory);
        EnsureSafeGuardScriptDestination(directory);

        string desired = BuildGuardScript().Replace("\r\n", "\n");
        byte[]? current = null;
        try
        {
            if (File.Exists(GuardScriptPath))
            {
                // Check the same bytes as RunNow: a BOM or mismatched
                // encoding must not be mistaken for our generated script.
                var info = new FileInfo(GuardScriptPath);
                if (info.Length > 0 && info.Length <= GuardScriptMaxBytes)
                    current = await File.ReadAllBytesAsync(GuardScriptPath, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogService.Error("power", "Power plan guard script read failed", ex); }
        if (current is not null && IsExpectedGuardScriptContent(current))
        {
            try { if (File.Exists(LegacyGuardScriptPath)) File.Delete(LegacyGuardScriptPath); }
            catch (Exception ex) { LogService.Error("power", "Legacy PowerShell guard cleanup failed", ex); }
            return;
        }

        // A fixed .new filename can be pre-created as a link by another
        // principal. Use an unpredictable sibling path and CREATE_NEW so
        // a pre-existing file/link is never opened for overwrite.
        string temp = BuildGuardScriptStagePath(directory, Path.GetRandomFileName());
        try
        {
            byte[] contents = Encoding.UTF8.GetBytes(desired);
            await using (var staged = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await staged.WriteAsync(contents, token).ConfigureAwait(false);
                await staged.FlushAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            EnsureSafeGuardScriptDestination(directory);
            File.Move(temp, GuardScriptPath, true);
            EnsureGuardScriptOnDiskMayBeRun();
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { LogService.Error("power", "Power plan guard staging cleanup failed", ex); }
        }
        try { if (File.Exists(LegacyGuardScriptPath)) File.Delete(LegacyGuardScriptPath); }
        catch (Exception ex) { LogService.Error("power", "Legacy PowerShell guard cleanup failed", ex); }
        LogService.Info("power", $"Power plan guard script refreshed path={GuardScriptPath}");
    }

    public static async Task EnsureAsync(string guid, CancellationToken token = default)
    {
        if (!Guid.TryParse(guid, out var parsed))
            throw new ArgumentException("Power plan GUID není platný.", nameof(guid));

        string normalized = parsed.ToString("D");
        // /Create /F can overwrite a same-named task owned by somebody else.
        // Check BOTH existing definitions before touching the shared script
        // or submitting either potentially destructive schtasks operation.
        string? existingLogonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? existingBootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);
        EnsureGuardTasksMayBeReplaced(existingLogonXml, existingBootXml);

        await EnsureGuardScriptAsync(token).ConfigureAwait(false);

        // Re-check each name just before /F, narrowing the replacement race.
        // schtasks /Create /F itself is not transactional with this COM read.
        string? latestLogonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        EnsureGuardTasksMayBeReplaced(latestLogonXml, null);
        token.ThrowIfCancellationRequested();
        EnsureGuardScriptOnDiskMayBeRun();
        var logonCreate = await ProcessRunner.RunAsync(
            "schtasks.exe",
            BuildCreateArguments(normalized, boot: false),
            TimeSpan.FromSeconds(20),
            token).ConfigureAwait(false);
        if (!logonCreate.Success)
            throw new InvalidOperationException("Nelze vytvořit Power Plan Logon guard: " + (logonCreate.StdErr + logonCreate.StdOut).Trim());

        string? latestBootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);
        EnsureGuardTasksMayBeReplaced(null, latestBootXml);
        token.ThrowIfCancellationRequested();
        EnsureGuardScriptOnDiskMayBeRun();
        var bootCreate = await ProcessRunner.RunAsync(
            "schtasks.exe",
            BuildCreateArguments(normalized, boot: true),
            TimeSpan.FromSeconds(20),
            token).ConfigureAwait(false);
        if (!bootCreate.Success)
            throw new InvalidOperationException("Nelze vytvořit Power Plan Boot guard: " + (bootCreate.StdErr + bootCreate.StdOut).Trim());

        string? logonXml = await Task.Run(() => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(() => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);
        if (!GuardTaskDefinitionMatches(logonXml, normalized, boot: false) ||
            !GuardTaskDefinitionMatches(bootXml, normalized, boot: true))
            throw new InvalidOperationException(
                "Power Plan guard nebyl po registraci ověřen: akce, SYSTEM účet, zvýšená oprávnění nebo Boot/Logon trigger neodpovídají.");

        LogService.Info("power", $"Power plan guard configured guid={normalized} triggers=ONSTART+ONLOGON delays=15s/30s criticalNever=4");
    }

    // Running a scheduled task is privileged too. Authorize and run the
    // SAME IRegisteredTask COM handle, not a name subsequently re-resolved
    // by schtasks /Run. The Scheduler service still owns concurrent state.
    internal static void EnsureGuardTaskMayBeRun(string? logonXml, string expectedGuid)
    {
        if (!TaskXmlMatches(logonXml, expectedGuid))
            throw new InvalidOperationException(
                "Power Plan guard má cizí, chybějící nebo neočekávanou definici úlohy; spuštění bylo zablokováno.");
    }

    // An old OK line is not proof that THIS Run has completed. Snapshot the
    // append-only log boundary before launching the validated COM task.
    // An unavailable log starts at byte zero; other I/O errors fail closed.
    internal static long CaptureGuardStatusCursor()
    {
        try { return new FileInfo(GuardStatusPath).Length; }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }

    // The registered Exec path alone is not sufficient authorization:
    // an attacker (or broken upgrade) could replace the CMD script while
    // retaining an otherwise legitimate scheduled-task XML definition.
    // Compare raw UTF-8 bytes against the exact script shipped by this
    // binary; accept LF (our writer) and historical CRLF only, never a BOM,
    // prefix, appended command, changed parameter, or other encoding.
    internal const int GuardScriptMaxBytes = 64 * 1024;

    internal static bool IsExpectedGuardScriptContent(ReadOnlySpan<byte> actual)
    {
        string script = BuildGuardScript().Replace("\r\n", "\n");
        byte[] lf = Encoding.UTF8.GetBytes(script);
        if (actual.SequenceEqual(lf)) return true;
        byte[] crlf = Encoding.UTF8.GetBytes(script.Replace("\n", "\r\n"));
        return actual.SequenceEqual(crlf);
    }

    internal static void EnsureGuardScriptContentMayBeRun(byte[]? actual)
    {
        if (actual is null || !IsExpectedGuardScriptContent(actual))
            throw new InvalidOperationException(
                "Power Plan guard script chybí nebo se jeho obsah změnil; spuštění bylo zablokováno.");
    }
}
