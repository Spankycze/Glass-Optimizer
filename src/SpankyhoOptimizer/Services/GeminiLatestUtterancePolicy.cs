namespace SpankyhoOptimizer.Services;

public static class GeminiLatestUtterancePolicy
{
    public static bool ShouldDispatch(int generation, int latestGeneration) =>
        generation == latestGeneration;
}
