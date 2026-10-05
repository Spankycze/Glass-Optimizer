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
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var started = DateTime.UtcNow;
        string id = ErrorService.NewCorrelationId();
        using var activity = DiagnosticsTelemetry.Start("operation.exclusive", ("key", key), ("id", id));
        try
        {
            LogService.Info($"OP {key} START id={id}");
            var result = await work(timeoutCts.Token).ConfigureAwait(false);
            LogService.Info($"OP {key} OK id={id} ms={(DateTime.UtcNow - started).TotalMilliseconds:0}");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
            gate.Release();
        }
    }

    public static Task RunExclusiveAsync(string key, Func<CancellationToken, Task> work,
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync<object?>(key, async token => { await work(token).ConfigureAwait(false); return null; }, timeout, cancellationToken);
}
