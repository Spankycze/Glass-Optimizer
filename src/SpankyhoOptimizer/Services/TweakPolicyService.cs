using System;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public enum TweakViewMode
{
    Manual,
    Diagnostics
}

public sealed record TweakPolicyInfo(
    bool CanEdit,
    string Verdict,
    string Detail);

public static class TweakPolicyService
{
    public static TweakPolicyInfo Evaluate(TweakDefinition tweak, TweakViewMode mode)
    {
        bool supported = tweak.IsLoaded && tweak.ExecutionState != TweakExecutionState.Unsupported;
        bool safeRecommended = tweak.Recommended && tweak.Risk != TweakRisk.Advanced;
        bool pendingLazyCheck = tweak.LazyDetect && !tweak.IsLoaded &&
            tweak.ExecutionState != TweakExecutionState.Failed &&
            tweak.ExecutionState != TweakExecutionState.RequiresElevation;
        bool revalidate = supported && WindowsBuildRevalidationService.NeedsRevalidation(tweak);
        bool canEdit = supported && mode != TweakViewMode.Diagnostics;
        string verdict = revalidate ? "Needs revalidation"
            : pendingLazyCheck ? "Checking"
            : tweak.ExecutionState == TweakExecutionState.RequiresElevation ? "Requires elevation"
            : !supported ? "Unsupported"
            : tweak.ExecutionState == TweakExecutionState.RequiresRestart ? "Requires restart"
            : tweak.LastError != null || tweak.ExecutionState == TweakExecutionState.Failed ? "Needs attention"
            : safeRecommended ? "Recommended"
            : tweak.Risk == TweakRisk.Advanced ? "Advanced"
            : "Manual";

        string verified = tweak.LastVerifiedUtc is DateTime ts
            ? ts.ToLocalTime().ToString("g")
            : "nikdy";
        string detail = revalidate
            ? "Windows build se změnil; před Recommended použitím je potřeba nový read-back."
            : pendingLazyCheck
            ? "Stav se kontroluje na pozadí…"
            : !supported ? tweak.Status
            : $"Stav: {tweak.Status} ({tweak.ExecutionState}) • Ověřeno: {verified}";
        if (!string.IsNullOrWhiteSpace(tweak.LastError))
            detail += " • Chyba: " + tweak.LastError;
        if (!string.IsNullOrWhiteSpace(tweak.Warning))
            detail += " • Pozor: " + tweak.Warning;

        return new TweakPolicyInfo(canEdit, verdict, detail);
    }
}
