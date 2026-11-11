using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // For shared WindowMetrics presets: originals are stored using their
    // historical "<tweak-id>.<index>" backup IDs, never overwritten on OFF.
    private static async Task RestoreMultiStringsAsync(
        int count, Func<int, CancellationToken, Task<bool>> restoreOriginal)
    {
        var failures = new List<Exception>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                if (!await restoreOriginal(i, CancellationToken.None).ConfigureAwait(false))
                    failures.Add(new InvalidOperationException(
                        "Chybí původní hodnota WindowMetrics na indexu " + i));
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Obnovení WindowMetrics selhalo na indexu " + i, ex));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException(
                "Některé původní rozměry prvků Windows nelze obnovit.", failures);
    }

    // This exact production transaction is testable using delegates with no
    // registry writes, while preserving the original backups in production.
    internal static async Task ExecuteMultiStringMutationAsync(
        bool enabled,
        int targetCount,
        Func<CancellationToken, Task> saveOriginals,
        Func<CancellationToken, Task> writeAndVerify,
        Func<int, CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetCount, 1);
        token.ThrowIfCancellationRequested();
        if (!enabled)
        {
            await RestoreMultiStringsAsync(targetCount, restoreOriginal).ConfigureAwait(false);
            return;
        }

        // Save ALL originals before the first change.
        await saveOriginals(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await writeAndVerify(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try
            {
                await RestoreMultiStringsAsync(targetCount, restoreOriginal).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Změna WindowMetrics i její obnova selhaly.",
                    mutationError, rollbackError);
            }
            throw;
        }
    }

    private static Task SetMultiStringsAsync(
        string id,
        RegistryHive hive,
        string path,
        string[] names,
        string enabledValue,
        bool on,
        CancellationToken ct) =>
        ExecuteMultiStringMutationAsync(
            on,
            names.Length,
            async token =>
            {
                for (int i = 0; i < names.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await TweakFactory.Backups.SaveOriginalAsync(
                        id + "." + i, hive, path, names[i], token).ConfigureAwait(false);
                }
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                // Do not create any new metrics key when enabling a preset.
                using RegistryKey key = baseKey.OpenSubKey(path, writable: true)
                    ?? throw new IOException("Systémová nastavení WindowMetrics nejsou dostupná.");

                foreach (string name in names)
                {
                    token.ThrowIfCancellationRequested();
                    key.SetValue(name, enabledValue, RegistryValueKind.String);
                }

                foreach (string name in names)
                {
                    token.ThrowIfCancellationRequested();
                    object? observed = key.GetValue(name, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (key.GetValueKind(name) != RegistryValueKind.String ||
                        observed is not string actual ||
                        !string.Equals(actual, enabledValue, StringComparison.Ordinal))
                        throw new IOException("Windows nepotvrdil rozměr " + name + ".");
                }
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            (index, token) => TweakFactory.Backups.RestoreAsync(id + "." + index, token),
            ct);
}
