using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record WorkerHealthEntry(string Action, int Runs, int Failures, int Timeouts, DateTimeOffset? LastFailureUtc, string? LastError);

public static class WorkerHealthService
{
    private sealed class Mutable
    {
        public int Runs;
        public int Failures;
        public int Timeouts;
        public DateTimeOffset? LastFailureUtc;
        public string? LastError;
    }

    private static readonly ConcurrentDictionary<string, Mutable> Stats = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PersistSync = new();
    private static readonly TimeSpan FailureRetention = TimeSpan.FromDays(7);
    private const int MaxPersistedFailures = 32;
    private static string FileName => Path.Combine(LogService.Root, "worker-health.json");

    static WorkerHealthService()
    {
        try
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - FailureRetention;
            var items = SelfHealingJsonService.ReadOrDefault(FileName, () => Array.Empty<WorkerHealthEntry>(), "worker-health")
                .Where(x => x.Failures > 0 && x.LastFailureUtc >= cutoff)
                .OrderByDescending(x => x.LastFailureUtc)
                .Take(MaxPersistedFailures)
                .ToArray();

            foreach (var item in items)
                Stats[item.Action] = new Mutable
                {
                    Runs = item.Runs,
                    Failures = item.Failures,
                    Timeouts = item.Timeouts,
                    LastFailureUtc = item.LastFailureUtc,
                    LastError = item.LastError
                };

            // One-time migration: remove stale QA/smoke and success-only history from disk.
            PersistFailures();
        }
        catch (Exception ex) { LogService.Error("worker-health", "Load failed", ex); }
    }

    private static bool MayResolve(DateTimeOffset startedUtc, DateTimeOffset? latestFailureUtc) =>
        latestFailureUtc == null || startedUtc >= latestFailureUtc.Value;

    // A late result from a request started before a NEWER failure must never
    // clear that failure's unresolved alert.
    internal static void RunResolutionOrderingSmoke()
    {
        DateTimeOffset t = new(2026, 10, 17, 20, 0, 0, TimeSpan.Zero);
        if (MayResolve(t, t.AddSeconds(1)) ||
            !MayResolve(t.AddSeconds(2), t.AddSeconds(1)) ||
            !MayResolve(t, null))
            throw new InvalidOperationException("Worker failure recovery ordering regressed.");
    }

    public static void Success(string action, DateTimeOffset requestStartedUtc)
    {
        var s = Stats.GetOrAdd(action, _ => new());
        // Successful IPC completion is proof that the LAST failure has been
        // resolved. Keep the run counter, but clear stale error state once.
        // The common healthy path performs no disk I/O.
        lock (PersistSync)
        {
            Interlocked.Increment(ref s.Runs);
            if ((s.Failures == 0 && s.LastFailureUtc == null && s.LastError == null) ||
                !MayResolve(requestStartedUtc, s.LastFailureUtc))
                return;
            s.Failures = 0;
            s.Timeouts = 0;
            s.LastFailureUtc = null;
            s.LastError = null;
            PersistFailures();
        }
    }

    public static void Failure(string action, Exception ex)
    {
        var s = Stats.GetOrAdd(action, _ => new());
        // Serialize error transitions with successful recovery so a late
        // completion cannot erase a newer failure or its durable record.
        lock (PersistSync)
        {
            Interlocked.Increment(ref s.Runs);
            Interlocked.Increment(ref s.Failures);
            if (ex is TimeoutException or OperationCanceledException)
                Interlocked.Increment(ref s.Timeouts);
            s.LastFailureUtc = DateTimeOffset.UtcNow;
            s.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            PersistFailures();
        }
    }

    public static void Reset()
    {
        lock (PersistSync)
        {
            Stats.Clear();
            try { if (File.Exists(FileName)) File.Delete(FileName); }
            catch (Exception ex) { LogService.Error("worker-health", "Reset file cleanup failed", ex); }
        }
    }

    public static WorkerHealthEntry[] Snapshot() =>
        Stats.Select(x => new WorkerHealthEntry(
                x.Key,
                Volatile.Read(ref x.Value.Runs),
                Volatile.Read(ref x.Value.Failures),
                Volatile.Read(ref x.Value.Timeouts),
                x.Value.LastFailureUtc,
                x.Value.LastError))
            .OrderByDescending(x => x.Failures)
            .ThenBy(x => x.Action)
            .ToArray();

    private static void PersistFailures()
    {
        try
        {
            lock (PersistSync)
            {
                DateTimeOffset cutoff = DateTimeOffset.UtcNow - FailureRetention;
                var persisted = Snapshot()
                    .Where(x => x.Failures > 0 && x.LastFailureUtc >= cutoff)
                    .OrderByDescending(x => x.LastFailureUtc)
                    .Take(MaxPersistedFailures)
                    .ToArray();

                Directory.CreateDirectory(LogService.Root);
                if (persisted.Length == 0)
                {
                    if (File.Exists(FileName)) File.Delete(FileName);
                    return;
                }

                AtomicFileService.WriteText(FileName, JsonSerializer.Serialize(persisted));
            }
        }
        catch (Exception ex) { LogService.Error("worker-health", "Persist failed", ex); }
    }
}
