using System;
using System.Text;

namespace SpankyhoOptimizer.Services;

public static class VoiceSpeechChunkPolicy
{
    public static bool TryTake(StringBuilder buffer, bool firstChunk, out string chunk)
    {
        chunk = string.Empty;
        int minSentence = firstChunk ? 6 : 24;
        if (buffer.Length < minSentence)
            return false;

        int boundary = FindSentenceBoundary(buffer, minSentence);

        if (firstChunk && boundary > 72)
        {
            int softBoundary = FindSoftBoundary(buffer, minLength: 40, maxSearch: 72, minSearch: 24);
            if (softBoundary > 0)
                boundary = softBoundary;
        }
        else if (boundary < 0)
        {
            boundary = firstChunk
                ? FindSoftBoundary(buffer, minLength: 40, maxSearch: 72, minSearch: 24)
                : FindSoftBoundary(buffer, minLength: 78, maxSearch: 108, minSearch: 58);
        }

        if (boundary < 0 && !firstChunk && buffer.Length >= 145)
            boundary = FindWhitespaceBoundary(buffer, 125, 70);

        if (boundary <= 0)
            return false;

        chunk = buffer.ToString(0, boundary).Trim();
        buffer.Remove(0, boundary);
        while (buffer.Length > 0 && char.IsWhiteSpace(buffer[0]))
            buffer.Remove(0, 1);

        return chunk.Length > 0;
    }

    private static int FindSentenceBoundary(StringBuilder buffer, int minLength)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            char c = buffer[i];
            if (c is not ('.' or '!' or '?' or '\n'))
                continue;

            bool safeBoundary = i == buffer.Length - 1 || char.IsWhiteSpace(buffer[i + 1]);
            if (safeBoundary && i + 1 >= minLength)
                return i + 1;
        }

        return -1;
    }

    private static int FindSoftBoundary(
        StringBuilder buffer,
        int minLength,
        int maxSearch,
        int minSearch)
    {
        if (buffer.Length < minLength)
            return -1;

        int start = Math.Min(maxSearch, buffer.Length - 1);
        for (int i = start; i >= minSearch; i--)
        {
            if (buffer[i] is ',' or ';' or ':')
                return i + 1;
        }

        return -1;
    }

    private static int FindWhitespaceBoundary(StringBuilder buffer, int maxSearch, int minSearch)
    {
        int start = Math.Min(maxSearch, buffer.Length - 1);
        for (int i = start; i >= minSearch; i--)
        {
            if (char.IsWhiteSpace(buffer[i]))
                return i + 1;
        }

        return Math.Min(maxSearch, buffer.Length);
    }
}
