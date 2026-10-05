using System;

namespace SpankyhoOptimizer.Services;

public static class VoiceTtsLatencyPolicy
{
    public static TimeSpan GenerationTimeout { get; } = TimeSpan.FromSeconds(5);
}
