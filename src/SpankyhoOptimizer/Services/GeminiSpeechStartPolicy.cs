namespace SpankyhoOptimizer.Services;

public readonly record struct GeminiSpeechStartDecision(
    int EvidenceChunks,
    bool ShouldStart);

public static class GeminiSpeechStartPolicy
{
    private const int RequiredEvidenceChunks = 2;

    public static GeminiSpeechStartDecision Next(
        int currentEvidence,
        bool aboveThreshold,
        int requiredEvidenceChunks = RequiredEvidenceChunks)
    {
        int evidence = aboveThreshold ? currentEvidence + 1 : 0;
        return new(evidence, evidence >= requiredEvidenceChunks);
    }
}
