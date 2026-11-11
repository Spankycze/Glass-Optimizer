using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record TweakBatchItem(string Id, string Name, string Section, bool Previous, bool Applied, bool Restored = false);
public sealed record TweakBatchRecord(string BatchId, DateTimeOffset Time, string Area, IReadOnlyList<TweakBatchItem> Items, bool Restored = false);
public sealed record TweakBatchOverviewSnapshot(TweakBatchRecord[] Recent, IReadOnlyDictionary<string, bool> ExpectedStates);

public static class TweakBatchHistoryService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<TweakBatchRecord>? Cache;
    private static DateTime CacheWriteUtc;
    // Derived Overview state is immutable until the on-disk history changes.
    // Avoid repeatedly sorting/merging up to 120 batches on every navigation.
    private static TweakBatchOverviewSnapshot? OverviewCached;
    private static DateTime OverviewCachedWriteUtc;
    private static int OverviewCachedRecent;
    private static int OverviewCachedBatches;
    private static string PathName => Path.Combine(LogService.Root, "tweak-batches.json");

    public static async Task WarmAsync(CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try { _ = await ReadInternalAsync(token); }
        finally { Gate.Release(); }
    }

    public static async Task AddAsync(string area, IEnumerable<TweakBatchItem> items, CancellationToken token = default)
    {
        var list = items.ToArray();
        if (list.Length == 0) return;
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(LogService.Root);
            var all = await ReadInternalAsync(token);
            all.Insert(0, new TweakBatchRecord(ErrorService.NewCorrelationId(), DateTimeOffset.Now, area, list));
            if (all.Count > 120) all.RemoveRange(120, all.Count - 120);
            await AtomicFileService.WriteTextAsync(PathName, JsonSerializer.Serialize(all, OptimizerJsonContext.Default.ListTweakBatchRecord), token);
            Cache = new List<TweakBatchRecord>(all);
            CacheWriteUtc = File.GetLastWriteTimeUtc(PathName);
            OverviewCached = null;
        }
        finally { Gate.Release(); }
    }

    public static async Task<TweakBatchRecord[]> ReadRecentAsync(int max = 40, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try { return (await ReadInternalAsync(token)).Take(Math.Max(1, max)).ToArray(); }
        finally { Gate.Release(); }
    }

    public static async Task<IReadOnlyDictionary<string, bool>> ReadLatestExpectedStatesAsync(
        int maxBatches = 120,
        CancellationToken token = default)
    {
        var snapshot = await ReadOverviewSnapshotAsync(1, maxBatches, token).ConfigureAwait(false);
        return snapshot.ExpectedStates;
    }

    public static async Task<TweakBatchOverviewSnapshot> ReadOverviewSnapshotAsync(
        int maxRecent = 4,
        int maxBatches = 120,
        CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            maxRecent = Math.Max(1, maxRecent);
            maxBatches = Math.Max(1, maxBatches);
            // One metadata check replaces a file read + list clone + sort +
            // dictionary merge when Overview is revisited with unchanged history.
            DateTime stamp = File.GetLastWriteTimeUtc(PathName);
            if (OverviewCached != null && Cache != null &&
                stamp == OverviewCachedWriteUtc && stamp == CacheWriteUtc &&
                maxRecent == OverviewCachedRecent && maxBatches == OverviewCachedBatches)
                return OverviewCached;

            var all = await ReadInternalAsync(token).ConfigureAwait(false);
            var recent = all.Take(maxRecent).ToArray();
            var latest = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var batch in all.Take(maxBatches).OrderByDescending(x => x.Time))
            {
                foreach (var item in batch.Items)
                {
                    if (item.Section.Equals("smoke", StringComparison.OrdinalIgnoreCase) ||
                        item.Id.StartsWith("smoke.", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (latest.ContainsKey(item.Id)) continue;
                    bool restored = batch.Restored || item.Restored;
                    latest[item.Id] = restored ? item.Previous : item.Applied;
                }
            }

            var result = new TweakBatchOverviewSnapshot(recent,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, bool>(latest));
            OverviewCached = result;
            OverviewCachedWriteUtc = CacheWriteUtc;
            OverviewCachedRecent = maxRecent;
            OverviewCachedBatches = maxBatches;
            return result;
        }
        finally { Gate.Release(); }
    }

    // Read-only regression and same-host A/B. Two changing parameter caps force
    // equivalent recomputations without modifying the user's history file.
    internal static async Task<string> RunOverviewSnapshotCacheSmokeAsync(CancellationToken token = default)
    {
        var first = await ReadOverviewSnapshotAsync(4, 120, token).ConfigureAwait(false);
        var warm = await ReadOverviewSnapshotAsync(4, 120, token).ConfigureAwait(false);
        if (!ReferenceEquals(first, warm))
            throw new InvalidOperationException("Overview history snapshot cache missed on unchanged file.");

        // Parameter changes must invalidate a derived view, while the original
        // 4/120 key must become stable again after returning to it.
        var different = await ReadOverviewSnapshotAsync(5, 120, token).ConfigureAwait(false);
        if (ReferenceEquals(different, first))
            throw new InvalidOperationException("Overview history cap invalidation failed.");
        var primed = await ReadOverviewSnapshotAsync(4, 120, token).ConfigureAwait(false);
        if (primed.Recent.Length != first.Recent.Length ||
            primed.ExpectedStates.Count != first.ExpectedStates.Count)
            throw new InvalidOperationException("Overview history cached content changed unexpectedly.");

        const int iterations = 120;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            var result = await ReadOverviewSnapshotAsync(4, 120, token).ConfigureAwait(false);
            if (!ReferenceEquals(result, primed))
                throw new InvalidOperationException("Overview unchanged-history cache identity regressed.");
        }
        double cacheMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long cacheBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Vary only the max-recent capacity, forcing a safe rebuild from
        // exactly the same local history, never touching disk contents.
        before = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
            _ = await ReadOverviewSnapshotAsync(i % 2 == 0 ? 4 : 5, 120, token).ConfigureAwait(false);
        double rebuildMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long rebuildBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        _ = await ReadOverviewSnapshotAsync(4, 120, token).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            _ = await ReadOverviewSnapshotAsync(4, 120, cancelled.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Overview cached read ignored cancellation.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }

        return $"OVERVIEW_HISTORY_SNAPSHOT_CACHE_SMOKE_OK iterations={iterations} " +
            $"unchangedWallMs={cacheMs:F3} forcedRebuildWallMs={rebuildMs:F3} " +
            $"unchangedAllocatedB={cacheBytes} forcedRebuildAllocatedB={rebuildBytes} " +
            $"recent={primed.Recent.Length} expected={primed.ExpectedStates.Count} " +
            "maxRecentInvalidation=true cancellation=true";
    }

    public static async Task<int> CountExpectedActiveAsync(CancellationToken token = default)
    {
        var latest = await ReadLatestExpectedStatesAsync(token: token).ConfigureAwait(false);
        return latest.Count(x => x.Value);
    }

    private static TweakBatchRecord MarkBatchRestoreResults(
        TweakBatchRecord batch, IReadOnlyList<bool> verified)
    {
        if (verified.Count != batch.Items.Count)
            throw new ArgumentException(
                "Restore read-back count differs from batch item count.", nameof(verified));

        // Store each verified outcome independently. In a partially failed
        // batch Overview must use Previous only for successfully restored
        // items, and Applied for the remainder.
        var updated = new TweakBatchItem[verified.Count];
        bool completelyRestored = true;
        for (int index = 0; index < updated.Length; index++)
        {
            bool succeeded = verified[index];
            updated[index] = batch.Items[index] with { Restored = succeeded };
            if (!succeeded) completelyRestored = false;
        }
        return batch with { Items = updated, Restored = completelyRestored };
    }

    internal static string RunPartialRestoreHistorySmoke()
    {
        var items = new[]
        {
            new TweakBatchItem("demo.a", "A", "system", Previous: true, Applied: false),
            new TweakBatchItem("demo.b", "B", "system", Previous: false, Applied: true),
            new TweakBatchItem("demo.c", "C", "system", Previous: true, Applied: false)
        };
        var batch = new TweakBatchRecord("fixture", DateTimeOffset.UtcNow, "regression", items);
        var partial = MarkBatchRestoreResults(batch, new[] { true, false, true });
        if (partial.Restored ||
            !partial.Items[0].Restored || partial.Items[1].Restored || !partial.Items[2].Restored)
            throw new InvalidOperationException(
                "Partial batch restore lost successful per-item read-back results.");

        // Expected-state projection is what Overview uses after restoration.
        bool[] expected = partial.Items.Select(item =>
            (partial.Restored || item.Restored) ? item.Previous : item.Applied).ToArray();
        if (!expected.SequenceEqual(new[] { true, true, true }))
            throw new InvalidOperationException("Overview projection ignored partially restored tweaks.");

        var complete = MarkBatchRestoreResults(partial, new[] { true, true, true });
        if (!complete.Restored || complete.Items.Any(x => !x.Restored))
            throw new InvalidOperationException("Complete batch restore did not mark every item.");

        var failed = MarkBatchRestoreResults(complete, new[] { false, false, false });
        if (failed.Restored || failed.Items.Any(x => x.Restored))
            throw new InvalidOperationException("Failed batch restore kept stale success flags.");

        bool invalidRejected = false;
        try { _ = MarkBatchRestoreResults(batch, new[] { true }); }
        catch (ArgumentException) { invalidRejected = true; }
        if (!invalidRejected)
            throw new InvalidOperationException("Restore outcomes did not match item count.");

        return "TWEAK_PARTIAL_RESTORE_HISTORY_SMOKE_OK success=2 failed=1 " +
               "perItem=true overviewProjection=true complete=true staleFlagsCleared=true " +
               "wrongCountRejected=true realTweaksApplied=0 realHistoryWrites=0";
    }

    public static async Task<string[]> RestoreAsync(string batchId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            var all = await ReadInternalAsync(token);
            var batch = all.FirstOrDefault(x => x.BatchId == batchId);
            if (batch == null) return new[] { "Batch nebyl nalezen." };
            var errors = new List<string>();
            var verified = new bool[batch.Items.Count];
            using var registryBatch = RegistryBatchScope.Begin();
            for (int itemIndex = batch.Items.Count - 1; itemIndex >= 0; itemIndex--)
            {
                token.ThrowIfCancellationRequested();
                var item = batch.Items[itemIndex];
                var tweak = Resolve(item.Id);
                if (tweak == null) { errors.Add(item.Name + ": definice tweaku nenalezena."); continue; }
                try
                {
                    await tweak.SetAsync(item.Previous, token);
                    bool actual = await (tweak.VerifyAsync ?? tweak.DetectAsync)(token);
                    if (actual != item.Previous)
                        errors.Add(item.Name + ": návrat nebyl potvrzen read-backem.");
                    else
                        verified[itemIndex] = true;
                }
                catch (Exception ex) { errors.Add(item.Name + ": " + ex.Message); }
            }
            int index = all.FindIndex(x => x.BatchId == batchId);
            if (index >= 0) all[index] = MarkBatchRestoreResults(batch, verified);
            await AtomicFileService.WriteTextAsync(PathName, JsonSerializer.Serialize(all, OptimizerJsonContext.Default.ListTweakBatchRecord), token);
            Cache = new List<TweakBatchRecord>(all);
            CacheWriteUtc = File.GetLastWriteTimeUtc(PathName);
            OverviewCached = null;
            return errors.ToArray();
        }
        finally { Gate.Release(); }
    }

    public static async Task<string?> RestoreItemAsync(string batchId, string tweakId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            var all = await ReadInternalAsync(token);
            int batchIndex = all.FindIndex(x => x.BatchId == batchId);
            if (batchIndex < 0) return "Batch nebyl nalezen.";
            var batch = all[batchIndex];
            int itemIndex = batch.Items.ToList().FindIndex(x => x.Id.Equals(tweakId, StringComparison.OrdinalIgnoreCase));
            if (itemIndex < 0) return "Položka nebyla nalezena.";
            var item = batch.Items[itemIndex];
            var tweak = Resolve(item.Id);
            if (tweak == null) return item.Name + ": definice tweaku nenalezena.";
            try
            {
                using var registryBatch = RegistryBatchScope.Begin();
                await tweak.SetAsync(item.Previous, token);
                bool actual = await (tweak.VerifyAsync ?? tweak.DetectAsync)(token);
                if (actual != item.Previous) return item.Name + ": návrat nebyl potvrzen read-backem.";
                var items = batch.Items.ToArray();
                items[itemIndex] = item with { Restored = true };
                all[batchIndex] = batch with { Items = items, Restored = items.All(x => x.Restored) };
                await AtomicFileService.WriteTextAsync(PathName, JsonSerializer.Serialize(all, OptimizerJsonContext.Default.ListTweakBatchRecord), token);
                Cache = new List<TweakBatchRecord>(all);
                CacheWriteUtc = File.GetLastWriteTimeUtc(PathName);
                OverviewCached = null;
                return null;
            }
            catch (Exception ex) { return item.Name + ": " + ex.Message; }
        }
        finally { Gate.Release(); }
    }

    private static async Task<List<TweakBatchRecord>> ReadInternalAsync(CancellationToken token)
    {
        try
        {
            if (!File.Exists(PathName))
            {
                Cache = null;
                OverviewCached = null;
                return new();
            }
            DateTime writeUtc = File.GetLastWriteTimeUtc(PathName);
            if (Cache != null && writeUtc == CacheWriteUtc) return new List<TweakBatchRecord>(Cache);
            OverviewCached = null;
            string json = await File.ReadAllTextAsync(PathName, token);
            var loaded = JsonSerializer.Deserialize(json, OptimizerJsonContext.Default.ListTweakBatchRecord) ?? new();

            bool sanitized = false;
            var cleaned = new List<TweakBatchRecord>(loaded.Count);
            foreach (var batch in loaded)
            {
                var items = batch.Items
                    .Where(item =>
                        !item.Section.Equals("smoke", StringComparison.OrdinalIgnoreCase) &&
                        !item.Id.StartsWith("smoke.", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (items.Length != batch.Items.Count)
                    sanitized = true;
                if (items.Length == 0)
                {
                    sanitized = true;
                    continue;
                }

                cleaned.Add(items.Length == batch.Items.Count ? batch : batch with { Items = items });
            }

            if (sanitized)
            {
                await AtomicFileService.WriteTextAsync(
                    PathName,
                    JsonSerializer.Serialize(cleaned, OptimizerJsonContext.Default.ListTweakBatchRecord),
                    token).ConfigureAwait(false);
                loaded = cleaned;
                writeUtc = File.GetLastWriteTimeUtc(PathName);
                LogService.Info("history", $"TWEAK_HISTORY_SANITIZED removedSynthetic=true remaining={loaded.Count}");
            }

            Cache = new List<TweakBatchRecord>(loaded);
            CacheWriteUtc = writeUtc;
            return loaded;
        }
        catch { return new(); }
    }
    private static readonly Lazy<Dictionary<string, TweakDefinition>> Resolver = new(BuildResolver, true);

    private static TweakDefinition? Resolve(string id) => Resolver.Value.GetValueOrDefault(id);

    private static Dictionary<string, TweakDefinition> BuildResolver()
    {
        var map = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in CatalogService.EnumerateTweakSources())
        {
            foreach (var tweak in CatalogService.GetTweaks(source.Section, source.Sub))
                map.TryAdd(tweak.Id, tweak);
        }
        return map;
    }
}
