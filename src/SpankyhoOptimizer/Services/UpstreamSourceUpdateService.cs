using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record UpstreamSourceStatus(string Id, string Name, string Repository, string Commit, DateTime? LastCheckedUtc, DateTime? LastUpdatedUtc, bool UpdateDownloaded, string? Error)
{
    public string LatestCommit { get; init; } = "-";
    public string IntegrationMode => UpstreamUpdatePolicy.IntegrationMode(Id);
    public bool NewerSourceAvailable => LatestCommit != "-" && !string.Equals(Commit, LatestCommit, StringComparison.OrdinalIgnoreCase);
}
public sealed record UpstreamUpdateCandidate(string Id, string Name, string Repository, string CurrentCommit, string LatestCommit);
internal sealed record UpstreamState(string Commit, DateTime LastCheckedUtc, DateTime? LastUpdatedUtc, string? LatestCommit = null, string? LastError = null);

public static class UpstreamSourceUpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private const long MaxSnapshotDownloadBytes = 512L * 1024 * 1024;
    private const long MaxSnapshotExpandedBytes = 1536L * 1024 * 1024;
    private const int MaxSnapshotEntries = 100_000;
    private static readonly (string Id, string Name, string Repo)[] CoreSources =
    {
        ("winutil", "WinUtil", "ChrisTitusTech/winutil"),
        ("sophia", "Sophia Script", "farag2/Sophia-Script-for-Windows"),
        ("win11debloat", "Win11Debloat", "Raphire/Win11Debloat"),
        ("winhance", "Winhance", "memstechtips/Winhance"),
        ("dnshop", "DNS-Hop", "center2055/DNS-Hop"),
        ("spicetify-marketplace", "Spicetify Marketplace", "spicetify/marketplace")
    };

    private static readonly IReadOnlyDictionary<string, SemaphoreSlim> SourceGates =
        CoreSources.ToDictionary(x => x.Id, _ => new SemaphoreSlim(1, 1), StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<(string Id, string Name, string Repo)> GetSources() => CoreSources;

    // The crash window between renaming current -> previous and next -> current
    // must be repaired BEFORE touching old snapshots or making a network call.
    private static void RecoverInterruptedSnapshotSwap(string directory)
    {
        string current = Path.Combine(directory, "current");
        string previous = Path.Combine(directory, "previous");
        if (Directory.Exists(current) || !Directory.Exists(previous))
            return;

        // Only a snapshot previously committed by this updater can be
        // restored. A failed/incomplete directory must never replace current.
        string marker = Path.Combine(previous, ".spanky-source-commit");
        string revision = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        if (revision.Length != 40 || revision.Any(c => !Uri.IsHexDigit(c)) ||
            !Directory.EnumerateFileSystemEntries(previous)
                .Any(entry => !string.Equals(entry, marker, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "Přerušená aktualizace obsahuje neověřenou předchozí kopii; data byla zachována.");

        // Atomic rename within the same snapshot parent. Do not delete the
        // previous backup before restoring the last known-good current state.
        Directory.Move(previous, current);
        LogService.Info("upstream", "Recovered interrupted source snapshot swap: " +
            Path.GetFileName(directory));
    }

    internal static string RunInterruptedSnapshotRecoverySmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-upstream-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string current = Path.Combine(root, "current");
            string previous = Path.Combine(root, "previous");
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, ".spanky-source-commit"), new string('a', 40));
            File.WriteAllText(Path.Combine(current, "settings.ps1"), "known-good-data");

            // Simulate process death immediately after current -> previous.
            Directory.Move(current, previous);
            RecoverInterruptedSnapshotSwap(root);
            if (!Directory.Exists(current) || Directory.Exists(previous) ||
                File.ReadAllText(Path.Combine(current, "settings.ps1")) != "known-good-data")
                throw new InvalidOperationException(
                    "Interrupted upstream source snapshot swap did not restore known-good current.");

            // Idempotent recovery; a valid current snapshot must never be
            // replaced with an older previous revision.
            Directory.CreateDirectory(previous);
            File.WriteAllText(Path.Combine(previous, ".spanky-source-commit"), new string('b', 40));
            File.WriteAllText(Path.Combine(previous, "settings.ps1"), "older-data");
            RecoverInterruptedSnapshotSwap(root);
            if (File.ReadAllText(Path.Combine(current, "settings.ps1")) != "known-good-data" ||
                File.ReadAllText(Path.Combine(previous, "settings.ps1")) != "older-data")
                throw new InvalidOperationException("Recovery overwrote an existing current snapshot.");

            Directory.Delete(current, recursive: true);
            Directory.Delete(previous, recursive: true);
            Directory.CreateDirectory(previous);
            File.WriteAllText(Path.Combine(previous, "settings.ps1"), "unverified-data");
            bool invalidRejected = false;
            try { RecoverInterruptedSnapshotSwap(root); }
            catch (InvalidDataException) { invalidRejected = true; }
            if (!invalidRejected || Directory.Exists(current) || !Directory.Exists(previous))
                throw new InvalidOperationException("Unverified recovery snapshot was promoted or deleted.");
            return "UPSTREAM_SNAPSHOT_RECOVERY_SMOKE_OK restoredCurrent=true " +
                   "existingCurrentPreserved=true invalidPreviousPreserved=true networkCalls=0";
        }
        finally { TryDeleteDirectory(root); }
    }

    private static bool HasVerifiedSnapshot(string id, string? commit)
    {
        if (string.IsNullOrEmpty(commit) || commit == "-") return false;
        try
        {
            string marker = Path.Combine(Root, id, "current", ".spanky-source-commit");
            return File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), commit, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static UpstreamSourceStatus Status(string id, string name, string repo, UpstreamState? state, string? error = null) =>
        new(id, name, repo, state?.Commit ?? "-", state?.LastCheckedUtc, state?.LastUpdatedUtc,
            HasVerifiedSnapshot(id, state?.Commit), error ?? state?.LastError)
        { LatestCommit = state?.LatestCommit ?? "-" };
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpankyhoOptimizer", "UpstreamSources");
    private static string LegacyRoot => Path.Combine(LogService.Root, "UpstreamSources");
    private static string AutoCheckFile => Path.Combine(Root, "auto-check.txt");
    public static bool AutoCheckEnabled
    {
        get
        {
            string current = AutoCheckFile;
            if (File.Exists(current)) return ReadAutoCheckAt(current);
            return ReadAutoCheckAt(Path.Combine(LegacyRoot, "auto-check.txt"));
        }
    }
    public static void SetAutoCheckEnabled(bool enabled)
    {
        Directory.CreateDirectory(Root);
        AtomicWriteAllText(AutoCheckFile, enabled ? "1" : "0");
    }
    private static bool ReadAutoCheckAt(string path)
    {
        try { return !File.Exists(path) || !string.Equals(File.ReadAllText(path).Trim(), "0", StringComparison.Ordinal); }
        catch { return true; }
    }

    // Exercise the same atomic write/read-back backend on a disposable path.
    // Release smoke must NEVER rewrite the user's real auto-check.txt.
    internal static string RunIsolatedPersistenceSmoke()
    {
        string directory = Path.Combine(Path.GetTempPath(), "spanky-upstream-smoke-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "auto-check.txt");
        try
        {
            Directory.CreateDirectory(directory);
            AtomicWriteAllText(path, "0");
            if (ReadAutoCheckAt(path) || File.ReadAllText(path).Trim() != "0")
                throw new InvalidOperationException("Upstream auto-check isolated disable persistence failed.");
            AtomicWriteAllText(path, "1");
            if (!ReadAutoCheckAt(path) || File.ReadAllText(path).Trim() != "1")
                throw new InvalidOperationException("Upstream auto-check isolated enable persistence failed.");
            if (Directory.GetFiles(directory, "*.tmp").Length != 0)
                throw new InvalidOperationException("Upstream auto-check atomic temporary files leaked.");
            return "UPSTREAM_AUTOCHECK_PERSISTENCE_ISOLATED_SMOKE_OK writeReadback=true tempLeak=0 actualUserSettingsTouched=false";
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    public static async Task<IReadOnlyList<UpstreamUpdateCandidate>> ProbeUpdatesAsync(bool force, CancellationToken token = default)
    {
        if (!force && !AutoCheckEnabled) return Array.Empty<UpstreamUpdateCandidate>();
        Directory.CreateDirectory(Root);
        using var concurrency = new SemaphoreSlim(3, 3);
        var tasks = GetSources().Select(async source =>
        {
            await concurrency.WaitAsync(token).ConfigureAwait(false);
            var sourceGate = SourceGates[source.Id]; bool acquired = false;
            try
            {
                await sourceGate.WaitAsync(token).ConfigureAwait(false); acquired = true;
                RecoverInterruptedSnapshotSwap(Path.Combine(Root, source.Id));
                var state = LoadState(source.Id);
                bool snapshot = HasVerifiedSnapshot(source.Id, state?.Commit);
                if (!UpstreamUpdatePolicy.ShouldProbe(force, AutoCheckEnabled, snapshot, state?.LastCheckedUtc, DateTime.UtcNow))
                    return null;
                string latest = await GetHeadCommitAsync(source.Repo, token).ConfigureAwait(false);
                // Observing HEAD is not downloading or activating it. Keep these revisions separate.
                var observed = state == null
                    ? new UpstreamState("-", DateTime.UtcNow, null, latest)
                    : state with { LastCheckedUtc = DateTime.UtcNow, LatestCommit = latest };
                SaveState(source.Id, observed);
                if (snapshot && string.Equals(observed.Commit, latest, StringComparison.OrdinalIgnoreCase) && observed.LastError == null)
                    return null;
                return new UpstreamUpdateCandidate(source.Id, source.Name, source.Repo, observed.Commit, latest);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { LogService.Info("upstream", "Source probe timed out: " + source.Name); return null; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { LogService.Error("upstream", "Source probe failed: " + source.Name, ex); return null; }
            finally { if (acquired) sourceGate.Release(); concurrency.Release(); }
        }).ToArray();
        var result = await Task.WhenAll(tasks).ConfigureAwait(false);
        return result.Where(x => x != null).Cast<UpstreamUpdateCandidate>().ToArray();
    }

    public static async Task<IReadOnlyList<UpstreamSourceStatus>> UpdateSourcesAsync(
        IEnumerable<string> ids,
        CancellationToken token = default,
        Action<int, int, string>? progress = null)
    {
        var wanted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = GetSources().Where(source => wanted.Contains(source.Id)).ToArray();
        var result = new List<UpstreamSourceStatus>(selected.Length);

        for (int i = 0; i < selected.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var source = selected[i];
            progress?.Invoke(i + 1, selected.Length, source.Name);
            result.Add(await CheckOneAsync(source.Id, source.Name, source.Repo, force: true, token).ConfigureAwait(false));
        }

        return result;
    }

    public static async Task<IReadOnlyList<UpstreamSourceStatus>> CheckAllAsync(bool force, CancellationToken token = default)
    {
        Directory.CreateDirectory(Root);
        if (!force && !AutoCheckEnabled) return ReadCachedStatuses();
        var sources = GetSources();
        var result = new List<UpstreamSourceStatus>(sources.Count);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            result.Add(await CheckOneAsync(source.Id, source.Name, source.Repo, force, token));
        }
        return result;
    }

    public static IReadOnlyList<UpstreamSourceStatus> ReadCachedStatuses()
    {
        return GetSources().Select(source => Status(source.Id, source.Name, source.Repo, LoadState(source.Id))).ToArray();
    }

    public static string GetDiffSummary(string id)
    {
        try
        {
            string dir = Path.Combine(Root, id);
            string current = Path.Combine(dir, "current");
            string previous = Path.Combine(dir, "previous");
            if (!Directory.Exists(current)) return "bez snapshotu";
            if (!Directory.Exists(previous)) return "první snapshot";
            var a = Directory.EnumerateFiles(previous, "*", SearchOption.AllDirectories)
                .Select(x => Path.GetRelativePath(previous, x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var b = Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories)
                .Select(x => Path.GetRelativePath(current, x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = b.Count(x => !a.Contains(x));
            int removed = a.Count(x => !b.Contains(x));
            int changed = b.Where(a.Contains).Count(rel =>
            {
                string p1 = Path.Combine(previous, rel);
                string p2 = Path.Combine(current, rel);
                var f1 = new FileInfo(p1);
                var f2 = new FileInfo(p2);
                if (f1.Length != f2.Length) return true;
                return !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(p1)), SHA256.HashData(File.ReadAllBytes(p2)));
            });
            return $"+{added} / -{removed} / ~{changed} souborů";
        }
        catch (Exception ex) { return "diff chyba: " + ex.Message; }
    }

    private static async Task<UpstreamSourceStatus> CheckOneAsync(string id, string name, string repo, bool force, CancellationToken token)
    {
        var gate = SourceGates[id];
        await gate.WaitAsync(token).ConfigureAwait(false);
        UpstreamState? state = null;
        try
        {
            string dir = Path.Combine(Root, id); Directory.CreateDirectory(dir);
            RecoverInterruptedSnapshotSwap(dir);
            state = LoadState(id);
            bool snapshot = HasVerifiedSnapshot(id, state?.Commit);
            if (!UpstreamUpdatePolicy.ShouldProbe(force, AutoCheckEnabled, snapshot, state?.LastCheckedUtc, DateTime.UtcNow))
                return Status(id, name, repo, state);
            string latest = await GetHeadCommitAsync(repo, token).ConfigureAwait(false);
            state = state == null ? new UpstreamState("-", DateTime.UtcNow, null, latest)
                : state with { LastCheckedUtc = DateTime.UtcNow, LatestCommit = latest };
            SaveState(id, state);
            bool changed = !snapshot || !string.Equals(state.Commit, latest, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                await DownloadSnapshotAsync(repo, latest, dir, token).ConfigureAwait(false);
                state = state with { Commit = latest, LastUpdatedUtc = DateTime.UtcNow };
            }
            state = state with { LastError = null };
            SaveState(id, state); // Snapshot truth survives even if a consumer rejects its new schema.
            token.ThrowIfCancellationRequested();
            if (id == "winutil")
                WinUtilServicePolicy.Activate(Path.Combine(dir, "current"), latest);
            return Status(id, name, repo, state);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Error("upstream", "Source/adapter update failed: " + name, ex);
            if (state != null)
            {
                state = state with { LastError = ex.Message };
                SaveState(id, state);
            }
            return Status(id, name, repo, state, ex.Message);
        }
        finally { gate.Release(); }
    }

    private static async Task<string> GetHeadCommitAsync(string repo, CancellationToken token)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestCts.CancelAfter(TimeSpan.FromSeconds(8));

        using var response = await Http.GetAsync(
            $"https://api.github.com/repos/{repo}/commits?per_page=1",
            HttpCompletionOption.ResponseHeadersRead,
            requestCts.Token).ConfigureAwait(false);

        if ((int)response.StatusCode == 403 &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.FirstOrDefault() == "0")
            throw new HttpRequestException("GitHub API rate limit byl vyčerpán.");

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(requestCts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidDataException("GitHub nevrátil commit.");
        return root[0].GetProperty("sha").GetString()
            ?? throw new InvalidDataException("Chybí SHA commitu.");
    }

    private static async Task DownloadSnapshotAsync(string repo, string commit, string dir, CancellationToken token)
    {
        string zip = Path.Combine(dir, "snapshot.zip");
        string staging = Path.Combine(dir, "staging");
        string next = Path.Combine(dir, "next");
        string current = Path.Combine(dir, "current");
        string previous = Path.Combine(dir, "previous");

        try
        {
            RecoverInterruptedSnapshotSwap(dir);
            TryDeleteFile(zip);
            TryDeleteDirectory(staging);
            TryDeleteDirectory(next);

            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            downloadCts.CancelAfter(TimeSpan.FromSeconds(90));
            using (var response = await Http.GetAsync(
                $"https://api.github.com/repos/{repo}/zipball/{commit}",
                HttpCompletionOption.ResponseHeadersRead,
                downloadCts.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength &&
                    contentLength > MaxSnapshotDownloadBytes)
                    throw new InvalidDataException($"GitHub snapshot je příliš velký ({contentLength / 1024 / 1024} MB).");

                await using var input = await response.Content.ReadAsStreamAsync(downloadCts.Token).ConfigureAwait(false);
                await using var output = File.Create(zip);
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, downloadCts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > MaxSnapshotDownloadBytes)
                        throw new InvalidDataException("GitHub snapshot překročil maximální povolenou velikost.");
                    await output.WriteAsync(buffer.AsMemory(0, read), downloadCts.Token).ConfigureAwait(false);
                }
            }

            ValidateSnapshotZip(zip);
            Directory.CreateDirectory(staging);
            await ZipFile.ExtractToDirectoryAsync(zip, staging, overwriteFiles: true, token).ConfigureAwait(false);
            string[] roots = Directory.GetDirectories(staging);
            string extracted = roots.Length == 1 && !Directory.EnumerateFiles(staging).Any()
                ? roots[0]
                : staging;

            if (string.Equals(extracted, staging, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(staging, next);
            }
            else
            {
                Directory.Move(extracted, next);
                TryDeleteDirectory(staging);
            }

            if (!Directory.EnumerateFileSystemEntries(next).Any())
                throw new InvalidDataException("Stažený GitHub snapshot je prázdný.");

            token.ThrowIfCancellationRequested();
            AtomicWriteAllText(Path.Combine(next, ".spanky-source-commit"), commit);
            // Prepare everything before touching the known-good snapshot. If the final swap fails,
            // restore the old current directory so a cancelled/failed update cannot corrupt cache state.
            TryDeleteDirectory(previous);
            bool currentMoved = false;
            try
            {
                if (Directory.Exists(current))
                {
                    Directory.Move(current, previous);
                    currentMoved = true;
                }

                Directory.Move(next, current);
            }
            catch
            {
                if (!Directory.Exists(current) && currentMoved && Directory.Exists(previous))
                    Directory.Move(previous, current);
                throw;
            }
        }
        finally
        {
            TryDeleteFile(zip);
            TryDeleteDirectory(staging);
            TryDeleteDirectory(next);
        }
    }

    private static void ValidateSnapshotZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("GitHub snapshot ZIP je prázdný.");
        if (archive.Entries.Count > MaxSnapshotEntries)
            throw new InvalidDataException($"GitHub snapshot obsahuje příliš mnoho položek ({archive.Entries.Count}).");

        long expandedBytes = 0;
        int files = 0;
        foreach (var entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
                throw new InvalidDataException("GitHub snapshot obsahuje neplatnou cestu.");

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaxSnapshotExpandedBytes)
                throw new InvalidDataException("Rozbalený GitHub snapshot překročil bezpečný velikostní limit.");
            if (!string.IsNullOrEmpty(entry.Name))
                files++;
        }

        if (files == 0)
            throw new InvalidDataException("GitHub snapshot neobsahuje žádné soubory.");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static UpstreamState? LoadState(string id)
    {
        try
        {
            string file = Path.Combine(Root, id, "state.json");
            if (!File.Exists(file)) file = Path.Combine(LegacyRoot, id, "state.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<UpstreamState>(File.ReadAllText(file)) : null;
        }
        catch { return null; }
    }
    private static void SaveState(string id, UpstreamState state)
    {
        string dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        AtomicWriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(state, IndentedJson));
    }
    private static void AtomicWriteAllText(string path, string content)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpankyhoOptimizer", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
