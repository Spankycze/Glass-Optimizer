using System.Text.RegularExpressions;

namespace SpankyhoOptimizer.Services;

public static class VoiceTranscriptCleanupPolicy
{
    public static string Normalize(string input)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        text = Regex.Replace(
            text,
            @"(?i)\bp\s+miej\s*vol(?:a|á|o)\b(?:\s*,?\s*hej)?",
            "hej vole");

        text = Regex.Replace(
            text,
            @"(?i)\bhejvol(?:a|á|e|é|o)\b",
            "hej vole");

        text = Regex.Replace(
            text,
            @"(?i)\bhej\s*,?\s*vol(?:a|á|e|é|o)\b",
            "hej vole");

        text = Regex.Replace(text, @"(?i)\bnucni\b", "noční");
        text = Regex.Replace(text, @"(?i)\bdiablo\s+imortal\b", "Diablo Immortal");
        text = Regex.Replace(text, @"(?i)\b(?:fire\s*fox|firefoks)\b", "Firefox");
        text = Regex.Replace(text, @"(?i)\bchat\s*gpt\b", "ChatGPT");
        text = Regex.Replace(text, @"(?i)\brajzen\b", "Ryzen");
        text = Regex.Replace(text, @"(?i)\bge\s*force\b", "GeForce");
        text = Regex.Replace(text, @"(?i)\b(?:slysis|slysiš|slyšíš)\b", "slyšíš");

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static bool HasExcessiveCharacterRun(string input)
    {
        string text = input ?? string.Empty;
        char previous = '\0';
        int run = 0;

        foreach (char raw in text)
        {
            if (!char.IsLetterOrDigit(raw))
            {
                previous = '\0';
                run = 0;
                continue;
            }

            char current = char.ToLowerInvariant(raw);
            if (current == previous)
            {
                run++;
            }
            else
            {
                previous = current;
                run = 1;
            }

            if (run >= 6)
                return true;
        }

        return false;
    }
}
