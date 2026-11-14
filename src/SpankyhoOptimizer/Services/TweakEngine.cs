using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed partial class TweakEngine
{
    public Task DetectAsync(
        IEnumerable<TweakDefinition> tweaks, CancellationToken token = default,
        bool resetPending = false)
    {
        var items = tweaks.ToArray();
        // Background reads must never discard a user-edited Pending value.
        // Only explicit post-Apply or cancellation reconciliation resets it.
        return OperationCoordinator.RunExclusiveAsync("tweak-engine",
            ct => DetectCoreAsync(items, ct, resetPending), TimeSpan.FromMinutes(5), token);
    }

    private async Task DetectCoreAsync(
        IEnumerable<TweakDefinition> tweaks, CancellationToken token, bool resetPending)
    {
        var items = tweaks as TweakDefinition[] ?? tweaks.ToArray();
        if (items.Length == 0) return;

        HardwareSnapshot? hardwareSnapshot = null;
        if (items.Any(x => !string.Equals(x.HardwareScope, "Any", StringComparison.OrdinalIgnoreCase)))
            hardwareSnapshot = await HardwareSnapshotService.GetAsync(false, token).ConfigureAwait(false);

        // Partition once instead of Where.ToArray + Except(HashSet).ToArray.
        // Exact old Except semantics: custom commands are de-duplicated by
        // TweakDefinition identity, registry rows preserve their original order.
        List<TweakDefinition>? registry = null;
        List<TweakDefinition>? commands = null;
        HashSet<TweakDefinition>? seenCommands = null;
        foreach (TweakDefinition tweak in items)
        {
            if (tweak.RegistryHive.HasValue && tweak.RegistryPath != null &&
                tweak.RegistryValueName != null && tweak.RegistryEnabledValue.HasValue)
            {
                (registry ??= new List<TweakDefinition>()).Add(tweak);
            }
            else if ((seenCommands ??= new HashSet<TweakDefinition>()).Add(tweak))
            {
                (commands ??= new List<TweakDefinition>()).Add(tweak);
            }
        }
        var hives = registry is null
            ? Array.Empty<IGrouping<RegistryHive, TweakDefinition>>()
            : registry.GroupBy(x => x.RegistryHive!.Value).ToArray();

        // Most pages contain only custom OR registry tweaks. Do not launch an
        // empty parallel branch when that domain has no actual work.
        Task registryTask = hives.Length == 0 ? Task.CompletedTask : Parallel.ForEachAsync(
            hives,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token },
            (hiveGroup, ct) =>
            {
                DetectRegistryHive(hiveGroup.Key, hiveGroup.ToArray(), hardwareSnapshot, ct, resetPending);
                return ValueTask.CompletedTask;
            });

        Task commandTask = commands is null ? Task.CompletedTask : Parallel.ForEachAsync(
            commands,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 3, 6),
                CancellationToken = token
            },
            (tweak, ct) => new ValueTask(DetectOneAsync(tweak, hardwareSnapshot, ct, resetPending)));

        await Task.WhenAll(registryTask, commandTask).ConfigureAwait(false);
    }

    public Task DetectFreshAsync(IEnumerable<TweakDefinition> tweaks, TimeSpan maxAge, CancellationToken token = default)
    {
        DateTime nowUtc = DateTime.UtcNow;
        var cutoff = nowUtc - maxAge;
        // A cached detection timestamp from the future is not fresh: wall
        // clock rollback must force a new native read, not freeze the row.
        // Most refreshes reuse already-verified tweaks. Avoid a LINQ closure,
        // enumerator and empty result array when an indexed page is fresh.
        // Stale records keep the exact original selection/cancellation path.
        if (tweaks is IReadOnlyList<TweakDefinition> list)
        {
            bool fresh = true;
            for (int i = 0; i < list.Count; i++)
            {
                TweakDefinition tweak = list[i];
                if (!tweak.IsLoaded || !tweak.LastVerifiedUtc.HasValue ||
                    tweak.LastVerifiedUtc.Value < cutoff ||
                    tweak.LastVerifiedUtc.Value > nowUtc)
                {
                    fresh = false;
                    break;
                }
            }
            if (fresh) return Task.CompletedTask;
        }
        var stale = tweaks.Where(x => !x.IsLoaded || !x.LastVerifiedUtc.HasValue ||
            x.LastVerifiedUtc.Value < cutoff || x.LastVerifiedUtc.Value > nowUtc).ToArray();
        return stale.Length == 0 ? Task.CompletedTask : DetectAsync(stale, token);
    }

    private static void DetectRegistryHive(
        RegistryHive hive, TweakDefinition[] tweaks, HardwareSnapshot? hardwareSnapshot,
        CancellationToken token, bool resetPending)
    {
        RegistryKey baseKey;
        try { baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64); }
        catch (Exception ex)
        {
            foreach (var tweak in tweaks) InvalidateDetection(tweak, ex);
            return;
        }
        using (baseKey)
        foreach (var pathGroup in tweaks.GroupBy(x => x.RegistryPath!, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (pathGroup.Key.Contains('\0')) throw new ArgumentException("Registry cesta obsahuje neplatný znak.");
                using RegistryKey? key = baseKey.OpenSubKey(pathGroup.Key, writable: false);
                foreach (var tweak in pathGroup)
                {
                    token.ThrowIfCancellationRequested();
                    var compatibility = TweakCompatibilityService.CheckWithSnapshot(tweak, hardwareSnapshot);
                    if (!compatibility.Supported)
                    {
                        tweak.Status = "Nepodporováno • " + compatibility.Reason;
                        tweak.ExecutionState = TweakExecutionState.Unsupported;
                        tweak.IsLoaded = false; tweak.LastVerifiedUtc = null;
                        continue;
                    }
                    tweak.IsBusy = true;
                    try
                    {
                        if (tweak.RegistryValueName!.Contains('\0')) throw new ArgumentException("Název registry hodnoty obsahuje neplatný znak.");
                        object? raw = key?.GetValue(tweak.RegistryValueName!);
                        bool value = raw != null && key!.GetValueKind(tweak.RegistryValueName!) == RegistryValueKind.DWord &&
                            (Convert.ToInt64(raw) == tweak.RegistryEnabledValue!.Value ||
                             (raw is int rawInt && (uint)rawInt == (uint)tweak.RegistryEnabledValue!.Value));
                        bool preserveUserEdit = !resetPending && tweak.IsLoaded && tweak.IsDirty;
                        tweak.Current = value;
                        if (!preserveUserEdit) tweak.Pending = value;
                        tweak.Status = value ? "Aktivní" : "Výchozí / vypnuto";
                        tweak.ExecutionState = value && tweak.ExecutionState == TweakExecutionState.RequiresRestart
                            ? TweakExecutionState.RequiresRestart
                            : value ? TweakExecutionState.Applied : TweakExecutionState.NotApplied;
                        tweak.IsLoaded = true; tweak.LastVerifiedUtc = DateTime.UtcNow; tweak.LastError = null;
                    }
                    catch (Exception ex) { InvalidateDetection(tweak, ex); }
                    finally { tweak.IsBusy = false; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { foreach (var tweak in pathGroup) InvalidateDetection(tweak, ex); }
        }
    }

    private static void InvalidateDetection(TweakDefinition tweak, Exception error)
    {
        tweak.IsLoaded = false;
        tweak.LastVerifiedUtc = null;
        if (error is NotSupportedException)
        {
            tweak.Status = "Nepodporováno • " + error.Message;
            tweak.ExecutionState = TweakExecutionState.Unsupported;
            tweak.LastError = null;
            return;
        }
        if (IsElevationException(error))
        {
            tweak.Status = "Vyžaduje oprávnění správce";
            tweak.ExecutionState = TweakExecutionState.RequiresElevation;
            tweak.LastError = error.Message;
            LogService.Error("tweaks", "Detect " + tweak.Id, error);
            return;
        }
        tweak.Status = "Nelze zjistit";
        tweak.ExecutionState = TweakExecutionState.Failed;
        tweak.LastError = error.Message;
        LogService.Error("tweaks", "Detect " + tweak.Id, error);
    }

    private static bool IsElevationException(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException ||
        ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 or 5 } ||
        ex.Message.Contains("UAC", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("oprávnění správce", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Přístup byl odepřen", StringComparison.OrdinalIgnoreCase);

    private static async Task DetectOneAsync(
        TweakDefinition tweak, HardwareSnapshot? hardwareSnapshot,
        CancellationToken token, bool resetPending)
    {
        var compatibility = TweakCompatibilityService.CheckWithSnapshot(tweak, hardwareSnapshot);
        if (!compatibility.Supported)
        {
            tweak.Status = "Nepodporováno • " + compatibility.Reason;
            tweak.ExecutionState = TweakExecutionState.Unsupported;
            tweak.IsLoaded = false;
            tweak.LastVerifiedUtc = null;
            return;
        }
        tweak.IsBusy = true;
        try
        {
            bool value = await tweak.DetectAsync(token);
            // Evaluate at completion, not dispatch: the user can toggle a
            // visible row while an async native detection is in flight.
            bool preserveUserEdit = !resetPending && tweak.IsLoaded && tweak.IsDirty;
            tweak.Current = value;
            if (!preserveUserEdit) tweak.Pending = value;
            tweak.Status = value ? "Aktivní" : "Výchozí / vypnuto";
            tweak.ExecutionState = value && tweak.ExecutionState == TweakExecutionState.RequiresRestart
                ? TweakExecutionState.RequiresRestart
                : value ? TweakExecutionState.Applied : TweakExecutionState.NotApplied;
            tweak.IsLoaded = true;
            tweak.LastVerifiedUtc = DateTime.UtcNow;
            tweak.LastError = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            InvalidateDetection(tweak, ex);
        }
        finally { tweak.IsBusy = false; }
    }

    // A target is immutable from user confirmation to its individual write.
    // A queued job must never substitute a later Pending value while
    // waiting for the exclusive engine, restore-point or preflight tasks.
    private readonly record struct ApplyIntent(
        TweakDefinition Tweak, bool Previous, bool Expected);

    // Rollback uses the confirmed apply target, not whatever Pending happens
    // to contain after a concurrent edit in the WinUI row.
    private readonly record struct RollbackIntent(
        TweakDefinition Tweak, bool Previous, bool Expected);

    private static ApplyIntent[] CaptureApplyIntents(IEnumerable<TweakDefinition> tweaks) =>
        tweaks.Where(x => x.IsDirty)
            // Collapse the identical UI instance only; two distinct pending
            // definitions sharing an ID must reach preflight as a conflict.
            .Distinct()
            .Select(x => new ApplyIntent(x, x.Current, x.Pending)).ToArray();

    public Task<IReadOnlyList<string>> ApplyAsync(IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        ApplyIntent[] intents = CaptureApplyIntents(tweaks);
        return OperationCoordinator.RunExclusiveAsync<IReadOnlyList<string>>(
            "tweak-engine", ct => ApplyCoreAsync(intents, ct, null), TimeSpan.FromMinutes(15), token);
    }

    public Task<IReadOnlyList<string>> ApplyWithProgressAsync(IEnumerable<TweakDefinition> tweaks, IProgress<string> progress, CancellationToken token = default)
    {
        ApplyIntent[] intents = CaptureApplyIntents(tweaks);
        return OperationCoordinator.RunExclusiveAsync<IReadOnlyList<string>>(
            "tweak-engine", ct => ApplyCoreAsync(intents, ct, progress), TimeSpan.FromMinutes(15), token);
    }

    private static bool IntentStillCurrent(ApplyIntent intent) =>
        intent.Tweak.IsLoaded && !intent.Tweak.IsBusy &&
        intent.Tweak.Current == intent.Previous &&
        intent.Tweak.Pending == intent.Expected &&
        intent.Previous != intent.Expected;
}
