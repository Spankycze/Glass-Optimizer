using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record FirefoxUpdateStatus(
    bool Installed,
    string InstalledVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string? ExePath,
    string Message,
    string LatestSource);

public static class FirefoxUpdateService
{
    private const string VersionUrl = "https://product-details.mozilla.org/1.0/firefox_versions.json";
    private const string DownloadUrl = "https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=cs";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly object Gate = new();
    private static FirefoxUpdateStatus? _cache;
    private static DateTime _cacheAt;
    private static Task<FirefoxUpdateStatus>? _flight;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly Regex ReleaseUriVersionRegex = new(
        @"/releases/(?<version>\d+(?:\.\d+){1,3})/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex WingetVersionRegex = new(
        @"(?im)^\s*(?:Version|Verze)\s*:\s*(?<version>\d+(?:\.\d+){1,3})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Task<FirefoxUpdateStatus> GetStatusAsync(
        bool forceRefresh = false,
        CancellationToken token = default)
    {
        lock (Gate)
        {
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < CacheTtl)
                return Task.FromResult(_cache);

            if (_flight != null && !_flight.IsCompleted)
                return _flight.WaitAsync(token);

            // LoadStatusAsync performs executable/version discovery before its first await.
            // Start the whole cold path on the thread pool so merely requesting the Task can never
            // block the UI thread during first Firefox page render.
            var created = Task.Run(async () => await LoadStatusAsync().ConfigureAwait(false));
            _flight = created;
            _ = ObserveStatusAsync(created);
            return created.WaitAsync(token);
        }
    }

    private static async Task ObserveStatusAsync(Task<FirefoxUpdateStatus> created)
    {
        try
        {
            FirefoxUpdateStatus result = await created.ConfigureAwait(false);
            lock (Gate)
            {
                _cache = result;
                _cacheAt = DateTime.UtcNow;
                if (ReferenceEquals(_flight, created)) _flight = null;
            }
        }
        catch
        {
            lock (Gate)
                if (ReferenceEquals(_flight, created)) _flight = null;
        }
    }

    private static async Task<FirefoxUpdateStatus> LoadStatusAsync()
    {
        string? exe = FindFirefoxExe();
        string installed = exe == null ? string.Empty : ReadInstalledVersion(exe);

        Task<(string Version, string Source)> productTask = GetProductDetailsVersionAsync();
        Task<(string Version, string Source)> redirectTask = GetDownloadRedirectVersionAsync();
        Task<(string Version, string Source)> wingetTask = GetWingetVersionAsync();

        var candidates = await Task.WhenAll(productTask, redirectTask, wingetTask).ConfigureAwait(false);
        (string Version, string Source) best = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x.Version))
            .OrderByDescending(x => ParseVersion(x.Version))
            .FirstOrDefault();

        string latest = best.Version ?? string.Empty;
        string latestSource = best.Source ?? string.Empty;
        if (string.IsNullOrWhiteSpace(latest))
            throw new InvalidOperationException("Nepodařilo se zjistit latest stable Firefox z oficiálních zdrojů.");

        bool installedFlag = exe != null && !string.IsNullOrWhiteSpace(installed);
        bool update = installedFlag && IsNewer(latest, installed);
        string message = !installedFlag
            ? "Firefox není nainstalovaný."
            : update
                ? $"Dostupná aktualizace {installed} → {latest}"
                : $"Firefox {installed} je aktuální.";

        LogService.Info(
            "firefox",
            $"FIREFOX_UPDATE_STATUS installed={installed} latest={latest} source={latestSource} update={update} " +
            $"product={candidates[0].Version} redirect={candidates[1].Version} winget={candidates[2].Version}");
        return new FirefoxUpdateStatus(installedFlag, installed, latest, update, exe, message, latestSource);
    }

    private static async Task<(string Version, string Source)> GetProductDetailsVersionAsync()
    {
        try
        {
            using var response = await Http.GetAsync(VersionUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            string version = doc.RootElement.TryGetProperty("LATEST_FIREFOX_VERSION", out var node)
                ? node.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            return (version, "Mozilla product-details");
        }
        catch (Exception ex)
        {
            LogService.Info("firefox", "Mozilla product-details detection failed: " + ex.Message);
            return (string.Empty, "Mozilla product-details");
        }
    }

    private static async Task<(string Version, string Source)> GetDownloadRedirectVersionAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, DownloadUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string uri = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
            Match match = ReleaseUriVersionRegex.Match(uri);
            return match.Success
                ? (match.Groups["version"].Value, "Mozilla stable redirect")
                : (string.Empty, "Mozilla stable redirect");
        }
        catch (Exception ex)
        {
            LogService.Info("firefox", "Mozilla stable redirect detection failed: " + ex.Message);
            return (string.Empty, "Mozilla stable redirect");
        }
    }

    private static async Task<(string Version, string Source)> GetWingetVersionAsync()
    {
        try
        {
            ProcessResult result = await UnelevatedProcessRunner.RunAsync(
                "winget.exe",
                "show --id Mozilla.Firefox --exact --accept-source-agreements --disable-interactivity",
                TimeSpan.FromSeconds(20),
                CancellationToken.None).ConfigureAwait(false);
            if (!result.Success) return (string.Empty, "winget");
            Match match = WingetVersionRegex.Match(result.StdOut ?? string.Empty);
            return match.Success
                ? (match.Groups["version"].Value, "winget")
                : (string.Empty, "winget");
        }
        catch (Exception ex)
        {
            LogService.Info("firefox", "Winget Firefox version detection failed: " + ex.Message);
            return (string.Empty, "winget");
        }
    }

    public static async Task<ProcessResult> UpdateAsync(CancellationToken token = default)
    {
        FirefoxUpdateStatus status = await GetStatusAsync(forceRefresh: true, token).ConfigureAwait(false);
        if (!status.Installed)
            throw new InvalidOperationException("Firefox není nainstalovaný.");

        var winget = await UnelevatedProcessRunner.RunAsync(
            "winget.exe",
            "upgrade --id Mozilla.Firefox --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
            TimeSpan.FromMinutes(20),
            token).ConfigureAwait(false);

        if (winget.Success)
        {
            Invalidate();
            return winget;
        }

        LogService.Info("firefox", $"FIREFOX_WINGET_UPDATE_FALLBACK exit={winget.ExitCode}");
        string installer = Path.Combine(Path.GetTempPath(), "SpankyhoOptimizer-Firefox-Latest.exe");
        using (var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(
                installer,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }

        var signature = await ProcessRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"$s=Get-AuthenticodeSignature -LiteralPath '" +
                installer.Replace("'", "''") +
                "'; if($s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -match 'Mozilla'){exit 0}else{Write-Error ($s.Status.ToString()+' '+$s.SignerCertificate.Subject);exit 7}\"",
            TimeSpan.FromSeconds(20),
            token).ConfigureAwait(false);
        if (!signature.Success)
            throw new InvalidOperationException("Stažený Firefox installer nemá platný Mozilla podpis: " +
                                                (signature.StdErr + signature.StdOut).Trim());

        ProcessResult installResult = await ProcessRunner
            .StartShellAndWaitAsync(installer, "/S", "runas", token)
            .ConfigureAwait(false);

        try { File.Delete(installer); }
        catch (IOException ex) { LogService.Info("firefox", "Firefox temp installer cleanup skipped: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { LogService.Info("firefox", "Firefox temp installer cleanup denied: " + ex.Message); }
        Invalidate();

        return new ProcessResult(
            installResult.ExitCode,
            installResult.ExitCode == 0 ? "Mozilla installer dokončen." : string.Empty,
            installResult.ExitCode == 0 ? string.Empty : $"Mozilla installer skončil kódem {installResult.ExitCode}.");
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _cache = null;
            _cacheAt = default;
        }
    }

    internal static string? FindFirefoxExe()
    {
        string[] paths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe")
        };
        foreach (string path in paths)
            if (File.Exists(path)) return path;
        return null;
    }

    private static string ReadInstalledVersion(string exe)
    {
        var info = FileVersionInfo.GetVersionInfo(exe);
        return (info.ProductVersion ?? info.FileVersion ?? string.Empty).Trim();
    }

    private static Version? ParseVersion(string value)
    {
        string clean = (value ?? string.Empty).Trim();
        int space = clean.IndexOf(' ');
        int dash = clean.IndexOf('-');
        int cut = space < 0 ? dash : dash < 0 ? space : Math.Min(space, dash);
        if (cut > 0) clean = clean[..cut];
        return Version.TryParse(clean, out var parsed) ? parsed : null;
    }

    internal static bool IsNewer(string candidate, string installed)
    {
        Version? c = ParseVersion(candidate);
        Version? i = ParseVersion(installed);
        return c != null && i != null && c > i;
    }
}
