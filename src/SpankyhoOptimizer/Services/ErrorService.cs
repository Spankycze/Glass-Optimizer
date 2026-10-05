using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class ErrorService
{
    public static string NewCorrelationId() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static async Task RunAsync(string area, Func<Task> action, Action<string>? onError = null)
    {
        string id = NewCorrelationId();
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            LogService.Info($"{area} cancelled id={id}");
        }
        catch (Exception ex)
        {
            LogService.Error($"{area} failed id={id}", ex);
            onError?.Invoke($"Operace selhala. Kód chyby: {id}");
        }
    }

    public static string Describe(Exception ex, string id) =>
        $"{ex.GetType().Name}: {ex.Message}  [ID {id}]";
}
