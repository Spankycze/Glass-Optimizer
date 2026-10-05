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

public static class TweakBatchHistoryService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<TweakBatchRecord>? Cache;
    private static DateTime CacheWriteUtc;
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
        await Gate.WaitAsync(token);
        try
        {
            var all = (await ReadInternalAsync(token))
                .Take(Math.Max(1, maxBatches))
                .OrderByDescending(x => x.Time);

            var latest = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in all)
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
            return latest;
        }
        finally { Gate.Release(); }
    }

    public static async Task<int> CountExpectedActiveAsync(CancellationToken token = default)
    {
        var latest = await ReadLatestExpectedStatesAsync(token: token).ConfigureAwait(false);
        return latest.Count(x => x.Value);
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
            using var registryBatch = RegistryBatchScope.Begin();
            foreach (var item in batch.Items.Reverse())
            {
                token.ThrowIfCancellationRequested();
                var tweak = Resolve(item.Id);
                if (tweak == null) { errors.Add(item.Name + ": definice tweaku nenalezena."); continue; }
                try
                {
                    await tweak.SetAsync(item.Previous, token);
                    bool actual = await (tweak.VerifyAsync ?? tweak.DetectAsync)(token);
                    if (actual != item.Previous) errors.Add(item.Name + ": návrat nebyl potvrzen read-backem.");
                }
                catch (Exception ex) { errors.Add(item.Name + ": " + ex.Message); }
            }
            int index = all.FindIndex(x => x.BatchId == batchId);
            if (index >= 0) all[index] = batch with { Restored = errors.Count == 0 };
            await AtomicFileService.WriteTextAsync(PathName, JsonSerializer.Serialize(all, OptimizerJsonContext.Default.ListTweakBatchRecord), token);
            Cache = new List<TweakBatchRecord>(all);
            CacheWriteUtc = File.GetLastWriteTimeUtc(PathName);
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
            if (!File.Exists(PathName)) return new();
            DateTime writeUtc = File.GetLastWriteTimeUtc(PathName);
            if (Cache != null && writeUtc == CacheWriteUtc) return new List<TweakBatchRecord>(Cache);
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
