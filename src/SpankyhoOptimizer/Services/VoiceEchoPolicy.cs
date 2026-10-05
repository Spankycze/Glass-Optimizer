using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SpankyhoOptimizer.Services;

public static class VoiceEchoPolicy
{
    public static bool IsLikelyEcho(string heard, string spoken)
    {
        string candidate = Normalize(heard);
        string source = Normalize(spoken);
        if (candidate.Length == 0 || source.Length == 0)
            return false;
        if (candidate.Length >= 8 && source.Contains(candidate, StringComparison.Ordinal))
            return true;

        string[] heardWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3).Distinct().ToArray();
        string[] spokenWords = source.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3).Distinct().ToArray();
        if (heardWords.Length < 3 || spokenWords.Length < 3)
            return false;

        int matched = heardWords.Count(h => spokenWords.Any(s => Similar(h, s)));
        return matched >= 3 && matched / (double)heardWords.Length >= 0.60;
    }

    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        int max = Math.Max(a.Length, b.Length);
        if (max < 4 || Math.Abs(a.Length - b.Length) > 2) return false;
        return Distance(a, b) <= (max >= 8 ? 2 : 1);
    }

    private static string Normalize(string value)
    {
        string d = (value ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int Distance(string a, string b)
    {
        int[] prev = Enumerable.Range(0, b.Length + 1).ToArray();
        int[] cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
