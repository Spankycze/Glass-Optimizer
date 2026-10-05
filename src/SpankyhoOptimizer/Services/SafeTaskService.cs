using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class SafeTaskService
{
    public static void Forget(Task task, string module, string operation) =>
        _ = ObserveAsync(task, module, operation);

    public static void ForgetDeferred(Task task, string module, string operation) =>
        _ = ObserveDeferredAsync(task, module, operation);

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
