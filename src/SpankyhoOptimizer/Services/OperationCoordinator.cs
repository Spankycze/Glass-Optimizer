using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class OperationCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<T> RunExclusiveAsync<T>(string key, Func<CancellationToken, Task<T>> work,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The budget covers both waiting in the queue AND the work itself.
        // Otherwise a queued request could wait indefinitely behind a stalled
        // tweak, GPU download or power-plan mutation.
        timeoutCts.CancelAfter(timeout);
        var started = DateTime.UtcNow;
        string id = ErrorService.NewCorrelationId();
        bool acquired = false;
        using var activity = DiagnosticsTelemetry.Start("operation.exclusive", ("key", key), ("id", id));
        try
        {
            await gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            acquired = true;
            LogService.Info($"OP {key} START id={id}");
            var result = await work(timeoutCts.Token).ConfigureAwait(false);
            LogService.Info($"OP {key} OK id={id} ms={(DateTime.UtcNow - started).TotalMilliseconds:0}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigation and user cancellation are expected outcomes, not
            // operational errors; preserve cancellation for the caller.
            LogService.Info($"OP {key} CANCELED id={id} ms={(DateTime.UtcNow - started).TotalMilliseconds:0}");
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var ex = new TimeoutException($"Operace {key} překročila limit {timeout.TotalSeconds:0}s.");
            LogService.Error($"OP {key} TIMEOUT id={id}", ex);
            throw ex;
        }
        catch (Exception ex)
        {
            LogService.Error($"OP {key} FAIL id={id}", ex);
            throw;
        }
        finally
        {
            DiagnosticsTelemetry.RecordDuration("operation.exclusive",
                (DateTime.UtcNow - started).TotalMilliseconds, ("key", key));
            if (acquired) gate.Release();
        }
    }

    internal static async Task<string> RunQueueDeadlineSmokeAsync()
    {
        string key = "operation-queue-smoke-" + Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int waitingWorkRuns = 0;
        Task<int> first = RunExclusiveAsync(key, async token =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return 1;
        }, TimeSpan.FromSeconds(4));
        Task<int>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            second = RunExclusiveAsync(key, token =>
            {
                Interlocked.Increment(ref waitingWorkRuns);
                return Task.FromResult(2);
            }, TimeSpan.FromMilliseconds(120));
            var finished = await Task.WhenAny(second, Task.Delay(450)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, second))
                throw new InvalidOperationException("Queued operation ignored its 120ms deadline.");
            bool timedOut = false;
            try { _ = await second.ConfigureAwait(false); }
            catch (TimeoutException) { timedOut = true; }
            if (!timedOut || Volatile.Read(ref waitingWorkRuns) != 0)
                throw new InvalidOperationException("Queued operation executed after timing out.");
        }
        finally
        {
            release.TrySetResult(true);
            await first.ConfigureAwait(false);
            if (second != null)
            {
                try { await second.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
        int next = await RunExclusiveAsync(key, _ => Task.FromResult(3),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (next != 3 || Volatile.Read(ref waitingWorkRuns) != 0)
            throw new InvalidOperationException("Timed-out waiter corrupted the semaphore release count.");

        var enteredAgain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAgain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holdAgain = RunExclusiveAsync(key, async token =>
        {
            enteredAgain.TrySetResult(true);
            await releaseAgain.Task.WaitAsync(token).ConfigureAwait(false);
        }, TimeSpan.FromSeconds(4));
        int cancelledWorkRuns = 0;
        try
        {
            await enteredAgain.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            using var cancel = new CancellationTokenSource();
            Task waiter = RunExclusiveAsync(key, _ =>
            {
                Interlocked.Increment(ref cancelledWorkRuns);
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(2), cancel.Token);
            cancel.Cancel();
            bool cancelled = false;
            try { await waiter.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled = true; }
            if (!cancelled || Volatile.Read(ref cancelledWorkRuns) != 0)
                throw new InvalidOperationException("Cancelled queued operation ran or lost cancellation.");
        }
        finally
        {
            releaseAgain.TrySetResult(true);
            await holdAgain.ConfigureAwait(false);
        }
        int afterCancel = await RunExclusiveAsync(key, _ => Task.FromResult(4),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (afterCancel != 4)
            throw new InvalidOperationException("Cancelled waiter corrupted the semaphore.");

        return "OPERATION_QUEUE_DEADLINE_SMOKE_OK queuedTimeout=true staleWorkBlocked=true gateReleased=true externalCancel=true";
    }

    public static Task RunExclusiveAsync(string key, Func<CancellationToken, Task> work,
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync<object?>(key, async token => { await work(token).ConfigureAwait(false); return null; }, timeout, cancellationToken);
}
