using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string ExplorerHubModeBackupId = "gt.ui.hubmode";
    private const string ExplorerHubModeRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";

    // The same operation drives the real setting and deterministic fault tests.
    // Failed or cancelled writes restore the previous value without a second capture.
    internal static async Task ExecuteExplorerHubModeMutationAsync(
        bool enable,
        Func<CancellationToken, Task> captureOriginal,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!enable)
        {
            if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException("Původní nastavení Explorer HubMode není zálohované.");
            return;
        }

        // A failed backup must never be followed by any Windows change.
        await captureOriginal(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await apply(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception applyError)
        {
            try
            {
                if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException("Chybí záloha pro obnovu Explorer HubMode.");
            }
            catch (Exception restoreError)
            {
                throw new AggregateException("Nelze potvrdit obnovení Explorer HubMode.", applyError, restoreError);
            }
            throw;
        }
    }

    private static Task SetExplorerHubModeAsync(bool on, CancellationToken ct) =>
        ExecuteExplorerHubModeMutationAsync(
            on,
            token => TweakFactory.Backups.SaveOriginalAsync(
                ExplorerHubModeBackupId, RegistryHive.LocalMachine,
                ExplorerHubModeRegistryPath, "HubMode", token),
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey key = baseKey.CreateSubKey(ExplorerHubModeRegistryPath, writable: true)
                    ?? throw new IOException("Nelze otevřít Explorer HubMode pro zápis.");
                key.SetValue("HubMode", 1, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                // Read back both value and type before declaring a successful tweak.
                if (key.GetValueKind("HubMode") != RegistryValueKind.DWord ||
                    Convert.ToInt32(key.GetValue("HubMode", -1)) != 1)
                    throw new IOException("Explorer nepotvrdil hodnotu HubMode = 1.");
                return Task.CompletedTask;
            },
            token => TweakFactory.Backups.RestoreAsync(ExplorerHubModeBackupId, token),
            ct);
}
