using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal readonly record struct ActivityProcessEntry(string Name, int Id, long Mb);

// Small read-only process snapshot for the Activity screen. Independent
// navigations share one scan and never retain Process/Win32 handles.
internal static class ActivityProcessSnapshotService
{
    private static readonly object Gate = new();
    private static ActivityProcessEntry[]? _cached;
    private static long _cachedAtTimestamp;
    private static Task<ActivityProcessEntry[]>? _flight;
    private static int _generation;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(3);
    private const int TopCount = 30;

    internal static Task<ActivityProcessEntry[]> GetAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (_cached != null && IsCacheFresh(
                    _cachedAtTimestamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency))
                return Task.FromResult(_cached);
            if (_flight is { IsCompleted: false })
                return _flight.WaitAsync(token);

            int generation = _generation;
            Task<ActivityProcessEntry[]> created = Task.Run(Capture);
            _flight = created;
            _ = created.ContinueWith(t =>
            {
                if (t.IsFaulted) _ = t.Exception;
                lock (Gate)
                {
                    if (!ReferenceEquals(_flight, created)) return;
                    if (generation == _generation && t.IsCompletedSuccessfully)
                    {
                        _cached = t.Result;
                        _cachedAtTimestamp = Stopwatch.GetTimestamp();
                    }
                    _flight = null;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return created.WaitAsync(token);
        }
    }

    internal static void Invalidate()
    {
        lock (Gate)
        {
            ++_generation;
            _cached = null;
            _cachedAtTimestamp = 0;
            _flight = null;
        }
    }

    // Monotonic cache lifetime is independent of Windows wall-clock edits.
    // A backwards clock correction must never make an old process list fresh.
    internal static bool IsCacheFresh(long capturedAt, long now, long frequency) =>
        capturedAt > 0 && now >= capturedAt && frequency > 0 &&
        (double)(now - capturedAt) / frequency < Ttl.TotalSeconds;

    private static ActivityProcessEntry[] Capture()
    {
        var processes = Process.GetProcesses();
        try
        {
            // No N-process temporary list. Keep only the largest 30 rows
            // while the native wrappers are already being visited once.
            var top = new TopCollector();
            foreach (var process in processes)
                try
                {
                    // Resolve the expensive process name only for rows that can enter top 30.
                    int id = process.Id;
                    if (id == 0) continue;
                    long mb = process.WorkingSet64 / 1048576;
                    if (!top.WouldAccept(mb)) continue;
                    top.Add(new ActivityProcessEntry(process.ProcessName, id, mb));
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException)
                {
                    // A process can exit or become protected between enumeration and counter reads.
                }
            return top.Complete();
        }
        finally { ProcessSnapshotCleanup.DisposeAll(processes); }
    }

    // Bounded streaming top 30. Both the live process enumeration and the
    // deterministic ranking tests share exactly the same stable-tie policy.
    private sealed class TopCollector
    {
        private readonly (ActivityProcessEntry Entry, int Index)[] _top = new
            (ActivityProcessEntry, int)[TopCount];
        private int _count;
        private int _smallest;
        private int _nextIndex;

        // The next candidate has a later enumeration index, so equal-size
        // rows never replace an earlier row when the bounded top is full.
        internal bool WouldAccept(long mb) =>
            _count < TopCount || mb > _top[_smallest].Entry.Mb;

        internal void Add(ActivityProcessEntry entry)
        {
            int index = _nextIndex++;
            if (entry.Id == 0) return;
            if (_count < TopCount)
            {
                _top[_count++] = (entry, index);
                if (entry.Mb < _top[_smallest].Entry.Mb ||
                    (entry.Mb == _top[_smallest].Entry.Mb &&
                     index > _top[_smallest].Index))
                    _smallest = _count - 1;
                return;
            }
            if (entry.Mb <= _top[_smallest].Entry.Mb) return;
            _top[_smallest] = (entry, index);
            _smallest = 0;
            for (int k = 1; k < _count; k++)
                if (_top[k].Entry.Mb < _top[_smallest].Entry.Mb ||
                    (_top[k].Entry.Mb == _top[_smallest].Entry.Mb &&
                     _top[k].Index > _top[_smallest].Index))
                    _smallest = k;
        }

        // One shared comparer: Comparer.Create() would allocate a new delegate
        // wrapper on every ranking pass of a hot read-only snapshot path.
        private static readonly IComparer<(ActivityProcessEntry Entry, int Index)> DescendingByMemory =
            Comparer<(ActivityProcessEntry Entry, int Index)>.Create(static (a, b) =>
            {
                int compare = b.Entry.Mb.CompareTo(a.Entry.Mb);
                return compare != 0 ? compare : a.Index.CompareTo(b.Index);
            });

        internal ActivityProcessEntry[] Complete()
        {
            // Sort the already-owned bounded buffer in place instead of
            // allocating a second tuple array for every ranking pass.
            Array.Sort(_top, 0, _count, DescendingByMemory);
            var output = new ActivityProcessEntry[_count];
            for (int i = 0; i < output.Length; i++) output[i] = _top[i].Entry;
            return output;
        }
    }

    // A single collector per live snapshot, never a list of every process.
    // The static test helper accepts any bounded/unbounded replay fixture.
    internal static ActivityProcessEntry[] Rank(IReadOnlyList<ActivityProcessEntry> entries)
    {
        var top = new TopCollector();
        for (int i = 0; i < entries.Count; i++)
            top.Add(entries[i]);
        return top.Complete();
    }

    internal static ActivityProcessEntry[] RankStream(IEnumerable<ActivityProcessEntry> entries)
    {
        var top = new TopCollector();
        foreach (ActivityProcessEntry entry in entries)
            top.Add(entry);
        return top.Complete();
    }

    internal static async Task RunCacheSmokeAsync()
    {
        Invalidate();
        var first = GetAsync();
        var second = GetAsync();
        await Task.WhenAll(first, second).ConfigureAwait(false);
        if (!ReferenceEquals(first.Result, second.Result) ||
            !ReferenceEquals(first.Result, await GetAsync().ConfigureAwait(false)))
            throw new InvalidOperationException("Activity snapshot coalescing/cache missed.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool honored = false;
        try { _ = await GetAsync(cancelled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { honored = true; }
        if (!honored)
            throw new InvalidOperationException("Activity snapshot cache ignored cancellation.");
        Invalidate();
        var refreshed = await GetAsync().ConfigureAwait(false);
        if (ReferenceEquals(first.Result, refreshed) || refreshed.Length > TopCount)
            throw new InvalidOperationException("Activity explicit refresh returned stale or oversized result.");
        LogService.Info("performance",
            $"ACTIVITY_SNAPSHOT_CACHE_SMOKE_OK entries={refreshed.Length} singleFlight=true ttlSeconds=3 cancelled=true invalidated=true");
    }

    internal static string RunRankingBenchmark()
    {
        var data = new ActivityProcessEntry[2048];
        for (int i = 0; i < data.Length; i++)
            data[i] = new ActivityProcessEntry("Synthetic" + (i % 41), i + 1,
                (i * 173L + (i >> 3) * 11) % 8192);
        var expected = data.Where(x => x.Id != 0)
            .OrderByDescending(x => x.Mb).Take(TopCount).ToArray();
        if (!Rank(data).SequenceEqual(expected) ||
            Rank(Array.Empty<ActivityProcessEntry>()).Length != 0)
            throw new InvalidOperationException("Activity top30 order differs from original stable ranking.");
        var equalMemory = Enumerable.Range(1, 48)
            .Select(i => new ActivityProcessEntry("Tie", i, 512)).ToArray();
        var tied = Rank(equalMemory);
        if (tied.Length != TopCount || tied[0].Id != 1 || tied[^1].Id != TopCount)
            throw new InvalidOperationException("Activity top30 stable tie ranking regression.");
        for (int i = 0; i < 8; i++) _ = Rank(data);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 300; i++)
        {
            var ranked = Rank(data);
            if (ranked.Length != TopCount || ranked[0].Mb < ranked[^1].Mb)
                throw new InvalidOperationException("Activity ranking length/order regression.");
        }
        sw.Stop();
        return $"ACTIVITY_TOP30_BENCH_OK entries={data.Length} runs=300 wallMs={sw.Elapsed.TotalMilliseconds:F2}" +
            $" allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocBefore} writes=false";
    }
}
