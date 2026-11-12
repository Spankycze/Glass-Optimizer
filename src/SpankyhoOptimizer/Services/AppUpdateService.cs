using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record AppUpdateManifest(string Version, string Url, string Sha256, string? Changelog, string? Signature = null, string? SignatureAlgorithm = null);
public sealed record AppUpdateCheck(bool Configured, bool Available, string CurrentVersion, AppUpdateManifest? Manifest, string Message);
public sealed record AppUpdateDownloadProgress(long Received, long? Total)
{
    public int Percent => Total.GetValueOrDefault() > 0
        ? (int)Math.Clamp(Received * 100L / Total!.Value, 0, 100)
        : -1;
}

public static class AppUpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const string GithubApiUrl = "https://api.github.com/repos/Spankycze/Glass-Optimizer/releases/latest";
    private const string GithubInstallerName = "Glass_Optimizer_Setup_Compact.exe";
    private static string SourceFile => Path.Combine(LogService.Root, "update-source.txt");
    private static string UpdatesRoot => Path.Combine(LogService.Root, "updates");
    private static string LocalBridgeManifestPath => Path.Combine(LogService.Root, "local-update-manifest.json");
    private static string LocalBridgeRoot => Path.Combine(LogService.Root, "release-bridge");
    private const string UpdatePublicKey = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAvOufTXMQhxrnoft4U+ocIhAx6QtKmSTr/+Gt16PTN3u+ErKqBu3zbrPfczix3eofG4rya1NkajeNHN95AuNhihUZ9Ei1eLn3wRQL7ys8iY5GcvUmp5R8GFF2Q779ZnE+X6nZZq+UTv+EHLL8l7zEZz+xNCO51VH3ks7XZJIo3gu+HAtjzWhXKb5zcRk+WgYcfR5y/Amo+xC4ZG1+PExZDdOWqZUxoi3soAo5yro8cMOfdSgoLkeHOhidC512HG1pMhmxi3Yyre0cgO6LjyLTq2wbD+Zx/b+ZEVQnlwPuRIL24VMW/Z+/zcltQGv1t/ltFV8Aze6jEMaranlMZ5RTmO7FAa+MBCiYY5hmZzFWaAOrxWkRxCmnFxjm/xBbhYl7f2MPtiiQmS351I2VZea/wimM0LVcgQucc1E3SDwOvoeTy9zLNaW0WQN149FLiBCGLand1BN/KJXwcqKIB+DNE15R+dVq60Gdjt8j0LGWxEMCqZV4XtFsqkE6l5aASiIRAgMBAAE=";
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(AppUpdateService).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static string GetSource()
    {
        try
        {
            if (File.Exists(SourceFile))
            {
                string configured = File.ReadAllText(SourceFile).Trim();
                if (!string.IsNullOrWhiteSpace(configured)) return configured;
            }
            return File.Exists(LocalBridgeManifestPath)
                ? new Uri(LocalBridgeManifestPath).AbsoluteUri
                : string.Empty;
        }
        catch { return string.Empty; }
    }
    public static void SetSource(string? source)
    {
        Directory.CreateDirectory(LogService.Root);
        AtomicFileService.WriteText(SourceFile, (source ?? string.Empty).Trim());
    }

    public static async Task<AppUpdateCheck> CheckAsync(CancellationToken token = default)
    {
        string source = GetSource();
        if (string.IsNullOrWhiteSpace(source))
            return await CheckOfficialGithubAsync(token).ConfigureAwait(false);
        if (IsLocalBridgeSource(source))
            return await CheckOfficialGithubAsync(token).ConfigureAwait(false);
        try
        {
            string json = source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? await File.ReadAllTextAsync(new Uri(source).LocalPath, token).ConfigureAwait(false)
                : await Client.GetStringAsync(source, token).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(json, OptimizerJsonContext.Default.AppUpdateManifest);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Url))
                throw new InvalidDataException("Update manifest neobsahuje Version/Url.");
            if (IsLocalBridgeSource(source))
            {
                if (!IsLocalBridgeManifest(manifest))
                    throw new InvalidDataException("Lokální update bridge manifest není důvěryhodný.");
            }
            else
            {
                VerifyManifestSignature(manifest);
            }
            if (!Version.TryParse(manifest.Version, out var remote) ||
                !Version.TryParse(CurrentVersion, out var local))
                throw new InvalidDataException("Update manifest obsahuje neplatné číslo verze.");
            bool available = IsNewerVersion(manifest.Version, CurrentVersion);
            return new(true, available, CurrentVersion, manifest,
                available ? $"Dostupná verze {RuntimeVersionInfo.PublicVersion(manifest.Version)}." : "Používáš aktuální verzi.");
        }
        catch (Exception ex)
        {
            LogService.Error("update", "Update check failed", ex);
            return new(true, false, CurrentVersion, null, "Kontrola aktualizace selhala: " + ex.Message);
        }
    }

    public static async Task<AppUpdateCheck> CheckOfficialGithubAsync(CancellationToken token = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubApiUrl);
            request.Headers.UserAgent.ParseAdd("Glass-Optimizer-Updater/1.0");
            using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            JsonElement root = release.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            string version = tag.TrimStart('v', 'V');
            if (!Version.TryParse(version, out _)) throw new InvalidDataException("GitHub release má neplatnou verzi.");

            string? installerUrl = null;
            string? shaUrl = null;
            foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? string.Empty;
                string url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                if (name.Equals(GithubInstallerName, StringComparison.OrdinalIgnoreCase)) installerUrl = url;
                if (name.EndsWith("-SHA256.txt", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
            }
            if (string.IsNullOrWhiteSpace(installerUrl) || string.IsNullOrWhiteSpace(shaUrl) ||
                !installerUrl.StartsWith("https://github.com/Spankycze/Glass-Optimizer/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub release neobsahuje installer a SHA-256 asset.");

            string sha = await ReadGithubShaAsync(shaUrl, GithubInstallerName, token).ConfigureAwait(false);
            var manifest = new AppUpdateManifest(
                version,
                installerUrl,
                sha,
                root.TryGetProperty("body", out JsonElement body) ? body.GetString() : null,
                shaUrl,
                "GITHUB-SHA256");
            bool available = IsNewerVersion(manifest.Version, CurrentVersion);
            return new(true, available, CurrentVersion, manifest,
                available ? $"Dostupná oficiální verze {RuntimeVersionInfo.PublicVersion(version)} z GitHubu." : "Používáš aktuální verzi.");
        }
        catch (Exception ex)
        {
            LogService.Error("update", "GitHub update check failed", ex);
            return new(false, false, CurrentVersion, null, "Kontrola GitHub aktualizace selhala: " + ex.Message);
        }
    }
    internal static bool IsNewerVersion(string? remoteVersion, string? installedVersion) =>
        Version.TryParse(remoteVersion, out var remote) &&
        Version.TryParse(installedVersion, out var installed) && remote > installed;

    private static bool IsLocalBridgeSource(string source)
    {
        try
        {
            if (!source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return false;
            string path = Path.GetFullPath(new Uri(source).LocalPath);
            return string.Equals(path, Path.GetFullPath(LocalBridgeManifestPath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsLocalBridgeManifest(AppUpdateManifest manifest)
    {
        try
        {
            if (!string.Equals(manifest.SignatureAlgorithm, "LOCAL-SHA256", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!manifest.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return false;

            string path = Path.GetFullPath(new Uri(manifest.Url).LocalPath);
            string root = Path.GetFullPath(LocalBridgeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }
        catch { return false; }
    }

    internal static void VerifyManifestSignature(AppUpdateManifest manifest)
    {
        if (string.Equals(manifest.SignatureAlgorithm, "GITHUB-SHA256", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(manifest.Signature) ||
                !manifest.Signature.StartsWith("https://github.com/Spankycze/Glass-Optimizer/releases/download/", StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(manifest.Sha256 ?? string.Empty, "^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("GitHub update manifest nemá platný SHA-256 asset.");
            return;
        }
        if (!string.Equals(manifest.SignatureAlgorithm, "RSA-PSS-SHA256", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.Signature))
            throw new InvalidDataException("Update manifest nemá platný kryptografický podpis.");

        string payload = string.Join("\n", new[]
        {
            manifest.Version.Trim(), manifest.Url.Trim(), manifest.Sha256.Trim().ToUpperInvariant(), manifest.Changelog ?? string.Empty
        });
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePublicKey), out _);
        bool ok = rsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(manifest.Signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (!ok) throw new InvalidDataException("Kryptografický podpis update manifestu nesouhlasí.");
    }

    public static Task<string> DownloadAsync(AppUpdateManifest manifest, CancellationToken token = default) =>
        DownloadAsync(manifest, null, token);

    public static async Task<string> DownloadAsync(
        AppUpdateManifest manifest,
        IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken token = default)
    {
        if (!IsLocalBridgeManifest(manifest) && !IsGithubManifest(manifest))
            VerifyManifestSignature(manifest);
        if (IsGithubManifest(manifest))
            manifest = manifest with { Sha256 = await ReadGithubShaAsync(manifest.Signature!, GithubInstallerName, token).ConfigureAwait(false) };
        if (string.IsNullOrWhiteSpace(manifest.Sha256)) throw new InvalidDataException("Update manifest neobsahuje povinný SHA-256 hash.");
        // Validate the filename before touching the update cache at all.
        // DownloadAsync can be called independently of CheckAsync.
        string path = InstallerDownloadPath(manifest.Version);
        Directory.CreateDirectory(UpdatesRoot);
        PruneDownloads();
        string part = path + ".part";
        try
        {
            if (File.Exists(part)) File.Delete(part);
            string sha;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var opened = await OpenDownloadStreamAsync(manifest.Url, token).ConfigureAwait(false);
                await using (Stream input = opened.Stream)
                await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    long? total = opened.Total;
                    long received = 0;
                    progress?.Report(new AppUpdateDownloadProgress(0, total));
                    byte[] buffer = new byte[131072];
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                        if (read == 0) break;
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        received += read;
                        progress?.Report(new AppUpdateDownloadProgress(received, total));
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    sha = Convert.ToHexString(hash.GetHashAndReset());
                }
            }

            if (!sha.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 staženého installeru nesouhlasí s manifestem.");

            // Both input and output handles are closed before promoting .part on Windows.
            File.Move(part, path, true);
            PruneDownloads(path);
            return path;
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
    }

    private static bool IsGithubManifest(AppUpdateManifest manifest) =>
        string.Equals(manifest.SignatureAlgorithm, "GITHUB-SHA256", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadGithubShaAsync(string url, string fileName, CancellationToken token)
    {
        if (!url.StartsWith("https://github.com/Spankycze/Glass-Optimizer/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA asset není z oficiálního GitHub repozitáře.");
        string text = await Client.GetStringAsync(url, token).ConfigureAwait(false);
        Match match = Regex.Match(text, $@"(?im)^\s*([0-9a-f]{{64}})\s+[* ]?{Regex.Escape(fileName)}\s*$");
        if (!match.Success) throw new InvalidDataException("GitHub SHA asset neobsahuje hash očekávaného installeru.");
        return match.Groups[1].Value.ToUpperInvariant();
    }

    // Recheck the manifest version at the actual filesystem write boundary.
    // A caller may invoke DownloadAsync without going through CheckAsync.
    private static string InstallerDownloadPath(string version)
    {
        // Version.TryParse alone is not enough for a filename policy: do not
        // accept whitespace, path separators, or arbitrary manifest text.
        // Numeric dot-separated versions preserve the existing installer name.
        if (string.IsNullOrEmpty(version) || version.Length > 40 ||
            !Version.TryParse(version, out _) ||
            version.Any(ch => ch != '.' && (ch < '0' || ch > '9')))
            throw new InvalidDataException("Update manifest obsahuje neplatnou verzi instalátoru.");
        return Path.Combine(UpdatesRoot, $"Spankyho_Optimizer_{version.Replace('.', '_')}_Setup.exe");
    }

    internal static string RunDownloadPathSafetySmoke()
    {
        string good = InstallerDownloadPath("16.114.58");
        string expected = Path.Combine(UpdatesRoot, "Spankyho_Optimizer_16_114_58_Setup.exe");
        if (!string.Equals(good, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Valid update filename changed.");

        string[] invalid =
        {
            "../outside", @"..\\..\\outside", "16.114.58/../outside",
            "16.114.58.exe", "16..114", "", "16.114.58 ", "1.2.3.4.5"
        };
        foreach (string version in invalid)
        {
            bool rejected = false;
            try { _ = InstallerDownloadPath(version); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException(
                    $"Unsafe update version was accepted: '{version}'.");
        }
        return "UPDATE_DOWNLOAD_PATH_SMOKE_OK accepted=1 rejected=8 writes=0";
    }

    private static void PruneDownloads(string? preservePath = null)
    {
        try
        {
            if (!Directory.Exists(UpdatesRoot)) return;
            DateTime stalePartCutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (string part in Directory.EnumerateFiles(UpdatesRoot, "*.part"))
            {
                try
                {
                    var info = new FileInfo(part);
                    if (info.LastWriteTimeUtc < stalePartCutoff) info.Delete();
                }
                catch { }
            }

            DateTime installerCutoff = DateTime.UtcNow - TimeSpan.FromDays(14);
            var installers = Directory.EnumerateFiles(UpdatesRoot, "*.exe")
                .Select(path => new FileInfo(path))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToArray();

            for (int i = 0; i < installers.Length; i++)
            {
                string full = installers[i].FullName;
                if (!string.IsNullOrWhiteSpace(preservePath) &&
                    string.Equals(full, preservePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool overCount = i >= 2;
                bool expired = installers[i].LastWriteTimeUtc < installerCutoff;
                if (!overCount && !expired) continue;
                try { installers[i].Delete(); } catch { }
            }
        }
        catch (Exception ex) { LogService.Error("update", "Update cache cleanup failed", ex); }
    }

    internal static void RunRetentionSmoke()
    {
        LogService.Info("update", RunDownloadPathSafetySmoke());
        Directory.CreateDirectory(UpdatesRoot);
        string id = Guid.NewGuid().ToString("N");
        string oldExe = Path.Combine(UpdatesRoot, $"smoke-old-{id}.exe");
        string oldPart = Path.Combine(UpdatesRoot, $"smoke-old-{id}.part");
        string keepExe = Path.Combine(UpdatesRoot, $"smoke-keep-{id}.exe");
        try
        {
            File.WriteAllText(oldExe, "old");
            File.WriteAllText(oldPart, "part");
            File.WriteAllText(keepExe, "keep");
            File.SetLastWriteTimeUtc(oldExe, DateTime.UtcNow - TimeSpan.FromDays(30));
            File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow - TimeSpan.FromDays(2));
            File.SetLastWriteTimeUtc(keepExe, DateTime.UtcNow);

            PruneDownloads(keepExe);

            if (File.Exists(oldExe)) throw new InvalidOperationException("Expired update installer was not pruned.");
            if (File.Exists(oldPart)) throw new InvalidOperationException("Expired partial update was not pruned.");
            if (!File.Exists(keepExe)) throw new InvalidOperationException("Preserved update installer was pruned.");
            Version? assemblyVersion = typeof(AppUpdateService).Assembly.GetName().Version;
            if (assemblyVersion == null || CurrentVersion != $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}")
                throw new InvalidOperationException("Runtime update version does not match assembly version.");
            // Public marketing version is deliberately two-part; full
            // assembly identity stays monotonic for updater/downgrade checks.
            if (RuntimeVersionInfo.PublicVersion(typeof(AppUpdateService).Assembly) !=
                    $"{assemblyVersion.Major}.{assemblyVersion.Minor}" ||
                RuntimeVersionInfo.PublicVersion(CurrentVersion) !=
                    $"{assemblyVersion.Major}.{assemblyVersion.Minor}" ||
                RuntimeVersionInfo.PublicVersion("16.114.263") != "16.114" ||
                !IsNewerVersion("16.115.0", "16.114.262"))
                throw new InvalidOperationException("Public/internal version separation regression.");
            if (!IsNewerVersion("16.100.0", "15.4.1493") ||
                !IsNewerVersion("16.100.1", "16.100.0") ||
                IsNewerVersion("16.100.0", "16.100.0") ||
                IsNewerVersion("15.4.1493", "16.100.0") ||
                IsNewerVersion("invalid", "16.100.0"))
                throw new InvalidOperationException("Update version ordering regression.");

            LogService.Info("update", $"UPDATE_RETENTION_SMOKE_OK version={CurrentVersion}");
        }
        finally
        {
            try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }
            try { if (File.Exists(oldPart)) File.Delete(oldPart); } catch { }
            try { if (File.Exists(keepExe)) File.Delete(keepExe); } catch { }
        }
    }

    private static async Task<(Stream Stream, long? Total)> OpenDownloadStreamAsync(string url, CancellationToken token)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string path = new Uri(url).LocalPath;
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return (file, file.Length);
        }

        HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        return (new ResponseOwnedStream(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), response), total);
    }

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage owner) : Stream
    {
        private int _disposed;
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                owner.Dispose();
            }
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    public static void LaunchInstallerForUpdate(string path, string? expectedSha256 = null, bool silent = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Update installer nebyl nalezen.", path);
        string fullPath = Path.GetFullPath(path);
        string trustedRoot = Path.GetFullPath(UpdatesRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(trustedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Update installer není v důvěryhodné složce aktualizací.");
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            using var stream = File.OpenRead(fullPath);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "SHA-256 staženého installeru se před spuštěním změnil; aktualizace byla zablokována.");
        }
        string arguments = silent
            ? "--silent-install --update-from " + Environment.ProcessId
            : "--update-from " + Environment.ProcessId;
        var info = new ProcessStartInfo(fullPath, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory
        };
        using var started = Process.Start(info);
        if (started == null)
            throw new InvalidOperationException("Update installer se nepodařilo spustit.");
    }
}
