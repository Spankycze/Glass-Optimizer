using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

public static class VoiceHistoryWindowPolicy
{
    public const int MaxMessages = 14;
    public const int MaxCharacters = 6000;

    public static int GetStartIndex(IReadOnlyList<int> contentLengths)
    {
        if (contentLengths.Count == 0)
            return 0;

        int start = Math.Max(0, contentLengths.Count - MaxMessages);
        int chars = 0;

        for (int i = contentLengths.Count - 1; i >= start; i--)
        {
            int next = chars + Math.Max(0, contentLengths[i]);
            if (next > MaxCharacters)
                return AlignToExchangeStart(i + 1, contentLengths.Count);

            chars = next;
        }

        return AlignToExchangeStart(start, contentLengths.Count);
    }

    private static int AlignToExchangeStart(int start, int count)
    {
        int bounded = Math.Clamp(start, 0, count);
        if (bounded == 0 || bounded == count)
            return bounded;

        return (bounded & 1) == 0
            ? bounded
            : Math.Min(count, bounded + 1);
    }
}
