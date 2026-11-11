using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// A bounded, passive per-target trial history. It does not rank processes
/// by CPU share as if that measured game latency, FPS or total RAM savings.
/// </summary>
internal static class AiPriorityTrialMemoryPolicy
{
    internal static readonly TimeSpan MinimumCooldown = TimeSpan.FromHours(1);
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    internal static readonly TimeSpan MaximumCooldown = TimeSpan.FromHours(8);
    internal const int MaximumEntries = 5;

    // ConsecutiveInconclusive is OPTIONAL to preserve the earlier on-disk
    // four-field checkpoint. New history stores max five allowlisted names.
    internal sealed record Checkpoint(
        string ProcessName, string Result, DateTime MeasuredUtc,
        DateTime NextEligibleUtc, int ConsecutiveInconclusive = 0);

    internal sealed record History(Checkpoint[] Items);

    internal static bool Accept(Checkpoint? value, DateTime nowUtc) =>
        value is not null &&
        AiAutomaticProcessPriorityPolicy.IsAllowlisted(value.ProcessName) &&
        !string.IsNullOrWhiteSpace(value.Result) &&
        value.Result.Length <= 320 &&
        value.ConsecutiveInconclusive is >= 0 and <= 4 &&
        value.MeasuredUtc <= nowUtc &&
        nowUtc - value.MeasuredUtc <= MaximumAge &&
        value.NextEligibleUtc >= value.MeasuredUtc &&
        value.NextEligibleUtc - value.MeasuredUtc <= MaximumCooldown;

    internal static bool IsExpired(Checkpoint? value, DateTime nowUtc) =>
        value is not null &&
        AiAutomaticProcessPriorityPolicy.IsAllowlisted(value.ProcessName) &&
        !string.IsNullOrWhiteSpace(value.Result) &&
        value.Result.Length <= 320 &&
        value.ConsecutiveInconclusive is >= 0 and <= 4 &&
        value.MeasuredUtc != default && value.MeasuredUtc <= nowUtc &&
        nowUtc - value.MeasuredUtc > MaximumAge &&
        value.NextEligibleUtc >= value.MeasuredUtc &&
        value.NextEligibleUtc - value.MeasuredUtc <= MaximumCooldown;

    internal static bool ShouldDefer(Checkpoint? value, string candidate, DateTime nowUtc) =>
        Accept(value, nowUtc) &&
        string.Equals(value!.ProcessName, candidate, StringComparison.OrdinalIgnoreCase) &&
        nowUtc < value.NextEligibleUtc;

    internal static bool IsInconclusive(string result) =>
        result.Contains("neprůkaz", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("bez průkazné", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("neprokázán", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("neprokázané", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("nelze bezpečně porovnat", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("nikoli prokázané", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("bez tvrzení o zrychlení", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("bez výkonového závěru", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("pozorování změny", StringComparison.OrdinalIgnoreCase);

    internal static Checkpoint Create(
        string name, string result, DateTime nowUtc, Checkpoint? prior = null)
    {
        bool inconclusive = IsInconclusive(result);
        int streak = inconclusive
            ? Math.Min(4, (prior?.ConsecutiveInconclusive ?? 0) + 1)
            : 0;
        TimeSpan cooldown = streak == 0
            ? MinimumCooldown
            : TimeSpan.FromHours(1 << (streak - 1));
        return new(name, result.Length > 320 ? result[..320] : result,
            nowUtc, nowUtc.Add(cooldown), streak);
    }

    internal static History Empty => new(Array.Empty<Checkpoint>());

    internal static Checkpoint? Find(History? history, string? candidate)
    {
        if (history?.Items is null ||
            !AiAutomaticProcessPriorityPolicy.IsAllowlisted(candidate))
            return null;
        foreach (Checkpoint item in history.Items)
            if (string.Equals(item.ProcessName, candidate,
                    StringComparison.OrdinalIgnoreCase))
                return item;
        return null;
    }

    internal static bool ShouldDefer(History? history, string candidate, DateTime nowUtc) =>
        ShouldDefer(Find(history, candidate), candidate, nowUtc);

    internal static bool ValidateHistory(History? history, DateTime nowUtc)
    {
        if (history?.Items is null || history.Items.Length > MaximumEntries)
            return false;
        for (int i = 0; i < history.Items.Length; i++)
        {
            Checkpoint item = history.Items[i];
            if (!(Accept(item, nowUtc) || IsExpired(item, nowUtc)))
                return false;
            for (int j = 0; j < i; j++)
                if (string.Equals(item.ProcessName, history.Items[j].ProcessName,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
        }
        return true;
    }

    internal static History Remember(
        History? current, string name, string result, DateTime nowUtc)
    {
        // No unbounded history, no per-tick writes; caller persists only when
        // a real trial reaches a definitive or inconclusive end.
        if (!AiAutomaticProcessPriorityPolicy.IsAllowlisted(name))
            throw new ArgumentException("Untrusted trial target", nameof(name));
        Checkpoint? previous = Find(current, name);
        Checkpoint updated = Create(name, result, nowUtc, previous);
        var items = new System.Collections.Generic.List<Checkpoint>(MaximumEntries);
        if (current?.Items is not null)
        {
            foreach (Checkpoint candidate in current.Items)
            {
                if (string.Equals(candidate.ProcessName, name,
                        StringComparison.OrdinalIgnoreCase) ||
                    !(Accept(candidate, nowUtc) || IsExpired(candidate, nowUtc)) ||
                    IsExpired(candidate, nowUtc))
                    continue;
                items.Add(candidate);
            }
        }
        if (items.Count >= MaximumEntries)
        {
            int oldest = 0;
            for (int i = 1; i < items.Count; i++)
                if (items[i].MeasuredUtc < items[oldest].MeasuredUtc)
                    oldest = i;
            items.RemoveAt(oldest);
        }
        items.Add(updated);
        return new(items.ToArray());
    }

    internal static Checkpoint? Latest(History? history)
    {
        if (history?.Items is null) return null;
        Checkpoint? latest = null;
        foreach (Checkpoint item in history.Items)
            if (latest is null || item.MeasuredUtc > latest.MeasuredUtc)
                latest = item;
        return latest;
    }

    internal static bool MustCheckOwnedImmediately(
        bool enabled, bool powerFresh, string ownedName,
        string? sampledForeground, AiWorkloadClass workload,
        DateTime appliedUtc, DateTime nowUtc)
    {
        if (!enabled || !powerFresh ||
            AiAutomaticProcessPriorityPolicy.ShouldRestore(enabled, workload,
                ownedName, sampledForeground, appliedUtc, nowUtc))
            return true;
        // All Windows Search background-priority trials check immediately on
        // each already existing FanAgent tick; no added timer/process scan.
        return AiAutomaticProcessPriorityPolicy.IsWindowsSearchIndexer(ownedName);
    }

    internal static bool CanReuseForegroundName(
        int sampledPid, int livePid, string? sampledName) =>
        sampledPid > 0 && livePid > 0 && sampledPid == livePid &&
        !string.IsNullOrWhiteSpace(sampledName);

    internal static bool SearchUserActive(
        string ownedName, string? sampledForeground, string? liveForeground) =>
        AiAutomaticProcessPriorityPolicy.IsWindowsSearchIndexer(ownedName) &&
        (string.IsNullOrWhiteSpace(liveForeground) ||
         AiAutomaticProcessPriorityPolicy.IsInteractiveSearchUi(sampledForeground) ||
         AiAutomaticProcessPriorityPolicy.IsInteractiveSearchUi(liveForeground));

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        Checkpoint p = Create("SearchIndexer", "neprůkazné", now);
        string serialized = System.Text.Json.JsonSerializer.Serialize(p);
        Checkpoint? restarted = System.Text.Json.JsonSerializer.Deserialize<Checkpoint>(
            serialized);
        History history = Empty;
        string[] candidates = {
            "OneDrive", "Dropbox", "SearchIndexer", "SearchProtocolHost",
            "SearchFilterHost"
        };
        for (int i = 0; i < candidates.Length; i++)
            history = Remember(history, candidates[i], "neprůkazné", now);
        string serializedHistory = System.Text.Json.JsonSerializer.Serialize(history);
        History? restartedHistory =
            System.Text.Json.JsonSerializer.Deserialize<History>(serializedHistory);
        if (!ValidateHistory(restartedHistory, now) ||
            restartedHistory!.Items.Length != MaximumEntries ||
            !ShouldDefer(restartedHistory, "Dropbox", now.AddMinutes(30)) ||
            ShouldDefer(restartedHistory, "MsMpEng", now) ||
            !ShouldDefer(restartedHistory, "OneDrive", now.AddMinutes(30)) ||
            ShouldDefer(restartedHistory, "OneDrive", now.AddHours(2)))
            throw new InvalidOperationException(
                "Per-target AI feedback history lost cooldowns or accepted forbidden processes.");
        DateTime retry = now;
        History backoff = Empty;
        for (int i = 0; i < 4; i++)
        {
            backoff = Remember(backoff, "SearchIndexer", "neprůkazné", retry);
            var latest = Find(backoff, "SearchIndexer")!;
            double expectedHours = 1 << i;
            if (latest.ConsecutiveInconclusive != i + 1 ||
                latest.NextEligibleUtc - latest.MeasuredUtc !=
                    TimeSpan.FromHours(expectedHours))
                throw new InvalidOperationException("Exponential retry cooldown is incorrect.");
            retry = latest.NextEligibleUtc.AddMinutes(1);
        }
        History recovered = System.Text.Json.JsonSerializer.Deserialize<History>(
            System.Text.Json.JsonSerializer.Serialize(backoff))!;
        DateTime lastMeasured = Find(recovered, "SearchIndexer")!.MeasuredUtc;
        if (!ValidateHistory(recovered, retry) ||
            !ShouldDefer(recovered, "SearchIndexer", lastMeasured.AddHours(2)) ||
            Find(recovered, "SearchIndexer")!.ConsecutiveInconclusive != 4 ||
            !ShouldDefer(recovered, "SearchIndexer", lastMeasured.AddHours(7)) ||
            ShouldDefer(recovered, "SearchIndexer", lastMeasured.AddHours(8)) ||
            !ValidateHistory(new History(new[] { p }), now) ||
            ValidateHistory(new History(new[] { p, p }), now) ||
            ValidateHistory(new History(new[] {
                p with { ProcessName = "MsMpEng" }
            }), now))
            throw new InvalidOperationException("History migration/validation or restart backoff failed.");
        if (!IsInconclusive("pozorování změny podílu CPU (nikoli prokázané zrychlení PC)") ||
            !IsInconclusive("Pracovní sada RAM procesu: nikoli prokázané uvolnění RAM"))
            throw new InvalidOperationException(
                "Observed CPU/RAM values incorrectly counted as proven speedup.");
        History reset = Remember(recovered, "SearchIndexer",
            "ověřený výsledek pouze v syntetickém testu", retry.AddHours(9));
        if (Find(reset, "SearchIndexer")!.ConsecutiveInconclusive != 0 ||
            Find(reset, "SearchIndexer")!.NextEligibleUtc -
                Find(reset, "SearchIndexer")!.MeasuredUtc != MinimumCooldown)
            throw new InvalidOperationException("Successful measured observation did not reset backoff.");
        if (restarted != p ||
            !ShouldDefer(restarted, "SearchIndexer", now.AddMinutes(30)) ||
            !Accept(p, now) || !ShouldDefer(p, "searchindexer", now.AddMinutes(30)) ||
            ShouldDefer(p, "OneDrive", now.AddMinutes(30)) ||
            ShouldDefer(p, "SearchIndexer", now.AddHours(2)) ||
            Accept(p with { Result = "" }, now) ||
            Accept(p with { ProcessName = "MsMpEng" }, now) ||
            Accept(p with { MeasuredUtc = now.AddMinutes(1) }, now) ||
            Accept(p with { NextEligibleUtc = now.AddDays(1) }, now) ||
            Accept(p, now.AddDays(8)) ||
            !IsExpired(p, now.AddDays(8)) ||
            IsExpired(p with { ProcessName = "MsMpEng" }, now.AddDays(8)) ||
            IsExpired(p with { MeasuredUtc = now.AddMinutes(1) }, now) ||
            IsExpired(p, now.AddDays(2)) ||
            !MustCheckOwnedImmediately(true, true, "SearchIndexer",
                "chrome", AiWorkloadClass.Interactive, now.AddSeconds(-4), now) ||
            !MustCheckOwnedImmediately(false, true, "OneDrive",
                "chrome", AiWorkloadClass.Interactive, now.AddSeconds(-4), now) ||
            !MustCheckOwnedImmediately(true, false, "OneDrive",
                "chrome", AiWorkloadClass.Interactive, now.AddSeconds(-4), now) ||
            MustCheckOwnedImmediately(true, true, "OneDrive",
                "chrome", AiWorkloadClass.Interactive, now.AddSeconds(-4), now) ||
            !CanReuseForegroundName(512, 512, "chrome") ||
            CanReuseForegroundName(512, 513, "chrome") ||
            CanReuseForegroundName(512, 0, "chrome") ||
            CanReuseForegroundName(0, 512, "chrome") ||
            CanReuseForegroundName(512, 512, "") ||
            !SearchUserActive("SearchIndexer", "chrome", "SearchHost") ||
            !SearchUserActive("SearchProtocolHost", "SearchApp", "chrome") ||
            !SearchUserActive("SearchFilterHost", "chrome", "") ||
            SearchUserActive("OneDrive", "chrome", "SearchHost") ||
            SearchUserActive("SearchIndexer", "chrome", "firefox"))
            throw new InvalidOperationException(
                "Priority scheduling did not protect Windows Search or bounded restart cooldown.");
        return "AI_PRIORITY_TRIAL_MEMORY_SMOKE_OK lastResultBounded=true " +
            "perTargetRestartCooldown=true boundedFiveTargets=true " +
            "exponential1to8h=true noFakeCpuRamSuccess=true legacyFeedbackMigrates=true " +
            "expiredCheckpointIgnored=true immediateSearchSafety=true " +
            "emptyLiveForegroundFailsClosed=true stablePidAvoidsHandle=true " +
            "criticalWindowsExcluded=true writes=0";
    }
}
