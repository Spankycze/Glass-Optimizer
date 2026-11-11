using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

internal readonly record struct AdaptiveProcessCpuSample(TimeSpan Cpu, string Name);

// Adaptive power polls processes repeatedly. Reuse two dictionaries and store
// value-type snapshots instead of creating one object per process per poll.
internal sealed class AdaptiveProcessCpuHistory
{
    // Typical Windows snapshots have hundreds of PIDs. Reuse both buffers
    // without their initial 3-5 resize/rehash operations on warm sampling.
    private Dictionary<int, AdaptiveProcessCpuSample> _previous = new(384);
    private Dictionary<int, AdaptiveProcessCpuSample> _next = new(384);

    internal int PreviousCount => _previous.Count;

    internal void BeginCycle() => _next.Clear();

    internal bool TryGetPrevious(int pid, out AdaptiveProcessCpuSample sample) =>
        _previous.TryGetValue(pid, out sample);

    internal void Remember(int pid, TimeSpan cpu, string name) =>
        _next[pid] = new AdaptiveProcessCpuSample(cpu, name);

    internal void EndCycle() => (_previous, _next) = (_next, _previous);
}
