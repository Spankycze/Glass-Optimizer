using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string RotatingLockScreenRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string RotatingLockScreenId = "gt.privacy.rotatinglock";
    private static readonly string[] RotatingLockScreenNames =
    [
        "RotatingLockScreenEnabled",
        "RotatingLockScreenOverlayEnabled",
        "SubscribedContent-338387Enabled"
    ];

    private static string RotatingLockScreenBackupId(int index) =>
        RotatingLockScreenId + "." + index;

    // Restore every independent original. Failure of one restoration must not
    // skip the others, even when a user has cancelled the requested change.
    private static async Task RestoreRotatingLockScreenAsync(
        Func<int, CancellationToken, Task<bool>> restore)
    {
        var failures = new List<Exception>();
        for (int i = 0; i < RotatingLockScreenNames.Length; i++)
        {
            try
            {
                if (!await restore(i, CancellationToken.None).ConfigureAwait(false))
                    failures.Add(new InvalidOperationException(
                        "Pro " + RotatingLockScreenNames[i] + " chybí původní záloha."));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Nelze obnovit původní hodnotu " + RotatingLockScreenNames[i], ex));
            }
        }
        if (failures.Count > 0)
            throw new AggregateException(
                "Některé hodnoty zamykací obrazovky nelze obnovit.", failures);
    }

    // Used both by production and no-write test fixtures.
    internal static async Task ExecuteRotatingLockScreenMutationAsync(
        bool disableSpotlight,
        Func<CancellationToken, Task> captureOriginals,
        Func<CancellationToken, Task> writeAndVerify,
        Func<int, CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!disableSpotlight)
        {
            await RestoreRotatingLockScreenAsync(restoreOriginal).ConfigureAwait(false);
            return;
        }

        // Every original must be persisted before the first registry write.
        await captureOriginals(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await writeAndVerify(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception original)
        {
            try
            {
                await RestoreRotatingLockScreenAsync(restoreOriginal).ConfigureAwait(false);
            }
            catch (Exception rollback)
            {
                throw new AggregateException(
                    "Změna Spotlight a obnova původních nastavení selhaly.", original, rollback);
            }
            throw;
        }
    }

    private static Task SetRotatingLockScreenAsync(bool disableSpotlight, CancellationToken ct) =>
        ExecuteRotatingLockScreenMutationAsync(
            disableSpotlight,
            async token =>
            {
                for (int i = 0; i < RotatingLockScreenNames.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await TweakFactory.Backups.SaveOriginalAsync(RotatingLockScreenBackupId(i),
                        RegistryHive.CurrentUser, RotatingLockScreenRegistryPath,
                        RotatingLockScreenNames[i], token).ConfigureAwait(false);
                }
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey key = root.CreateSubKey(
                    RotatingLockScreenRegistryPath, writable: true)
                    ?? throw new IOException("Nelze otevřít nastavení zamykací obrazovky.");
                foreach (string name in RotatingLockScreenNames)
                {
                    token.ThrowIfCancellationRequested();
                    key.SetValue(name, 0, RegistryValueKind.DWord);
                }
                foreach (string name in RotatingLockScreenNames)
                {
                    token.ThrowIfCancellationRequested();
                    if (key.GetValueKind(name) != RegistryValueKind.DWord ||
                        Convert.ToInt64(key.GetValue(name, -1)) != 0)
                        throw new IOException(
                            "Windows nepotvrdil změnu zamykací obrazovky: " + name);
                }
                return Task.CompletedTask;
            },
            (index, token) => TweakFactory.Backups.RestoreAsync(
                RotatingLockScreenBackupId(index), token),
            ct);
}
