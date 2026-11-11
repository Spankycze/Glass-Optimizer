using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string SettingSyncTweakId = "gt.privacy.settingsync";
    private const string SettingSyncRoot = @"Software\Microsoft\Windows\CurrentVersion\SettingSync\Groups";
    private static readonly string[] SettingSyncGroups =
    [
        "Accessibility", "BrowserSettings", "Credentials",
        "Language", "Personalization", "Windows"
    ];

    private static string SettingSyncBackupId(int index) => SettingSyncTweakId + "." + index;

    // Each restore is independent; an error with one group must not skip the rest.
    // A rollback is uncancellable because part of the transaction may already be applied.
    private static async Task RestoreSettingSyncAsync(
        Func<int, CancellationToken, Task<bool>> restore)
    {
        var failures = new List<Exception>();
        for (int i = 0; i < SettingSyncGroups.Length; i++)
        {
            try
            {
                if (!await restore(i, CancellationToken.None).ConfigureAwait(false))
                    failures.Add(new InvalidOperationException(
                        "Chybí záloha původního nastavení synchronizace: " + SettingSyncGroups[i]));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Nelze obnovit původní synchronizaci: " + SettingSyncGroups[i], ex));
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("Některé skupiny synchronizace se nepodařilo obnovit.", failures);
    }

    // Production function seam for isolated fault-injection tests. Capture all
    // six values first, then apply; restore every value after partial failure.
    internal static async Task ExecuteSettingSyncMutationAsync(
        bool disableSync,
        Func<CancellationToken, Task> captureOriginals,
        Func<CancellationToken, Task> applyAndVerify,
        Func<int, CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!disableSync)
        {
            await RestoreSettingSyncAsync(restoreOriginal).ConfigureAwait(false);
            return;
        }

        await captureOriginals(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await applyAndVerify(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try
            {
                await RestoreSettingSyncAsync(restoreOriginal).ConfigureAwait(false);
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "Vypnutí synchronizace i obnovení původních hodnot selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static Task SetSettingSyncAsync(bool disableSync, CancellationToken ct) =>
        ExecuteSettingSyncMutationAsync(
            disableSync,
            async token =>
            {
                for (int i = 0; i < SettingSyncGroups.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await TweakFactory.Backups.SaveOriginalAsync(SettingSyncBackupId(i),
                        RegistryHive.CurrentUser,
                        SettingSyncRoot + "\\" + SettingSyncGroups[i],
                        "Enabled", token).ConfigureAwait(false);
                }
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using var root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                foreach (string group in SettingSyncGroups)
                {
                    token.ThrowIfCancellationRequested();
                    using RegistryKey key = root.CreateSubKey(
                        SettingSyncRoot + "\\" + group, writable: true)
                        ?? throw new IOException("Nelze změnit synchronizaci: " + group);
                    key.SetValue("Enabled", 0, RegistryValueKind.DWord);
                }
                foreach (string group in SettingSyncGroups)
                {
                    token.ThrowIfCancellationRequested();
                    using RegistryKey? key = root.OpenSubKey(
                        SettingSyncRoot + "\\" + group, writable: false);
                    if (key is null || key.GetValueKind("Enabled") != RegistryValueKind.DWord ||
                        Convert.ToInt64(key.GetValue("Enabled", -1)) != 0)
                        throw new IOException("Windows nepotvrdil vypnutí synchronizace: " + group);
                }
                return Task.CompletedTask;
            },
            (index, token) => TweakFactory.Backups.RestoreAsync(
                SettingSyncBackupId(index), token),
            ct);
}
