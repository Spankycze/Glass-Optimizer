using System;
using System.Net;

namespace SpankyhoOptimizer.Services;

public static class GeminiPrimarySttPolicy
{
    public static TimeSpan PrimaryTimeout { get; } = TimeSpan.FromSeconds(6);
    public static TimeSpan FallbackModelTimeout { get; } = TimeSpan.FromSeconds(8);
    public static TimeSpan FallbackDelay { get; } = TimeSpan.Zero;

    public static bool ShouldFallbackAfterTimeout(bool timedOut) => timedOut;

    public static bool ShouldFallbackAfterStatus(HttpStatusCode status) =>
        status == HttpStatusCode.ServiceUnavailable;
}
