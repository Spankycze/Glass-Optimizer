using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record BackgroundWorkSnapshot(int Pending, int Active, string? Current, long LastDurationMs);

public static class BackgroundWorkSchedulerService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _pending;
    private static int _active;
    private static string? _current;
    private static long _lastDurationMs;

    public static BackgroundWorkSnapshot Snapshot() =>
        new(Volatile.Read(ref _pending), Volatile.Read(ref _active),
            Volatile.Read(ref _current), Interlocked.Read(ref _lastDurationMs));

    public static async Task RunAsync(string name, Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        Interlocked.Increment(ref _pending);
        try { await Gate.WaitAsync(token).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _pending); }
        Interlocked.Exchange(ref _active, 1);
        Volatile.Write(ref _current, name);
        // Timestamp arithmetic avoids allocating a Stopwatch per background job.
        long started = Stopwatch.GetTimestamp();
        try { await action(token).ConfigureAwait(false); }
        finally
        {
            long durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Interlocked.Exchange(ref _lastDurationMs, durationMs);
            Volatile.Write(ref _current, null);
            Interlocked.Exchange(ref _active, 0);
            Gate.Release();
            if (durationMs > 1000)
                LogService.Info("scheduler", $"BACKGROUND name={name} ms={durationMs}");
        }
    }
}
