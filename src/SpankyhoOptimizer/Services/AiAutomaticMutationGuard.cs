using System;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static class AiAutomaticMutationGuard
{
    public static bool IsProtectedManualSection(TweakDefinition tweak) =>
        tweak.Section.Equals("system", StringComparison.OrdinalIgnoreCase) ||
        tweak.Section.Equals("ui", StringComparison.OrdinalIgnoreCase) ||
        tweak.Section.Equals("privacy", StringComparison.OrdinalIgnoreCase) ||
        tweak.Section.Equals("defender", StringComparison.OrdinalIgnoreCase) ||
        tweak.Group.Contains("Explorer", StringComparison.OrdinalIgnoreCase);

    public static bool IsAiOwnedMutation(TweakDefinition tweak) =>
        tweak.Id.StartsWith("ai.", StringComparison.OrdinalIgnoreCase);

    public static bool Blocks(TweakDefinition tweak) =>
        IsAiOwnedMutation(tweak) && IsProtectedManualSection(tweak);

    public static string Reason(TweakDefinition tweak) =>
        Blocks(tweak)
            ? "AI automatic mutation blocked: System/UI/Explorer/Security are manual-only."
            : string.Empty;
}
