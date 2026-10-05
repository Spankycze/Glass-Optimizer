namespace SpankyhoOptimizer.Services;

public static class VoiceFanPresetIntentPolicy
{
    private static readonly string[] Night =
    {
        "nocni rezim", "nocni profil", "vetraky na nocni", "dej vetraky na nocni"
    };

    private static readonly string[] Quiet =
    {
        "tichy rezim", "tichy profil", "vetraky na tichy", "silent rezim"
    };

    private static readonly string[] Auto =
    {
        "auto rezim", "automaticky rezim", "automaticky rezim vetraku",
        "dej vetraky na auto", "vetraky na auto"
    };

    private static readonly string[] Balanced =
    {
        "normalni rezim", "normalni rezim vetraku", "balanced rezim",
        "vyvazeny rezim", "vetraky normalne"
    };

    private static readonly string[] Performance =
    {
        "vykonny rezim", "vetraky na vykon", "performance rezim",
        "gaming rezim", "gaming rezim vetraku", "vetraky naplno"
    };

    public static string? TryResolve(string normalizedText)
    {
        if (SpankyVoiceToolService.MatchesIntent(normalizedText, Night))
            return "night";
        if (SpankyVoiceToolService.MatchesIntent(normalizedText, Quiet))
            return "quiet";
        if (SpankyVoiceToolService.MatchesIntent(normalizedText, Auto))
            return "auto";
        if (SpankyVoiceToolService.MatchesIntent(normalizedText, Balanced))
            return "balanced";
        if (SpankyVoiceToolService.MatchesIntent(normalizedText, Performance))
            return "performance";

        return null;
    }
}
