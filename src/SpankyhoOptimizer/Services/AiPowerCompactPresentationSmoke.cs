using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class AiPowerCompactPresentationSmoke
{
    internal static Task RunAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException("AI_POWER_COMPACT_SMOKE_FAIL: " + scenario);
        }
        // 1-5: statuses are concise and safe even during asynchronous reloads.
        Check(AiPowerCompactPresentationPolicy.Status(false, false, false, null, null, null, null)
            .Contains("není dostupný", StringComparison.Ordinal), "offline agent");
        Check(AiPowerCompactPresentationPolicy.Status(true, false, false, 55, null, null, null)
            .Contains("čeká", StringComparison.Ordinal), "stale runtime");
        Check(AiPowerCompactPresentationPolicy.Status(true, true, false, 55, "Observe", 20, 0)
            .Contains("sledování", StringComparison.Ordinal), "observe mode");
        string active = AiPowerCompactPresentationPolicy.Status(
            true, true, true, 1234, "Performance", 12, 1);
        Check(active.Contains("EPP 12", StringComparison.Ordinal) &&
            active.Contains("Boost 1", StringComparison.Ordinal) &&
            active.Contains("PID 1234", StringComparison.Ordinal), "active status");
        Check(AiPowerCompactPresentationPolicy.Status(true, true, true, null, null, null, null)
            .Contains("EPP —", StringComparison.Ordinal), "missing metrics safe");

        // 6-9: status details cannot expand the compact header arbitrarily.
        string longMode = AiPowerCompactPresentationPolicy.Status(true, true, true, 12,
            new string('X', 200), 20, 1);
        Check(longMode.Length < 90, "bounded status");
        Check(!AiPowerCompactPresentationPolicy.Status(true, true, true, null, "A\nB", 20, 1)
            .Contains('\n'), "no injected multiline status");
        Check(AiPowerCompactPresentationPolicy.Pressure(35.3, 64.1, 8.8)
            .Contains("CPU 35,3 %", StringComparison.Ordinal) ||
            AiPowerCompactPresentationPolicy.Pressure(35.3, 64.1, 8.8)
            .Contains("CPU 35.3 %", StringComparison.Ordinal), "pressure values");
        Check(AiPowerCompactPresentationPolicy.Pressure(double.NaN, -1, double.PositiveInfinity)
            .Contains("CPU — • RAM — • volná —", StringComparison.Ordinal), "invalid sensor text");

        // 10-13: existing managed metrics remain available on demand.
        Check(AiPowerCompactPresentationPolicy.ManagedSummary(0, null)
            .Contains("nemění", StringComparison.Ordinal), "zero managed");
        Check(AiPowerCompactPresentationPolicy.ManagedSummary(3, "CPU 20→12")
            .Contains("spravuje 3", StringComparison.Ordinal), "managed count");
        Check(AiPowerCompactPresentationPolicy.ManagedSummary(4, new string('Z', 900)).Length < 190,
            "bounded managed detail");
        Check(!AiPowerCompactPresentationPolicy.ManagedSummary(2, "a\r\nb")
            .Contains('\n'), "managed text one line");
        return Task.CompletedTask;
    }
}
