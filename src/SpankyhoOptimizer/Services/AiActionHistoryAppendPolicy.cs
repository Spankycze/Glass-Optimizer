using System;

namespace SpankyhoOptimizer.Services;

internal static class AiActionHistoryAppendPolicy
{
    internal static AiSystemActionHistoryEntry[] Append(
        AiSystemActionHistoryEntry[] source, AiSystemActionHistoryEntry next)
    {
        // Retain newest 199 entries and append one fresh immutable record.
        // Unlike List + RemoveRange + ToArray this allocates exactly one
        // result array and does not copy the discarded oldest rows.
        int kept = Math.Min(source.Length, 199);
        var result = new AiSystemActionHistoryEntry[kept + 1];
        Array.Copy(source, source.Length - kept, result, 0, kept);
        result[kept] = next;
        return result;
    }
}
