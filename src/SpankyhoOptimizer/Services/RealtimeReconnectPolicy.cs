namespace SpankyhoOptimizer.Services;

public static class RealtimeReconnectPolicy
{
    public static int GetDelayMs(int attempt) =>
        attempt <= 0 ? 250 :
        attempt == 1 ? 500 :
        attempt == 2 ? 1000 : 2000;

    public static bool ShouldRetry(int attempt) => attempt < 3;
}
