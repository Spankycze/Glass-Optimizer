using System;

namespace SpankyhoOptimizer.Services;

internal static class FanRpmTooltipPolicy
{
    // Only four fan channels are displayed. Format directly into the resulting
    // string: no LINQ array, StringBuilder buffer or per-RPM temporary strings.
    internal static string FormatFirstFour(int[]? rpms)
    {
        if (rpms is null || rpms.Length == 0) return "RPM —";

        int channels = Math.Min(4, rpms.Length);
        int active = 0;
        int digits = 0;
        Span<char> scratch = stackalloc char[11];
        for (int i = 0; i < channels; i++)
        {
            int rpm = rpms[i];
            if (rpm <= 100) continue;
            if (!rpm.TryFormat(scratch, out int written))
                throw new InvalidOperationException("Unable to format fan RPM.");
            digits += written;
            active++;
        }
        if (active == 0) return "RPM 0";

        // Prefix "Fan ", suffix " RPM", plus '/' between active channels.
        return string.Create(8 + digits + active - 1, rpms, static (destination, values) =>
        {
            "Fan ".AsSpan().CopyTo(destination);
            int offset = 4;
            bool first = true;
            for (int i = 0; i < Math.Min(4, values.Length); i++)
            {
                int rpm = values[i];
                if (rpm <= 100) continue;
                if (!first) destination[offset++] = '/';
                first = false;
                if (!rpm.TryFormat(destination[offset..], out int written))
                    throw new InvalidOperationException("Unable to format fan RPM.");
                offset += written;
            }
            " RPM".AsSpan().CopyTo(destination[offset..]);
        });
    }
}
