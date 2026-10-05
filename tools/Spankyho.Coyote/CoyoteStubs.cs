namespace SpankyhoOptimizer.Services;

internal static class ErrorService
{
    private static int _id;
    public static string NewCorrelationId() => Interlocked.Increment(ref _id).ToString();
}

internal static class LogService
{
    public static void Info(string message) { }
    public static void Error(string message, Exception ex) { }
}

internal static class DiagnosticsTelemetry
{
    public static IDisposable Start(string name, params (string Key, object? Value)[] tags) => NullScope.Instance;
    public static void RecordDuration(string name, double milliseconds, params (string Key, object? Value)[] tags) { }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
