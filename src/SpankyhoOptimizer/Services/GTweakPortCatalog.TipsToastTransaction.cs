using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private static readonly string[] TipsToastBackupIds =
    [
        "gt.ui.tipstoast.cu",
        "gt.ui.tipstoast.lm"
    ];

    private static async Task RestoreTipsToastAsync(
        Func<string, CancellationToken, Task<bool>> restore)
    {
        var errors = new List<Exception>();
        foreach (string id in TipsToastBackupIds)
        {
            try
            {
                // Rollback is deliberately not interrupted by an already-cancelled request.
                if (!await restore(id, CancellationToken.None).ConfigureAwait(false))
                    errors.Add(new InvalidOperationException("Chybí původní hodnota pro " + id));
            }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException("Nelze obnovit " + id, ex));
            }
        }
        if (errors.Count > 0)
            throw new AggregateException("Obnova nastavení upozornění Windows není úplná.", errors);
    }

    internal static async Task ExecuteTipsToastMutationAsync(
        bool enabled,
        Func<CancellationToken, Task> captureOriginals,
        Func<CancellationToken, Task> apply,
        Func<string, CancellationToken, Task<bool>> restore,
        CancellationToken ct)
    {
        if (!enabled)
        {
            await RestoreTipsToastAsync(restore).ConfigureAwait(false);
            return;
        }

        ct.ThrowIfCancellationRequested();
        // No Windows writes may occur until both original values are safely captured.
        await captureOriginals(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await apply(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try
            {
                await RestoreTipsToastAsync(restore).ConfigureAwait(false);
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Změna upozornění i rollback selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static Task SetTipsToastAsync(bool on, CancellationToken ct)
    {
        const string cu = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
        const string lm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        return ExecuteTipsToastMutationAsync(
            on,
            async token =>
            {
                await TweakFactory.Backups.SaveOriginalAsync(TipsToastBackupIds[0],
                    RegistryHive.CurrentUser, cu, "ToastEnabled", token).ConfigureAwait(false);
                await TweakFactory.Backups.SaveOriginalAsync(TipsToastBackupIds[1],
                    RegistryHive.LocalMachine, lm, "AllowOnlineTips", token).ConfigureAwait(false);
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using var userKey = Registry.CurrentUser.CreateSubKey(cu, writable: true);
                userKey.SetValue("ToastEnabled", 0, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                using var machineKey = Registry.LocalMachine.CreateSubKey(lm, writable: true);
                machineKey.SetValue("AllowOnlineTips", 0, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                if (userKey.GetValueKind("ToastEnabled") != RegistryValueKind.DWord ||
                    Convert.ToInt32(userKey.GetValue("ToastEnabled", -1)) != 0 ||
                    machineKey.GetValueKind("AllowOnlineTips") != RegistryValueKind.DWord ||
                    Convert.ToInt32(machineKey.GetValue("AllowOnlineTips", -1)) != 0)
                    throw new IOException("Windows nepotvrdil obě změny upozornění.");
                return Task.CompletedTask;
            },
            (id, token) => TweakFactory.Backups.RestoreAsync(id, token),
            ct);
    }

    internal static async Task RunTipsToastMutationSmokeAsync()
    {
        static void Ensure(bool ok, string reason)
        {
            if (!ok) throw new InvalidOperationException("TIPS_TOAST_MUTATION: " + reason);
        }

        int captures = 0, writes = 0;
        var restored = new List<string>();
        Func<CancellationToken, Task> capture = _ =>
        {
            captures++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> write = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<string, CancellationToken, Task<bool>> restore = (id, _) =>
        {
            restored.Add(id);
            return Task.FromResult(true);
        };

        // 1: Healthy opt-in writes once and never attempts rollback.
        await ExecuteTipsToastMutationAsync(true, capture, write, restore, CancellationToken.None);
        Ensure(captures == 1 && writes == 1 && restored.Count == 0, "successful enable");

        // 2: Disable restores both without recapturing or modifying Windows.
        await ExecuteTipsToastMutationAsync(false, capture, write, restore, CancellationToken.None);
        Ensure(captures == 1 && writes == 1 && restored.SequenceEqual(TipsToastBackupIds),
            "disable restores both");
        restored.Clear();

        // 3: Simulated failure after the first registry write recovers both.
        bool failedWrite = false;
        try
        {
            await ExecuteTipsToastMutationAsync(true, capture,
                _ => throw new IOException("injected second-key failure"), restore, CancellationToken.None);
        }
        catch (IOException) { failedWrite = true; }
        Ensure(failedWrite && restored.SequenceEqual(TipsToastBackupIds), "partial write rollback");
        restored.Clear();

        // 4: A bad readback is treated identically to a failed write.
        bool failedReadback = false;
        try
        {
            await ExecuteTipsToastMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected readback mismatch"), restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failedReadback = true; }
        Ensure(failedReadback && restored.SequenceEqual(TipsToastBackupIds), "readback rollback");
        restored.Clear();

        // 5: Even a broken first rollback must not skip the other original.
        bool partialRestoreReported = false;
        try
        {
            await ExecuteTipsToastMutationAsync(false, capture, write, (id, _) =>
            {
                restored.Add(id);
                return id == TipsToastBackupIds[0]
                    ? Task.FromException<bool>(new IOException("injected restore fault"))
                    : Task.FromResult(false);
            }, CancellationToken.None);
        }
        catch (AggregateException ex) { partialRestoreReported = ex.InnerExceptions.Count == 2; }
        Ensure(partialRestoreReported && restored.SequenceEqual(TipsToastBackupIds),
            "independent rollback failure");
        restored.Clear();

        // 6: Cancellation during a write does not cancel rollback.
        using var cts = new CancellationTokenSource();
        bool cancellation = false;
        try
        {
            await ExecuteTipsToastMutationAsync(true, capture, _ =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            }, restore, cts.Token);
        }
        catch (OperationCanceledException) { cancellation = true; }
        Ensure(cancellation && restored.SequenceEqual(TipsToastBackupIds), "cancellation rollback");
        restored.Clear();

        // 7: Mutation fault and failed rollback must both be retained.
        bool combinedFault = false;
        try
        {
            await ExecuteTipsToastMutationAsync(true, capture,
                _ => throw new IOException("injected mutation failure"),
                (id, _) => Task.FromResult(id != TipsToastBackupIds[1]), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combinedFault = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Ensure(combinedFault, "combined fault reporting");

        // 8: Failure to save either backup cannot perform any Windows write.
        int before = writes;
        bool captureFault = false;
        try
        {
            await ExecuteTipsToastMutationAsync(true,
                _ => throw new IOException("injected backup persistence failure"), write,
                restore, CancellationToken.None);
        }
        catch (IOException) { captureFault = true; }
        Ensure(captureFault && writes == before, "no mutation after backup fault");
    }
}
