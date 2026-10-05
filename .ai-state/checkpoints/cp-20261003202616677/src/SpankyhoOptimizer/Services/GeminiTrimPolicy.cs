namespace SpankyhoOptimizer.Services;

public static class GeminiTrimPolicy
{
    public static int GetRetainedChunkCount(int totalChunks, int trailingSilenceChunks)
    {
        if (totalChunks <= 1)
            return totalChunks;

        int retained = totalChunks - trailingSilenceChunks;
        return retained < 1 ? 1 : retained;
    }
}
