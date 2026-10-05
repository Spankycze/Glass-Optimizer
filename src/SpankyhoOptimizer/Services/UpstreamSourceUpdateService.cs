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

public sealed record UpstreamSourceStatus(string Id, string Name, string Repository, string Commit, DateTime? LastCheckedUtc, DateTime? LastUpdatedUtc, bool UpdateDownloaded, string? Error);
public sealed record UpstreamUpdateCandidate(string Id, string Name, string Repository, string CurrentCommit, string LatestCommit);
internal sealed record UpstreamState(string Commit, DateTime LastCheckedUtc, DateTime? LastUpdatedUtc);

public static class UpstreamSourceUpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private const long MaxSnapshotDownloadBytes = 512L * 1024 * 1024;
    private const long MaxSnapshotExpandedBytes = 1536L * 1024 * 1024;
    private const int MaxSnapshotEntries = 100_000;
    private static readonly (string Id, string Name, string Repo)[] CoreSources =
    {
        ("winutil", "WinUtil", "ChrisTitusTech/winutil"),
        ("sophia", "Sophia Script", "farag2/Sophia-Script-for-Windows"),
        ("win11debloat", "Win11Debloat", "Raphire/Win11Debloat"),
        ("removewindowsai", "RemoveWindowsAI", "zoicware/RemoveWindowsAI"),
        ("winhance", "Winhance", "memstechtips/Winhance"),
        ("dnshop", "DNS-Hop", "center2055/DNS-Hop"),
        ("spicetify-marketplace", "Spicetify Marketplace", "spicetify/marketplace")
    };

    private static IReadOnlyList<(string Id, string Name, string Repo)> GetSources() => CoreSources;
    public static string Root => Path.Combine(LogService.Root, "UpstreamSources");
    private static string AutoCheckFile => Path.Combine(Root, "auto-check.txt");
    public static bool AutoCheckEnabled
    {
        get
        {
            try { return !File.Exists(AutoCheckFile) || !string.Equals(File.ReadAllText(AutoCheckFile).Trim(), "0", StringComparison.Ordinal); }
            catch { return true; }
        }
    }
    public static void SetAutoCheckEnabled(bool enabled)
    {
        Directory.CreateDirectory(Root);
        AtomicWriteAllText(AutoCheckFile, enabled ? "1" : "0");
    }

    public static async Task<IReadOnlyList<UpstreamUpdateCandidate>> ProbeUpdatesAsync(bool force, CancellationToken token = default)
    {
        Directory.CreateDirectory(Root);
        if (!AutoCheckEnabled) return Array.Empty<UpstreamUpdateCandidate>();

        var sources = GetSources()
            .Where(source =>
            {
                var state = LoadState(source.Id);
                return force || state == null || DateTime.UtcNow - state.LastCheckedUtc >= CheckInterval;
            })
            .ToArray();

        if (sources.Length == 0) return Array.Empty<UpstreamUpdateCandidate>();

        using var gate = new SemaphoreSlim(3, 3);
        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var state = LoadState(source.Id);
                string latest = await GetHeadCommitAsync(source.Repo, token).ConfigureAwait(false);
                if (state == null)
                {
                    // First automatic check establishes a baseline only. Do not annoy the user
                    // with a startup popup just because this source has never been seen before.
                    SaveState(source.Id, new UpstreamState(latest, DateTime.UtcNow, null));
                    return null;
                }

                if (string.Equals(state.Commit, latest, StringComparison.OrdinalIgnoreCase))
                {
                    SaveState(source.Id, state with { LastCheckedUtc = DateTime.UtcNow });
                    return null;
                }

                return new UpstreamUpdateCandidate(source.Id, source.Name, source.Repo, state.Commit, latest);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                LogService.Info("upstream", $"Silent update probe timed out: {source.Name}");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Error("upstream", $"Silent update probe failed: {source.Name}", ex);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var probed = await Task.WhenAll(tasks).ConfigureAwait(false);
        return probed.Where(x => x != null).Cast<UpstreamUpdateCandidate>().ToArray();
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
        Directory.CreateDirectory(Root);
        var sources = GetSources();
        var result = new List<UpstreamSourceStatus>(sources.Count);
        foreach (var source in sources)
        {
            var state = LoadState(source.Id);
            bool staged = Directory.Exists(Path.Combine(Root, source.Id, "current"));
            result.Add(new(source.Id, source.Name, source.Repo, state?.Commit ?? "-", state?.LastCheckedUtc, state?.LastUpdatedUtc, staged, null));
        }
        return result;
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
        string dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        var state = LoadState(id);
        if (!force && state != null && DateTime.UtcNow - state.LastCheckedUtc < CheckInterval)
            return new(id, name, repo, state.Commit, state.LastCheckedUtc, state.LastUpdatedUtc, Directory.Exists(Path.Combine(dir, "current")), null);
        try
        {
            string commit = await GetHeadCommitAsync(repo, token);
            bool hasSnapshot = Directory.Exists(Path.Combine(dir, "current"));
            bool changed = state == null || !hasSnapshot || !string.Equals(state.Commit, commit, StringComparison.OrdinalIgnoreCase);
            DateTime? updated = state?.LastUpdatedUtc;
            if (changed)
            {
                await DownloadSnapshotAsync(repo, commit, dir, token);
                updated = DateTime.UtcNow;
            }
            var next = new UpstreamState(commit, DateTime.UtcNow, updated);
            SaveState(id, next);
            return new(id, name, repo, commit, next.LastCheckedUtc, next.LastUpdatedUtc, Directory.Exists(Path.Combine(dir, "current")), null);
        }
        catch (Exception ex)
        {
            LogService.Error("upstream", $"Source check failed: {name}", ex);
            return new(id, name, repo, state?.Commit ?? "-", DateTime.UtcNow, state?.LastUpdatedUtc, Directory.Exists(Path.Combine(dir, "current")), ex.Message);
        }
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
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpankyhoOptimizer", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}

