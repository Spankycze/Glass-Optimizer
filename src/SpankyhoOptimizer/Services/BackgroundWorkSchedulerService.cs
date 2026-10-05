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
        new(Volatile.Read(ref _pending), Volatile.Read(ref _active), _current, Interlocked.Read(ref _lastDurationMs));

    public static async Task RunAsync(string name, Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        Interlocked.Increment(ref _pending);
        await Gate.WaitAsync(token);
        Interlocked.Decrement(ref _pending);
        Interlocked.Exchange(ref _active, 1);
        _current = name;
        var sw = Stopwatch.StartNew();
        try { await action(token); }
        finally
        {
            sw.Stop(); Interlocked.Exchange(ref _lastDurationMs, sw.ElapsedMilliseconds);
            _current = null; Interlocked.Exchange(ref _active, 0); Gate.Release();
            if (sw.ElapsedMilliseconds > 1000) LogService.Info("scheduler", $"BACKGROUND name={name} ms={sw.ElapsedMilliseconds}");
        }
    }
}
