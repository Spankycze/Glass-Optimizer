using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class FaultInjectionService
{
    private static readonly string? Raw = Environment.GetEnvironmentVariable("SPANKY_FAULTS");
    private static readonly Dictionary<string, string> Rules = Parse(Raw);

    internal static bool Enabled => Rules.Count > 0;

    internal static async ValueTask InjectAsync(string point, CancellationToken token = default)
    {
        if (!Rules.TryGetValue(point, out string? rule) && !Rules.TryGetValue("*", out rule))
            return;

        string[] parts = rule.Split(':', 2, StringSplitOptions.TrimEntries);
        string kind = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (kind)
        {
            case "delay":
                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delayMs))
                    delayMs = 100;
                await Task.Delay(Math.Clamp(delayMs, 0, 30_000), token).ConfigureAwait(false);
                break;

            case "timeout":
                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int timeoutMs))
                    timeoutMs = 50;
                await Task.Delay(Math.Clamp(timeoutMs, 0, 30_000), token).ConfigureAwait(false);
                throw new TimeoutException($"Injected timeout at '{point}'.");

            case "cancel":
                throw new OperationCanceledException($"Injected cancellation at '{point}'.", token);

            case "denied":
                throw new UnauthorizedAccessException($"Injected access denied at '{point}'.");

            case "exception":
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(arg)
                    ? $"Injected exception at '{point}'."
                    : arg);

            default:
                throw new InvalidOperationException($"Unknown fault injection rule '{rule}' for '{point}'.");
        }
    }

    internal static void ThrowIfInjected(string point)
    {
        InjectAsync(point).AsTask().GetAwaiter().GetResult();
    }

    private static Dictionary<string, string> Parse(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (string item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            if (eq <= 0 || eq == item.Length - 1)
                continue;
            result[item[..eq].Trim()] = item[(eq + 1)..].Trim();
        }
        return result;
    }
}
