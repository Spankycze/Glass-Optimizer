using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Pure fault-injection tests run the exact production transaction.
    // No HKCU changes, no Explorer restart and no user-setting modifications.
    internal static async Task RunDesktopIconsMutationSmokeAsync()
    {
        static void Verify(bool ok, string caseName)
        {
            if (!ok) throw new InvalidOperationException("DESKTOP_ICONS_SMOKE_FAIL: " + caseName);
        }

        // 1: All six existing icon tweaks retain their catalogue identifiers,
        // DWORD target metadata and independent original-value restore.
        string[] ids =
        [
            "gt.ui.iconpc", "gt.ui.iconnetwork", "gt.ui.iconrecycle",
            "gt.ui.iconcontrol", "gt.ui.iconuser", "gt.ui.icononedrive"
        ];
        var icons = UiTweaks().Where(x => ids.Contains(x.Id, StringComparer.Ordinal)).ToArray();
        Verify(icons.Length == 6 &&
               icons.Select(x => x.Id).SequenceEqual(ids) &&
               icons.All(x => x.Section == "ui" && x.Group == "Plocha / ikony" &&
                   !x.Recommended && !x.RequiresAdmin && !x.RestartRequired &&
                   x.RevertMode == "OriginalRegistryValue" && x.RevertAsync is not null &&
                   x.RegistryHive == Microsoft.Win32.RegistryHive.CurrentUser &&
                   x.RegistryPath == DesktopIconRegistryPath &&
                   x.RegistryEnabledValue == 0 && !string.IsNullOrWhiteSpace(x.RegistryValueName)),
               "six stable catalogue entries");

        int saves = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> save = _ =>
        {
            saves++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> write = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task<bool>> restore = ct =>
        {
            Verify(!ct.IsCancellationRequested, "recovery received cancelled token");
            restores++;
            return Task.FromResult(true);
        };

        // 2: ON saves before changing one icon.
        await ExecuteDesktopIconMutationAsync(true, save, write, restore, CancellationToken.None);
        Verify(saves == 1 && writes == 1 && restores == 0, "show icon");

        // 3: OFF restores original; it never substitutes DWORD=1.
        await ExecuteDesktopIconMutationAsync(false, save, write, restore, CancellationToken.None);
        Verify(saves == 1 && writes == 1 && restores == 1, "original on OFF");

        // 4: A failed registry write recovers the original.
        bool failedWrite = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(true, save,
                _ => throw new IOException("injected DWORD write fault"),
                restore, CancellationToken.None);
        }
        catch (IOException) { failedWrite = true; }
        Verify(failedWrite && restores == 2, "write failure rollback");

        // 5: Wrong DWORD kind or value after write also recovers.
        bool badReadback = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(true, save,
                _ => throw new InvalidDataException("injected readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { badReadback = true; }
        Verify(badReadback && restores == 3, "readback rollback");

        // 6: Backup failure must prevent the first write.
        bool failedBackup = false;
        int writesBefore = writes;
        try
        {
            await ExecuteDesktopIconMutationAsync(true,
                _ => throw new IOException("injected backup storage fault"),
                write, restore, CancellationToken.None);
        }
        catch (IOException) { failedBackup = true; }
        Verify(failedBackup && writes == writesBefore && restores == 3,
               "failed backup prevents write");

        // 7: Missing original on OFF fails closed without deleting value.
        bool missingOriginal = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(false, save, write,
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (InvalidOperationException) { missingOriginal = true; }
        Verify(missingOriginal && writes == writesBefore, "missing original on OFF");

        // 8: A failed mutation and thrown rollback error are both reported.
        bool combined = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(true, save,
                _ => throw new InvalidDataException("injected verification fault"),
                _ => Task.FromException<bool>(
                    new UnauthorizedAccessException("injected restore failure")),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is InvalidDataException &&
                       ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Verify(combined, "compound errors");

        // 9: Missing rollback after partial write retains both failure causes.
        bool missingRollback = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(true, save,
                _ => throw new IOException("injected partial write"),
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missingRollback = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is InvalidOperationException;
        }
        Verify(missingRollback, "missing rollback surfaced");

        // 10: Cancel after a simulated write, then rollback without cancellation.
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(true, save,
                _ =>
                {
                    afterWrite.Cancel();
                    return Task.CompletedTask;
                }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Verify(cancelled && restores == 4, "cancel after write restores");

        // 11: Pre-cancelled ON must not save or change Windows.
        using var preCancel = new CancellationTokenSource();
        preCancel.Cancel();
        int savedBefore = saves, restoredBefore = restores;
        bool preOn = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(true, save, write, restore, preCancel.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        Verify(preOn && saves == savedBefore && writes == writesBefore &&
               restores == restoredBefore, "pre-cancel ON");

        // 12: Pre-cancelled OFF leaves original settings untouched.
        bool preOff = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(false, save, write, restore, preCancel.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Verify(preOff && restores == restoredBefore, "pre-cancel OFF");

        // 13: Cancellation during backup prevents any registry write.
        using var duringCapture = new CancellationTokenSource();
        bool captureCancelled = false;
        try
        {
            await ExecuteDesktopIconMutationAsync(true,
                _ =>
                {
                    duringCapture.Cancel();
                    return Task.CompletedTask;
                }, write, restore, duringCapture.Token);
        }
        catch (OperationCanceledException) { captureCancelled = true; }
        Verify(captureCancelled && writes == writesBefore &&
               restores == restoredBefore, "cancel during backup");
    }
}
