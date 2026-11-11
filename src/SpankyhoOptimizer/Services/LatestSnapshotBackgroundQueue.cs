using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Coalesce bursty background notifications while one asynchronous operation is
// in flight. Only the latest pending immutable snapshot is retained; no chain
// of Task.Run operations or outdated queued uploads can accumulate.
internal sealed class LatestSnapshotBackgroundQueue<T> where T : class
{
    private readonly object _gate = new();
    private readonly Func<T, Task> _send;
    private bool _running;
    private T? _pending;

    internal LatestSnapshotBackgroundQueue(Func<T, Task> send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    internal void Enqueue(T snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool startWorker;
        lock (_gate)
        {
            _pending = snapshot;
            startWorker = !_running;
            if (startWorker)
                _running = true;
        }

        if (startWorker)
            _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            T current;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _running = false;
                    return;
                }

                current = _pending;
                _pending = null;
            }

            // Best effort: a failed upload must not strand newer observations,
            // and the single background worker must keep draining the queue.
            try { await _send(current).ConfigureAwait(false); }
            catch { }
        }
    }
}
