namespace SpankyhoOptimizer.Services;

public static class GeminiBargeInPolicy
{
    public static double GetStartThreshold(double normalThreshold, bool ttsActive) =>
        ttsActive ? normalThreshold * 2.0 : normalThreshold;

    public static int RequiredEvidenceChunks(bool ttsActive) =>
        ttsActive ? 3 : 2;
}
