using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Two reusable process-time dictionaries avoid allocating and copying a map
// for every performance measurement window.
internal sealed class ProcessSamplingHistory
{
    private readonly record struct NamedCpuTime(TimeSpan Cpu, string ProcessName);
    private Dictionary<int, NamedCpuTime> _previous = new();
    private Dictionary<int, NamedCpuTime> _next = new();

    internal int Count => _previous.Count;
    internal void Begin() => _next.Clear();
    internal bool TryGet(int pid, out TimeSpan cpu)
    {
        bool found = _previous.TryGetValue(pid, out var sample);
        cpu = sample.Cpu;
        return found;
    }
    // Native PID reuse must not import CPU deltas from a DIFFERENT executable.
    // No extra process handles or StartTime reads are needed for the name gate.
    internal bool TryGet(int pid, string name, out TimeSpan cpu)
    {
        bool found = _previous.TryGetValue(pid, out var sample) &&
            string.Equals(sample.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        cpu = found ? sample.Cpu : default;
        return found;
    }
    internal void Record(int pid, TimeSpan cpu) =>
        _next[pid] = new NamedCpuTime(cpu, string.Empty);
    internal void Record(int pid, string name, TimeSpan cpu) =>
        _next[pid] = new NamedCpuTime(cpu, name);
    internal void Commit() => (_previous, _next) = (_next, _previous);
}

internal static class ProcessSnapshotCleanup
{
    internal static TResult Inspect<T, TResult>(Func<T[]> acquire, Func<T[], TResult> inspect)
        where T : IDisposable
    {
        T[] resources = acquire();
        try { return inspect(resources); }
        finally { DisposeAll(resources); }
    }

    internal static async Task InspectAsync<T>(Func<T[]> acquire, Func<T[], Task> inspect)
        where T : IDisposable
    {
        T[] resources = acquire();
        try { await inspect(resources).ConfigureAwait(false); }
        finally { DisposeAll(resources); }
    }

    internal static async Task<TResult> InspectAsync<T, TResult>(
        Func<T[]> acquire, Func<T[], Task<TResult>> inspect) where T : IDisposable
    {
        T[] resources = acquire();
        try { return await inspect(resources).ConfigureAwait(false); }
        finally { DisposeAll(resources); }
    }

    // Cleanup ALL enumerated handles even if the consumer short-circuits,
    // encounters access denied or cancels halfway through a process snapshot.
    internal static void DisposeAll<T>(T[] resources) where T : IDisposable
    {
        foreach (T resource in resources)
        {
            try { resource.Dispose(); }
            catch { /* Keep releasing the remaining native resources. */ }
        }
    }
}
