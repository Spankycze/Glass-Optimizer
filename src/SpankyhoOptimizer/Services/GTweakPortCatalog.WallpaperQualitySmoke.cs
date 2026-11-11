using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Tests the production transaction callbacks without any HKCU writes,
    // registry reads, real wallpaper changes or Explorer restarts.
    internal static async Task RunWallpaperQualityMutationSmokeAsync()
    {
        static void Check(bool pass, string scenario)
        {
            if (!pass)
                throw new InvalidOperationException("WALLPAPER_QUALITY_SMOKE_FAIL: " + scenario);
        }

        // 1. Preserve public catalogue identity and UI metadata.
        TweakDefinition tweak = WallpaperQualityTweak();
        Check(tweak.Id == WallpaperQualityId && tweak.Section == "ui" &&
              tweak.Group == "Vzhled Windows" &&
              tweak.Risk == TweakRisk.Optional && !tweak.Recommended &&
              !tweak.RestartRequired && !tweak.RequiresAdmin &&
              tweak.MinWindowsBuild == 22000 && tweak.RegistryHive == RegistryHive.CurrentUser &&
              tweak.RegistryPath == WallpaperQualityKey &&
              tweak.RegistryValueName == "JPEGImportQuality" &&
              tweak.RegistryEnabledValue == 100 &&
              tweak.RevertMode == "OriginalRegistryValue" &&
              tweak.RevertAsync is not null,
              "catalog contract");

        // 2. Use a new dedicated backup to avoid stale generic-factory snapshots.
        Check(WallpaperQualityBackupId == "gt.ui.wallpaperquality.original-v1" &&
              WallpaperQualityBackupId != WallpaperQualityId,
              "separate original backup version");

        // 3-5. Restore must not remove a missing/external target.
        Check(CanRestoreWallpaperQuality(true, true), "owned DWORD 100");
        Check(!CanRestoreWallpaperQuality(true, false), "changed value protected");
        Check(!CanRestoreWallpaperQuality(false, true) &&
              !CanRestoreWallpaperQuality(false, false), "missing path protected");

        int? current = 75, original = null;
        bool backedUp = false;
        int captures = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> capture = token =>
        {
            Check(!token.IsCancellationRequested, "capture token");
            captures++;
            if (!backedUp) { original = current; backedUp = true; }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = token =>
        {
            Check(!token.IsCancellationRequested, "write token");
            writes++;
            current = 100;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = token =>
        {
            Check(!token.IsCancellationRequested, "restore token");
            restores++;
            if (!backedUp)
                throw new InvalidOperationException("original backup missing");
            current = original;
            return Task.CompletedTask;
        };

        // 6-7. Nondefault 75 -> 100 -> precise original 75.
        await ExecuteWallpaperQualityMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        Check(current == 100 && original == 75 && captures == 1 &&
              writes == 1 && restores == 0, "enable quality");
        await ExecuteWallpaperQualityMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 75 && captures == 1 && writes == 1 && restores == 1,
              "restore original 75");

        // 8. The dangerous prior bug: already-maximum original 100 remains
        // 100 on OFF, never gets removed by a fallback delete-on-disable.
        current = 100; backedUp = false; original = null;
        await ExecuteWallpaperQualityMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteWallpaperQualityMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 100 && original == 100,
            "originally maximum 100 preserved");

        // 9. Originally absent JPEGImportQuality must be absent again on OFF.
        current = null; backedUp = false; original = null;
        await ExecuteWallpaperQualityMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteWallpaperQualityMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current is null && original is null,
            "originally absent remains absent");

        // 10. Repeat ON cannot change the first backup baseline.
        current = 65; backedUp = false; original = null;
        await ExecuteWallpaperQualityMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteWallpaperQualityMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        Check(original == 65 && current == 100, "repeat ON preserves original");
        await ExecuteWallpaperQualityMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 65, "repeat ON reverted original");

        // 11. OFF with no backup never invents an original value.
        bool failed = false;
        int oldWrites = writes;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("no durable backup"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && writes == oldWrites, "missing backup fails closed");

        // 12. If capture cannot persist backup, the registry is untouched.
        failed = false;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(true,
                _ => throw new IOException("simulated disk write failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && writes == oldWrites, "capture failure no write");

        // 13. A failed write after a partial mutation triggers original restore.
        current = 75; backedUp = false; original = null;
        failed = false;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(true, capture,
                _ =>
                {
                    current = 100;
                    throw new IOException("simulated HKCU write fault");
                }, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && current == 75, "partial write rollback");

        // 14. A wrong kind/readback also restores the original.
        current = 75; backedUp = false; original = null;
        failed = false;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(true, capture,
                _ =>
                {
                    current = 100;
                    throw new InvalidDataException("simulated value kind mismatch");
                }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed && current == 75, "readback rollback");

        // 15. If both mutation and rollback fail, report both reasons.
        bool combined = false;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(true, capture,
                _ => throw new IOException("write failed"),
                _ => throw new UnauthorizedAccessException("rollback denied"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(combined, "compound errors preserved");

        // 16. A user change to a non-100 value on OFF is protected by the
        // production restore delegate rather than overwritten.
        current = 85;
        failed = false;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("external wallpaper edit"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && current == 85, "external edit preserved");

        // 17. Post-write cancellation restores with an uncancelled token.
        current = 75; backedUp = false; original = null;
        using var midWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(true, capture,
                _ =>
                {
                    current = 100;
                    midWrite.Cancel();
                    return Task.CompletedTask;
                }, restore, midWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && current == 75, "cancelled write rollback");

        // 18. Pre-cancelled ON and OFF and capture-time cancellation are no-ops.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int oldCaptures = captures, oldRestores = restores, previousWrites = writes;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(true, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        try
        {
            await ExecuteWallpaperQualityMutationAsync(false, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        using var duringCapture = new CancellationTokenSource();
        bool captureCancelled = false;
        try
        {
            await ExecuteWallpaperQualityMutationAsync(true, _ =>
            {
                duringCapture.Cancel();
                return Task.CompletedTask;
            }, apply, restore, duringCapture.Token);
        }
        catch (OperationCanceledException) { captureCancelled = true; }
        Check(preOn && preOff && captureCancelled &&
              oldCaptures == captures && oldRestores == restores &&
              previousWrites == writes, "pre-cancel/capture cancel");
    }
}
