using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // All production transaction steps here are simulated: zero HKCU writes.
    internal static async Task RunThemeModeMutationSmokeAsync()
    {
        static void Check(bool pass, string scenario)
        {
            if (!pass) throw new InvalidOperationException(
                "THEME_MODE_SMOKE_FAIL: " + scenario);
        }

        // 1-2. Both user-visible entries retain IDs, registry targets and
        // no elevation/restart requirements. Explicit Revert is supported.
        string[] ids = ["gt.ui.darkapps", "gt.ui.darksystem"];
        string[] values = ["AppsUseLightTheme", "SystemUsesLightTheme"];
        var definitions = UiTweaks().Where(t =>
            ids.Contains(t.Id, StringComparer.Ordinal)).ToArray();
        Check(definitions.Length == 2 &&
              definitions.Select(t => t.Id).SequenceEqual(ids) &&
              definitions.Select(t => t.RegistryValueName).SequenceEqual(values) &&
              definitions.All(t =>
                  t.Section == "ui" && t.Group == "Vzhled Windows" &&
                  !t.Recommended && !t.RequiresAdmin && !t.RestartRequired &&
                  t.RegistryHive == RegistryHive.CurrentUser &&
                  t.RegistryPath == WindowsThemePath &&
                  t.RegistryEnabledValue == 0 &&
                  t.RevertAsync is not null &&
                  t.RevertMode == "OriginalRegistryValue"),
            "both theme catalogue entries");

        // 3-4. Light (1) -> Dark (0) -> exact original Light (1).
        int? current = 1, original = null;
        bool backedUp = false;
        int captured = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> capture = _ =>
        {
            captured++;
            if (!backedUp) { original = current; backedUp = true; }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> write = _ =>
        {
            writes++;
            current = 0;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task<bool>> restore = ct =>
        {
            Check(!ct.IsCancellationRequested, "rollback token");
            restores++;
            if (!backedUp) return Task.FromResult(false);
            current = original;
            return Task.FromResult(true);
        };
        await ExecuteThemeModeMutationAsync(true, capture, write, restore, CancellationToken.None);
        Check(current == 0 && original == 1 && captured == 1 && writes == 1 &&
              restores == 0, "light to dark");
        await ExecuteThemeModeMutationAsync(false, capture, write, restore, CancellationToken.None);
        Check(current == 1 && captured == 1 && writes == 1 && restores == 1,
              "restore original light");

        // 5-6. Original DARK is preserved on OFF, never forced Light (1).
        current = 0; backedUp = false; original = null;
        await ExecuteThemeModeMutationAsync(true, capture, write, restore, CancellationToken.None);
        await ExecuteThemeModeMutationAsync(false, capture, write, restore, CancellationToken.None);
        Check(current == 0 && original == 0, "restore preexisting dark");

        // 7. Original missing registry value is preserved as missing.
        current = null; backedUp = false; original = null;
        await ExecuteThemeModeMutationAsync(true, capture, write, restore, CancellationToken.None);
        await ExecuteThemeModeMutationAsync(false, capture, write, restore, CancellationToken.None);
        Check(current is null && original is null, "restore initial missing DWORD");

        // 8. Repeated ON must never replace first original backup.
        current = 1; backedUp = false; original = null;
        await ExecuteThemeModeMutationAsync(true, capture, write, restore, CancellationToken.None);
        await ExecuteThemeModeMutationAsync(true, capture, write, restore, CancellationToken.None);
        Check(original == 1 && current == 0, "repeated ON first original");
        await ExecuteThemeModeMutationAsync(false, capture, write, restore, CancellationToken.None);
        Check(current == 1, "repeat ON original restoration");

        // 9. Missing backup on OFF fails closed; no forced light DWORD.
        int beforeWrites = writes;
        bool missingBackup = false;
        try
        {
            await ExecuteThemeModeMutationAsync(false, capture, write,
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (InvalidOperationException) { missingBackup = true; }
        Check(missingBackup && writes == beforeWrites, "OFF with missing backup");

        // 10. Failed backup storage prevents all registry mutations.
        bool failedCapture = false;
        try
        {
            await ExecuteThemeModeMutationAsync(true,
                _ => throw new IOException("injected backup storage failure"),
                write, restore, CancellationToken.None);
        }
        catch (IOException) { failedCapture = true; }
        Check(failedCapture && writes == beforeWrites, "backup fail closed");

        // 11. Failed write restores first recorded original.
        current = 1; backedUp = false; original = null;
        bool writeFailed = false;
        try
        {
            await ExecuteThemeModeMutationAsync(true, capture,
                _ =>
                {
                    current = 0;
                    throw new IOException("simulated partial registry write");
                }, restore, CancellationToken.None);
        }
        catch (IOException) { writeFailed = true; }
        Check(writeFailed && current == 1, "failed write rollback");

        // 12. Incorrect kind/readback also restores the original.
        current = 1; backedUp = false; original = null;
        bool badReadback = false;
        try
        {
            await ExecuteThemeModeMutationAsync(true, capture,
                _ =>
                {
                    current = 0;
                    throw new InvalidDataException("simulated DWORD type mismatch");
                }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { badReadback = true; }
        Check(badReadback && current == 1, "readback rollback");

        // 13. Missing rollback after write is surfaced with original error.
        bool combined = false;
        try
        {
            await ExecuteThemeModeMutationAsync(true, capture,
                _ => throw new IOException("mutation fault"),
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is IOException &&
                       ex.InnerExceptions[1] is InvalidOperationException;
        }
        Check(combined, "missing rollback and mutation combined");

        // 14. Thrown recovery exception likewise retains both failures.
        combined = false;
        try
        {
            await ExecuteThemeModeMutationAsync(true, capture,
                _ => throw new InvalidDataException("verification fault"),
                _ => Task.FromException<bool>(
                    new UnauthorizedAccessException("restore denied")),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is InvalidDataException &&
                       ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(combined, "compound failure");

        // 15. Externally changed theme on OFF must be protected by the
        // production restore delegate instead of replacing the user setting.
        current = 1;
        bool thirdParty = false;
        try
        {
            await ExecuteThemeModeMutationAsync(false, capture, write,
                _ => throw new InvalidOperationException("user changed theme externally"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { thirdParty = true; }
        Check(thirdParty && current == 1, "external theme preserved");

        // 16. Cancellation after a write triggers uncancelled restoration.
        current = 1; backedUp = false; original = null;
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteThemeModeMutationAsync(true, capture,
                _ =>
                {
                    current = 0;
                    afterWrite.Cancel();
                    return Task.CompletedTask;
                }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && current == 1, "post-write cancellation rollback");

        // 17-18. Already-cancelled ON/OFF perform no work.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        int priorWrites = writes, priorCaptures = captured, priorRestores = restores;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteThemeModeMutationAsync(true, capture, write, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        try
        {
            await ExecuteThemeModeMutationAsync(false, capture, write, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOn && preOff && writes == priorWrites &&
              captured == priorCaptures && restores == priorRestores,
              "pre-cancelled actions");

        // 19. Cancellation during capture prevents even the first write.
        using var afterCapture = new CancellationTokenSource();
        cancelled = false;
        try
        {
            await ExecuteThemeModeMutationAsync(true, _ =>
                {
                    afterCapture.Cancel();
                    return Task.CompletedTask;
                }, write, restore, afterCapture.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && writes == priorWrites, "cancel during backup");
    }
}
