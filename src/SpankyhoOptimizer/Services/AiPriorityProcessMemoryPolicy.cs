using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Passive per-process WORKING SET snapshots collected by the existing CPU
/// sampling pass. Scheduling priority does not free RAM. This is evidence
/// quality only, never an automatic EmptyWorkingSet/process trim action.
/// </summary>
internal static class AiPriorityProcessMemoryPolicy
{
    internal const int RequiredSnapshots = 2;

    internal readonly record struct Window(
        int Count, double TotalMiB, double MinMiB, double MaxMiB,
        DateTime LastUpdatedUtc)
    {
        internal double AverageMiB => Count > 0 ? TotalMiB / Count : double.NaN;
    }

    internal static bool TryRead(
        AdaptivePowerRuntimeStatus? sample, string processName,
        out double workingSetMiB)
    {
        workingSetMiB = double.NaN;
        if (!AiPriorityProcessIdentityPolicy.TryGetTargetPid(
                sample, processName, out _) ||
            sample is null ||
            !AiWorkingSetSamplingPolicy.IsFresh(
                sample.WorkingSetObservedUtc, sample.UpdatedUtc))
            return false;
        double? measured =
            string.Equals(processName, "OneDrive", StringComparison.OrdinalIgnoreCase)
                ? sample!.OneDriveWorkingSetMiB
                : string.Equals(processName, "Dropbox", StringComparison.OrdinalIgnoreCase)
                    ? sample!.DropboxWorkingSetMiB
                    : string.Equals(processName, "EpicGamesLauncher", StringComparison.OrdinalIgnoreCase)
                        ? sample!.EpicGamesLauncherWorkingSetMiB
                    : string.Equals(processName, "qbittorrent", StringComparison.OrdinalIgnoreCase)
                        ? sample!.QBittorrentWorkingSetMiB
                    : string.Equals(processName, "SearchIndexer", StringComparison.OrdinalIgnoreCase)
                        ? sample!.SearchIndexerWorkingSetMiB
                        : string.Equals(processName, "SearchProtocolHost", StringComparison.OrdinalIgnoreCase)
                            ? sample!.SearchProtocolHostWorkingSetMiB
                            : string.Equals(processName, "SearchFilterHost", StringComparison.OrdinalIgnoreCase)
                                ? sample!.SearchFilterHostWorkingSetMiB
                                : null;
        if (measured is not double value || !double.IsFinite(value) ||
            value < 0 || value > 1048576)
            return false;
        workingSetMiB = value;
        return true;
    }

    internal static Window AddDistinct(
        Window current, AdaptivePowerRuntimeStatus? sample, string processName)
    {
        if (sample is null ||
            sample.WorkingSetObservedUtc <= current.LastUpdatedUtc ||
            !TryRead(sample, processName, out double workingSetMiB))
            return current;
        return new(
            current.Count + 1, current.TotalMiB + workingSetMiB,
            current.Count == 0 ? workingSetMiB :
                Math.Min(current.MinMiB, workingSetMiB),
            current.Count == 0 ? workingSetMiB :
                Math.Max(current.MaxMiB, workingSetMiB),
            sample.WorkingSetObservedUtc);
    }

    internal static bool Stable(Window value) =>
        value.Count >= RequiredSnapshots &&
        double.IsFinite(value.AverageMiB) &&
        value.MinMiB >= 0 &&
        value.MaxMiB - value.MinMiB <=
            Math.Max(64, value.AverageMiB * 0.25);

    internal static bool ComparableAba(Window a, Window b, Window restored) =>
        Stable(a) && Stable(b) && Stable(restored) &&
        Math.Abs(a.AverageMiB - restored.AverageMiB) <=
            Math.Max(64, a.AverageMiB * 0.20);

    internal static string Describe(Window a, Window b, Window restored) =>
        !ComparableAba(a, b, restored)
            ? "RAM procesu: nelze bezpečně porovnat, žádná prokázaná úspora"
            : $"Pracovní sada RAM procesu (pozorování): " +
              $"A={a.AverageMiB:0.##} MiB / B={b.AverageMiB:0.##} MiB / " +
              $"A={restored.AverageMiB:0.##} MiB; " +
              "nikoli prokázané uvolnění RAM Windows";

    internal static string RunFunctionalSmoke()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var sample = new AdaptivePowerRuntimeStatus(
            now.AddSeconds(-20), "Observation", 20, "", null, null, null,
            false, false, 20, 2, "Interactive", "", "OneDrive", 3.2, "chrome",
            OneDriveInstanceCount: 1, OneDriveProcessId: 111,
            ForegroundProcessId: 222, OneDriveWorkingSetMiB: 120,
            WorkingSetObservedUtc: now.AddSeconds(-20));
        Window baseline = AddDistinct(default, sample, "OneDrive");
        Window sameTime = AddDistinct(baseline, sample, "OneDrive");
        baseline = AddDistinct(baseline, sample with {
            UpdatedUtc = now.AddSeconds(-10),
            WorkingSetObservedUtc = now.AddSeconds(-10),
            OneDriveWorkingSetMiB = 124
        }, "OneDrive");
        Window candidate = AddDistinct(default, sample with {
            OneDriveWorkingSetMiB = 110
        }, "OneDrive");
        candidate = AddDistinct(candidate, sample with {
            UpdatedUtc = now.AddSeconds(-10),
            WorkingSetObservedUtc = now.AddSeconds(-10),
            OneDriveWorkingSetMiB = 112
        }, "OneDrive");
        Window restored = AddDistinct(default, sample with {
            OneDriveWorkingSetMiB = 125
        }, "OneDrive");
        restored = AddDistinct(restored, sample with {
            UpdatedUtc = now.AddSeconds(-10),
            WorkingSetObservedUtc = now.AddSeconds(-10),
            OneDriveWorkingSetMiB = 123
        }, "OneDrive");
        Window fakeSecond = AddDistinct(
            AddDistinct(default, sample, "OneDrive"),
            sample with { UpdatedUtc = now.AddSeconds(-10) },
            "OneDrive");
        if (fakeSecond.Count != 1 ||
            AddDistinct(default, sample with {
                WorkingSetObservedUtc = now.AddMinutes(-2)
            }, "OneDrive").Count != 0 ||
            AddDistinct(default, sample with {
                WorkingSetObservedUtc = default
            }, "OneDrive").Count != 0 ||
            sameTime.Count != 1 ||
            !ComparableAba(baseline, candidate, restored) ||
            !Describe(baseline, candidate, restored).Contains(
                "nikoli prokázané", StringComparison.Ordinal) ||
            ComparableAba(baseline, candidate,
                restored with { MaxMiB = 900 }) ||
            ComparableAba(baseline, candidate,
                restored with { TotalMiB = 1200 }) ||
            TryRead(sample with { OneDriveInstanceCount = 2 },
                "OneDrive", out _) ||
            TryRead(sample with { OneDriveProcessId = 0 },
                "OneDrive", out _) ||
            TryRead(sample with { OneDriveWorkingSetMiB = null },
                "OneDrive", out _) ||
            TryRead(sample with { OneDriveWorkingSetMiB = double.NaN },
                "OneDrive", out _) ||
            TryRead(sample, "MsMpEng", out _) ||
            AddDistinct(default, sample with {
                OneDriveWorkingSetMiB = null
            }, "OneDrive").Count != 0)
            throw new InvalidOperationException(
                "AI passive process RAM policy confused snapshots, missing PID or claimed freed RAM.");

        const int rounds = 32768;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
            if (!TryRead(sample, "OneDrive", out double mib) || mib != 120)
                throw new InvalidOperationException("Passive RAM snapshot lost.");
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
        if (bytes > 128)
            throw new InvalidOperationException("Passive RAM hot path allocated unexpectedly.");
        return $"AI_PASSIVE_PROCESS_RAM_SMOKE_OK samePid=true multiInstanceRejected=true " +
            $"cachedReuseNotDistinct=true workingSetNotSavedRam=true noNativeTrim=true syntheticReads={rounds} " +
            $"elapsedMs={ms:0.###} allocatedB={bytes} writes=0";
    }
}
