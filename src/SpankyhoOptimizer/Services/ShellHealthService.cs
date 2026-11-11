using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record ShellHealthFinding(string Area, string Severity, string Message, bool Repairable);
public sealed record ShellHealthReport(IReadOnlyList<ShellHealthFinding> Findings)
{
    public int Errors => Findings.Count(x => x.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
    public int Warnings => Findings.Count(x => x.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
}
public sealed record ShellStartupCost(string Name, double Milliseconds, bool Available);

public static class ShellHealthService
{
    private const string EnvKeyPath = @"Environment";
    private const string BackupKeyPath = @"Software\SpankyhoOptimizer\Backups\ShellHealth";
    private const string CommandProcessorKeyPath = @"Software\Microsoft\Command Processor";
    private static readonly object ProfileAnalysisLock = new();
    private static Task<string?>? _profileAnalysisInFlight;
    private static string? _profileAnalysisFile;
    private static long _profileAnalysisLength;
    private static string? _profileAnalysisHash;
    private static string? _profileAnalysisParserIdentity;
    private static DateTime _profileAnalysisModified;
    private static DateTime _profileAnalysisCachedAt;
    private static bool _profileAnalysisCached;
    private static string? _profileAnalysisError;
    private static int _profileParseStarts;
    private static int _profileExternalStarts;
    private const string ProfileSyntaxPersistentKey = "shell-profile-syntax-v1";
    private static readonly TimeSpan ProfileSyntaxPersistentTtl = TimeSpan.FromHours(6);
    private sealed record ProfileSyntaxDiskSnapshot(
        string ProfilePath, string ContentHash, string ParserIdentity, string? Error);
    internal static int ProfileParseStartsForDiagnostics => Volatile.Read(ref _profileParseStarts);

    private static string GetProfileParserIdentity()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7");
        // Both PowerShell parser assembly and fallback host can change syntax behavior.
        static string Stamp(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path + "|missing";
        }
        return Stamp(Path.Combine(root, "System.Management.Automation.dll")) + ";" +
            Stamp(Path.Combine(root, "pwsh.exe"));
    }

    private static bool ReusableProfileSnapshot(ProfileSyntaxDiskSnapshot? snapshot,
        string path, string hash, string parserIdentity) =>
        snapshot != null &&
        string.Equals(snapshot.ProfilePath, path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.ContentHash, hash, StringComparison.Ordinal) &&
        string.Equals(snapshot.ParserIdentity, parserIdentity, StringComparison.Ordinal);


    public static async Task<ShellHealthReport> AnalyzeAsync(CancellationToken token = default)
    {
        var list = new List<ShellHealthFinding>();
        AnalyzeUserPath(list);
        AnalyzeCmdAutorun(list);
        AnalyzeTerminalJson(list);
        await AnalyzePowerShellProfileAsync(list, token).ConfigureAwait(false);
        return new ShellHealthReport(list);
    }

    public static string RepairRecommended()
    {
        var changed = new List<string>();
        if (RepairUserPath()) changed.Add("User PATH");
        if (RepairTerminalJson()) changed.Add("Windows Terminal JSON");
        return changed.Count == 0
            ? "Nebyla nalezena bezpečně opravitelná odchylka."
            : "Opraveno: " + string.Join(", ", changed) + ".";
    }

    // One pwsh startup instead of six. Each test keeps its own Stopwatch and
    // failure marker; module failures cannot suppress the other five results.
    private static readonly string[] ProfileProbeNames =
        ["PSReadLine", "Terminal-Icons", "PSFzf", "posh-git", "zoxide", "Oh My Posh"];

    private const string BatchedProfileProbeScript = """
$ErrorActionPreference='Stop'
$probes=@(
  { Import-Module PSReadLine -ErrorAction Stop },
  { Import-Module Terminal-Icons -ErrorAction Stop },
  { Import-Module PSFzf -ErrorAction Stop },
  { Import-Module posh-git -ErrorAction Stop },
  { zoxide init powershell | Out-Null },
  { oh-my-posh init pwsh | Out-Null }
)
for($i=0;$i -lt $probes.Count;$i++){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  try {
    & $probes[$i] *>$null
    $sw.Stop()
    [Console]::Out.WriteLine('SPANKY_PROFILE_PROBE|'+$i+'|'+$sw.Elapsed.TotalMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture))
  } catch {
    [Console]::Out.WriteLine('SPANKY_PROFILE_PROBE|'+$i+'|NA')
  }
}
""";

    public static async Task<IReadOnlyList<ShellStartupCost>> MeasureKnownProfileCostsAsync(
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string exe = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
            ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "pwsh.exe";
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(BatchedProfileProbeScript));
        // Process initialization can briefly run synchronously: keep it off the UI thread.
        var run = await Task.Run(() => ProcessRunner.RunAsync(
            exe, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded,
            TimeSpan.FromSeconds(45), token), token).ConfigureAwait(false);

        var readings = new double?[ProfileProbeNames.Length];
        if (run.Success)
        {
            foreach (string line in run.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Trim().Split('|');
                if (fields.Length != 3 || fields[0] != "SPANKY_PROFILE_PROBE" ||
                    !int.TryParse(fields[1], out int i) || i < 0 || i >= readings.Length) continue;
                if (double.TryParse(fields[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double ms) &&
                    double.IsFinite(ms) && ms >= 0)
                    readings[i] = ms;
            }
        }
        var results = ProfileProbeNames.Select((name, i) =>
            new ShellStartupCost(name, readings[i] ?? 0, readings[i].HasValue)).ToArray();
        LogService.Info("performance", "SHELL_PROFILE_BATCH_PROBE processStarts=1 probes=" +
            results.Length + " available=" + results.Count(x => x.Available));
        return results;
    }    private static void AnalyzeUserPath(List<ShellHealthFinding> list)
    {
        string raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        string[] entries = raw.Split(';', StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0;
        int empty = 0;
        int missing = 0;

        foreach (string entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) { empty++; continue; }
            string normalized = NormalizePathEntry(entry);
            if (!seen.Add(normalized)) duplicates++;
            string expanded = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (!Directory.Exists(expanded)) missing++;
        }

        if (duplicates > 0 || empty > 0)
            list.Add(new ShellHealthFinding(
                "PATH", "Warning",
                $"User PATH: duplicity {duplicates}, prázdné položky {empty}.",
                true));
        if (missing > 0)
            list.Add(new ShellHealthFinding(
                "PATH", "Info",
                $"User PATH obsahuje {missing} aktuálně neexistujících cest; nemažu je automaticky.",
                false));
    }

    private static void AnalyzeCmdAutorun(List<ShellHealthFinding> list)
    {
        string user = ReadAutorun(Registry.CurrentUser, CommandProcessorKeyPath);
        string machine = ReadAutorun(Registry.LocalMachine, @"SOFTWARE\Microsoft\Command Processor");
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(machine))
            list.Add(new ShellHealthFinding(
                "CMD AutoRun", "Warning",
                "Je nastavený HKCU i HKLM AutoRun; příkazy se mohou řetězit a zpomalovat start CMD.",
                false));
        if (!string.IsNullOrWhiteSpace(user) &&
            !user.Contains("clink", StringComparison.OrdinalIgnoreCase))
            list.Add(new ShellHealthFinding(
                "CMD AutoRun", "Info",
                "HKCU AutoRun obsahuje vlastní příkaz mimo Clink; ponecháno beze změny.",
                false));
    }    private static void AnalyzeTerminalJson(List<ShellHealthFinding> list)
    {
        foreach (string path in TerminalSettingsPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (Exception ex)
            {
                list.Add(new ShellHealthFinding(
                    "Windows Terminal", "Error",
                    $"{Path.GetFileName(path)} není validní JSON: {ex.Message}",
                    true));
            }
        }
    }

    internal static async Task RunProfileCacheSmokeAsync(CancellationToken token)
    {
        string path = Path.Combine(Path.GetTempPath(), "spanky-profile-cache-" + Guid.NewGuid().ToString("N") + ".ps1");
        var diskFixture = new ProfileSyntaxDiskSnapshot(path, "ABC", "parser-v1", "syntax");
        if (!ReusableProfileSnapshot(diskFixture, path, "ABC", "parser-v1") ||
            ReusableProfileSnapshot(diskFixture, path, "CHANGED", "parser-v1") ||
            ReusableProfileSnapshot(diskFixture, path, "ABC", "parser-v2") ||
            ReusableProfileSnapshot(diskFixture, path + ".other", "ABC", "parser-v1") ||
            ReusableProfileSnapshot(null, path, "ABC", "parser-v1"))
            throw new InvalidOperationException("Persistent PowerShell parser fingerprint validation failed.");
        try
        {
            await File.WriteAllTextAsync(path, "Write-Output 'first'", token).ConfigureAwait(false);
            var timestamp = File.GetLastWriteTimeUtc(path);
            int beforeExternal = Volatile.Read(ref _profileExternalStarts);
            int before = ProfileParseStartsForDiagnostics;
            var sw = Stopwatch.StartNew();
            var group = Enumerable.Range(0, 6).Select(_ =>
            {
                var findings = new List<ShellHealthFinding>();
                return AnalyzePowerShellProfileAsync(findings, token, path);
            }).ToArray();
            await Task.WhenAll(group).ConfigureAwait(false);
            int cold = ProfileParseStartsForDiagnostics - before;
            if (cold != 1) throw new InvalidOperationException("Concurrent profile scan spawned " + cold + " parsers, expected 1.");
            before = ProfileParseStartsForDiagnostics;
            await AnalyzePowerShellProfileAsync(new List<ShellHealthFinding>(), token, path).ConfigureAwait(false);
            int warm = ProfileParseStartsForDiagnostics - before;
            if (warm != 0) throw new InvalidOperationException("Warm profile scan started another parser.");
            await File.WriteAllTextAsync(path, "Write-Output 'other'", token).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, timestamp);
            before = ProfileParseStartsForDiagnostics;
            await AnalyzePowerShellProfileAsync(new List<ShellHealthFinding>(), token, path).ConfigureAwait(false);
            int changed = ProfileParseStartsForDiagnostics - before;
            if (changed != 1) throw new InvalidOperationException("Same length/timestamp content edit did not invalidate cache.");
            int external = Volatile.Read(ref _profileExternalStarts) - beforeExternal;
            string automation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "System.Management.Automation.dll");
            if (File.Exists(automation) && external != 0)
                throw new InvalidOperationException("In-process parser was not used. externalStarts=" + external);
            // Compare the actual parser paths on the same temporary profile.
            // Do not execute the script: both methods only parse syntax.
            var nativeTimes = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                if (!TryParseProfileInProcess(path, out var err) || err != null)
                    throw new InvalidOperationException("In-process parser benchmark failed: " + err);
                nativeTimes.Add(watch.Elapsed.TotalMilliseconds);
            }
            string benchScript = "[System.Management.Automation.Language.Parser]::ParseFile('" +
                path.Replace("'", "''") + "',[ref]$null,[ref]$null)|Out-Null";
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(benchScript));
            string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwsh))
            {
                var externalTimes = new List<double>();
                for (int i = 0; i < 3; i++)
                {
                    var watch = Stopwatch.StartNew();
                    var result = await ProcessRunner.RunAsync(pwsh,
                        "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded,
                        TimeSpan.FromSeconds(12), token).ConfigureAwait(false);
                    if (!result.Success)
                        throw new InvalidOperationException("External parser benchmark failed: " + result.StdErr);
                    externalTimes.Add(watch.Elapsed.TotalMilliseconds);
                }
                nativeTimes.Sort();
                externalTimes.Sort();
                LogService.Info("performance", "SHELL_PROFILE_AB medianNativeMs=" +
                    nativeTimes[1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    " medianExternalMs=" + externalTimes[1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    " n=3");
            }
            LogService.Info("performance", "SHELL_PROFILE_CACHE_SMOKE_OK external=" + external + " cold=" + cold +
                " warm=" + warm + " changed=" + changed + " wallMs=" + sw.ElapsedMilliseconds);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task AnalyzePowerShellProfileAsync(
        List<ShellHealthFinding> list,
        CancellationToken token, string? testProfile = null)
    {
        string profile = testProfile ?? PowerShellCenterService.PowerShell7ProfilePath;
        if (!File.Exists(profile)) return;
        var info = new FileInfo(profile);
        // Content fingerprint also detects edits that preserve length and timestamp.
        // Large profiles bypass the cache rather than hashing unbounded data.
        string? hash = info.Length <= 1024 * 1024
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(profile)))
            : null;
        token.ThrowIfCancellationRequested();
        bool persistentEligible = testProfile == null && hash != null;
        string parserIdentity = persistentEligible ? GetProfileParserIdentity() : string.Empty;
        ProfileSyntaxDiskSnapshot? diskSnapshot = null;
        bool needDiskRead;
        lock (ProfileAnalysisLock)
        {
            bool current = string.Equals(_profileAnalysisFile, profile, StringComparison.OrdinalIgnoreCase) &&
                _profileAnalysisLength == info.Length && _profileAnalysisModified == info.LastWriteTimeUtc &&
                hash != null && hash == _profileAnalysisHash &&
                (!persistentEligible || _profileAnalysisParserIdentity == parserIdentity);
            needDiskRead = !current ||
                (!(_profileAnalysisCached && DateTime.UtcNow - _profileAnalysisCachedAt < TimeSpan.FromMinutes(2)) &&
                 _profileAnalysisInFlight is not { IsCompleted: false });
        }
        if (persistentEligible && needDiskRead)
            _ = UserScopedCacheService.TryRead(
                ProfileSyntaxPersistentKey, ProfileSyntaxPersistentTtl, out diskSnapshot);
        Task<string?> analysis;
        lock (ProfileAnalysisLock)
        {
            bool sameFile = string.Equals(_profileAnalysisFile, profile, StringComparison.OrdinalIgnoreCase) &&
                _profileAnalysisLength == info.Length && _profileAnalysisModified == info.LastWriteTimeUtc &&
                hash != null && hash == _profileAnalysisHash &&
                (!persistentEligible || _profileAnalysisParserIdentity == parserIdentity);
            if (sameFile && _profileAnalysisCached && DateTime.UtcNow - _profileAnalysisCachedAt < TimeSpan.FromMinutes(2))
            {
                analysis = Task.FromResult(_profileAnalysisError);
            }
            else if (sameFile && _profileAnalysisInFlight is { IsCompleted: false })
            {
                analysis = _profileAnalysisInFlight;
            }
            else if (hash != null &&
                ReusableProfileSnapshot(diskSnapshot, profile, hash, parserIdentity))
            {
                _profileAnalysisFile = profile;
                _profileAnalysisLength = info.Length;
                _profileAnalysisModified = info.LastWriteTimeUtc;
                _profileAnalysisHash = hash;
                _profileAnalysisParserIdentity = parserIdentity;
                _profileAnalysisError = diskSnapshot!.Error;
                _profileAnalysisCachedAt = DateTime.UtcNow;
                _profileAnalysisCached = true;
                _profileAnalysisInFlight = null;
                analysis = Task.FromResult(diskSnapshot.Error);
                LogService.Info("performance", "PS_PROFILE_DISK_CACHE_HIT parserStarts=0");
            }
            else
            {
                _profileAnalysisFile = profile;
                _profileAnalysisLength = info.Length;
                _profileAnalysisModified = info.LastWriteTimeUtc;
                _profileAnalysisHash = hash;
                _profileAnalysisParserIdentity = parserIdentity;
                _profileAnalysisCached = false;
                analysis = ParsePowerShellProfileAsync(profile);
                _profileAnalysisInFlight = analysis;
                _ = CachePowerShellProfileResultAsync(analysis, profile, info.Length,
                    info.LastWriteTimeUtc, hash, persistentEligible ? parserIdentity : null);
            }
        }
        string? error = await analysis.WaitAsync(token).ConfigureAwait(false);
        if (error != null)
            list.Add(new ShellHealthFinding("PowerShell profile", "Error", error, false));
    }

    private static async Task CachePowerShellProfileResultAsync(Task<string?> analysis,
        string profile, long length, DateTime modified, string? hash, string? parserIdentity)
    {
        try
        {
            string? error = await analysis.ConfigureAwait(false);
            bool persist = false;
            lock (ProfileAnalysisLock)
            {
                if (ReferenceEquals(_profileAnalysisInFlight, analysis) &&
                    _profileAnalysisFile == profile && _profileAnalysisLength == length &&
                    _profileAnalysisModified == modified && _profileAnalysisHash == hash &&
                    (parserIdentity == null || _profileAnalysisParserIdentity == parserIdentity))
                {
                    _profileAnalysisError = error;
                    _profileAnalysisCachedAt = DateTime.UtcNow;
                    _profileAnalysisCached = true;
                    _profileAnalysisInFlight = null;
                    persist = parserIdentity != null && hash != null;
                }
            }
            // Never persist the result of a file edited while parsing. A
            // SHA-256 match and parser stamp are required at every cold read.
            if (persist && File.Exists(profile))
            {
                var current = new FileInfo(profile);
                if (current.Length == length && current.LastWriteTimeUtc == modified &&
                    current.Length <= 1024 * 1024 &&
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        await File.ReadAllBytesAsync(profile).ConfigureAwait(false))) == hash &&
                    GetProfileParserIdentity() == parserIdentity)
                    UserScopedCacheService.Write(ProfileSyntaxPersistentKey,
                        new ProfileSyntaxDiskSnapshot(profile, hash!, parserIdentity!, error));
            }
        }
        catch (Exception ex)
        {
            LogService.Info("powershell", "Profile syntax cache persistence skipped: " + ex.GetType().Name);
            lock (ProfileAnalysisLock)
                if (ReferenceEquals(_profileAnalysisInFlight, analysis))
                    _profileAnalysisInFlight = null;
        }
    }

    // Invoke the actual PowerShell parser in-process when the locally installed
    // PS7 automation assembly is available. This runs no profile instructions.
    private static bool TryParseProfileInProcess(string profile, out string? error)
    {
        error = null;
        string assemblyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "System.Management.Automation.dll");
        if (!File.Exists(assemblyPath)) return false;
        try
        {
            var sw = Stopwatch.StartNew();
            long beforeMemory = GC.GetTotalMemory(false);
            var assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);
            var parser = assembly.GetType("System.Management.Automation.Language.Parser", true)!;
            var method = parser.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .First(m => m.Name == "ParseFile" && m.GetParameters().Length == 3);
            object?[] args = [profile, null, null];
            method.Invoke(null, args);
            if (args[2] is Array errors && errors.Length > 0)
            {
                error = errors.GetValue(0)?.GetType().GetProperty("Message")?.GetValue(errors.GetValue(0))?.ToString()
                    ?? "Profil obsahuje syntaktickou chybu.";
                if (error.Length > 240) error = error[..240];
            }
            LogService.Info("performance", "PS_PROFILE_INPROC ms=" + sw.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                " managedDeltaBytes=" + (GC.GetTotalMemory(false) - beforeMemory));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Info("powershell", "In-process profile parser unavailable: " + ex.GetType().Name);
            return false;
        }
    }

    private static async Task<string?> ParsePowerShellProfileAsync(string profile)
    {
        Interlocked.Increment(ref _profileParseStarts);
        // Reflection + first-load PowerShell parser JIT can block 100+ ms.
        // This health check must never consume a UI dispatcher turn.
        var native = await Task.Run(() =>
        {
            bool available = TryParseProfileInProcess(profile, out string? parseError);
            return (available, parseError);
        }).ConfigureAwait(false);
        if (native.available)
            return native.parseError;
        string escaped = profile.Replace("'", "''");
        string script =
            "$t=$null;$e=$null;" +
            "[System.Management.Automation.Language.Parser]::ParseFile('" + escaped + "',[ref]$t,[ref]$e)|Out-Null;" +
            "if($e.Count -gt 0){$e|ForEach-Object{$_.Message};exit 9}";
        string exe = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
            ? @"C:\Program Files\PowerShell\7\pwsh.exe"
            : "pwsh.exe";
        // EncodedCommand preserves literal profile paths (including quotes and Unicode)
        // rather than passing script source through Windows command-line quoting.
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Interlocked.Increment(ref _profileExternalStarts);
        var result = await ProcessRunner.RunAsync(
            exe, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded,
            TimeSpan.FromSeconds(12), CancellationToken.None).ConfigureAwait(false);
        if (result.Success) return null;
        string message = (result.StdOut + " " + result.StdErr).Trim();
        if (message.Length > 240) message = message[..240];
        return string.IsNullOrWhiteSpace(message) ? "Profil obsahuje syntaktickou chybu." : message;
    }    private static bool RepairUserPath()
    {
        string raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        string[] entries = raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();
        foreach (string entry in entries)
        {
            string normalized = NormalizePathEntry(entry);
            if (seen.Add(normalized)) cleaned.Add(entry.Trim());
        }

        string next = string.Join(';', cleaned);
        if (string.Equals(raw.Trim(';'), next, StringComparison.Ordinal)) return false;

        BackupStringOnce("UserPath", raw);
        Environment.SetEnvironmentVariable("PATH", next, EnvironmentVariableTarget.User);
        return true;
    }

    private static bool RepairTerminalJson()
    {
        bool changed = false;
        foreach (string path in TerminalSettingsPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch
            {
                string backup = path + ".shell-health.backup";
                if (!File.Exists(backup)) File.Copy(path, backup, false);
                File.WriteAllText(path, "{}");
                changed = true;
            }
        }
        return changed;
    }    private static string NormalizePathEntry(string entry) =>
        Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ReadAutorun(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue("AutoRun")?.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static void BackupStringOnce(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(BackupKeyPath, true)
            ?? throw new InvalidOperationException("Nelze vytvořit Shell Health backup.");
        if (key.GetValue(name + ".Saved") is int) return;
        key.SetValue(name + ".Saved", 1, RegistryValueKind.DWord);
        key.SetValue(name + ".Value", value ?? string.Empty, RegistryValueKind.String);
    }

    private static IEnumerable<string> TerminalSettingsPaths()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(local, @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(local, @"Microsoft\Windows Terminal\settings.json");
    }
}
