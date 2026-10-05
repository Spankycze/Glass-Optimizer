namespace SpankyhoOptimizer.Services;

public static class RealtimeVoiceVadPolicy
{
    public static int RequiredEndSilenceChunks(int speechChunkCount) =>
        speechChunkCount <= 24
            ? 5
            : speechChunkCount <= 72
                ? 6
                : 7;

    public static int NextSilenceCount(int currentSilence, bool speechResumed) =>
        speechResumed ? 0 : currentSilence + 1;
}
