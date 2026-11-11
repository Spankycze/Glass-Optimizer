using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// A refresh started before Invalidate() must never republish obsolete RAM state
// or clear a newer in-flight refresh. Caller cancellation only stops waiting;
// it does not interrupt the shared native/WMI read.
internal sealed class MemorySnapshotRefreshGate<T> where T : class
{
    private readonly object _gate = new();
    private readonly MemorySnapshotTaskCache<T> _completed = new();
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _clock;
    private T? _cached;
    private DateTime _cachedUtc;
    private Task<T>? _inFlight;
    private long _generation;

    internal MemorySnapshotRefreshGate(TimeSpan ttl, Func<DateTime>? clock = null)
    {
        _ttl = ttl;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    internal Task<T> ReadAsync(Func<Task<T>> fetch, CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            if (_cached != null && MemorySnapshotCachePolicy.IsFresh(_cachedUtc, _clock(), _ttl))
                return _completed.For(_cached);

            Task<T>? flight = _inFlight;
            if (flight == null)
            {
                long generation = _generation;
                var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                flight = source.Task;
                _inFlight = flight;
                _ = RefreshAsync(fetch, source, generation);
            }
            return flight.WaitAsync(cancellation);
        }
    }

    private async Task RefreshAsync(Func<Task<T>> fetch, TaskCompletionSource<T> source, long generation)
    {
        try
        {
            T result = await fetch().ConfigureAwait(false);
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _cached = result;
                    _cachedUtc = _clock();
                }
            }
            source.TrySetResult(result);
        }
        catch (Exception ex) { source.TrySetException(ex); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, source.Task))
                    _inFlight = null;
            }
        }
    }

    // Used by the UI first-paint path to avoid scheduling a continuation and
    // rebuilding a loading shell when the exact 10s snapshot is already valid.
    internal T? TryGetFreshSnapshot()
    {
        lock (_gate)
            return _cached != null &&
                MemorySnapshotCachePolicy.IsFresh(_cachedUtc, _clock(), _ttl)
                ? _cached : null;
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _cached = null;
            _cachedUtc = default;
            _completed.Invalidate();
            _inFlight = null;
        }
    }
}
