using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SpankyhoOptimizer.Services;

public static class ChatGptPlanToolRoutingPolicy
{
    private static readonly string[] LiveOrDiagnosticTerms =
    {
        "ted", "aktual", "prave", "dnes", "zitra",
        "pocasi", "predpoved", "teplota", "vytizeni",
        "kolik je hodin", "datum", "ping", "latence",
        "dns server", "bezi", "stav pc", "stav systemu",
        "windows verze", "power plan", "ventilator", "vetrak",
        "mikrofon", "voice pipeline", "hlasova pipeline",
        "lag", "problem", "nefunguje", "pada", "pomale",
        "firefox", "diablo"
    };

    private static readonly string[] GeneralPrefixes =
    {
        "co je ", "co znamena ", "vysvetli ", "vysvetli mi ",
        "jak funguje ", "proc je ", "proc se ", "jak se mas",
        "ahoj", "cau", "cus", "dobry den", "rekni mi neco o ",
        "co si myslis o "
    };
    public static bool CanUseToolFreeFastPath(string? text)
    {
        string normalized = Normalize(text);
        if (normalized.Length == 0)
            return false;

        if (LiveOrDiagnosticTerms.Any(normalized.Contains))
            return false;

        return GeneralPrefixes.Any(prefix =>
            normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string decomposed = text.Trim().ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return string.Join(
            ' ',
            sb.ToString()
                .Normalize(NormalizationForm.FormC)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
