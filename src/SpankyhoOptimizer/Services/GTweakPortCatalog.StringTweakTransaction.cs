using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Shared transaction for the three shell-response REG_SZ tweaks. The exact
    // operation below is also exercised by no-registry-write smoke fixtures.
    internal static async Task ExecuteStringTweakMutationAsync(
        bool enable,
        Func<CancellationToken, Task> captureOriginal,
        Func<CancellationToken, Task> writeAndVerify,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enable)
        {
            if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Původní hodnota uživatelského nastavení Windows není zálohovaná.");
            return;
        }

        // Do not write even one value until its original has been persisted.
        await captureOriginal(token).ConfigureAwait(false);
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
                // Recovery must continue even if cancellation follows a write.
                if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Nelze obnovit původní uživatelské nastavení; záloha chybí.");
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "Změna uživatelského nastavení i obnovení původní hodnoty selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static Task SetStringTweakAsync(
        bool enable,
        string backupId,
        RegistryHive hive,
        string registryPath,
        string valueName,
        string desiredValue,
        CancellationToken token) =>
        ExecuteStringTweakMutationAsync(
            enable,
            ct => TweakFactory.Backups.SaveOriginalAsync(
                backupId, hive, registryPath, valueName, ct),
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey key = root.OpenSubKey(registryPath, writable: true)
                    ?? throw new IOException("Nastavení Windows nelze otevřít: " + registryPath);
                key.SetValue(valueName, desiredValue, RegistryValueKind.String);
                ct.ThrowIfCancellationRequested();
                object? actual = key.GetValue(valueName, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (key.GetValueKind(valueName) != RegistryValueKind.String ||
                    actual is not string text ||
                    !string.Equals(text, desiredValue, StringComparison.Ordinal))
                    throw new IOException("Windows nepotvrdil nastavení " + valueName + ".");
                return Task.CompletedTask;
            },
            ct => TweakFactory.Backups.RestoreAsync(backupId, ct),
            token);
}
