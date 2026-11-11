using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// One serial AI work lane, no second FanAgent/OS process or polling timer.
// The producer never awaits slow optional maintenance; concurrent submissions
// collapse to the latest observation, with no unbounded Channel/task queue.
// The agent must await DisposeAsync BEFORE releasing its singleton mutex.
internal sealed class LatestOnlyAsyncWorkLane<T> : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<T, CancellationToken, Task> _action;
    private readonly Action<Exception> _error;
    private readonly CancellationTokenSource _cts;
    private Task _drain = Task.CompletedTask;
    private T _pending = default!;
    private bool _hasPending;
    private bool _isDraining;
    private bool _closed;

    internal LatestOnlyAsyncWorkLane(Func<T, CancellationToken, Task> action,
        Action<Exception> error, CancellationToken lifetime)
    {
        _action = action;
        _error = error;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    internal bool Submit(T value)
    {
        lock (_gate)
        {
            if (_closed || _cts.IsCancellationRequested) return false;
            // Never accumulate old telemetry while one AI mutation finishes.
            _pending = value;
            _hasPending = true;
            if (!_isDraining)
            {
                _isDraining = true;
                _drain = Task.Run(DrainAsync);
            }
            return true;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            T next;
            lock (_gate)
            {
                if (_closed || _cts.IsCancellationRequested || !_hasPending)
                {
                    _isDraining = false;
                    return;
                }
                next = _pending;
                _hasPending = false;
            }

            try { await _action(next, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // A failing optional AI task must not kill the fan control
                // loop; the next valid epoch still gets an independent attempt.
                try { _error(ex); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task draining;
        lock (_gate)
        {
            _closed = true;
            _hasPending = false;
            _cts.Cancel();
            draining = _drain;
        }
        try { await draining.ConfigureAwait(false); }
        finally { _cts.Dispose(); }
    }
}

// Strictly fail closed if an AI observation waited in the queue through a
// foreground/window change or stale sensor epoch. The AI engine still runs
// its normal immediate rollback path with cpuEvidenceAvailable=false.
internal readonly record struct FanAiTickObservation(
    DateTime CapturedUtc,
    bool RealIdle,
    double CpuPercent,
    bool CpuEvidenceAvailable,
    AdaptivePowerRuntimeStatus? Status,
    AiSystemRuntimeConfig? Config)
{
    internal bool CanUseEvidence(DateTime nowUtc, (nint Handle, int ProcessId) live)
    {
        if (!CpuEvidenceAvailable || Status is null ||
            CapturedUtc > nowUtc || nowUtc - CapturedUtc > TimeSpan.FromSeconds(4) ||
            Status.UpdatedUtc > nowUtc ||
            nowUtc - Status.UpdatedUtc > TimeSpan.FromSeconds(4))
            return false;
        return Status.ForegroundWindowHandle != 0 &&
            Status.ForegroundProcessId > 0 &&
            Status.ForegroundWindowHandle == (long)live.Handle &&
            Status.ForegroundProcessId == live.ProcessId;
    }
}
