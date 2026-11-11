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
    private const string RealtekIdleId = "gt.sys.realtekaudioidle";
    private static readonly string[] RealtekIdleNames =
    [
        "ConservationIdleTime", "PerformanceIdleTime", "IdlePowerState"
    ];

    private static string RealtekIdleBackupId(int index) => RealtekIdleId + "." + index;

    private static async Task RestoreRealtekIdleAsync(
        Func<int, CancellationToken, Task<bool>> restoreOriginal)
    {
        List<Exception> failures = new();
        for (int i = 0; i < RealtekIdleNames.Length; i++)
        {
            try
            {
                // Recovery must not be interrupted after a partially completed mutation.
                if (!await restoreOriginal(i, CancellationToken.None).ConfigureAwait(false))
                    failures.Add(new InvalidOperationException(
                        "Chybí záloha původní hodnoty Realtek: " + RealtekIdleNames[i]));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Obnova hodnoty Realtek selhala: " + RealtekIdleNames[i], ex));
            }
        }
        if (failures.Count > 0)
            throw new AggregateException(
                "Ne všechny původní hodnoty Realtek Audio Idle Power se podařilo obnovit.",
                failures);
    }

    // This exact production transaction accepts fake operations for fault testing.
    // A saved original is a prerequisite for changing even the first registry value.
    internal static async Task ExecuteRealtekIdleMutationAsync(
        bool on,
        Func<CancellationToken, Task> saveOriginals,
        Func<CancellationToken, Task> applyAndVerify,
        Func<int, CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!on)
        {
            // Backups contain both the original registry path and the values:
            // disabling must work even after a device or driver was removed.
            await RestoreRealtekIdleAsync(restoreOriginal).ConfigureAwait(false);
            return;
        }

        await saveOriginals(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await applyAndVerify(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationFailure)
        {
            try
            {
                await RestoreRealtekIdleAsync(restoreOriginal).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Změna Realtek Audio Idle Power i její obnova selhaly.",
                    mutationFailure, rollbackFailure);
            }
            throw;
        }
    }

    private static Task SetRealtekAudioIdlePowerAsync(bool on, CancellationToken ct)
    {
        // On OFF, no hardware discovery is performed. The persisted backups
        // provide the original driver subkey even when hardware has changed.
        string? discoveredPath = on ? FindRealtekPowerSettingsPath() ??
            throw new PlatformNotSupportedException("Realtek HDA PowerSettings nebyly nalezeny.") : null;

        return ExecuteRealtekIdleMutationAsync(
            on,
            async token =>
            {
                for (int i = 0; i < RealtekIdleNames.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await TweakFactory.Backups.SaveOriginalAsync(RealtekIdleBackupId(i),
                        RegistryHive.LocalMachine, discoveredPath!, RealtekIdleNames[i], token)
                        .ConfigureAwait(false);
                }
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey key = root.OpenSubKey(discoveredPath!, writable: true)
                    ?? throw new IOException("Realtek PowerSettings nelze otevřít pro zápis.");
                ReadOnlyMemory<byte>[] expected =
                [
                    new byte[] { 255, 255, 255, 255 },
                    new byte[] { 255, 255, 255, 255 },
                    new byte[] { 0, 0, 0, 0 }
                ];
                for (int i = 0; i < RealtekIdleNames.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    key.SetValue(RealtekIdleNames[i], expected[i].ToArray(),
                        RegistryValueKind.Binary);
                }
                for (int i = 0; i < RealtekIdleNames.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (key.GetValueKind(RealtekIdleNames[i]) != RegistryValueKind.Binary ||
                        key.GetValue(RealtekIdleNames[i]) is not byte[] actual ||
                        !actual.AsSpan().SequenceEqual(expected[i].Span))
                        throw new IOException(
                            "Windows nepotvrdil binární nastavení Realtek " + RealtekIdleNames[i]);
                }
                return Task.CompletedTask;
            },
            (index, token) => TweakFactory.Backups.RestoreAsync(RealtekIdleBackupId(index), token),
            ct);
    }
}
