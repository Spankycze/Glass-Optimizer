using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string CabcTweakId = "gt.ui.cabc";
    private const string CabcRegistryPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private static readonly string[] CabcBackupIds = ["gt.ui.cabc.disable", "gt.ui.cabc.option"];

    private static async Task RestoreCabcOriginalsAsync(
        Func<int, CancellationToken, Task<bool>> restore)
    {
        var failures = new List<Exception>();
        for (int i = 0; i < CabcBackupIds.Length; i++)
        {
            try
            {
                // Continue even if user cancelled midway through a Windows mutation.
                if (!await restore(i, CancellationToken.None).ConfigureAwait(false))
                    failures.Add(new InvalidOperationException("Chybí původní hodnota: " + CabcBackupIds[i]));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException("Obnova CABC selhala: " + CabcBackupIds[i], ex));
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("Původní nastavení CABC nebylo úplně obnoveno.", failures);
    }

    // Called by real registry implementation and the no-registry-write failure tests.
    internal static async Task ExecuteCabcMutationAsync(
        bool disableCabc,
        Func<CancellationToken, Task> captureOriginals,
        Func<CancellationToken, Task> applyAndVerify,
        Func<int, CancellationToken, Task<bool>> restore,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!disableCabc)
        {
            await RestoreCabcOriginalsAsync(restore).ConfigureAwait(false);
            return;
        }

        // Both originals must be durable before writing either registry value.
        await captureOriginals(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await applyAndVerify(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception originalError)
        {
            try
            {
                await RestoreCabcOriginalsAsync(restore).ConfigureAwait(false);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException("Změna CABC i její rollback selhaly.",
                    originalError, restoreError);
            }
            throw;
        }
    }

    private static Task SetCabcAsync(bool disableCabc, CancellationToken ct) =>
        ExecuteCabcMutationAsync(
            disableCabc,
            async token =>
            {
                await TweakFactory.Backups.SaveOriginalAsync(CabcBackupIds[0],
                    RegistryHive.LocalMachine, CabcRegistryPath, "DisableCABC", token)
                    .ConfigureAwait(false);
                await TweakFactory.Backups.SaveOriginalAsync(CabcBackupIds[1],
                    RegistryHive.LocalMachine, CabcRegistryPath, "CABCOption", token)
                    .ConfigureAwait(false);
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                    RegistryView.Registry64);
                // The GraphicsDrivers key must already exist; never create an
                // arbitrary driver registry subtree when enabling this tweak.
                using RegistryKey key = root.OpenSubKey(CabcRegistryPath, writable: true)
                    ?? throw new IOException("Nastavení GraphicsDrivers pro CABC není dostupné.");
                key.SetValue("DisableCABC", 1, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                bool windows11 = Environment.OSVersion.Version.Build >= 22000;
                if (windows11)
                {
                    key.SetValue("CABCOption", 0, RegistryValueKind.DWord);
                    token.ThrowIfCancellationRequested();
                }

                if (key.GetValueKind("DisableCABC") != RegistryValueKind.DWord ||
                    Convert.ToInt64(key.GetValue("DisableCABC", -1)) != 1)
                    throw new IOException("Windows nepotvrdil hodnotu DisableCABC=1.");
                if (windows11 && (key.GetValueKind("CABCOption") != RegistryValueKind.DWord ||
                                  Convert.ToInt64(key.GetValue("CABCOption", -1)) != 0))
                    throw new IOException("Windows nepotvrdil hodnotu CABCOption=0.");
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            (i, token) => TweakFactory.Backups.RestoreAsync(CabcBackupIds[i], token),
            ct);
}
