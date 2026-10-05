using System;

namespace SpankyhoOptimizer.Services;

public static class VoiceBrainFallbackPolicy
{
    private static readonly string[] FailureMarkers =
    {
        "chatgpt přihlášení",
        "chatgpt plán teď",
        "chatgpt plan teď",
        "došel limit použití chatgpt",
        "chatgpt odpověď selhala",
        "chatgpt stream skončil",
        "bez kompletní odpovědi",
        "nepodařilo se dokončit živý dotaz",
        "openai brain teď není dostupný",
        "openai nevrátil odpověď",
        "gemini teď není dostupný",
        "gemini nevrátil odpověď",
        "došel bezplatný gemini limit",
        "api klíč chybí",
        "api klic chybi"
    };

    public static bool IsFailureReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return true;

        string value = reply.Trim();
        foreach (string marker in FailureMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool ShouldFallback(string? reply, bool streamedSpeech) =>
        !streamedSpeech && IsFailureReply(reply);
}
