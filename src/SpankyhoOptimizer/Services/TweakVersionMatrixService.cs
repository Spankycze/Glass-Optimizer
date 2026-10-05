using System;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record TweakVersionStatus(bool Supported, bool Tested, int CurrentBuild,
    int MinimumBuild, int TestedThroughBuild, string Message);

public static class TweakVersionMatrixService
{
    public const int OptimizerTestedThroughBuild = 26300;

    public static TweakVersionStatus Check(TweakDefinition tweak)
    {
        int current = Environment.OSVersion.Version.Build;
        if (current < tweak.MinWindowsBuild)
            return new(false, false, current, tweak.MinWindowsBuild, OptimizerTestedThroughBuild,
                $"Vyžaduje Windows build {tweak.MinWindowsBuild}+.");
        bool tested = current <= OptimizerTestedThroughBuild;
        return new(true, tested, current, tweak.MinWindowsBuild, OptimizerTestedThroughBuild,
            tested ? $"Kompatibilní s buildem {current}." : $"Build {current} je novější než validační rozsah Optimizeru ({OptimizerTestedThroughBuild}); změna bude provedena jen po read-back ověření.");
    }
}
