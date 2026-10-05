using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record SpicetifyStatus(
    bool SpotifyInstalled,
    bool SpicetifyInstalled,
    bool MarketplaceInstalled,
    string Version,
    string ExecutablePath,
    string ConfigRoot,
    IReadOnlyList<string> Themes,
    IReadOnlyList<string> Extensions);

public sealed record SpicetifyVersionCachePayload(string ExecutablePath, long LastWriteTicks, string Version);

public static partial class SpicetifyService
{
    static SpicetifyService()
    {
        PersistentHardwareCacheService.Remove("spicetify-version-v1");
    }

    private static string? _versionCache;
    private static DateTime _versionCacheAt;
    private static int _versionProbeStarted;
    private static readonly object StatusCacheLock = new();
    private static SpicetifyStatus? _statusCache;
    private static DateTime _statusCacheAt;
    private static Task<SpicetifyStatus>? _statusInFlight;
    private static int _statusGeneration;

    public static string ConfigRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "spicetify");
    public static string ThemesRoot => Path.Combine(ConfigRoot, "Themes");
    public static string ExtensionsRoot => Path.Combine(ConfigRoot, "Extensions");
    public static string CustomAppsRoot => Path.Combine(ConfigRoot, "CustomApps");
    public static async Task<SpicetifyStatus> GetStatusAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<SpicetifyStatus> work;
        lock (StatusCacheLock)
        {
            if (_statusCache != null && DateTime.UtcNow - _statusCacheAt < TimeSpan.FromSeconds(30))
                return _statusCache;

            if (_statusInFlight == null)
            {
                int generation = _statusGeneration;
                var created = Task.Run(() => GetStatusCore(CancellationToken.None));
                _statusInFlight = created;
                _ = ObserveStatusAsync(created, generation);
            }
            work = _statusInFlight;
        }

        return await work.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task ObserveStatusAsync(Task<SpicetifyStatus> created, int generation)
    {
        try
        {
            SpicetifyStatus result = await created.ConfigureAwait(false);
            lock (StatusCacheLock)
            {
                if (generation == _statusGeneration)
                {
                    _statusCache = result;
                    _statusCacheAt = DateTime.UtcNow;
                }
                if (ReferenceEquals(_statusInFlight, created))
                    _statusInFlight = null;
            }
        }
        catch
        {
            lock (StatusCacheLock)
                if (ReferenceEquals(_statusInFlight, created))
                    _statusInFlight = null;
        }
    }

    private static SpicetifyStatus GetStatusCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string exe = ResolveExecutable();
        bool installed = File.Exists(exe);
        string version = "Nenainstalováno";
        if (installed)
        {
            long stamp = 0;
            try { stamp = File.GetLastWriteTimeUtc(exe).Ticks; } catch { }
            if (!string.IsNullOrWhiteSpace(_versionCache) && DateTime.UtcNow - _versionCacheAt < TimeSpan.FromMinutes(5))
                version = _versionCache!;
            else if (UserScopedCacheService.TryRead<SpicetifyVersionCachePayload>("spicetify-version-v1", TimeSpan.FromDays(7), out var persisted) &&
                     persisted != null && string.Equals(persisted.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase) && persisted.LastWriteTicks == stamp && !string.IsNullOrWhiteSpace(persisted.Version))
            {
                _versionCache = persisted.Version;
                _versionCacheAt = DateTime.UtcNow;
                version = persisted.Version;
            }
            else
            {
                var values = ReadConfigValues();
                version = values.TryGetValue("with", out string? hint) && !string.IsNullOrWhiteSpace(hint)
                    ? hint.Trim() : "Nainstalováno";
                if (Interlocked.Exchange(ref _versionProbeStarted, 1) == 0)
                    SafeTaskService.Forget(Task.Run(() => ProbeVersionAsync(exe), CancellationToken.None), "spicetify", "Background version probe");
            }
        }

        token.ThrowIfCancellationRequested();
        bool spotify = DetectSpotify();
        bool marketplace = IsMarketplaceReady(installed);
        return new SpicetifyStatus(spotify, installed, marketplace, version, exe, ConfigRoot,
            ListDirectories(ThemesRoot), ListFiles(ExtensionsRoot, "*.js"));
    }

    private static bool IsMarketplaceReady(bool spicetifyInstalled)
    {
        if (!spicetifyInstalled) return false;
        string root = Path.Combine(CustomAppsRoot, "marketplace");
        if (!Directory.Exists(root) ||
            !File.Exists(Path.Combine(root, "manifest.json")) ||
            !File.Exists(Path.Combine(root, "index.js")))
            return false;

        var values = ReadConfigValues();
        if (!values.TryGetValue("custom_apps", out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("marketplace", StringComparer.OrdinalIgnoreCase);
    }

    private static string MarketplaceReadbackReason(bool spicetifyInstalled)
    {
        if (!spicetifyInstalled) return "Spicetify CLI nebylo po instalaci nalezeno.";
        string root = Path.Combine(CustomAppsRoot, "marketplace");
        if (!Directory.Exists(root)) return "Chybí CustomApps\\marketplace.";
        if (!File.Exists(Path.Combine(root, "manifest.json"))) return "Marketplace nemá manifest.json.";
        if (!File.Exists(Path.Combine(root, "index.js"))) return "Marketplace nemá index.js.";
        var values = ReadConfigValues();
        if (!values.TryGetValue("custom_apps", out string? raw) || string.IsNullOrWhiteSpace(raw))
            return "Spicetify config neobsahuje custom_apps.";
        if (!raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("marketplace", StringComparer.OrdinalIgnoreCase))
            return "Spicetify config custom_apps neobsahuje marketplace.";
        return "OK";
    }
    public static void InvalidateStatusCache()
    {
        lock (StatusCacheLock)
        {
            _statusGeneration++;
            _statusCache = null;
            _statusCacheAt = default;
            _statusInFlight = null;
        }
        InvalidateTweakStateCache();
    }

    public static async Task WarmAsync(CancellationToken token = default)
    {
        var statusTask = GetStatusAsync(token);
        var tweaksTask = GetTweakStateAsync(token);
        await Task.WhenAll(statusTask, tweaksTask).ConfigureAwait(false);
    }

    private static async Task ProbeVersionAsync(string exe)
    {
        try
        {
            var result = await UnelevatedProcessRunner.RunAsync(exe, "-v", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            if (result.Success && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                _versionCache = result.StdOut.Trim();
                _versionCacheAt = DateTime.UtcNow;
                long stamp = 0;
                try { stamp = File.GetLastWriteTimeUtc(exe).Ticks; } catch { }
                UserScopedCacheService.Write("spicetify-version-v1", new SpicetifyVersionCachePayload(exe, stamp, _versionCache));
            }
        }
        catch (Exception ex) { LogService.Error("spicetify", "Background version probe failed", ex); }
        finally { Interlocked.Exchange(ref _versionProbeStarted, 0); }
    }

    private const string SpotifyInstallerUrl = "https://download.scdn.co/SpotifyFullSetupX64.exe";
    private static readonly HttpClient SpotifyHttp = new() { Timeout = TimeSpan.FromMinutes(6) };

    public static Task<ProcessResult> InstallSpotifyAsync(CancellationToken token, Action<string>? onOutput = null) => DownloadAndInstallSpotifyAsync(token, onOutput);
    public static Task<ProcessResult> UpdateSpotifyAsync(CancellationToken token, Action<string>? onOutput = null) => DownloadAndInstallSpotifyAsync(token, onOutput);

    private static async Task<ProcessResult> DownloadAndInstallSpotifyAsync(CancellationToken token, Action<string>? onOutput)
    {
        string installer = Path.Combine(Path.GetTempPath(), $"SpotifyFullSetupX64-{Guid.NewGuid():N}.exe");
        try
        {
            onOutput?.Invoke("Stahuji oficiální Spotify installer…");
            using var response = await SpotifyHttp.GetAsync(SpotifyInstallerUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            {
                await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var output = new FileStream(installer, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                byte[] buffer = new byte[131072]; long received = 0; int lastPct = -1;
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); received += read;
                    if (total > 0)
                    {
                        int pct = (int)Math.Clamp(received * 100L / total, 0, 100);
                        if (pct != lastPct) { lastPct = pct; onOutput?.Invoke($"{pct}% • Stahuji Spotify ({received / 1048576d:0.0}/{total / 1048576d:0.0} MB)"); }
                    }
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            onOutput?.Invoke("100% • Staženo, ověřuji digitální podpis Spotify…");
            token.ThrowIfCancellationRequested();
            if (!AuthenticodeVerifier.TryVerify(installer, "Spotify", out string signatureDetail))
                return new ProcessResult(5, string.Empty,
                    "Stažený Spotify installer neprošel ověřením digitálního podpisu. " + signatureDetail);
            LogService.Info("spicetify", "Spotify installer Authenticode OK signer=" + signatureDetail);

            onOutput?.Invoke("Instaluji Spotify jako běžný uživatel…");
            return await UnelevatedProcessRunner.RunAsync(installer, "/silent /skip-app-launch",
                TimeSpan.FromMinutes(6), token, onOutput).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProcessResult(1, string.Empty, ex.Message);
        }
        finally { try { File.Delete(installer); } catch { } }
    }

    public static Task<ProcessResult> InstallAsync(CancellationToken token) =>
        RunWingetAsync("install --id Spicetify.Spicetify -e --accept-source-agreements --accept-package-agreements --silent --disable-interactivity", token);
    public static Task<ProcessResult> UpdateAsync(CancellationToken token) =>
        RunWingetAsync("upgrade --id Spicetify.Spicetify -e --accept-source-agreements --accept-package-agreements --silent --disable-interactivity", token);

    public static Task<ProcessResult> UninstallAsync(CancellationToken token) =>
        RunWingetAsync("uninstall --id Spicetify.Spicetify -e --silent --disable-interactivity", token);

    public static Task<ProcessResult> BackupApplyAsync(CancellationToken token) => RunCliAsync("backup apply", token, TimeSpan.FromMinutes(4));
    public static Task<ProcessResult> ApplyAsync(CancellationToken token) => RunCliAsync("apply", token, TimeSpan.FromMinutes(3));
    public static Task<ProcessResult> RestoreAsync(CancellationToken token) => RunCliAsync("restore", token, TimeSpan.FromMinutes(3));
    public static Task<ProcessResult> RestartAsync(CancellationToken token) => RunCliAsync("restart", token, TimeSpan.FromSeconds(45));

    public static async Task<ProcessResult> InstallMarketplaceWithPrerequisitesAsync(CancellationToken token)
    {
        if (!File.Exists(ResolveExecutable()))
        {
            ProcessResult cli = await InstallAsync(token).ConfigureAwait(false);
            if (!cli.Success) return cli;
        }
        return await InstallMarketplaceAsync(token).ConfigureAwait(false);
    }

    public static async Task<ProcessResult> InstallMarketplaceAsync(CancellationToken token)
    {
        string exe = ResolveExecutable();
        if (!File.Exists(exe)) return new ProcessResult(2, string.Empty, "Spicetify není nainstalované.");
        string dir = Path.GetDirectoryName(exe) ?? string.Empty;
        string command = "$env:Path='" + EscapePs(dir) + ";'+$env:Path; " +
            "$u='https://raw.githubusercontent.com/spicetify/marketplace/main/resources/install.ps1'; " +
            "Invoke-Expression (Invoke-WebRequest -UseBasicParsing $u).Content";

        ProcessResult install = await UnelevatedProcessRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "`\"") + "\"",
            TimeSpan.FromMinutes(4), token).ConfigureAwait(false);
        if (!install.Success) return install;

        InvalidateStatusCache();
        SpicetifyStatus afterInstall = await GetStatusAsync(token).ConfigureAwait(false);
        if (!afterInstall.MarketplaceInstalled)
        {
            string marketplaceRoot = Path.Combine(CustomAppsRoot, "marketplace");
            bool filesReady = Directory.Exists(marketplaceRoot) &&
                              File.Exists(Path.Combine(marketplaceRoot, "manifest.json")) &&
                              File.Exists(Path.Combine(marketplaceRoot, "index.js"));
            if (filesReady)
            {
                ProcessResult repair = await RunCliAsync("config custom_apps marketplace", token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (repair.Success)
                {
                    if (afterInstall.SpotifyInstalled)
                    {
                        ProcessResult repairApply = await ApplyAsync(token).ConfigureAwait(false);
                        if (!repairApply.Success) return repairApply;
                    }
                    InvalidateStatusCache();
                    afterInstall = await GetStatusAsync(token).ConfigureAwait(false);
                    if (afterInstall.MarketplaceInstalled)
                        LogService.Info("spicetify", "MARKETPLACE_AUTO_REPAIR_OK custom_apps=marketplace");
                }
            }

            if (!afterInstall.MarketplaceInstalled)
            {
                string reason = MarketplaceReadbackReason(afterInstall.SpicetifyInstalled);
                return new ProcessResult(3, install.StdOut,
                    "Marketplace installer skončil bez ověřeného výsledku: " + reason +
                    (string.IsNullOrWhiteSpace(install.StdErr) ? string.Empty : Environment.NewLine + install.StdErr));
            }
        }

        if (afterInstall.SpotifyInstalled)
        {
            ProcessResult apply = await ApplyAsync(token).ConfigureAwait(false);
            if (!apply.Success)
            {
                string stdout = string.Join(Environment.NewLine,
                    new[] { install.StdOut, apply.StdOut }.Where(x => !string.IsNullOrWhiteSpace(x)));
                string stderr = "Marketplace je nakonfigurovaný, ale finální Spicetify apply selhalo." +
                    (string.IsNullOrWhiteSpace(apply.StdErr) ? string.Empty : Environment.NewLine + apply.StdErr);
                return new ProcessResult(apply.ExitCode, stdout, stderr);
            }
        }

        InvalidateStatusCache();
        SpicetifyStatus verified = await GetStatusAsync(token).ConfigureAwait(false);
        if (!verified.MarketplaceInstalled)
            return new ProcessResult(4, install.StdOut,
                "Marketplace read-back po apply selhal: " + MarketplaceReadbackReason(verified.SpicetifyInstalled));

        string outText = string.Join(Environment.NewLine, new[]
        {
            install.StdOut.Trim(),
            verified.SpotifyInstalled
                ? "Marketplace ověřen v configu a Spicetify apply dokončen."
                : "Marketplace ověřen v configu; Spotify není nainstalované, apply se přeskočilo."
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new ProcessResult(0, outText, install.StdErr);
    }
    public static async Task<ProcessResult> ApplyThemeAsync(string theme, string? scheme, CancellationToken token)
    {
        string args = "config current_theme " + Quote(theme);
        if (!string.IsNullOrWhiteSpace(scheme)) args += " color_scheme " + Quote(scheme);
        var config = await RunCliAsync(args, token, TimeSpan.FromSeconds(30));
        if (!config.Success) return config;
        return await ApplyAsync(token);
    }

    public static IReadOnlyList<string> GetColorSchemes(string theme)
    {
        string file = Path.Combine(ThemesRoot, theme, "color.ini");
        if (!File.Exists(file)) return Array.Empty<string>();
        try
        {
            return File.ReadLines(file)
                .Select(x => x.Trim())
                .Where(x => x.Length > 2 && x[0] == '[' && x[^1] == ']')
                .Select(x => x[1..^1].Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public static void OpenConfigRoot() { Directory.CreateDirectory(ConfigRoot); ProcessRunner.Open(ConfigRoot); }
    public static void OpenThemesRoot() { Directory.CreateDirectory(ThemesRoot); ProcessRunner.Open(ThemesRoot); }
    public static void OpenExtensionsRoot() { Directory.CreateDirectory(ExtensionsRoot); ProcessRunner.Open(ExtensionsRoot); }
    public static void OpenCustomAppsRoot() { Directory.CreateDirectory(CustomAppsRoot); ProcessRunner.Open(CustomAppsRoot); }
    private static Task<ProcessResult> RunWingetAsync(string args, CancellationToken token)
    {
        _versionCache = null;
        _versionCacheAt = default;
        UserScopedCacheService.Remove("spicetify-version-v1");
        return UnelevatedProcessRunner.RunAsync("winget.exe", args, TimeSpan.FromMinutes(5), token);
    }

    private static Task<ProcessResult> RunCliAsync(string args, CancellationToken token, TimeSpan timeout)
    {
        string exe = ResolveExecutable();
        if (!File.Exists(exe)) return Task.FromResult(new ProcessResult(2, string.Empty, "Spicetify není nainstalované."));
        return UnelevatedProcessRunner.RunAsync(exe, args, timeout, token);
    }

    private static string ResolveExecutable()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(local, "spicetify", "spicetify.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Links", "spicetify.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Packages", "Spicetify.Spicetify_Microsoft.Winget.Source_8wekyb3d8bbwe", "spicetify.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "spicetify.exe")
        };
        foreach (string candidate in candidates) if (File.Exists(candidate)) return candidate;
        try
        {
            string wingetPackages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPackages))
            {
                string? wingetExe = Directory.EnumerateDirectories(wingetPackages, "Spicetify.Spicetify_*")
                    .Select(dir => Path.Combine(dir, "spicetify.exe"))
                    .FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(wingetExe)) return wingetExe;
            }
        }
        catch { }
        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string part in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { string candidate = Path.Combine(part.Trim(), "spicetify.exe"); if (File.Exists(candidate)) return candidate; } catch { }
        }
        return candidates[0];
    }
    private static bool DetectSpotify()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (File.Exists(Path.Combine(appData, "Spotify", "Spotify.exe"))) return true;
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try { return Directory.Exists(packages) && Directory.EnumerateDirectories(packages, "SpotifyAB.SpotifyMusic_*").Any(); }
        catch { return false; }
    }

    private static string[] ListDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().OrderBy(x => x).ToArray() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string[] ListFiles(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().OrderBy(x => x).ToArray() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static string EscapePs(string value) => value.Replace("'", "''");
}
