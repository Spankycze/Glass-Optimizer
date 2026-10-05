using System;

namespace SpankyhoOptimizer.Services;

public static class GeminiTrimPolicy
{
    public static int GetRetainedChunkCount(int totalChunks, int trailingSilenceChunks)
    {
        if (totalChunks <= 1)
            return totalChunks;

        // Keep 200ms of the release tail; low-energy consonants can look like silence.
        int retained = totalChunks - Math.Max(0, trailingSilenceChunks - 2);
        return Math.Min(totalChunks, Math.Max(1, retained));
    }
}
