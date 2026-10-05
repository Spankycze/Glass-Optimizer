using System;

namespace SpankyhoOptimizer.Services;

public static class MemoryPressureService
{
    private const long SoftWorkingSetBytes = 420L * 1024 * 1024;
    private const long SystemPressureMinWorkingSetBytes = 320L * 1024 * 1024;

    public static bool IsElevated()
    {
        try
        {
            long ws = Environment.WorkingSet;
            if (ws >= SoftWorkingSetBytes) return true;
            if (ws < SystemPressureMinWorkingSetBytes) return false;

            var info = GC.GetGCMemoryInfo();
            if (info.HighMemoryLoadThresholdBytes > 0 && info.MemoryLoadBytes > 0)
            {
                double ratio = (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes;
                if (ratio >= 0.90) return true;
            }
        }
        catch { }

        return Environment.WorkingSet >= SoftWorkingSetBytes;
    }

    public static void TrimHeavyCaches(bool pressureAlreadyConfirmed = false)
    {
        if (!pressureAlreadyConfirmed && !IsElevated()) return;
        InstalledAppService.TrimHeavyCache();
        HardwareSnapshotService.TrimMemoryCache();
    }
}
