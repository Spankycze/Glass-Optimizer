using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record StorageOptimizationSnapshot(
    bool? TrimEnabled,
    int? LastAccessMode,
    string LastAccessText,
    int? EightDotThreeMode,
    string EightDotThreeText,
    bool? ScheduledOptimizeEnabled,
    string WriteCacheText);

public static class StorageOptimizationService
{
    private const string ScheduledDefrag = @"\Microsoft\Windows\Defrag\ScheduledDefrag";
    private static readonly object SnapshotLock = new();
    private static readonly SemaphoreSlim SnapshotLoadGate = new(1, 1);
    private static StorageOptimizationSnapshot? _recentSnapshot;
    private static long _recentSnapshotStamp;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(2);
    private static int _actualProbeCount;
    internal static int ActualProbeCountForDiagnostics => Volatile.Read(ref _actualProbeCount);

    // This is a read-only two-second snapshot cache, never an authorization
    // for writes. An NTP/Windows wall-clock correction cannot extend its TTL.
    internal static bool IsSnapshotFresh(long observedStamp, long nowStamp,
        long frequency, TimeSpan ttl) =>
        observedStamp > 0 && nowStamp >= observedStamp && frequency > 0 &&
        ttl > TimeSpan.Zero &&
        (nowStamp - (double)observedStamp) / frequency < ttl.TotalSeconds;

    internal static string RunSnapshotMonotonicCacheSmoke()
    {
        const long frequency = 10_000;
        const long start = 100_000;
        long expires = start + 2 * frequency;
        if (!IsSnapshotFresh(start, start, frequency, SnapshotTtl) ||
            !IsSnapshotFresh(start, expires - 1, frequency, SnapshotTtl) ||
            IsSnapshotFresh(start, expires, frequency, SnapshotTtl) ||
            IsSnapshotFresh(start, start - 1, frequency, SnapshotTtl) ||
            IsSnapshotFresh(0, start, frequency, SnapshotTtl) ||
            IsSnapshotFresh(start, expires, 0, SnapshotTtl) ||
            IsSnapshotFresh(start, start, frequency, TimeSpan.Zero))
            throw new InvalidOperationException("Storage snapshot elapsed-cache policy regressed.");
        DateTime wall = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (wall.AddHours(-2) >= wall ||
            !IsSnapshotFresh(start, start + frequency, frequency, SnapshotTtl))
            throw new InvalidOperationException("SSD snapshot cache depended on mutable Windows time.");
        return "STORAGE_SNAPSHOT_MONOTONIC_SMOKE_OK twoSecondTtl=true " +
            "backwardsClockIgnored=true invalidCountersFailClosed=true writes=0";
    }

    public static async Task<StorageOptimizationSnapshot> ReadAsync(
        CancellationToken token = default, bool forceRefresh = false)
    {
        token.ThrowIfCancellationRequested();
        if (!forceRefresh)
        {
            lock (SnapshotLock)
                if (_recentSnapshot is { } recent &&
                    IsSnapshotFresh(_recentSnapshotStamp, Stopwatch.GetTimestamp(),
                        Stopwatch.Frequency, SnapshotTtl))
                    return recent;
        }

        // A burst of concurrent passive page refreshes must share one process inventory.
        // Forced read-back still performs an independent, serialized system query.
        await SnapshotLoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!forceRefresh)
            {
                lock (SnapshotLock)
                    if (_recentSnapshot is { } recent &&
                        IsSnapshotFresh(_recentSnapshotStamp, Stopwatch.GetTimestamp(),
                            Stopwatch.Frequency, SnapshotTtl))
                        return recent;
            }

        Interlocked.Increment(ref _actualProbeCount);
        var trimTask = ProcessRunner.RunAsync(
            "fsutil.exe", "behavior query DisableDeleteNotify", TimeSpan.FromSeconds(12), token);
        var lastTask = ProcessRunner.RunAsync(
            "fsutil.exe", "behavior query disablelastaccess", TimeSpan.FromSeconds(12), token);
        var eightTask = ProcessRunner.RunAsync(
            "fsutil.exe", "8dot3name query C:", TimeSpan.FromSeconds(12), token);
        var taskState = ReadScheduledOptimizeAsync(token);

        await Task.WhenAll(trimTask, lastTask, eightTask, taskState).ConfigureAwait(false);

        ProcessResult trim = await trimTask.ConfigureAwait(false);
        ProcessResult last = await lastTask.ConfigureAwait(false);
        ProcessResult eight = await eightTask.ConfigureAwait(false);

        bool? trimEnabled = trim.Success ? ParseTrimEnabled(trim.StdOut + trim.StdErr) : null;
        int? lastMode = last.Success ? ParseFirstInt(last.StdOut + last.StdErr, @"DisableLastAccess\s*=\s*(\d+)") : null;
        int? eightMode = eight.Success ? ParseEightDotThree(eight.StdOut + eight.StdErr) : null;

        var snapshot = new StorageOptimizationSnapshot(
            trimEnabled,
            lastMode,
            FormatLastAccess(lastMode),
            eightMode,
            FormatEightDotThree(eightMode),
            await taskState.ConfigureAwait(false),
            "Windows / driver managed • buffer flushing ponechat zapnutý");
        if (!token.IsCancellationRequested)
        {
            lock (SnapshotLock)
            {
                _recentSnapshot = snapshot;
                _recentSnapshotStamp = Stopwatch.GetTimestamp();
            }
        }
        return snapshot;
        }
        finally { SnapshotLoadGate.Release(); }
    }

    public static async Task<StorageOptimizationSnapshot> ApplyRecommendedAsync(CancellationToken token = default)
    {
        using var mutationGate = await StorageMutationGate.EnterAsync(token).ConfigureAwait(false);
        var before = await ReadAsync(token, forceRefresh: true).ConfigureAwait(false);
        bool trimChanged = false;
        bool lastChanged = false;
        bool taskChanged = false;
        int? targetLastAccessMode = RecommendedLastAccessMode(before.LastAccessMode);

        try
        {
            if (before.TrimEnabled == false)
            {
                // Mark the write attempt BEFORE awaiting the external tool:
                // it can modify Windows but return an error or be cancelled.
                trimChanged = true;
                await RequireFsutilAsync("behavior set DisableDeleteNotify 0", token).ConfigureAwait(false);
            }

            if (targetLastAccessMode.HasValue)
            {
                // Keep user-managed (0 -> 1) or system-managed (2 -> 3)
                // ownership; do not silently force a system policy to manual.
                lastChanged = true;
                await RequireFsutilAsync(
                    $"behavior set disablelastaccess {targetLastAccessMode.Value}", token)
                    .ConfigureAwait(false);
            }

            if (before.ScheduledOptimizeEnabled == false)
            {
                taskChanged = true;
                await new TaskSchedulerService().SetEnabledAsync(ScheduledDefrag, true, token).ConfigureAwait(false);
            }

            var after = await ReadAsync(token, forceRefresh: true).ConfigureAwait(false);
            if (before.TrimEnabled == false && after.TrimEnabled != true)
                throw new InvalidOperationException("TRIM read-back nepotvrdil Enabled.");
            if (targetLastAccessMode.HasValue && after.LastAccessMode != targetLastAccessMode)
                throw new InvalidOperationException("Last Access read-back nepotvrdil požadovaný režim správy.");
            if (before.ScheduledOptimizeEnabled == false && after.ScheduledOptimizeEnabled != true)
                throw new InvalidOperationException("Scheduled Optimize read-back nepotvrdil Enabled.");

            LogService.Info("storage",
                $"STORAGE_RECOMMENDED_OK trim={after.TrimEnabled} lastAccess={after.LastAccessMode} scheduled={after.ScheduledOptimizeEnabled}");
            return after;
        }
        catch (Exception applyError)
        {
            try
            {
                await RestoreAttemptedChangesAsync(before, trimChanged, lastChanged, taskChanged)
                    .ConfigureAwait(false);
                LogService.Info("storage", "STORAGE_RECOMMENDED_ROLLBACK_VERIFIED");
            }
            catch (Exception rollbackError)
            {
                LogService.Error("storage", "Storage Recommended rollback failed or read-back mismatched", rollbackError);
                throw new AggregateException(
                    "Storage Recommended selhal a původní nastavení se nepodařilo bezpečně ověřit.",
                    applyError, rollbackError);
            }
            throw;
        }
    }

    // Background SSD AI only restores two safe Windows maintenance defaults.
    // It NEVER writes LastAccess, 8.3 names, driver cache, flush or benchmarks.
    public static async Task<StorageOptimizationSnapshot> RepairSafeSsdDefaultsAsync(
        bool enableNtfsTrim, bool enableScheduledOptimization, CancellationToken token = default)
    {
        using var mutationGate = await StorageMutationGate.EnterAsync(token).ConfigureAwait(false);
        var before = await ReadAsync(token, forceRefresh: true).ConfigureAwait(false);
        bool trimChanged = false, taskChanged = false;
        try
        {
            if (enableNtfsTrim && before.TrimEnabled == false)
            {
                trimChanged = true;
                await RequireFsutilAsync("behavior set DisableDeleteNotify 0", token).ConfigureAwait(false);
            }
            if (enableScheduledOptimization && before.ScheduledOptimizeEnabled == false)
            {
                taskChanged = true;
                await new TaskSchedulerService().SetEnabledAsync(
                    ScheduledDefrag, true, token).ConfigureAwait(false);
            }
            var after = await ReadAsync(token, forceRefresh: true).ConfigureAwait(false);
            if (trimChanged && after.TrimEnabled != true)
                throw new InvalidOperationException("SSD NTFS TRIM read-back failed.");
            if (taskChanged && after.ScheduledOptimizeEnabled != true)
                throw new InvalidOperationException("SSD Scheduled Optimize read-back failed.");
            return after;
        }
        catch (Exception applyError)
        {
            try
            {
                await RestoreAttemptedChangesAsync(before, trimChanged, false, taskChanged)
                    .ConfigureAwait(false);
                LogService.Info("storage", "SSD_AI_ROLLBACK_VERIFIED");
            }
            catch (Exception rollbackError)
            {
                LogService.Error("storage", "SSD automatic maintenance rollback failed", rollbackError);
                throw new AggregateException(
                    "Automatická údržba SSD selhala a původní nastavení nebylo ověřeno.",
                    applyError, rollbackError);
            }
            throw;
        }
    }

    // A failed or cancelled external command may still have changed the OS.
    // Derive restoration exclusively from the captured pre-write snapshot.
    internal sealed record StorageRestorePlan(
        int? TrimDisableDeleteNotify, int? LastAccessMode, bool? ScheduledOptimize);

    internal static StorageRestorePlan PlanRestore(
        StorageOptimizationSnapshot before, bool trimAttempted,
        bool lastAccessAttempted, bool taskAttempted) => new(
            trimAttempted && before.TrimEnabled.HasValue
                ? (before.TrimEnabled.Value ? 0 : 1) : null,
            lastAccessAttempted ? before.LastAccessMode : null,
            taskAttempted ? before.ScheduledOptimizeEnabled : null);

    private static async Task RestoreAttemptedChangesAsync(
        StorageOptimizationSnapshot before, bool trimAttempted,
        bool lastAccessAttempted, bool taskAttempted)
    {
        var plan = PlanRestore(before, trimAttempted, lastAccessAttempted, taskAttempted);
        if (plan.TrimDisableDeleteNotify is null &&
            plan.LastAccessMode is null && plan.ScheduledOptimize is null)
            return;

        using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await ExecuteRestorePlanAsync(
            plan,
            (enabled, token) => new TaskSchedulerService().SetEnabledAsync(ScheduledDefrag, enabled, token),
            (value, token) => RequireFsutilAsync($"behavior set disablelastaccess {value}", token),
            (value, token) => RequireFsutilAsync($"behavior set DisableDeleteNotify {value}", token),
            token => ReadAsync(token, forceRefresh: true),
            rollbackTimeout.Token).ConfigureAwait(false);
    }

    // Same production rollback logic receives mock delegates in offline tests:
    // no fsutil, TaskScheduler or registry modifications during the fixture.
    internal static async Task ExecuteRestorePlanAsync(
        StorageRestorePlan plan,
        Func<bool, CancellationToken, Task> restoreScheduled,
        Func<int, CancellationToken, Task> restoreLastAccess,
        Func<int, CancellationToken, Task> restoreTrim,
        Func<CancellationToken, Task<StorageOptimizationSnapshot>> readback,
        CancellationToken token)
    {
        // Continue independent restore attempts if one command fails; never
        // report success before an uncached read-back of attempted fields.
        var errors = new List<Exception>(4);
        async Task AttemptAsync(string label, Func<Task> restore)
        {
            try { await restore().ConfigureAwait(false); }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException(label + " rollback selhal.", ex));
            }
        }

        if (plan.ScheduledOptimize is bool enabled)
            await AttemptAsync("Scheduled Optimize", () =>
                restoreScheduled(enabled, token)).ConfigureAwait(false);
        if (plan.LastAccessMode is int lastMode)
            await AttemptAsync("NTFS Last Access", () =>
                restoreLastAccess(lastMode, token)).ConfigureAwait(false);
        if (plan.TrimDisableDeleteNotify is int trimValue)
            await AttemptAsync("NTFS TRIM", () =>
                restoreTrim(trimValue, token)).ConfigureAwait(false);

        // Force a fresh readback, never a possibly stale two-second snapshot.
        try
        {
            var restored = await readback(token).ConfigureAwait(false);
            if (plan.ScheduledOptimize.HasValue &&
                restored.ScheduledOptimizeEnabled != plan.ScheduledOptimize)
                errors.Add(new InvalidOperationException("Scheduled Optimize rollback read-back mismatch."));
            if (plan.LastAccessMode.HasValue && restored.LastAccessMode != plan.LastAccessMode)
                errors.Add(new InvalidOperationException("Last Access rollback read-back mismatch."));
            if (plan.TrimDisableDeleteNotify.HasValue &&
                restored.TrimEnabled != (plan.TrimDisableDeleteNotify.Value == 0))
                errors.Add(new InvalidOperationException("TRIM rollback read-back mismatch."));
        }
        catch (Exception ex)
        {
            errors.Add(new InvalidOperationException("Storage rollback read-back failed.", ex));
        }

        if (errors.Count != 0)
            throw new AggregateException("Původní stav úložiště nebyl plně obnoven a ověřen.", errors);
    }

    // Fault injection into the actual production rollback executor: all
    // operations below are delegates in memory, no storage settings touched.
    internal static async Task<string> RunRollbackFaultInjectionSmokeAsync()
    {
        var original = new StorageOptimizationSnapshot(false, 2, "", null, "", false, "");
        var plan = PlanRestore(original, true, true, true);
        var invoked = new List<string>();
        Task RestoreTask(bool value, CancellationToken _) {
            invoked.Add("task:" + value);
            return Task.CompletedTask;
        }
        Task RestoreLast(int value, CancellationToken _) {
            invoked.Add("last:" + value);
            return Task.CompletedTask;
        }
        Task RestoreTrim(int value, CancellationToken _) {
            invoked.Add("trim:" + value);
            return Task.CompletedTask;
        }
        Task<StorageOptimizationSnapshot> Read(CancellationToken _) {
            invoked.Add("read");
            return Task.FromResult(original);
        }
        static void Verify(List<string> actual, string expected)
        {
            if (string.Join(",", actual) != expected)
                throw new InvalidOperationException("Rollback fault-injection order mismatch: " +
                    string.Join(",", actual));
        }

        await ExecuteRestorePlanAsync(plan, RestoreTask, RestoreLast, RestoreTrim,
            Read, CancellationToken.None).ConfigureAwait(false);
        Verify(invoked, "task:False,last:2,trim:1,read");

        invoked.Clear();
        async Task FailedTask(bool value, CancellationToken ct) {
            await RestoreTask(value, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Injected task write failure.");
        }
        try
        {
            await ExecuteRestorePlanAsync(plan, FailedTask, RestoreLast, RestoreTrim,
                Read, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Injected task write failure went unnoticed.");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 &&
            ex.InnerExceptions[0].Message.Contains("Scheduled Optimize", StringComparison.Ordinal)) { }
        Verify(invoked, "task:False,last:2,trim:1,read");

        invoked.Clear();
        Task<StorageOptimizationSnapshot> WrongRead(CancellationToken _) {
            invoked.Add("read");
            return Task.FromResult(original with { LastAccessMode = 3 });
        }
        try
        {
            await ExecuteRestorePlanAsync(plan, RestoreTask, RestoreLast, RestoreTrim,
                WrongRead, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Injected readback mismatch went unnoticed.");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 &&
            ex.InnerExceptions[0].Message.Contains("Last Access", StringComparison.Ordinal)) { }
        Verify(invoked, "task:False,last:2,trim:1,read");

        invoked.Clear();
        Task<StorageOptimizationSnapshot> FailedRead(CancellationToken _) {
            invoked.Add("read");
            throw new InvalidOperationException("Injected readback unavailable.");
        }
        try
        {
            await ExecuteRestorePlanAsync(plan, RestoreTask, RestoreLast, RestoreTrim,
                FailedRead, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Injected readback failure went unnoticed.");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 &&
            ex.InnerExceptions[0].Message.Contains("read-back", StringComparison.Ordinal)) { }
        Verify(invoked, "task:False,last:2,trim:1,read");

        invoked.Clear();
        async Task CancelledTask(bool value, CancellationToken ct) {
            await RestoreTask(value, ct).ConfigureAwait(false);
            throw new OperationCanceledException("Injected cancelled task operation.");
        }
        try
        {
            await ExecuteRestorePlanAsync(plan, CancelledTask, RestoreLast, RestoreTrim,
                Read, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Injected cancellation went unnoticed.");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 &&
            ex.InnerExceptions[0].InnerException is OperationCanceledException) { }
        Verify(invoked, "task:False,last:2,trim:1,read");
        return "STORAGE_ROLLBACK_FAULT_INJECTION_OK cases=5 commands=mocked readback=mocked";
    }

    private static async Task<bool?> ReadScheduledOptimizeAsync(CancellationToken token)
    {
        try
        {
            return await Task.Run(
                () => TaskSchedulerComService.GetEnabled(ScheduledDefrag, token),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Page navigation cancellation is not a failed disk read. Keep
            // it cancelled so ReadAsync cannot publish an incomplete snapshot.
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "Scheduled Optimize read-back failed", ex);
            return null;
        }
    }

    internal static async Task<string> RunScheduledOptimizeCancellationSmokeAsync()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await ReadScheduledOptimizeAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return "STORAGE_CANCEL_SMOKE_OK";
        }
        throw new InvalidOperationException("Storage cancellation did not propagate.");
    }

    private static async Task RequireFsutilAsync(string args, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(
            "fsutil.exe", args, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException((result.StdErr + Environment.NewLine + result.StdOut).Trim());
    }

    private static bool? ParseTrimEnabled(string text)
    {
        // NTFS is the reliable default for local Windows SSD volumes.
        // ReFS can have different defaults by version; don't confuse an enabled
        // ReFS line with NTFS being disabled (or vice versa).
        var match = Regex.Match(text,
            @"\bNTFS\s+DisableDeleteNotify\s*=\s*([01])\b",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value == "0" : null;
    }

    internal static string RunTrimParserSmoke()
    {
        if (ParseTrimEnabled("NTFS DisableDeleteNotify = 0\r\nReFS DisableDeleteNotify = 1") != true ||
            ParseTrimEnabled("NTFS DisableDeleteNotify = 1\r\nReFS DisableDeleteNotify = 0") != false ||
            ParseTrimEnabled("ReFS DisableDeleteNotify = 0") != null)
            throw new InvalidOperationException("NTFS TRIM filesystem parsing regression.");
        if (RecommendedLastAccessMode(0) != 1 ||
            RecommendedLastAccessMode(2) != 3 ||
            RecommendedLastAccessMode(1) != null ||
            RecommendedLastAccessMode(3) != null ||
            RecommendedLastAccessMode(4) != null ||
            RecommendedLastAccessMode(null) != null)
            throw new InvalidOperationException("NTFS Last Access mode preservation regression.");

        var original = new StorageOptimizationSnapshot(false, 2, "", null, "", false, "");
        var restore = PlanRestore(original, true, true, true);
        if (restore.TrimDisableDeleteNotify != 1 || restore.LastAccessMode != 2 ||
            restore.ScheduledOptimize != false)
            throw new InvalidOperationException("Storage rollback original-values regression.");
        restore = PlanRestore(original, true, false, false);
        if (restore.TrimDisableDeleteNotify != 1 || restore.LastAccessMode != null ||
            restore.ScheduledOptimize != null)
            throw new InvalidOperationException("Storage rollback attempted-only regression.");
        restore = PlanRestore(original with { TrimEnabled = true, LastAccessMode = 0,
            ScheduledOptimizeEnabled = true }, true, true, true);
        if (restore.TrimDisableDeleteNotify != 0 || restore.LastAccessMode != 0 ||
            restore.ScheduledOptimize != true)
            throw new InvalidOperationException("Storage rollback inverse-original regression.");
        restore = PlanRestore(original with { TrimEnabled = null, LastAccessMode = null,
            ScheduledOptimizeEnabled = null }, true, true, true);
        if (restore.TrimDisableDeleteNotify != null || restore.LastAccessMode != null ||
            restore.ScheduledOptimize != null)
            throw new InvalidOperationException("Storage rollback unknown-values regression.");
        return "SSD_TRIM_PARSER_SMOKE_OK ntfs=independent refs=ignored lastaccess=ownership-preserved rollback=attempted-only";
    }

    private static int? ParseFirstInt(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : null;
    }

    private static int? ParseEightDotThree(string text)
    {
        int? value = ParseFirstInt(text, @"volume state is\s*:\s*(\d+)");
        if (value.HasValue) return value;
        return ParseFirstInt(text, @"8dot3name creation state is\s*:\s*(\d+)");
    }

    // A real, reversible Storage Recommended tweak: preserve the original
    // Windows-managed vs user-managed policy mode when disabling Last Access.
    // 0/1 are user-managed; 2/3 are system-managed. Never overwrite unknowns.
    internal static int? RecommendedLastAccessMode(int? mode) => mode switch
    {
        0 => 1,
        2 => 3,
        _ => null
    };

    private static bool IsLastAccessDisabled(int mode) => mode is 1 or 3;

    private static string FormatLastAccess(int? mode) => mode switch
    {
        0 => "Enabled (user managed)",
        1 => "Disabled (user managed)",
        2 => "Enabled (system managed)",
        3 => "Disabled (system managed)",
        _ => "Nezjištěno"
    };

    private static string FormatEightDotThree(int? mode) => mode switch
    {
        0 => "Enabled on all volumes",
        1 => "Disabled on all volumes",
        2 => "Per-volume",
        3 => "Disabled except system volume",
        _ => "Nezjištěno / per-volume state"
    };
}
