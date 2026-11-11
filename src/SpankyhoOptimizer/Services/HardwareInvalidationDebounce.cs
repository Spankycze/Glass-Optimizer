using System.Threading;

namespace SpankyhoOptimizer.Services;

internal static class HardwareInvalidationDebounce
{
    // Do not slide the debounce deadline forward on suppressed events.
    internal static bool TryAcquire(long nowTick, ref long lastAcceptedTick, long minSpacingMs = 500)
    {
        while (true)
        {
            long previous = Volatile.Read(ref lastAcceptedTick);
            if (previous != 0 && nowTick - previous >= 0 && nowTick - previous < minSpacingMs)
                return false;
            if (Interlocked.CompareExchange(ref lastAcceptedTick, nowTick, previous) == previous)
                return true;
        }
    }
}
