using System;

namespace SpankyhoOptimizer.Services;

// Reject native-counter discontinuities instead of reporting misleading CPU or
// physical-memory numbers after sleep/resume, clock changes or provider failure.
internal static class AiNativeSamplingValidity
{
    internal static bool CanCompareCpu(bool previousReadable, DateTime previousUtc,
        DateTime nowUtc, ulong oldTotal, ulong newTotal, ulong oldIdle, ulong newIdle) =>
        previousReadable && previousUtc != default && nowUtc > previousUtc &&
        nowUtc - previousUtc <= TimeSpan.FromSeconds(60) &&
        newTotal > oldTotal && newIdle >= oldIdle &&
        newIdle - oldIdle <= newTotal - oldTotal;

    internal static bool TryUsedMemoryMb(ulong totalPhysicalBytes, ulong availablePhysicalBytes,
        out double usedMemoryMb)
    {
        if (totalPhysicalBytes == 0 || availablePhysicalBytes > totalPhysicalBytes)
        {
            usedMemoryMb = double.NaN;
            return false;
        }
        usedMemoryMb = (totalPhysicalBytes - availablePhysicalBytes) / 1048576d;
        return true;
    }
}
