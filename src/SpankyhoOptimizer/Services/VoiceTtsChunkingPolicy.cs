using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpankyhoOptimizer.Services;

public static class VoiceTtsChunkingPolicy
{
    private const int FirstTargetChars = 52;
    private const int TargetChars = 90;

    public static string[] Split(string text)
    {
        string cleaned = Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");
        if (cleaned.Length == 0)
            return Array.Empty<string>();

        var result = new List<string>();

        if (cleaned.Length > FirstTargetChars)
        {
            int firstCut = FindCut(cleaned, FirstTargetChars);
            result.Add(cleaned[..firstCut].Trim());
            cleaned = cleaned[firstCut..].TrimStart();
        }

        if (cleaned.Length == 0)
            return result.Where(x => x.Length > 0).ToArray();

        foreach (string sentence in Regex.Split(cleaned, @"(?<=[.!?])\s+"))
            SplitSentence(sentence.Trim(), result);

        return result.Where(x => x.Length > 0).ToArray();
    }

    private static void SplitSentence(string sentence, List<string> result)
    {
        while (sentence.Length > TargetChars)
        {
            int cut = FindCut(sentence, TargetChars);
            result.Add(sentence[..cut].Trim());
            sentence = sentence[cut..].TrimStart();
        }

        if (sentence.Length > 0)
            result.Add(sentence);
    }

    private static int FindCut(string text, int target)
    {
        int search = Math.Min(target, text.Length - 1);

        int comma = text.LastIndexOf(',', search);
        if (comma >= target / 2)
            return comma + 1;

        int space = text.LastIndexOf(' ', search);
        if (space >= target / 2)
            return space;

        int nextSpace = text.IndexOf(' ', search);
        if (nextSpace > 0 && nextSpace <= target + 3)
            return nextSpace;

        return Math.Min(target, text.Length);
    }
}
