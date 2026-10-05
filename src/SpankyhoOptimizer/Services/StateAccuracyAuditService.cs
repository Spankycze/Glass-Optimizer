using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record StateAccuracyAuditResult(int Audited, int Unsupported, int Failed, IReadOnlyList<string> Issues);

public static class StateAccuracyAuditService
{
    private static readonly (string Section, string? Sub)[] Scopes =
    {
        ("system", null), ("gpu", null), ("memory", null), ("storage", null),
        ("network", null), ("ui", null), ("update", null), ("privacy", null),
        ("ai", null), ("defender", null),
        ("components", "Služby"), ("components", "Ovladače"), ("components", "Task Scheduler"),
        ("browsers", "Firefox"), ("browsers", "Chrome"), ("browsers", "Edge"),
        ("browsers", "Brave"), ("browsers", "Opera"), ("browsers", "Opera GX"), ("browsers", "Vivaldi")
    };

    public static async Task<StateAccuracyAuditResult> RunAsync(CancellationToken token = default)
    {
        var issues = new List<string>();
        int audited = 0, unsupported = 0, failed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coverage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scopeCoverage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in Scopes)
        {
            foreach (var tweak in CatalogService.GetTweaks(scope.Section, scope.Sub))
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(tweak.Id)) continue;
                coverage[scope.Section] = coverage.TryGetValue(scope.Section, out int count) ? count + 1 : 1;
                string scopeKey = string.IsNullOrWhiteSpace(scope.Sub) ? scope.Section : $"{scope.Section}/{scope.Sub}";
                scopeCoverage[scopeKey] = scopeCoverage.TryGetValue(scopeKey, out int scopeCount) ? scopeCount + 1 : 1;
                var compatibility = await TweakCompatibilityService.CheckAsync(tweak, token);
                if (!compatibility.Supported) { unsupported++; continue; }
                try
                {
                    bool actual = await ReadActualAsync(tweak, token);
                    await new TweakEngine().DetectAsync(new[] { tweak }, token);
                    if (!tweak.IsLoaded) throw new InvalidOperationException(tweak.LastError ?? tweak.Status);
                    audited++;
                    if (tweak.Current != actual)
                    {
                        failed++;
                        issues.Add($"{tweak.Id}: engine={tweak.Current} actual={actual}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    issues.Add($"{tweak.Id}: {ex.Message}");
                }
            }
        }

        foreach (string required in new[] { "system", "gpu", "network", "privacy", "defender" })
        {
            if (!coverage.TryGetValue(required, out int count) || count == 0)
            {
                failed++;
                issues.Add($"{required}: no tweaks were covered by the state-accuracy audit");
            }
        }

        LogService.Info("state-accuracy",
            "STATE_ACCURACY_SCOPE_COVERAGE " +
            string.Join(" ", scopeCoverage.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.Key}={x.Value}")));
        LogService.Info("state-accuracy",
            $"STATE_ACCURACY_TOTAL audited={audited} unsupported={unsupported} failed={failed} scopes={scopeCoverage.Count}");

        return new StateAccuracyAuditResult(audited, unsupported, failed, issues);
    }

    private static async Task<bool> ReadActualAsync(TweakDefinition tweak, CancellationToken token)
    {
        if (tweak.RegistryHive.HasValue && tweak.RegistryPath != null && tweak.RegistryValueName != null && tweak.RegistryEnabledValue.HasValue)
        {
            using var baseKey = RegistryKey.OpenBaseKey(tweak.RegistryHive.Value, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(tweak.RegistryPath, false);
            object? raw = key?.GetValue(tweak.RegistryValueName);
            return raw != null && Convert.ToInt64(raw) == tweak.RegistryEnabledValue.Value;
        }
        return await tweak.DetectAsync(token);
    }
}
