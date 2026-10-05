using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record AppUpdateManifest(string Version, string Url, string Sha256, string? Changelog, string? Signature = null, string? SignatureAlgorithm = null);
public sealed record AppUpdateCheck(bool Configured, bool Available, string CurrentVersion, AppUpdateManifest? Manifest, string Message);

public static class AppUpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static string SourceFile => Path.Combine(LogService.Root, "update-source.txt");
    private static string UpdatesRoot => Path.Combine(LogService.Root, "updates");
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
        try { return File.Exists(SourceFile) ? File.ReadAllText(SourceFile).Trim() : string.Empty; }
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
            return new(false, false, CurrentVersion, null, "Není nastavený update manifest URL.");
        try
        {
            string json = source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? await File.ReadAllTextAsync(new Uri(source).LocalPath, token).ConfigureAwait(false)
                : await Client.GetStringAsync(source, token).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(json, OptimizerJsonContext.Default.AppUpdateManifest);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Url))
                throw new InvalidDataException("Update manifest neobsahuje Version/Url.");
            VerifyManifestSignature(manifest);
            bool available = Version.TryParse(manifest.Version, out var remote)
                && Version.TryParse(CurrentVersion, out var local) && remote > local;
            return new(true, available, CurrentVersion, manifest,
                available ? $"Dostupná verze {manifest.Version}." : "Používáš aktuální verzi.");
        }
        catch (Exception ex)
        {
            LogService.Error("update", "Update check failed", ex);
            return new(true, false, CurrentVersion, null, "Kontrola aktualizace selhala: " + ex.Message);
        }
    }
    internal static void VerifyManifestSignature(AppUpdateManifest manifest)
    {
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

    public static async Task<string> DownloadAsync(AppUpdateManifest manifest, CancellationToken token = default)
    {
        VerifyManifestSignature(manifest);
        if (string.IsNullOrWhiteSpace(manifest.Sha256)) throw new InvalidDataException("Update manifest neobsahuje povinný SHA-256 hash.");
        string dir = UpdatesRoot;
        Directory.CreateDirectory(dir);
        PruneDownloads();
        string path = Path.Combine(dir, $"Spankyho_Optimizer_{manifest.Version.Replace('.', '_')}_Setup.exe");
        string part = path + ".part";
        try
        {
            if (File.Exists(part)) File.Delete(part);
            await using var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream input = await OpenDownloadStreamAsync(manifest.Url, token).ConfigureAwait(false);
            byte[] buffer = new byte[131072];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            string sha = Convert.ToHexString(hash.GetHashAndReset());
            if (!sha.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 staženého installeru nesouhlasí s manifestem.");
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
            if (!CurrentVersion.StartsWith("15.4.", StringComparison.Ordinal))
                throw new InvalidOperationException("Runtime update version does not match current 15.4 build.");

            LogService.Info("update", $"UPDATE_RETENTION_SMOKE_OK version={CurrentVersion}");
        }
        finally
        {
            try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }
            try { if (File.Exists(oldPart)) File.Delete(oldPart); } catch { }
            try { if (File.Exists(keepExe)) File.Delete(keepExe); } catch { }
        }
    }

    private static async Task<Stream> OpenDownloadStreamAsync(string url, CancellationToken token)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return new FileStream(new Uri(url).LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return new ResponseOwnedStream(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), response);
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

    public static void LaunchInstallerForUpdate(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Update installer nebyl nalezen.", path);
        var info = new ProcessStartInfo(path, "--update-from " + Environment.ProcessId)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory
        };
        using var started = Process.Start(info);
        if (started == null)
            throw new InvalidOperationException("Update installer se nepodařilo spustit.");
    }
}


