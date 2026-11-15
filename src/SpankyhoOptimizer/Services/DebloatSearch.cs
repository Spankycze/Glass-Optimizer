using System;
using System.Globalization;
using System.Text;

namespace SpankyhoOptimizer.Services;

public static class DebloatSearch
{
    // Czech users type queries without diacritics; fold both sides so
    // "media" still finds "Média".
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    public static bool Contains(string? haystack, string normalizedQuery) =>
        normalizedQuery.Length == 0 ||
        Normalize(haystack).Contains(normalizedQuery, StringComparison.Ordinal);
}
