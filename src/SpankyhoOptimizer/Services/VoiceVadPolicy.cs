namespace SpankyhoOptimizer.Services;

public static class VoiceVadPolicy
{
    public static int RequiredEndSilenceChunks(int speechChunkCount) =>
        speechChunkCount <= 24
            ? 3
            : speechChunkCount <= 72
                ? 4
                : 5;
}
