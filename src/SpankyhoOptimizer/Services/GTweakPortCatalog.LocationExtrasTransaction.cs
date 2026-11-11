using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string LocationExtrasId = "gt.privacy.locationextras";
    private const string LocationExtrasPath = @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors";
    private static readonly string[] LocationExtrasNames =
    [
        "DisableLocationScripting",
        "DisableWindowsLocationProvider"
    ];

    private static string LocationExtrasBackupId(int index) => LocationExtrasId + "." + index;

    // Restore all originals regardless of errors or cancellation. A partial
    // policy change must not be left silently enabled.
    private static async Task RestoreLocationExtrasAsync(
        Func<int, CancellationToken, Task<bool>> restoreOriginal)
    {
        var errors = new List<Exception>();
        for (int i = 0; i < LocationExtrasNames.Length; i++)
        {
            try
            {
                if (!await restoreOriginal(i, CancellationToken.None).ConfigureAwait(false))
                    errors.Add(new InvalidOperationException(
                        "Chybí záloha původní hodnoty " + LocationExtrasNames[i]));
            }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException(
                    "Obnova původní hodnoty selhala: " + LocationExtrasNames[i], ex));
            }
        }
        if (errors.Count > 0)
            throw new AggregateException("Nastavení Location Provider nebylo úplně obnoveno.", errors);
    }

    // The exact production transaction is exercised by fake delegates in smoke tests.
    internal static async Task ExecuteLocationExtrasMutationAsync(
        bool turnOffProvider,
        Func<CancellationToken, Task> captureOriginals,
        Func<CancellationToken, Task> applyAndVerify,
        Func<int, CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!turnOffProvider)
        {
            // OFF restores original state without taking a new backup or opening
            // registry keys for writing before the restore.
            await RestoreLocationExtrasAsync(restoreOriginal).ConfigureAwait(false);
            return;
        }

        // Never apply the first policy unless both originals are durable.
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
                await RestoreLocationExtrasAsync(restoreOriginal).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Vypnutí Location Provider i obnova původních hodnot selhaly.",
                    mutationError, rollbackError);
            }
            throw;
        }
    }

    private static Task SetLocationExtrasAsync(bool turnOffProvider, CancellationToken ct) =>
        ExecuteLocationExtrasMutationAsync(
            turnOffProvider,
            async token =>
            {
                for (int i = 0; i < LocationExtrasNames.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await TweakFactory.Backups.SaveOriginalAsync(LocationExtrasBackupId(i),
                        RegistryHive.LocalMachine, LocationExtrasPath,
                        LocationExtrasNames[i], token).ConfigureAwait(false);
                }
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey key = root.CreateSubKey(LocationExtrasPath, writable: true)
                    ?? throw new IOException("Nelze otevřít zásady Location Provider.");
                foreach (string name in LocationExtrasNames)
                {
                    token.ThrowIfCancellationRequested();
                    key.SetValue(name, 1, RegistryValueKind.DWord);
                }
                foreach (string name in LocationExtrasNames)
                {
                    token.ThrowIfCancellationRequested();
                    if (key.GetValueKind(name) != RegistryValueKind.DWord ||
                        Convert.ToInt64(key.GetValue(name, -1)) != 1)
                        throw new IOException("Windows nepotvrdil zásadu " + name);
                }
                return Task.CompletedTask;
            },
            (index, token) => TweakFactory.Backups.RestoreAsync(
                LocationExtrasBackupId(index), token),
            ct);
}
