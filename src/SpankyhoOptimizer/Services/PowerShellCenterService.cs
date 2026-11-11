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

public static class PowerShellCenterService
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

    public static async Task<string> InstallResourceAsync(string name, CancellationToken token = default)
    {
        string safe = (name ?? string.Empty).Replace("'", "''").Trim();
        if (safe.Length == 0) throw new ArgumentException("Název modulu nesmí být prázdný.", nameof(name));
        await EnsurePSResourceGetAsync(token).ConfigureAwait(false);
        string script =
            "$ErrorActionPreference='Stop'\n" +
            "Import-Module Microsoft.PowerShell.PSResourceGet -ErrorAction Stop\n" +
            "Install-PSResource -Name '" + safe + "' -Scope CurrentUser -Repository PSGallery -TrustRepository -AcceptLicense -Quiet\n" +
            "$m=Get-Module -ListAvailable -Name '" + safe + "' | Sort-Object Version -Descending | Select-Object -First 1\n" +
            "if(-not $m){ throw 'Modul po instalaci nebyl nalezen.' }\n" +
            "\"$($m.Name) $($m.Version)\"";
        string result = await RunPwshTextAsync(script, TimeSpan.FromMinutes(8), token).ConfigureAwait(false);
        if (string.Equals(safe, "Microsoft.WinGet.Client", StringComparison.OrdinalIgnoreCase))
            InvalidateWinGetClientAvailabilityCache();
        InvalidateStatusCache();
        return result;
    }

    public static async Task<string> UpdateResourceAsync(string name, CancellationToken token = default)
    {
        string safe = (name ?? string.Empty).Replace("'", "''").Trim();
        if (safe.Length == 0) throw new ArgumentException("Název modulu nesmí být prázdný.", nameof(name));
        await EnsurePSResourceGetAsync(token).ConfigureAwait(false);
        string script =
            "$ErrorActionPreference='Stop'\n" +
            "Import-Module Microsoft.PowerShell.PSResourceGet -ErrorAction Stop\n" +
            "$installed=$null\n" +
            "try { $installed=Get-InstalledPSResource -Name '" + safe + "' -ErrorAction SilentlyContinue | Select-Object -First 1 } catch {}\n" +
            "if($installed){\n" +
            "  Update-PSResource -Name '" + safe + "' -Scope CurrentUser -TrustRepository -AcceptLicense -Quiet\n" +
            "} else {\n" +
            "  Install-PSResource -Name '" + safe + "' -Scope CurrentUser -Repository PSGallery -TrustRepository -AcceptLicense -Quiet\n" +
            "}\n" +
            "$m=Get-Module -ListAvailable -Name '" + safe + "' | Sort-Object Version -Descending | Select-Object -First 1\n" +
            "if(-not $m){ throw 'Modul po instalaci/aktualizaci nebyl nalezen.' }\n" +
            "\"$($m.Name) $($m.Version)\"";
        string result = await RunPwshTextAsync(script, TimeSpan.FromMinutes(8), token).ConfigureAwait(false);
        if (string.Equals(safe, "Microsoft.WinGet.Client", StringComparison.OrdinalIgnoreCase))
            InvalidateWinGetClientAvailabilityCache();
        InvalidateStatusCache();
        return result;
    }

    public static async Task<string> UpdateManagedResourcesAsync(CancellationToken token = default)
    {
        await EnsurePSResourceGetAsync(token).ConfigureAwait(false);
        const string script = """
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.PSResourceGet -ErrorAction Stop
$names=@('PSReadLine','PSScriptAnalyzer','Terminal-Icons','Microsoft.WinGet.Client','PSFzf','posh-git')
$out=@()
$errors=@()
foreach($name in $names){
  $m=Get-Module -ListAvailable -Name $name | Sort-Object Version -Descending | Select-Object -First 1
  if(-not $m){ continue }
  try {
    $installed=$null
    try { $installed=Get-InstalledPSResource -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1 } catch {}
    if($installed){
      Update-PSResource -Name $name -Scope CurrentUser -TrustRepository -AcceptLicense -Quiet
    } else {
      Install-PSResource -Name $name -Scope CurrentUser -Repository PSGallery -TrustRepository -AcceptLicense -Quiet
    }
    $n=Get-Module -ListAvailable -Name $name | Sort-Object Version -Descending | Select-Object -First 1
    $out += "$name $($n.Version)"
  } catch {
    $errors += "$name ERROR: $($_.Exception.Message)"
  }
}
if($errors.Count){ throw ('Managed resource update failed: ' + ($errors -join '; ')) }
$out -join [Environment]::NewLine
""";
        string result = await RunPwshTextAsync(script, TimeSpan.FromMinutes(12), token).ConfigureAwait(false);
        InvalidateWinGetClientAvailabilityCache();
        InvalidateStatusCache();
        return result;
    }

    public static async Task<IReadOnlyList<PowerShellModuleStatus>> GetManagedModulesAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var cached = GetCachedModules();
        if (cached != null) return cached;
        await ModulesRefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = GetCachedModules();
            if (cached != null) return cached;
            int generation;
            lock (StatusCacheGate) generation = _cacheGeneration;
            var modules = await LoadManagedModulesFreshAsync(token).ConfigureAwait(false);
            lock (StatusCacheGate)
            {
                if (generation == _cacheGeneration)
                {
                    _modulesCache = modules;
                    _modulesCacheAtUtc = DateTime.UtcNow;
                }
            }
            return modules;
        }
        finally { ModulesRefreshGate.Release(); }
    }

    private static async Task<IReadOnlyList<PowerShellModuleStatus>> LoadManagedModulesFreshAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _modulesProcessLoads);
        const string script = """
$names=@('PSReadLine','Microsoft.PowerShell.PSResourceGet','PSScriptAnalyzer','Microsoft.WinGet.Client','Terminal-Icons','PSFzf','posh-git')
foreach($name in $names){
  $m=Get-Module -ListAvailable -Name $name | Sort-Object Version -Descending | Select-Object -First 1
  if($m){ "$name|$($m.Version)" } else { "$name|" }
}
""";
        string text = await RunPwshTextAsync(script, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        var list = new List<PowerShellModuleStatus>();
        foreach (string line in text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = line.Split('|', 2);
            if (p.Length == 0) continue;
            string ver = p.Length > 1 ? p[1].Trim() : string.Empty;
            list.Add(new PowerShellModuleStatus(p[0].Trim(), ver, ver.Length > 0));
        }
        return list;
    }

    public static async Task<IReadOnlyList<ScriptHealthFinding>> AnalyzeScriptsAsync(
        string root,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<ScriptHealthFinding>();

        string safeRoot = root.Replace("'", "''");
        string script =
            "$ErrorActionPreference='Stop'\n" +
            "if(-not (Get-Module -ListAvailable PSScriptAnalyzer)){ throw 'PSScriptAnalyzer není nainstalovaný.' }\n" +
            "Import-Module PSScriptAnalyzer -ErrorAction Stop\n" +
            "$files=Get-ChildItem -LiteralPath '" + safeRoot + "' -Recurse -File -Include *.ps1,*.psm1,*.psd1 -ErrorAction SilentlyContinue\n" +
            "foreach($file in $files){ foreach($r in (Invoke-ScriptAnalyzer -Path $file.FullName -Severity Warning,Error -Recurse:$false)){ " +
            "$msg=($r.Message -replace '[\\r\\n|]',' '); " +
            "\"$($file.FullName)|$($r.Line)|$($r.Severity)|$($r.RuleName)|$msg\" } }";
        string text = await RunPwshTextAsync(script, TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        var list = new List<ScriptHealthFinding>();
        foreach (string line in text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = line.Split('|', 5);
            if (p.Length < 5) continue;
            _ = int.TryParse(p[1], out int lineNo);
            list.Add(new ScriptHealthFinding(p[0], lineNo, p[2], p[3], p[4]));
        }
        return list;
    }

    public static async Task<string> EnsurePSReadLineAsync(CancellationToken token = default) =>
        await InstallOrUpdateResourceAsync("PSReadLine", token).ConfigureAwait(false);

    public static async Task<string> EnsureWinGetClientAsync(CancellationToken token = default) =>
        await InstallOrUpdateResourceAsync("Microsoft.WinGet.Client", token).ConfigureAwait(false);

    public static async Task<string> EnsurePSScriptAnalyzerAsync(CancellationToken token = default) =>
        await InstallOrUpdateResourceAsync("PSScriptAnalyzer", token).ConfigureAwait(false);

    public static async Task<string> EnsureTerminalIconsAsync(CancellationToken token = default)
    {
        string result = await InstallOrUpdateResourceAsync("Terminal-Icons", token).ConfigureAwait(false);
        EnsureProfileLine(DeferredProfileLine("Import-Module Terminal-Icons -ErrorAction SilentlyContinue"), "Terminal-Icons");
        return result;
    }

    public static async Task<string> EnsurePSFzfAsync(CancellationToken token = default)
    {
        string result = await InstallOrUpdateResourceAsync("PSFzf", token).ConfigureAwait(false);
        EnsureProfileLine(DeferredProfileLine("Import-Module PSFzf -ErrorAction SilentlyContinue"), "PSFzf");
        return result;
    }

    public static async Task<string> EnsurePoshGitAsync(CancellationToken token = default)
    {
        string result = await InstallOrUpdateResourceAsync("posh-git", token).ConfigureAwait(false);
        EnsureProfileLine(DeferredProfileLine("Import-Module posh-git -ErrorAction SilentlyContinue"), "posh-git");
        return result;
    }

    public static void EnableZoxideInProfile()
    {
        const string command = "$cache=Join-Path $env:LOCALAPPDATA 'SpankyhoOptimizer\\Cache\\zoxide-init.ps1'; if(Test-Path -LiteralPath $cache){. $cache}else{$z=Get-Command zoxide -ErrorAction SilentlyContinue;if($z){New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cache)|Out-Null;& $z.Source init powershell | Set-Content -LiteralPath $cache -Encoding utf8;. $cache}}";
        EnsureProfileLine(DeferredProfileLine(command), "zoxide");
    }

    private static async Task<string> InstallOrUpdateResourceAsync(string name, CancellationToken token)
    {
        var modules = await GetManagedModulesAsync(token).ConfigureAwait(false);
        bool installed = modules.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.Installed);
        return installed
            ? await UpdateResourceAsync(name, token).ConfigureAwait(false)
            : await InstallResourceAsync(name, token).ConfigureAwait(false);
    }

    public static async Task<string> EnsureOhMyPoshAsync(CancellationToken token = default)
    {
        var result = await UnelevatedProcessRunner.RunAsync(
            "winget.exe",
            "install --id JanDeDobbeleer.OhMyPosh --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
            TimeSpan.FromMinutes(10), token).ConfigureAwait(false);
        if (!result.Success && result.ExitCode != unchecked((int)0x8A15002B))
            throw new InvalidOperationException((result.StdErr + result.StdOut).Trim());
        return string.IsNullOrWhiteSpace(result.StdOut) ? "Oh My Posh připraven." : result.StdOut.Trim();
    }

    public static async Task<string> UpdateOhMyPoshAsync(CancellationToken token = default)
    {
        var result = await UnelevatedProcessRunner.RunAsync(
            "winget.exe",
            "upgrade --id JanDeDobbeleer.OhMyPosh --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
            TimeSpan.FromMinutes(10), token).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException((result.StdErr + result.StdOut).Trim());
        return string.IsNullOrWhiteSpace(result.StdOut) ? "Oh My Posh aktualizován." : result.StdOut.Trim();
    }

    public static void EnableOhMyPoshInProfile(string themePath = "")
    {
        string command = string.IsNullOrWhiteSpace(themePath)
            ? "oh-my-posh init pwsh | Invoke-Expression"
            : $"oh-my-posh init pwsh --config '{themePath.Replace("'", "''")}' | Invoke-Expression";
        EnsureProfileLine(DeferredProfileLine(command), "OhMyPosh");
    }

    private static string DeferredProfileLine(string command)
    {
        string escaped = command.Replace("'", "''", StringComparison.Ordinal);
        return $"Register-EngineEvent -SourceIdentifier PowerShell.OnIdle -MaxTriggerCount 1 -Action {{ Invoke-Expression '{escaped}' }} | Out-Null";
    }

    private static void EnsureProfileLine(string line, string tag)
    {
        string profile = PowerShell7ProfilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        string existing = File.Exists(profile) ? File.ReadAllText(profile) : string.Empty;
        string marker = $"# Spankyho:{tag}";

        int markerIndex = existing.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            int lineStart = existing.IndexOf('\n', markerIndex);
            if (lineStart < 0)
            {
                File.WriteAllText(profile, existing.TrimEnd() + Environment.NewLine + line + Environment.NewLine,
                    new UTF8Encoding(false));
                return;
            }

            lineStart++;
            int lineEnd = existing.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = existing.Length;
            string current = existing[lineStart..lineEnd].TrimEnd('\r');
            if (string.Equals(current, line, StringComparison.Ordinal)) return;

            string updated = existing[..lineStart] + line +
                (lineEnd < existing.Length ? existing[lineEnd..] : Environment.NewLine);
            File.WriteAllText(profile, updated, new UTF8Encoding(false));
            return;
        }

        string append = $"{Environment.NewLine}{marker}{Environment.NewLine}{line}{Environment.NewLine}";
        File.AppendAllText(profile, append, new UTF8Encoding(false));
    }

    public static int ApplyFastStartupProfile()
    {
        string profile = PowerShell7ProfilePath;
        if (!File.Exists(profile)) return 0;

        string backup = profile + ".spanky-startup.backup";
        if (!File.Exists(backup)) File.Copy(profile, backup, false);

        string text = File.ReadAllText(profile);
        int removed = 0;
        foreach (string tag in new[] { "OhMyPosh", "Terminal-Icons", "PSFzf", "posh-git", "zoxide" })
        {
            string marker = "# Spankyho:" + tag;
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            else
            {
                int second = text.IndexOf('\n', end + 1);
                end = second < 0 ? text.Length : second + 1;
            }
            text = text.Remove(start, end - start);
            removed++;
        }

        if (removed > 0)
            File.WriteAllText(profile, text.TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
        return removed;
    }

    public static bool RestoreFastStartupProfileBackup()
    {
        string profile = PowerShell7ProfilePath;
        string backup = profile + ".spanky-startup.backup";
        if (!File.Exists(backup)) return false;
        File.Copy(backup, profile, true);
        return true;
    }

    public static async Task<string> TestWinGetClientAsync(CancellationToken token = default)
    {
        const string script = """
$ErrorActionPreference='Stop'
Import-Module Microsoft.WinGet.Client -ErrorAction Stop
$u=@(Get-WinGetPackage | Where-Object IsUpdateAvailable)
"Microsoft.WinGet.Client OK | updates=$($u.Count)"
""";
        return await RunPwshTextAsync(script, TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
    }

    public static async Task<bool> IsWinGetClientAvailableAsync(CancellationToken token = default)
    {
        if (_winGetClientAvailable.HasValue &&
            DateTime.UtcNow - _winGetClientAvailableAtUtc < WinGetClientAvailabilityTtl)
            return _winGetClientAvailable.Value;

        await WinGetClientStatusGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_winGetClientAvailable.HasValue &&
                DateTime.UtcNow - _winGetClientAvailableAtUtc < WinGetClientAvailabilityTtl)
                return _winGetClientAvailable.Value;

            const string script = """
$m=Get-Module -ListAvailable Microsoft.WinGet.Client | Sort-Object Version -Descending | Select-Object -First 1
if($m){ '1' } else { '0' }
""";
            string text = await RunPwshTextAsync(script, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            _winGetClientAvailable = text.Trim() == "1";
            _winGetClientAvailableAtUtc = DateTime.UtcNow;
            return _winGetClientAvailable.Value;
        }
        finally
        {
            WinGetClientStatusGate.Release();
        }
    }

    public static void InvalidateWinGetClientAvailabilityCache()
    {
        _winGetClientAvailable = null;
        _winGetClientAvailableAtUtc = default;
    }

    public static string RunProfileIdempotencySmoke()
    {
        string profile = PowerShell7ProfilePath;
        bool existed = File.Exists(profile);
        string original = existed ? File.ReadAllText(profile) : string.Empty;
        var options = new PsReadLineProfileOptions(
            "Windows", "History", "ListView", 10000,
            true, true, true, true, true);
        try
        {
            ApplyPsReadLineProfile(options);
            string first = File.ReadAllText(profile);
            ApplyPsReadLineProfile(options);
            string second = File.ReadAllText(profile);
            if (!string.Equals(first, second, StringComparison.Ordinal))
                throw new InvalidOperationException("Opakované použití stejného PowerShell profilu změnilo obsah.");
            return "SHELL_SETUP_IDEMPOTENCY_SMOKE_OK bytes=" + Encoding.UTF8.GetByteCount(second);
        }
        finally
        {
            try
            {
                if (existed)
                    File.WriteAllText(profile, original, new UTF8Encoding(false));
                else if (File.Exists(profile))
                    File.Delete(profile);
            }
            catch (Exception ex)
            {
                LogService.Error("shell", "Idempotency smoke profile restore failed", ex);
            }
        }
    }

    public static async Task<ProcessResult> UpdateWinGetPackageAsync(string id, CancellationToken token = default)
    {
        string safe = (id ?? string.Empty).Replace("'", "''").Trim();
        if (safe.Length == 0) return new ProcessResult(2, string.Empty, "Package ID is empty.");
        string script =
            "$ErrorActionPreference='Stop'\n" +
            "Import-Module Microsoft.WinGet.Client -ErrorAction Stop\n" +
            "$r=Update-WinGetPackage -Id '" + safe + "' -MatchOption EqualsCaseInsensitive -Mode Silent -ErrorAction Stop\n" +
            "if(-not $r){ 'No result'; exit 0 }\n" +
            "$bad=@($r | Where-Object { $_.Status.ToString() -ne 'Ok' })\n" +
            "$r | ForEach-Object { \"$($_.Name)|$($_.Status)|$($_.ExtendedErrorCode)|reboot=$($_.RebootRequired)\" }\n" +
            "if($bad.Count -gt 0){ exit 2 }";
        return await RunPwshAsync(script, TimeSpan.FromMinutes(25), token).ConfigureAwait(false);
    }

    public static async Task<ProcessResult> UpdateAllWinGetPackagesAsync(CancellationToken token = default)
    {
        const string script = """
$ErrorActionPreference='Stop'
Import-Module Microsoft.WinGet.Client -ErrorAction Stop
$packages=@(Get-WinGetPackage | Where-Object IsUpdateAvailable)
if($packages.Count -eq 0){ 'No updates'; exit 0 }
$failed=0
foreach($p in $packages){
  try {
    $r=$p | Update-WinGetPackage -Mode Silent -ErrorAction Stop
    foreach($item in @($r)){
      "$($item.Name)|$($item.Status)|$($item.ExtendedErrorCode)|reboot=$($item.RebootRequired)"
      if($item.Status.ToString() -ne 'Ok'){ $failed++ }
    }
  } catch {
    "$($p.Name)|Exception|$($_.Exception.Message)"
    $failed++
  }
}
if($failed -gt 0){ exit 2 }
""";
        return await RunPwshAsync(script, TimeSpan.FromMinutes(45), token).ConfigureAwait(false);
    }
}
