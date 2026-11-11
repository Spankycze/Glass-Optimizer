using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Identity of a passive CPU observation. Names alone do not distinguish
/// recycled/restarted processes. A valid experiment requires the exact same
/// observed PID in A/B/A, plus the same active window PID.
/// </summary>
internal static class AiPriorityProcessIdentityPolicy
{
    internal static bool TryGetTargetPid(
        AdaptivePowerRuntimeStatus? snapshot, string? name, out int pid)
    {
        pid = 0;
        if (snapshot is null || !AiAutomaticProcessPriorityPolicy.IsAllowlisted(name))
            return false;
        int instances;
        if (string.Equals(name, "OneDrive", StringComparison.OrdinalIgnoreCase))
        {
            instances = snapshot.OneDriveInstanceCount;
            pid = snapshot.OneDriveProcessId;
        }
        else if (string.Equals(name, "Dropbox", StringComparison.OrdinalIgnoreCase))
        {
            instances = snapshot.DropboxInstanceCount;
            pid = snapshot.DropboxProcessId;
        }
        else if (string.Equals(name, "EpicGamesLauncher", StringComparison.OrdinalIgnoreCase))
        {
            instances = snapshot.EpicGamesLauncherInstanceCount;
            pid = snapshot.EpicGamesLauncherProcessId;
        }
        else if (string.Equals(name, "qbittorrent", StringComparison.OrdinalIgnoreCase))
        {
            instances = snapshot.QBittorrentInstanceCount;
            pid = snapshot.QBittorrentProcessId;
        }
        else if (string.Equals(name, "SearchIndexer", StringComparison.OrdinalIgnoreCase))
        {
            instances = snapshot.SearchIndexerInstanceCount;
            pid = snapshot.SearchIndexerProcessId;
        }
        else if (string.Equals(name, "SearchProtocolHost", StringComparison.OrdinalIgnoreCase))
        {
            instances = snapshot.SearchProtocolHostInstanceCount;
            pid = snapshot.SearchProtocolHostProcessId;
        }
        else if (string.Equals(name, "SearchFilterHost", StringComparison.OrdinalIgnoreCase))
        {
            instances = snapshot.SearchFilterHostInstanceCount;
            pid = snapshot.SearchFilterHostProcessId;
        }
        else return false;
        if (instances != 1 || pid <= 0)
        {
            pid = 0;
            return false;
        }
        return true;
    }

    internal static bool Matches(
        AdaptivePowerRuntimeStatus? power, string target,
        int expectedTargetPid, int expectedForegroundPid)
    {
        return expectedTargetPid > 0 && expectedForegroundPid > 0 &&
            power is not null && power.ForegroundProcessId == expectedForegroundPid &&
            TryGetTargetPid(power, target, out int actualPid) &&
            actualPid == expectedTargetPid;
    }

    internal static bool IdentityChanged(
        AdaptivePowerRuntimeStatus? status, string target,
        int expectedTargetPid, int expectedForegroundPid) =>
        !Matches(status, target, expectedTargetPid, expectedForegroundPid);

    internal static string RunFunctionalSmoke()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var status = new AdaptivePowerRuntimeStatus(
            now, "Observation", 10, "none", null, null, null, false, false,
            20, 2, "Interactive", "", "SearchIndexer", 4.0, "chrome",
            null, null, 0, 0, 3.0, 2.5, 2.2, 1, 1, 1,
            501, 0, 0, 1001, 1002, 1003);
        if (!TryGetTargetPid(status, "SearchIndexer", out int id) || id != 1001 ||
            !TryGetTargetPid(status, "SearchProtocolHost", out id) || id != 1002 ||
            !TryGetTargetPid(status, "SearchFilterHost", out id) || id != 1003 ||
            !Matches(status, "SearchIndexer", 1001, 501) ||
            IdentityChanged(status, "SearchIndexer", 1001, 501) ||
            !IdentityChanged(null, "SearchIndexer", 1001, 501) ||
            !IdentityChanged(status with { SearchIndexerProcessId = 2001 },
                "SearchIndexer", 1001, 501) ||
            !IdentityChanged(status with { ForegroundProcessId = 502 },
                "SearchIndexer", 1001, 501) ||
            Matches(status with { SearchIndexerProcessId = 2001 }, "SearchIndexer", 1001, 501) ||
            Matches(status with { ForegroundProcessId = 502 }, "SearchIndexer", 1001, 501) ||
            Matches(status with { SearchIndexerInstanceCount = 2 }, "SearchIndexer", 1001, 501) ||
            Matches(status with { SearchIndexerProcessId = 0 }, "SearchIndexer", 1001, 501) ||
            Matches(status, "SearchIndexer", 0, 501) ||
            Matches(status, "SearchIndexer", 1001, 0) ||
            TryGetTargetPid(status, "svchost", out _) ||
            TryGetTargetPid(status with { SearchIndexerInstanceCount = -1 }, "SearchIndexer", out _) ||
            TryGetTargetPid(status with { SearchIndexerProcessId = -1 }, "SearchIndexer", out _))
            throw new InvalidOperationException(
                "AI priority A/B/A accepted a recycled PID, swapped foreground or protected process.");
        // No extra process enumeration/handle in the hot path: the existing
        // immutable native Process[] snapshot supplies both identities.
        const int probes = 32768;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < probes; i++)
            if (!Matches(status, "SearchIndexer", 1001, 501))
                throw new InvalidOperationException("Stable native snapshot identity lost.");
        long allocatedB = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        if (allocatedB > 128)
            throw new InvalidOperationException("Priority PID hot path allocated unexpectedly.");
        return "AI_PRIORITY_PROCESS_IDENTITY_SMOKE_OK targetPidABAStrict=true " +
            "foregroundPidABAStrict=true restartedProcessRejected=true " +
            "earlyRollbackOnIdentityChange=true multiInstanceRejected=true " +
            $"hotChecks={probes} hotMs={elapsedMs:0.###} hotAllocB={allocatedB} " +
            "criticalWindowsProtected=true writes=0";
    }
}
