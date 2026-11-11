using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Memory Overview can ask for its ten-second snapshot repeatedly. Reuse the
// completed Task for the exact snapshot instead of allocating Task.FromResult
// on every UI refresh. Callers serialize access with MemoryStateService.CacheLock.
internal sealed class MemorySnapshotTaskCache<T> where T : class
{
    private T? _value;
    private Task<T>? _task;

    internal Task<T> For(T value)
    {
        if (_task is null || !ReferenceEquals(value, _value))
        {
            _value = value;
            _task = Task.FromResult(value);
        }
        return _task;
    }

    internal void Invalidate()
    {
        _value = null;
        _task = null;
    }
}

internal static class MemorySnapshotCachePolicy
{
    internal static bool IsFresh(DateTime writtenUtc, DateTime nowUtc,
        TimeSpan lifetime) =>
        writtenUtc != default && nowUtc >= writtenUtc &&
        nowUtc - writtenUtc < lifetime;
}
