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

    // Fail closed on file/directory reparse points, unreadable or unbounded
    // scripts. Keep this synchronous and small inside the already background
    // COM callback, just before calling IRegisteredTask.Run on that handle.
    private static void EnsureGuardScriptOnDiskMayBeRun()
    {
        string path = GuardScriptPath;
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Power Plan guard script nemá platnou cestu.");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Power Plan guard script nebo složka je přesměrovaná; spuštění bylo zablokováno.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > GuardScriptMaxBytes)
            throw new InvalidOperationException(
                "Power Plan guard script má neočekávanou velikost; spuštění bylo zablokováno.");
        byte[] actual = new byte[(int)stream.Length];
        stream.ReadExactly(actual);
        EnsureGuardScriptContentMayBeRun(actual);
    }

    public static async Task<long> RunNowAsync(
        string expectedGuid, CancellationToken token = default)
    {
        if (!Guid.TryParse(expectedGuid, out var parsed))
            throw new ArgumentException("Power plan GUID není platný.", nameof(expectedGuid));
        token.ThrowIfCancellationRequested();
        string normalized = parsed.ToString("D");
        long statusCursor = CaptureGuardStatusCursor();
        await Task.Run(() => TaskSchedulerComService.RunAuthorizedTask(
            TaskName,
            xml =>
            {
                EnsureGuardTaskMayBeRun(xml, normalized);
                EnsureGuardScriptOnDiskMayBeRun();
            },
            token), token).ConfigureAwait(false);
        return statusCursor;
    }

    // A registered task name is not proof that the task still belongs to
    // this optimizer. Only a single actual Exec action invoking OUR script
    // through the trusted system cmd.exe may be deleted as Power Plan guard.
    // Do not accept strings found in XML description/metadata/other actions.
    internal static bool IsOwnedGuardTaskXml(string? xml) =>
        TryReadOwnedGuardTaskGuid(xml, out _);

    // A path merely mentioned in a CMD argument is not proof of ownership:
    // /C must directly execute the quoted absolute guard script as the FIRST
    // command. Accept the schtasks outer-quote form too, but never a second
    // shell command, redirection, expansion, or arbitrary argument text.
    // Return the GUID from this verified action only (never Description/XML).
    internal static bool TryReadOwnedGuardTaskGuid(string? xml, out string guid)
    {
        guid = string.Empty;
        if (string.IsNullOrWhiteSpace(xml)) return false;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256 * 1024
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);
            if (doc.Root is not { } root || root.Name.LocalName != "Task")
                return false;
            var actions = doc.Descendants()
                .Where(node => node.Name.LocalName == "Actions")
                .ToArray();
            if (actions.Length != 1 || actions[0].Parent != root ||
                actions[0].Name.Namespace != root.Name.Namespace)
                return false;
            var actionNodes = actions[0].Elements().ToArray();
            if (actionNodes.Length != 1 ||
                actionNodes[0].Name != root.Name.Namespace + "Exec")
                return false;
            var exec = actionNodes[0];
            var commands = exec.Elements()
                .Where(node => node.Name.LocalName == "Command").ToArray();
            var argumentsNodes = exec.Elements()
                .Where(node => node.Name.LocalName == "Arguments").ToArray();
            if (commands.Length != 1 || argumentsNodes.Length != 1 ||
                commands[0].Name.Namespace != root.Name.Namespace ||
                argumentsNodes[0].Name.Namespace != root.Name.Namespace)
                return false;

            string trustedCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string command = commands[0].Value.Trim().Trim('"');
            if (!string.Equals(command, trustedCmd, StringComparison.OrdinalIgnoreCase))
                return false;

            string arguments = argumentsNodes[0].Value;
            var switches = GuardCmdSwitches.Match(arguments);
            if (!switches.Success) return false;
            string execution = arguments[switches.Length..];
            // schtasks may retain an outer CMD quote around the quoted script
            // and its parameters. Both forms must still begin with OUR script.
            bool outerQuote = execution.StartsWith("\"\"", StringComparison.Ordinal);
            if (outerQuote) execution = execution[1..];
            string script = "\"" + GuardScriptPath + "\"";
            if (!execution.StartsWith(script, StringComparison.OrdinalIgnoreCase))
                return false;
            string remainder = execution[script.Length..];
            if (remainder.Length < 2 || remainder[0] is not (' ' or '\t'))
                return false;
            remainder = remainder.Trim();
            if (outerQuote)
            {
                if (!remainder.EndsWith('"')) return false;
                remainder = remainder[..^1].TrimEnd();
            }
            if (remainder.Length == 0 || remainder.IndexOfAny(
                    ['\r', '\n', '&', '|', '<', '>', '^', '%', '!', '"']) >= 0)
                return false;
            var parts = remainder.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 11 ||
                !Guid.TryParseExact(parts[0], "D", out var parsed))
                return false;
            // Additional parameters generated by BuildCreateArguments are
            // nonnegative numeric/hex scalars, never free-form shell text.
            if (parts.Skip(1).Any(part => part.Length == 0 ||
                part.Any(ch => !((ch >= '0' && ch <= '9') ||
                                 (ch >= 'a' && ch <= 'f') ||
                                 (ch >= 'A' && ch <= 'F')))))
                return false;
            guid = parsed.ToString("D");
            return true;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or
                                      InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // The /Create /F flag is destructive too. Only absent tasks or tasks
    // whose actual XML matches our guard may be replaced. COM read errors
    // propagate instead of being treated as absence.
    internal static void EnsureGuardTasksMayBeReplaced(
        string? logonXml, string? bootXml)
    {
        if ((logonXml is not null && !IsOwnedGuardTaskXml(logonXml)) ||
            (bootXml is not null && !IsOwnedGuardTaskXml(bootXml)))
            throw new InvalidOperationException(
                "Power Plan guard má cizí nebo neočekávanou definici úlohy; přepsání bylo zablokováno.");
    }

    // Validate both before touching either one. Missing tasks are fine,
    // but a replaced, unreadable or structurally foreign task blocks ALL
    // removals and keeps the associated guard script for safe inspection.
    internal static void EnsureGuardTasksMayBeDeleted(
        string? logonXml, string? bootXml)
    {
        if ((logonXml is not null && !IsOwnedGuardTaskXml(logonXml)) ||
            (bootXml is not null && !IsOwnedGuardTaskXml(bootXml)))
            throw new InvalidOperationException(
                "Power Plan guard má cizí nebo neočekávanou definici úlohy; odstranění bylo zablokováno.");
    }

    // Fully synthetic: no COM, schtasks, powercfg, file or registry writes.
    internal static bool RunOwnedGuardTask191Smoke()
    {
        string validXml = new XElement("Task",
            new XElement("Actions",
                new XElement("Exec",
                    new XElement("Command",
                        Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                    new XElement("Arguments",
                        "/D /S /C \"" + GuardScriptPath + "\" 11111111-2222-3333-4444-555555555555"))))
            .ToString(SaveOptions.DisableFormatting);
        string foreignXml = new XElement("Task",
            new XElement("Actions",
                new XElement("Exec",
                    new XElement("Command",
                        Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                    new XElement("Arguments",
                        "/D /S /C \"C:\\OtherVendor\\other-task.cmd\""))))
            .ToString(SaveOptions.DisableFormatting);
        if (!IsOwnedGuardTaskXml(validXml) || IsOwnedGuardTaskXml(foreignXml) ||
            IsOwnedGuardTaskXml(null))
            return false;
        EnsureGuardTasksMayBeDeleted(validXml, validXml);
        EnsureGuardTasksMayBeDeleted(null, validXml);
        EnsureGuardTasksMayBeReplaced(validXml, validXml);
        EnsureGuardTasksMayBeReplaced(null, validXml);
        bool foreignBlocked = false, foreignReplaceBlocked = false;
        try { EnsureGuardTasksMayBeDeleted(validXml, foreignXml); }
        catch (InvalidOperationException) { foreignBlocked = true; }
        try { EnsureGuardTasksMayBeReplaced(validXml, foreignXml); }
        catch (InvalidOperationException) { foreignReplaceBlocked = true; }
        if (!foreignBlocked || !foreignReplaceBlocked) return false;
        int deleted = 0, released = 0;
        object handle = new();
        try
        {
            _ = TaskSchedulerComService.DeleteRegisteredTaskIfPresent(
                () => handle, () => deleted++, _ => released++,
                _ => IsOwnedGuardTaskXml(foreignXml));
            return false;
        }
        catch (InvalidOperationException) { }
        string echoedXml = new XElement("Task",
            new XElement("Actions",
                new XElement("Exec",
                    new XElement("Command", Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                    new XElement("Arguments",
                        "/D /S /C echo \"" + GuardScriptPath +
                        "\" 11111111-2222-3333-4444-555555555555"))))
            .ToString(SaveOptions.DisableFormatting);
        // Compiled executable policy smoke: the old substring admission
        // accepted echo spoofing; /Run previously checked only the task name.
        const string expectedGuid = "11111111-2222-3333-4444-555555555555";
        EnsureGuardTaskMayBeRun(validXml, expectedGuid);
        bool runRejectedForeign = false, runRejectedWrongPlan = false;
        try { EnsureGuardTaskMayBeRun(echoedXml, expectedGuid); }
        catch (InvalidOperationException) { runRejectedForeign = true; }
        try { EnsureGuardTaskMayBeRun(validXml, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"); }
        catch (InvalidOperationException) { runRejectedWrongPlan = true; }
        // Same-handle COM dispatch policy, fully synthetic. No live
        // Schedule.Service connection, task execution, or system mutations.
        object sameTask = new(), runningHandle = new();
        int lookups = 0, reads = 0, launches = 0, releasedTask = 0, releasedRun = 0;
        TaskSchedulerComService.RunRegisteredTaskIfAuthorized(
            () => { lookups++; return sameTask; },
            registered => {
                if (!ReferenceEquals(registered, sameTask))
                    throw new InvalidOperationException("Synthetic COM task identity changed.");
                reads++;
                return validXml;
            },
            xml => EnsureGuardTaskMayBeRun(xml, expectedGuid),
            registered => {
                if (!ReferenceEquals(registered, sameTask))
                    throw new InvalidOperationException("Synthetic COM run identity changed.");
                launches++;
                return runningHandle;
            },
            registered => {
                if (ReferenceEquals(registered, sameTask)) releasedTask++;
                if (ReferenceEquals(registered, runningHandle)) releasedRun++;
            });
        bool sameHandleForeignBlocked = false;
        try
        {
            TaskSchedulerComService.RunRegisteredTaskIfAuthorized(
                () => sameTask,
                _ => echoedXml,
                xml => EnsureGuardTaskMayBeRun(xml, expectedGuid),
                _ => { launches++; return runningHandle; },
                _ => releasedTask++);
        }
        catch (InvalidOperationException) { sameHandleForeignBlocked = true; }

        return deleted == 0 && released == 1 &&
               runRejectedForeign && runRejectedWrongPlan &&
               sameHandleForeignBlocked &&
               lookups == 1 && reads == 1 && launches == 1 &&
               releasedTask == 2 && releasedRun == 1 &&
               !IsOwnedGuardTaskXml(echoedXml) &&
               TaskXmlMatches(validXml, expectedGuid) &&
               !TaskXmlMatches(validXml, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") &&
               RunGuardStatus196Smoke() &&
               RunGuardStatus197Smoke() &&
               RunGuardScript198Smoke() &&
               RunGuardScriptStage199Smoke() &&
               RunGuardDefinition200Smoke() &&
               RunGuardScope201Smoke() &&
               RunGuardGuidConsistency202Smoke();
    }

    public static async Task DisableAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // A task with our old name is not necessarily ours any longer.
        // Preflight BOTH definitions before deleting either task; a replaced
        // or unreadable task must leave the guard script untouched.
        string? logonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);
        EnsureGuardTasksMayBeDeleted(logonXml, bootXml);
        // Revalidate XML from the actual COM task handle at delete time to
        // reduce the chance of deleting a concurrently replaced task.
        bool removedLogon = await Task.Run(
            () => TaskSchedulerComService.DeleteIfExists(
                TaskName, IsOwnedGuardTaskXml), token).ConfigureAwait(false);
        bool removedBoot = await Task.Run(
            () => TaskSchedulerComService.DeleteIfExists(
                BootTaskName, IsOwnedGuardTaskXml), token).ConfigureAwait(false);
        if (removedLogon || removedBoot) LogService.Info("power", "Power plan Boot/Logon guard removed.");
        try { if (File.Exists(GuardScriptPath)) File.Delete(GuardScriptPath); }
        catch (Exception ex) { LogService.Error("power", "Power plan guard script cleanup failed", ex); }
        try { if (File.Exists(LegacyGuardScriptPath)) File.Delete(LegacyGuardScriptPath); }
        catch (Exception ex) { LogService.Error("power", "Legacy PowerShell guard cleanup failed", ex); }
    }

    // A partially registered guard can contain only its Boot task. Never
    // report "unconfigured" in that case: duplicate-plan cleanup uses this
    // GUID to decide whether it may delete older power plans. Likewise two
    // conflicting tasks must not silently pick the Logon task's GUID.
    internal static string? ResolveConsistentGuardTaskGuid(
        string? logonXml, string? bootXml)
    {
        string? ReadOwnedGuid(string? xml, string name)
        {
            if (xml is null) return null;
            if (!TryReadOwnedGuardTaskGuid(xml, out string guid))
                throw new InvalidOperationException(
                    "Power Plan guard má cizí nebo nečitelnou definici " +
                    name + " úlohy; identifikace plánu byla zablokována.");
            return guid;
        }

        string? logonGuid = ReadOwnedGuid(logonXml, "Logon");
        string? bootGuid = ReadOwnedGuid(bootXml, "Boot");
        if (logonGuid is not null && bootGuid is not null &&
            !string.Equals(logonGuid, bootGuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Power Plan guard Boot a Logon ukazují na rozdílné GUID; " +
                "automatické čištění napájecích plánů bylo zablokováno.");
        return logonGuid ?? bootGuid;
    }

    // Synthetic smoke: no COM, actual task, power plan or filesystem access.
    internal static bool RunGuardGuidConsistency202Smoke()
    {
        string XmlFor(string guid) => new XElement("Task",
            new XElement("Actions", new XElement("Exec",
                new XElement("Command",
                    Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                new XElement("Arguments",
                    "/D /S /C \"" + GuardScriptPath + "\" " + guid))))
            .ToString(SaveOptions.DisableFormatting);
        const string first = "11111111-2222-3333-4444-555555555555";
        const string second = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        string logon = XmlFor(first);
        string boot = XmlFor(second);
        bool conflictRejected = false, foreignRejected = false;
        try { _ = ResolveConsistentGuardTaskGuid(logon, boot); }
        catch (InvalidOperationException) { conflictRejected = true; }
        try { _ = ResolveConsistentGuardTaskGuid(logon, "<Task>foreign</Task>"); }
        catch (InvalidOperationException) { foreignRejected = true; }
        return ResolveConsistentGuardTaskGuid(null, null) is null &&
               ResolveConsistentGuardTaskGuid(null, logon) == first &&
               ResolveConsistentGuardTaskGuid(logon, null) == first &&
               ResolveConsistentGuardTaskGuid(logon, logon) == first &&
               conflictRejected && foreignRejected;
    }

    public static async Task<string?> TryGetConfiguredGuidAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string? logonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);
        return ResolveConsistentGuardTaskGuid(logonXml, bootXml);
    }

    // The latest unfinished write must NOT be skipped in favor of an
    // earlier OK. Never read more than GuardStatusTailLimitBytes, regardless
    // of how many months the CMD task has appended to this status file.
    internal static (string? Line, bool Complete) SelectGuardStatusTail(
        ReadOnlySpan<byte> tail, bool startsMidLine)
    {
        var selected = SelectGuardStatusTailWithOffset(tail, startsMidLine);
        return (selected.Line, selected.Complete);
    }

    // RecordStart is relative to the fixed tail window. A fresh-run receipt
    // accepts only a fully completed line STARTING after the pre-run EOF.
    internal static (string? Line, bool Complete, int RecordStart)
        SelectGuardStatusTailWithOffset(ReadOnlySpan<byte> tail, bool startsMidLine)
    {
        if (tail.IsEmpty) return (null, false, -1);
        int finalNewline = tail.LastIndexOf((byte)'\n');
        if (finalNewline != tail.Length - 1)
        {
            ReadOnlySpan<byte> unfinished = tail[(finalNewline + 1)..];
            if (!IsWhitespaceStatusBytes(unfinished))
                return (DecodeStatusLine(unfinished), false, finalNewline + 1);
        }

        int cursor = finalNewline;
        while (cursor >= 0)
        {
            int previousNewline = tail[..cursor].LastIndexOf((byte)'\n');
            if (previousNewline < 0 && startsMidLine)
                return (null, false, -1); // First record is truncated.
            int recordStart = previousNewline + 1;
            ReadOnlySpan<byte> record = tail[recordStart..cursor];
            if (!IsWhitespaceStatusBytes(record))
                return (DecodeStatusLine(record), true, recordStart);
            cursor = previousNewline;
        }
        return (null, false, -1);
    }

    // Shared offset admission for async status reading and the synchronous
    // release policy smoke; no blocking on asynchronous operations.
    internal static bool IsFreshGuardStatusRecord(
        long windowStart, int recordStart, long requiredOffset) =>
        recordStart >= 0 && windowStart + recordStart >= requiredOffset;

    private static bool IsWhitespaceStatusBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (byte ch in bytes)
            if (ch is not (0x20 or 0x09 or 0x0D or 0x0A))
                return false;
        return true;
    }

    private static string DecodeStatusLine(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > GuardStatusLineDisplayLimitBytes)
            bytes = bytes[^GuardStatusLineDisplayLimitBytes..];
        return Encoding.UTF8.GetString(bytes).Trim().TrimStart('\uFEFF');
    }

    internal static async Task<(string? Line, bool Complete)> ReadGuardStatusTailAsync(
        Stream source, CancellationToken token = default, long minimumRecordOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new InvalidOperationException("Power guard status stream musí být čitelný a seekovatelný.");
        ArgumentOutOfRangeException.ThrowIfNegative(minimumRecordOffset);
        token.ThrowIfCancellationRequested();
        long length = source.Length;
        int count = (int)Math.Min(length, GuardStatusTailLimitBytes);
        if (count == 0) return (null, false);
        long start = length - count;
        source.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[count];
        await source.ReadExactlyAsync(buffer.AsMemory(), token).ConfigureAwait(false);
        var result = SelectGuardStatusTailWithOffset(buffer, startsMidLine: start != 0);
        if (minimumRecordOffset > 0 && result.Complete &&
            !IsFreshGuardStatusRecord(start, result.RecordStart, minimumRecordOffset))
            return (result.Line, false);
        return (result.Line, result.Complete);
    }

    internal static bool IsSuccessfulGuardStatusLine(
        string? line, bool complete, string? expectedGuid)
    {
        if (!complete || string.IsNullOrWhiteSpace(line) ||
            !Guid.TryParseExact(expectedGuid, "D", out var expected) ||
            expected == Guid.Empty)
            return false;
        var match = GuardSuccessLineRegex.Match(line);
        return match.Success &&
               Guid.TryParseExact(match.Groups["guid"].Value, "D", out var reported) &&
               expected == reported;
    }

    // Safe compiled release policy smoke. Pure byte buffers only; no COM,
    // status file I/O, guard execution or Windows configuration writes.
    internal static bool RunGuardStatus196Smoke()
    {
        const string expected = "11111111-2222-3333-4444-555555555555";
        string ok = "[25.10.2026 04:00:01] OK guid=" + expected +
            " repaired=1 attempt=1";
        string fail = "[25.10.2026 04:00:02] FAIL guid=" + expected +
            " reason=plan-missing";
        var valid = SelectGuardStatusTail(Encoding.UTF8.GetBytes(ok + "\r\n"), false);
        var latestFailure = SelectGuardStatusTail(
            Encoding.UTF8.GetBytes(ok + "\r\n" + fail + "\r\n"), false);
        var incomplete = SelectGuardStatusTail(
            Encoding.UTF8.GetBytes(ok + "\r\n" + fail), false);
        return valid.Complete &&
               IsSuccessfulGuardStatusLine(valid.Line, valid.Complete, expected) &&
               latestFailure.Complete &&
               !IsSuccessfulGuardStatusLine(
                   latestFailure.Line, latestFailure.Complete, expected) &&
               !incomplete.Complete &&
               !IsSuccessfulGuardStatusLine(
                   incomplete.Line, incomplete.Complete, expected) &&
               !IsSuccessfulGuardStatusLine(
                   "note OK guid=" + expected, true, expected) &&
               !IsSuccessfulGuardStatusLine(ok, true,
                   "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    // Synthetic release smoke for staging-path and reparse-point policy.
    // Does not touch ProgramData, files, Windows tasks or privileges.
    internal static bool RunGuardScriptStage199Smoke()
    {
        EnsureSafeGuardScriptAttributes(FileAttributes.Directory, null);
        EnsureSafeGuardScriptAttributes(FileAttributes.Directory, FileAttributes.Normal);
        bool rejected = false;
        try
        {
            EnsureSafeGuardScriptAttributes(
                FileAttributes.Directory | FileAttributes.ReparsePoint,
                FileAttributes.Normal);
        }
        catch (InvalidOperationException) { rejected = true; }
        bool fileRejected = false;
        try
        {
            EnsureSafeGuardScriptAttributes(
                FileAttributes.Directory, FileAttributes.ReparsePoint);
        }
        catch (InvalidOperationException) { fileRejected = true; }
        const string testDirectory = @"C:\GuardSynthetic";
        string stage = BuildGuardScriptStagePath(testDirectory, "a1b2");
        return rejected && fileRejected &&
               string.Equals(
                   Path.GetDirectoryName(stage), testDirectory,
                   StringComparison.OrdinalIgnoreCase) &&
               stage.EndsWith("power-plan-guard-a1b2.tmp", StringComparison.Ordinal) &&
               !stage.EndsWith("power-plan-guard.cmd.new", StringComparison.Ordinal);
    }

    // Pure release smoke for guard CMD integrity: no disk or task access.
    internal static bool RunGuardScript198Smoke()
    {
        byte[] valid = Encoding.UTF8.GetBytes(BuildGuardScript().Replace("\r\n", "\n"));
        byte[] altered = Encoding.UTF8.GetBytes(BuildGuardScript() + "\nrem different");
        return IsExpectedGuardScriptContent(valid) &&
               !IsExpectedGuardScriptContent(altered) &&
               !IsExpectedGuardScriptContent(Array.Empty<byte>());
    }

    // Compiled, in-memory release smoke: a historical OK is not evidence
    // for a newly requested task Run. No Scheduler/file/registry changes.
    internal static bool RunGuardStatus197Smoke()
    {
        const string guid = "11111111-2222-3333-4444-555555555555";
        string oldOk = "[25.10.2026 04:00:01] OK guid=" + guid +
            " repaired=0 attempt=1\r\n";
        long cursor = Encoding.UTF8.GetByteCount(oldOk);
        var stale = SelectGuardStatusTailWithOffset(
            Encoding.UTF8.GetBytes(oldOk), startsMidLine: false);
        var fresh = SelectGuardStatusTailWithOffset(
            Encoding.UTF8.GetBytes(oldOk + oldOk), startsMidLine: false);
        return stale.Complete &&
               !IsFreshGuardStatusRecord(0, stale.RecordStart, cursor) &&
               fresh.Complete &&
               IsFreshGuardStatusRecord(0, fresh.RecordStart, cursor) &&
               IsSuccessfulGuardStatusLine(fresh.Line, fresh.Complete, guid);
    }

    public static async Task<PowerPlanGuardStatus> ReadStatusAsync(
        string? expectedGuid = null,
        CancellationToken token = default,
        long minimumStatusRecordOffset = 0)
    {
        token.ThrowIfCancellationRequested();

        string? logonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);

        string? configuredGuid = TryReadOwnedGuardTaskGuid(logonXml, out string logonGuid)
            ? logonGuid
            : TryReadOwnedGuardTaskGuid(bootXml, out string bootGuid) ? bootGuid : null;

        string normalizedExpected = Guid.TryParse(expectedGuid, out var parsed)
            ? parsed.ToString("D")
            : configuredGuid ?? string.Empty;

        bool logonOk = GuardTaskDefinitionMatches(logonXml, normalizedExpected, boot: false);
        bool bootOk = GuardTaskDefinitionMatches(bootXml, normalizedExpected, boot: true);

        string last = "Guard zatím nemá uložený výsledek.";
        bool completeStatusRecord = false;
        try
        {
            if (File.Exists(GuardStatusPath))
            {
                await using var stream = new FileStream(
                    GuardStatusPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var status = await ReadGuardStatusTailAsync(
                    stream, token, minimumStatusRecordOffset).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(status.Line))
                    last = status.Line;
                completeStatusRecord = status.Complete;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("power", "Power plan guard status read failed", ex);
        }

        bool lastOk = IsSuccessfulGuardStatusLine(
            last, completeStatusRecord, normalizedExpected);
        return new PowerPlanGuardStatus(configuredGuid, logonOk, bootOk, last, lastOk);
    }

    internal static bool TaskXmlMatches(string? xml, string guid) =>
        Guid.TryParse(guid, out var expected) &&
        TryReadOwnedGuardTaskGuid(xml, out string actual) &&
        string.Equals(actual, expected.ToString("D"), StringComparison.OrdinalIgnoreCase);

    // Ownership of an Exec action does not establish that our automatic
    // guard will actually run as SYSTEM at ONSTART / ONLOGON. Inspect the
    // *real* root-level triggers/principal/settings rather than descriptions
    // or other metadata. Keep this distinct from ownership admission:
    // a damaged owned task must remain safe to replace/delete.
    internal static bool GuardTaskDefinitionMatches(
        string? xml, string expectedGuid, bool boot)
    {
        if (!TaskXmlMatches(xml, expectedGuid)) return false;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256 * 1024
            };
            using var reader = XmlReader.Create(new StringReader(xml!), settings);
            var doc = XDocument.Load(reader);
            if (doc.Root is not { } root || root.Name.LocalName != "Task")
                return false;
            XNamespace ns = root.Name.Namespace;

            // Exactly one root-level Triggers containing only our trigger.
            var triggersNodes = doc.Descendants(ns + "Triggers").ToArray();
            if (triggersNodes.Length != 1 || triggersNodes[0].Parent != root)
                return false;
            XElement[] triggers = triggersNodes[0].Elements().ToArray();
            if (triggers.Length != 1 ||
                triggers[0].Name != ns + (boot ? "BootTrigger" : "LogonTrigger"))
                return false;
            XElement trigger = triggers[0];
            if (!GuardXmlEnabled(trigger, ns)) return false;
            // /SC ONLOGON without a user filter must cover EVERY logon.
            // An EndBoundary would silently stop our automatic protection.
            // Repetition would schedule unintended extra SYSTEM runs.
            if (trigger.Elements(ns + "UserId").Any() ||
                trigger.Elements(ns + "EndBoundary").Any() ||
                trigger.Elements(ns + "Repetition").Any())
                return false;
            var delay = trigger.Elements(ns + "Delay").ToArray();
            if (delay.Length != 1 ||
                XmlConvert.ToTimeSpan(delay[0].Value) !=
                TimeSpan.FromSeconds(boot ? 15 : 30))
                return false;

            // Scheduler's built-in LocalSystem SID, with its common
            // account-name serializations. Reject GroupId or another user.
            var principalNodes = doc.Descendants(ns + "Principals").ToArray();
            if (principalNodes.Length != 1 ||
                principalNodes[0].Parent != root)
                return false;
            XElement[] principals = principalNodes[0].Elements().ToArray();
            if (principals.Length != 1 || principals[0].Name != ns + "Principal")
                return false;
            var users = principals[0].Elements(ns + "UserId").ToArray();
            var levels = principals[0].Elements(ns + "RunLevel").ToArray();
            var logonTypes = principals[0].Elements(ns + "LogonType").ToArray();
            if (users.Length != 1 || levels.Length != 1 ||
                logonTypes.Length > 1 ||
                (logonTypes.Length == 1 &&
                 !string.Equals(logonTypes[0].Value.Trim(), "ServiceAccount",
                     StringComparison.OrdinalIgnoreCase)) ||
                principals[0].Elements(ns + "GroupId").Any())
                return false;
            string user = users[0].Value.Trim();
            if (!string.Equals(user, "S-1-5-18", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(user, "SYSTEM", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(user, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(levels[0].Value.Trim(), "HighestAvailable",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            // An explicitly disabled task will not run at its trigger.
            var settingsNodes = doc.Descendants(ns + "Settings").ToArray();
            return settingsNodes.Length <= 1 &&
                   (settingsNodes.Length == 0 ||
                    (settingsNodes[0].Parent == root &&
                     GuardXmlEnabled(settingsNodes[0], ns)));
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or
                                      FormatException or OverflowException or
                                      InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool GuardXmlEnabled(XElement element, XNamespace ns)
    {
        XElement[] enabled = element.Elements(ns + "Enabled").ToArray();
        return enabled.Length == 0 ||
               (enabled.Length == 1 &&
                bool.TryParse(enabled[0].Value.Trim(), out bool value) && value);
    }

    // Compiled service policy smoke: inspect only constructed XML trees.
    // No Scheduler COM, processes, files or Windows mutations.
    internal static bool RunGuardDefinition200Smoke()
    {
        const string guid = "11111111-2222-3333-4444-555555555555";
        string command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        XElement task = new("Task",
            new XElement("Triggers",
                new XElement("LogonTrigger", new XElement("Delay", "PT30S"))),
            new XElement("Principals",
                new XElement("Principal",
                    new XElement("UserId", "S-1-5-18"),
                    new XElement("RunLevel", "HighestAvailable"))),
            new XElement("Settings", new XElement("Enabled", "true")),
            new XElement("Actions",
                new XElement("Exec",
                    new XElement("Command", command),
                    new XElement("Arguments", "/D /S /C \"" +
                        GuardScriptPath + "\" " + guid))));
        string valid = task.ToString(SaveOptions.DisableFormatting);
        if (!GuardTaskDefinitionMatches(valid, guid, boot: false)) return false;
        task.Element("Triggers")!.Element("LogonTrigger")!
            .Element("Delay")!.Value = "PT15S";
        if (GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false))
            return false;
        task.Element("Triggers")!.Element("LogonTrigger")!
            .Element("Delay")!.Value = "PT30S";
        task.Element("Principals")!.Element("Principal")!
            .Element("UserId")!.Value = "S-1-5-19";
        return !GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false);
    }

    // Release smoke for all-user trigger scope and SYSTEM logon type.
    // Only synthetic task XML is inspected; no Windows tasks are touched.
    internal static bool RunGuardScope201Smoke()
    {
        const string guid = "11111111-2222-3333-4444-555555555555";
        XElement task = new("Task",
            new XElement("Triggers",
                new XElement("LogonTrigger",
                    new XElement("Delay", "PT30S"))),
            new XElement("Principals",
                new XElement("Principal",
                    new XElement("UserId", "S-1-5-18"),
                    new XElement("LogonType", "ServiceAccount"),
                    new XElement("RunLevel", "HighestAvailable"))),
            new XElement("Actions", new XElement("Exec",
                new XElement("Command",
                    Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                new XElement("Arguments",
                    "/D /S /C \"" + GuardScriptPath + "\" " + guid))));
        if (!GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false))
            return false;
        var trigger = task.Element("Triggers")!.Element("LogonTrigger")!;
        trigger.Add(new XElement("UserId", "S-1-5-21-1-2-3-1001"));
        if (GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false))
            return false;
        trigger.Element("UserId")!.Remove();
        task.Element("Principals")!.Element("Principal")!
            .Element("LogonType")!.Value = "Password";
        return !GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false);
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
        // CMD exposes positional parameters 10 and 11 only after shifting.
        // Without these captures, the boot/logon task silently hardcodes the
        // performance check interval (15 ms) even for mobile profiles (20 ms).
        if (!script.Contains("set \"PERFCHECK=%~9\"", StringComparison.Ordinal) ||
            !script.Contains("set \"PERFCHECKHEX=%~9\"", StringComparison.Ordinal) ||
            script.Split("shift /8", StringSplitOptions.None).Length != 3)
            missing.Add("perf-check-arguments-10-11");
        if (!script.Contains("%PERFCHECK% %PERFCHECKHEX% perf-check", StringComparison.Ordinal) ||
            script.Contains("15 0000000f perf-check", StringComparison.Ordinal))
            missing.Add("perf-check-not-hardcoded");
        if (!args.Contains(
            $"{PerformanceProfilesService.GetAdaptivePowerProfile().PerfCheckMs} {PerformanceProfilesService.GetAdaptivePowerProfile().PerfCheckMs:x8}",
            StringComparison.OrdinalIgnoreCase))
            missing.Add("scheduler-perf-check-forwarding");

        if (missing.Count != 0)
            throw new InvalidOperationException(
                "Power Plan guard argument/script smoke failed: " + string.Join(",", missing) +
                " | args=" + args.Replace(Environment.NewLine, " ") +
                " | script=" + script.Replace(Environment.NewLine, " | "));

        LogService.Info("POWER_PLAN_GUARD_ARGUMENT_SMOKE_OK criticalNever=4 backend=cmd");
    }
}
