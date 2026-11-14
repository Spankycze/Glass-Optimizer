using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record PowerShellCenterStatus(
    string PowerShellVersion,
    string PSReadLineVersion,
    string PSResourceGetVersion,
    string PSScriptAnalyzerVersion,
    string WinGetClientVersion,
    string TerminalIconsVersion,
    string OhMyPoshVersion);

public sealed record PowerShellModuleStatus(string Name, string Version, bool Installed);
public sealed record ScriptHealthFinding(string File, int Line, string Severity, string Rule, string Message);

public sealed record PsReadLineProfileOptions(
    string EditMode,
    string PredictionSource,
    string PredictionViewStyle,
    int MaximumHistoryCount,
    bool HistoryNoDuplicates,
    bool HistorySearchCursorMovesToEnd,
    bool BellOff,
    bool HistoryArrowSearch,
    bool MenuCompleteTab);

public static partial class PowerShellCenterService
{
    private static readonly SemaphoreSlim WinGetClientStatusGate = new(1, 1);
    private static readonly SemaphoreSlim StatusRefreshGate = new(1, 1);
    private static readonly object StatusCacheGate = new();
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(45);
    private static PowerShellCenterStatus? _statusCache;
    private static DateTime _statusCacheAtUtc;
    private static int _statusProcessLoads;
    private static int _statusNativeLoads;
    internal static int StatusNativeLoadsForDiagnostics => Volatile.Read(ref _statusNativeLoads);
    private static int _historyCleanupRunning;
    private static readonly SemaphoreSlim ModulesRefreshGate = new(1, 1);
    private static IReadOnlyList<PowerShellModuleStatus>? _modulesCache;
    private static DateTime _modulesCacheAtUtc;
    private static int _cacheGeneration;
    private static int _modulesProcessLoads;
    internal static int ModulesProcessLoadsForDiagnostics => Volatile.Read(ref _modulesProcessLoads);
    internal static int StatusProcessLoadsForDiagnostics => Volatile.Read(ref _statusProcessLoads);

    internal static void InvalidateStatusCache()
    {
        lock (StatusCacheGate)
        {
            _statusCache = null;
            _statusCacheAtUtc = default;
            _modulesCache = null;
            _modulesCacheAtUtc = default;
            _cacheGeneration++;
        }
    }

    private static IReadOnlyList<PowerShellModuleStatus>? GetCachedModules()
    {
        lock (StatusCacheGate)
            return _modulesCache != null && DateTime.UtcNow - _modulesCacheAtUtc < StatusCacheTtl
                ? _modulesCache : null;
    }

    private static PowerShellCenterStatus? GetCachedStatus()
    {
        lock (StatusCacheGate)
            return _statusCache != null && DateTime.UtcNow - _statusCacheAtUtc < StatusCacheTtl
                ? _statusCache : null;
    }
    private static bool? _winGetClientAvailable;
    private static DateTime _winGetClientAvailableAtUtc;
    private static readonly TimeSpan WinGetClientAvailabilityTtl = TimeSpan.FromMinutes(10);

    private const string MarkerStart = "# >>> Spankyho Optimizer PowerShell Center >>>";
    private const string MarkerEnd = "# <<< Spankyho Optimizer PowerShell Center <<<";

    private static string PwshPath =>
        File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
            ? @"C:\Program Files\PowerShell\7\pwsh.exe"
            : "pwsh.exe";

    public static string PowerShell7ProfilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PowerShell", "Microsoft.PowerShell_profile.ps1");

    private static string EncodeCommand(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static async Task<ProcessResult> RunPwshAsync(string script, TimeSpan timeout, CancellationToken token)
    {
        await FaultInjectionService.InjectAsync("powershell.run", token).ConfigureAwait(false);
        return await ProcessRunner.RunAsync(
            PwshPath,
            $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {EncodeCommand(script)}",
            timeout,
            token).ConfigureAwait(false);
    }

    private static async Task<string> RunPwshTextAsync(string script, TimeSpan timeout, CancellationToken token)
    {
        var result = await RunPwshAsync(script, timeout, token).ConfigureAwait(false);
        if (!result.Success)
        {
            string clean = CleanPowerShellMessage(result.StdErr);
            if (string.IsNullOrWhiteSpace(clean))
                clean = CleanPowerShellMessage(result.StdOut);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(clean)
                    ? $"PowerShell skončil kódem {result.ExitCode}."
                    : clean);
        }
        return CleanPowerShellMessage(result.StdOut);
    }

    private static string CleanPowerShellMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string value = text.Trim();
        if (value.Contains("#< CLIXML", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("<Objs", StringComparison.OrdinalIgnoreCase))
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                value,
                "<S S=\"(?:Error|Output)\">(.*?)</S>",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matches.Count > 0)
            {
                value = string.Join(Environment.NewLine,
                    matches.Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)));
            }
        }

        value = System.Text.RegularExpressions.Regex.Replace(value, "_x[0-9A-Fa-f]{4}_", string.Empty);
        value = value.Replace("#< CLIXML", string.Empty, StringComparison.OrdinalIgnoreCase);
        value = System.Text.RegularExpressions.Regex.Replace(value, "\u001B\\[[0-9;]*[A-Za-z]", string.Empty);
        return value.Trim();
    }

    public static async Task<PowerShellCenterStatus> GetStatusAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var cached = GetCachedStatus();
        if (cached != null) return cached;
        await StatusRefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = GetCachedStatus();
            if (cached != null) return cached;
            int generation;
            lock (StatusCacheGate) generation = _cacheGeneration;
            var status = await LoadStatusFreshAsync(token).ConfigureAwait(false);
            lock (StatusCacheGate)
            {
                if (generation == _cacheGeneration)
                {
                    _statusCache = status;
                    _statusCacheAtUtc = DateTime.UtcNow;
                }
            }
            return status;
        }
        finally { StatusRefreshGate.Release(); }
    }

    private static async Task<PowerShellCenterStatus?> TryGetNativeStatusAsync(CancellationToken token)
    {
        // Only use the native fast path for a normal PS7 installation. Custom
        // startup module paths and arbitrary script commands still use pwsh.
        string exe = @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (!File.Exists(exe)) return null;
        string? rawPaths = Environment.GetEnvironmentVariable("PSModulePath");
        if (string.IsNullOrWhiteSpace(rawPaths)) return null;
        string[] paths = rawPaths.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0) return null;
        string[] names = ["PSReadLine", "Microsoft.PowerShell.PSResourceGet", "PSScriptAnalyzer",
            "Microsoft.WinGet.Client", "Terminal-Icons"];
        string[] versions = new string[names.Length];
        // Normalize and deduplicate module roots before enumerating them.
        paths = paths.Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        for (int i = 0; i < names.Length; i++)
        {
            Version? best = null;
            foreach (string root in paths)
            {
                token.ThrowIfCancellationRequested();
                string directory = Path.Combine(root, names[i]);
                if (!Directory.Exists(directory)) continue;
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if (Version.TryParse(Path.GetFileName(child), out var version) &&
                        File.Exists(Path.Combine(child, names[i] + ".psd1")) &&
                        (best == null || version > best))
                        best = version;
                }
                // An unversioned module may contain an arbitrary version, so use
                // the existing PowerShell discovery fallback rather than guess.
                string manifest = Path.Combine(directory, names[i] + ".psd1");
                if (File.Exists(manifest))
                {
                    string data = await File.ReadAllTextAsync(manifest, token).ConfigureAwait(false);
                    var match = System.Text.RegularExpressions.Regex.Match(data,
                        @"(?im)^\s*ModuleVersion\s*=\s*'(?<version>\d+(?:\.\d+){1,3})'");
                    if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var declaredVersion))
                        return null;
                    if (best == null || declaredVersion > best) best = declaredVersion;
                }
            }
            versions[i] = best?.ToString() ?? "Missing";
        }
        // Oh My Posh version may differ from file product version or PATH shim.
        // Keep the external discovery path when that executable is present.
        string[] pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? omp = pathDirectories.Concat(paths).Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(x => Path.Combine(x, "oh-my-posh.exe"))
            .FirstOrDefault(File.Exists);
        string ompVersion = "Missing";
        if (omp != null)
        {
            var result = await ProcessRunner.RunAsync(omp, "version", TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
            if (!result.Success) return null;
            ompVersion = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Missing";
        }
        string versionString = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "";
        if (string.IsNullOrWhiteSpace(versionString)) return null;
        return new PowerShellCenterStatus(versionString, versions[0], versions[1], versions[2],
            versions[3], versions[4], ompVersion);
    }

    private static async Task<PowerShellCenterStatus> LoadStatusFreshAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        // Native module discovery performs synchronous directory enumeration and
        // manifest reads before its first await. Never run that cold I/O on the UI thread.
        var native = await Task.Run(() => TryGetNativeStatusAsync(token), token).ConfigureAwait(false);
        if (native != null)
        {
            Interlocked.Increment(ref _statusNativeLoads);
            LogService.Info("performance", "POWERSHELL_CENTER_STATUS source=native pwshStarts=0 ms=" + elapsed.ElapsedMilliseconds);
            return native;
        }
        Interlocked.Increment(ref _statusProcessLoads);
        const string script = """
$ErrorActionPreference='SilentlyContinue'
function Ver([string]$Name) {
  $m = Get-Module -ListAvailable -Name $Name | Sort-Object Version -Descending | Select-Object -First 1
  if($m){ return $m.Version.ToString() }
  return ''
}
$omp=''
$c=Get-Command oh-my-posh -ErrorAction SilentlyContinue
if($c){
  try { $omp = (& $c.Source version 2>$null | Select-Object -First 1).ToString().Trim() } catch {}
}
@(
  $PSVersionTable.PSVersion.ToString(),
  (Ver 'PSReadLine'),
  (Ver 'Microsoft.PowerShell.PSResourceGet'),
  (Ver 'PSScriptAnalyzer'),
  (Ver 'Microsoft.WinGet.Client'),
  (Ver 'Terminal-Icons'),
  $omp
) -join '|'
""";
        // Starting pwsh can do synchronous process/discovery work before the async
        // pipeline yields. Keep that cold path away from the UI thread.
        string text = await Task.Run(
            () => RunPwshTextAsync(script, TimeSpan.FromSeconds(20), token),
            token).ConfigureAwait(false);
        string[] p = text.Split('|');
        string At(int i) => i < p.Length && !string.IsNullOrWhiteSpace(p[i]) ? p[i].Trim() : "Missing";
        LogService.Info("performance", "POWERSHELL_CENTER_STATUS source=pwsh pwshStarts=1 ms=" + elapsed.ElapsedMilliseconds);
        return new PowerShellCenterStatus(At(0), At(1), At(2), At(3), At(4), At(5), At(6));
    }

    public static void ApplyPsReadLineProfile(PsReadLineProfileOptions options)
    {
        string profile = PowerShell7ProfilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        string existing = File.Exists(profile) ? File.ReadAllText(profile) : string.Empty;
        string cleaned = RemoveManagedBlock(existing);

        string predictionSource = options.PredictionSource switch
        {
            "None" => "None",
            "HistoryAndPlugin" => "HistoryAndPlugin",
            "Plugin" => "Plugin",
            _ => "History"
        };
        string predictionView = string.Equals(options.PredictionViewStyle, "InlineView", StringComparison.OrdinalIgnoreCase)
            ? "InlineView"
            : "ListView";
        string editMode = options.EditMode is "Emacs" or "Vi" ? options.EditMode : "Windows";

        var sb = new StringBuilder();
        sb.AppendLine(MarkerStart);
        sb.AppendLine("Register-EngineEvent -SourceIdentifier PowerShell.OnIdle -MaxTriggerCount 1 -Action {");
        sb.AppendLine("Import-Module PSReadLine -ErrorAction SilentlyContinue");
        sb.Append("Set-PSReadLineOption");
        sb.Append($" -EditMode {editMode}");
        sb.Append($" -PredictionSource {predictionSource}");
        if (predictionSource != "None")
            sb.Append($" -PredictionViewStyle {predictionView}");
        sb.Append($" -MaximumHistoryCount {Math.Clamp(options.MaximumHistoryCount, 100, 100000)}");
        if (options.HistoryNoDuplicates) sb.Append(" -HistoryNoDuplicates");
        if (options.HistorySearchCursorMovesToEnd) sb.Append(" -HistorySearchCursorMovesToEnd");
        if (options.BellOff) sb.Append(" -BellStyle None");
        sb.AppendLine();
        if (options.HistoryArrowSearch)
        {
            sb.AppendLine("Set-PSReadLineKeyHandler -Key UpArrow -Function HistorySearchBackward");
            sb.AppendLine("Set-PSReadLineKeyHandler -Key DownArrow -Function HistorySearchForward");
        }
        if (options.MenuCompleteTab)
            sb.AppendLine("Set-PSReadLineKeyHandler -Key Tab -Function MenuComplete");
        sb.AppendLine("} | Out-Null");
        sb.AppendLine(MarkerEnd);

        string final = cleaned.TrimEnd();
        if (final.Length > 0) final += Environment.NewLine + Environment.NewLine;
        final += sb.ToString();
        File.WriteAllText(profile, final, new UTF8Encoding(false));
        ScheduleHistoryCleanupOnce();
    }

    public static void RemovePsReadLineProfileBlock()
    {
        string profile = PowerShell7ProfilePath;
        if (!File.Exists(profile)) return;
        File.WriteAllText(profile, RemoveManagedBlock(File.ReadAllText(profile)).TrimEnd() + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static string RemoveManagedBlock(string text)
    {
        int start = text.IndexOf(MarkerStart, StringComparison.Ordinal);
        if (start < 0) return text;
        int end = text.IndexOf(MarkerEnd, start, StringComparison.Ordinal);
        if (end < 0) return text[..start];
        end += MarkerEnd.Length;
        while (end < text.Length && (text[end] == '\r' || text[end] == '\n')) end++;
        return text.Remove(start, end - start);
    }

    // Fast repeated profile edits should not queue multiple expensive history scans.
    private static void ScheduleHistoryCleanupOnce()
    {
        if (Interlocked.CompareExchange(ref _historyCleanupRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { NormalizePowerShellHistory(); }
            finally { Volatile.Write(ref _historyCleanupRunning, 0); }
        });
    }

    private static void NormalizePowerShellHistory()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt");
            _ = CompactHistoryFile(path, 2 * 1024 * 1024);
        }
        catch (Exception ex)
        {
            LogService.Info("powershell", "PowerShell history cleanup skipped: " + ex.Message);
        }
    }

    // Streaming last-N ring (not File.ReadAllLines): memory consumption is
    // independent of total history length. Replace the file atomically and
    // only if PSReadLine has not appended new commands during compaction.
    internal static int CompactHistoryFile(string path, long minimumBytes = 2 * 1024 * 1024)
    {
        if (!File.Exists(path)) return 0;
        var before = new FileInfo(path);
        if (before.Length < minimumBytes || before.Length > 512L * 1024 * 1024) return 0;
        long length = before.Length;
        DateTime timestamp = before.LastWriteTimeUtc;
        const int keep = 10000;
        var recent = new Queue<string>(keep);
        foreach (string line in File.ReadLines(path))
        {
            if (recent.Count == keep) recent.Dequeue();
            recent.Enqueue(line);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var compact = new List<string>(keep);
        foreach (string line in recent.Reverse())
        {
            if (string.IsNullOrWhiteSpace(line) || !seen.Add(line)) continue;
            compact.Add(line);
        }
        compact.Reverse();

        // Never rewrite a file modified while being read; PSReadLine may be
        // appending from a running shell and its new commands must survive.
        var current = new FileInfo(path);
        if (current.Length != length || current.LastWriteTimeUtc != timestamp) return 0;
        string temporary = path + ".spanky-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, compact, new UTF8Encoding(false));
            current.Refresh();
            if (current.Length != length || current.LastWriteTimeUtc != timestamp) return 0;
            File.Move(temporary, path, overwrite: true);
            return compact.Count;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static void RemoveManagedProfileTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        string profile = PowerShell7ProfilePath;
        if (!File.Exists(profile)) return;

        string text = File.ReadAllText(profile);
        string marker = "# Spankyho:" + tag.Trim();
        int start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return;

        int firstEnd = text.IndexOf('\n', start);
        if (firstEnd < 0) firstEnd = text.Length;
        int secondEnd = firstEnd < text.Length ? text.IndexOf('\n', firstEnd + 1) : -1;
        int end = secondEnd < 0 ? text.Length : secondEnd + 1;
        File.WriteAllText(profile, text.Remove(start, end - start).TrimEnd() + Environment.NewLine,
            new UTF8Encoding(false));
    }

    public static async Task<string> EnsurePSResourceGetAsync(CancellationToken token = default)
    {
        const string script = """
$ErrorActionPreference='Stop'
if(Get-Command Install-PSResource -ErrorAction SilentlyContinue){
  $m=Get-Module -ListAvailable Microsoft.PowerShell.PSResourceGet | Sort-Object Version -Descending | Select-Object -First 1
  "PSResourceGet $($m.Version)"
  exit 0
}
if(-not (Get-Command Install-Module -ErrorAction SilentlyContinue)){ throw 'PowerShellGet/Install-Module není dostupný.' }
Install-Module Microsoft.PowerShell.PSResourceGet -Scope CurrentUser -Force -AllowClobber -Repository PSGallery
Import-Module Microsoft.PowerShell.PSResourceGet -Force
$m=Get-Module Microsoft.PowerShell.PSResourceGet
"PSResourceGet $($m.Version)"
""";
        string result = await RunPwshTextAsync(script, TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        InvalidateStatusCache();
        return result;
    }
}
