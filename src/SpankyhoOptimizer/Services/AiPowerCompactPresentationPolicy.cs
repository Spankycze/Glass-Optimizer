using System;

namespace SpankyhoOptimizer.Services;

// Presentation only: concise status for existing Power Plan controls, without
// additional metrics, polling, diagnostics, or Windows setting changes.
internal static class AiPowerCompactPresentationPolicy
{
    internal static string Status(bool agentLive, bool runtimeLive, bool active,
        int? agentPid, string? mode, int? epp, int? boost)
    {
        if (!agentLive) return "AI Power • FanAgent není dostupný";
        if (!runtimeLive) return "AI Power • čeká na aktuální stav runtime";
        if (!active) return "AI Power • pouze sledování";

        string safeMode = string.IsNullOrWhiteSpace(mode)
            ? "Balanced" : LimitOneLine(mode, 28);
        return $"AI Power • {safeMode} • EPP {epp?.ToString() ?? "—"}" +
            $" • Boost {boost?.ToString() ?? "—"}" +
            (agentPid is > 0 ? $" • PID {agentPid}" : "");
    }

    internal static string Pressure(double cpuPercent, double ramPercent, double freeRamGiB)
    {
        static string Percent(double value) =>
            double.IsFinite(value) && value is >= 0 and <= 100
                ? $"{value:0.#} %" : "—";
        string free = double.IsFinite(freeRamGiB) && freeRamGiB >= 0
            ? $"{freeRamGiB:0.#} GiB" : "—";
        return $"CPU {Percent(cpuPercent)} • RAM {Percent(ramPercent)} • volná {free}";
    }

    internal static string ManagedSummary(int count, string? examples)
    {
        if (count <= 0) return "AI nemění žádné sledované hodnoty plánu.";
        string safe = LimitOneLine(examples ?? "", 155);
        return $"AI spravuje {count} hodnot" +
            (safe.Length > 0 ? ": " + safe : ".");
    }

    private static string LimitOneLine(string value, int maxChars)
    {
        string cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= maxChars
            ? cleaned : cleaned[..(maxChars - 1)] + "…";
    }
}
