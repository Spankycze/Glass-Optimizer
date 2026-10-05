using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class WatchdogService
{
    public static async Task RunAsync(string name, Func<CancellationToken, Task> work,
        TimeSpan timeout, CancellationToken token = default, bool logTimeoutAsError = true)
    {
        using var cts = token.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(token)
            : new CancellationTokenSource();
        cts.CancelAfter(timeout);
        try { await work(cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            var ex = new TimeoutException($"{name} překročil timeout {timeout.TotalSeconds:0}s.");
            if (logTimeoutAsError) LogService.Error("watchdog", name + " timed out", ex);
            else LogService.Info("watchdog", name + " expected timeout observed.");
            throw ex;
        }
    }
}
