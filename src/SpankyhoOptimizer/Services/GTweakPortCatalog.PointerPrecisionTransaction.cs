using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string PointerPrecisionPath = @"Control Panel\Mouse";
    private const string PointerPrecisionId = "gt.sys.pointerprecision";

    private static readonly string[] PointerPrecisionNames =
    [
        "MouseSpeed", "MouseThreshold1", "MouseThreshold2"
    ];

    private static string PointerPrecisionBackupId(int index) => PointerPrecisionId + "." + index;

    // Always try to restore all independent values, including when a prior
    // restore fails. Recovery uses an uncancelled token intentionally.
    private static async Task RestorePointerPrecisionAsync(
        Func<int, CancellationToken, Task<bool>> restore)
    {
        var errors = new List<Exception>();
        for (int i = 0; i < PointerPrecisionNames.Length; i++)
        {
            try
            {
                if (!await restore(i, CancellationToken.None).ConfigureAwait(false))
                    errors.Add(new InvalidOperationException(
                        "Chybí původní hodnota pro " + PointerPrecisionNames[i]));
            }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException(
                    "Nepodařilo se obnovit " + PointerPrecisionNames[i], ex));
            }
        }
        if (errors.Count != 0)
            throw new AggregateException(
                "Nepodařilo se úplně obnovit původní nastavení akcelerace myši.", errors);
    }

    // Production and no-write fixtures exercise this exact transaction.
    internal static async Task ExecutePointerPrecisionMutationAsync(
        bool disableAcceleration,
        Func<CancellationToken, Task> captureOriginals,
        Func<CancellationToken, Task> applyAndVerify,
        Func<int, CancellationToken, Task<bool>> restore,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!disableAcceleration)
        {
            // OFF means return to the original mouse settings, not hard-coded
            // default values. Do not re-capture after the change.
            await RestorePointerPrecisionAsync(restore).ConfigureAwait(false);
            return;
        }

        // No registry writes happen unless all originals have been persisted.
        await captureOriginals(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        try
        {
            await applyAndVerify(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception applyError)
        {
            try
            {
                await RestorePointerPrecisionAsync(restore).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Změna akcelerace myši i její obnovení selhaly.",
                    applyError, rollbackError);
            }
            throw;
        }
    }

    private static Task SetPointerPrecisionAsync(bool disableAcceleration, CancellationToken token) =>
        ExecutePointerPrecisionMutationAsync(
            disableAcceleration,
            async ct =>
            {
                for (int i = 0; i < PointerPrecisionNames.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await TweakFactory.Backups.SaveOriginalAsync(
                        PointerPrecisionBackupId(i), RegistryHive.CurrentUser,
                        PointerPrecisionPath, PointerPrecisionNames[i], ct).ConfigureAwait(false);
                }
            },
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey key = root.CreateSubKey(PointerPrecisionPath, writable: true)
                    ?? throw new IOException("Nastavení akcelerace myši nelze otevřít pro zápis.");

                foreach (string name in PointerPrecisionNames)
                {
                    ct.ThrowIfCancellationRequested();
                    key.SetValue(name, "0", RegistryValueKind.String);
                }
                foreach (string name in PointerPrecisionNames)
                {
                    ct.ThrowIfCancellationRequested();
                    if (key.GetValueKind(name) != RegistryValueKind.String ||
                        !string.Equals(key.GetValue(name)?.ToString(), "0", StringComparison.Ordinal))
                        throw new IOException(
                            "Windows nepotvrdil změnu původních parametrů myši: " + name);
                }
                return Task.CompletedTask;
            },
            (index, ct) => TweakFactory.Backups.RestoreAsync(PointerPrecisionBackupId(index), ct),
            token);
}
