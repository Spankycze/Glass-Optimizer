using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class SafeTaskService
{
    // The fast path is important for repeated successful UI/background operations:
    // no observer state machine, error correlation ID, or logging scope is needed.
    public static void Forget(Task task, string module, string operation)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = ObserveAsync(task, module, operation);
    }

    public static void ForgetDeferred(Task task, string module, string operation)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = ObserveDeferredAsync(task, module, operation);
    }

    private static async Task ObserveAsync(Task task, string module, string operation)
    {
        string id = ErrorService.NewCorrelationId();
        using var scope = LogService.BeginScope(module + "/" + operation, id);
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogService.Info(module, operation + " cancelled");
        }
        catch (Exception ex)
        {
            LogService.Error(module, operation + " failed id=" + id, ex);
        }
    }

    private static async Task ObserveDeferredAsync(Task task, string module, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogService.Info(module, operation + " cancelled");
        }
        catch (Exception ex)
        {
            string id = ErrorService.NewCorrelationId();
            LogService.Error(module, operation + " failed id=" + id, ex);
        }
    }
}
