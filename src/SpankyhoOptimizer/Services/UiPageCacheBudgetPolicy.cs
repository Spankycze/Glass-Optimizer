using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

// Bounded retention of heavyweight off-screen WinUI page trees. Never evict
// the currently attached page or the destination of an in-flight navigation.
// The caller invokes this only when the GUI's own working set is elevated;
// ordinary warm navigation keeps every existing page cache behavior.
internal readonly record struct UiPageCacheBudgetEntry(string Section, DateTime LastUsedUtc);

internal static class UiPageCacheBudgetPolicy
{
    internal const long PressureWorkingSetBytes = 256L * 1024 * 1024;
    internal const int MaxHeavyPages = 5;
    internal const int MaxEvictionsPerNavigation = 2;

    internal static string[] SelectEvictions(
        IReadOnlyList<UiPageCacheBudgetEntry> present,
        string currentSection,
        string targetSection,
        int maxPages = MaxHeavyPages,
        int maxEvictions = MaxEvictionsPerNavigation)
    {
        if (present.Count <= maxPages || maxEvictions <= 0)
            return Array.Empty<string>();

        int toRelease = Math.Min(present.Count - maxPages, maxEvictions);
        // Only the oldest one or two entries are needed on normal navigation.
        // Select in-place without allocating and sorting the entire candidate list.
        int eligibleCount = 0;
        for (int i = 0; i < present.Count; i++)
        {
            string section = present[i].Section;
            if (!string.Equals(section, currentSection, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(section, targetSection, StringComparison.OrdinalIgnoreCase))
                eligibleCount++;
        }
        int count = Math.Min(toRelease, eligibleCount);
        if (count == 0) return Array.Empty<string>();
        var eviction = new string[count];
        int previousIndex = -1;
        for (int selected = 0; selected < count; selected++)
        {
            int oldest = -1;
            for (int i = 0; i < present.Count; i++)
            {
                var entry = present[i];
                if (string.Equals(entry.Section, currentSection, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Section, targetSection, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (previousIndex >= 0)
                {
                    int afterPrevious = entry.LastUsedUtc.CompareTo(present[previousIndex].LastUsedUtc);
                    if (afterPrevious < 0 || (afterPrevious == 0 && i <= previousIndex))
                        continue;
                }
                if (oldest < 0 ||
                    entry.LastUsedUtc < present[oldest].LastUsedUtc ||
                    (entry.LastUsedUtc == present[oldest].LastUsedUtc && i < oldest))
                    oldest = i;
            }
            eviction[selected] = present[oldest].Section;
            previousIndex = oldest;
        }
        return eviction;
    }
}
