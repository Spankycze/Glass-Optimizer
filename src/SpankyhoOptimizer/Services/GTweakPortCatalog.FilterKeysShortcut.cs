using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string FilterKeysShortcutId = "gt.ui.filterkeys";
    private const string FilterKeysShortcutPath = @"Control Panel\Accessibility\Keyboard Response";
    private const string FilterKeysShortcutValue = "Flags";
    private const string FilterKeysShortcutOffFlags = "26";
    private const string ToggleKeysShortcutId = "sys.togglekeys";
    private const string ToggleKeysShortcutPath = @"Control Panel\Accessibility\ToggleKeys";
    private const string ToggleKeysShortcutValue = "Flags";
    private const string ToggleKeysShortcutOffFlags = "58";
    private const string ToggleKeysShortcutDefaultFlags = "62";

    private static TweakDefinition StickyFilterShortcutsOff() =>
        TweakFactory.Command(
            FilterKeysShortcutId, "ui", "Input", "Vypnout Filter Keys zkratku",
            "Zakáže nechtěnou aktivaci Filter Keys klávesovou zkratkou.",
            token => Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(FilterKeysShortcutPath, writable: false);
                object? original = key?.GetValue(FilterKeysShortcutValue, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                return original is string flags &&
                       key!.GetValueKind(FilterKeysShortcutValue) == RegistryValueKind.String &&
                       string.Equals(flags, FilterKeysShortcutOffFlags, StringComparison.Ordinal);
            }, token),
            SetFilterKeysShortcutAsync, false, TweakRisk.Optional, false, "Nízký",
            null, 19041, "Any", false, false,
            revert: token => SetFilterKeysShortcutAsync(false, token));

    private static TweakDefinition ToggleKeysShortcutOff() =>
        TweakFactory.Command(
            ToggleKeysShortcutId, "system", "Klávesnice a vstup",
            "Vypnout zkratku Toggle Keys (pípání NumLocku po 5 s)",
            "Zakáže nechtěnou aktivaci funkce Ozvučení kláves (Toggle Keys) po přidržení NumLocku na 5 sekund se zálohou původní hodnoty.",
            token => Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(ToggleKeysShortcutPath, writable: false);
                object? original = key?.GetValue(ToggleKeysShortcutValue, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                return original is string flags &&
                       key!.GetValueKind(ToggleKeysShortcutValue) == RegistryValueKind.String &&
                       string.Equals(flags, ToggleKeysShortcutOffFlags, StringComparison.Ordinal);
            }, token),
            SetToggleKeysShortcutAsync, true, TweakRisk.Safe, false, "Nízký",
            null, 19041, "Any", false, false,
            revert: token => SetToggleKeysShortcutAsync(false, token));

    // Same mutation for production and no-OS-write fault injection. OFF has
    // only one meaning: restore the original REG_SZ or its original absence.
    internal static async Task ExecuteFilterKeysShortcutMutationAsync(
        bool disableHotkey,
        Func<CancellationToken, Task> captureOriginal,
        Func<CancellationToken, Task> applyAndVerify,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!disableHotkey)
        {
            if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Původní nastavení zkratky Filter Keys není zálohované.");
            return;
        }

        // Backup must be persisted before the first mutation, and cancellation
        // after the registry write must not prevent restoration.
        await captureOriginal(ct).ConfigureAwait(false);
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
                if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Chybí záloha původní hodnoty Filter Keys.");
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Změna Filter Keys i obnovení původní hodnoty selhaly.",
                    mutationError, restoreError);
            }
            throw;
        }
    }

    private static Task SetFilterKeysShortcutAsync(bool disableHotkey, CancellationToken ct) =>
        ExecuteFilterKeysShortcutMutationAsync(
            disableHotkey,
            token => TweakFactory.Backups.SaveOriginalAsync(
                FilterKeysShortcutId, RegistryHive.CurrentUser, FilterKeysShortcutPath,
                FilterKeysShortcutValue, token),
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                // Do not invent a new accessibility settings key if absent.
                using RegistryKey key = root.OpenSubKey(FilterKeysShortcutPath, writable: true)
                    ?? throw new IOException("Nastavení zkratky Filter Keys není dostupné.");
                key.SetValue(FilterKeysShortcutValue,
                    FilterKeysShortcutOffFlags, RegistryValueKind.String);
                token.ThrowIfCancellationRequested();
                object? observed = key.GetValue(FilterKeysShortcutValue, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (key.GetValueKind(FilterKeysShortcutValue) != RegistryValueKind.String ||
                    observed is not string flags ||
                    !string.Equals(flags, FilterKeysShortcutOffFlags, StringComparison.Ordinal))
                    throw new IOException("Windows nepotvrdil vypnutí zkratky Filter Keys.");
                return Task.CompletedTask;
            },
            token => TweakFactory.Backups.RestoreAsync(FilterKeysShortcutId, token),
            ct);

    private static Task SetToggleKeysShortcutAsync(bool disableHotkey, CancellationToken ct) =>
        ExecuteFilterKeysShortcutMutationAsync(
            disableHotkey,
            token => TweakFactory.Backups.SaveOriginalAsync(
                ToggleKeysShortcutId, RegistryHive.CurrentUser, ToggleKeysShortcutPath,
                ToggleKeysShortcutValue, token),
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey key = root.OpenSubKey(ToggleKeysShortcutPath, writable: true)
                    ?? throw new IOException("Nastavení zkratky Toggle Keys není dostupné.");
                key.SetValue(ToggleKeysShortcutValue,
                    ToggleKeysShortcutOffFlags, RegistryValueKind.String);
                token.ThrowIfCancellationRequested();
                object? observed = key.GetValue(ToggleKeysShortcutValue, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (key.GetValueKind(ToggleKeysShortcutValue) != RegistryValueKind.String ||
                    observed is not string flags ||
                    !string.Equals(flags, ToggleKeysShortcutOffFlags, StringComparison.Ordinal))
                    throw new IOException("Windows nepotvrdil vypnutí zkratky Toggle Keys.");
                return Task.CompletedTask;
            },
            async token =>
            {
                if (await TweakFactory.Backups.RestoreAsync(ToggleKeysShortcutId, token).ConfigureAwait(false))
                    return true;

                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(ToggleKeysShortcutPath, writable: true);
                if (key == null)
                    return false;

                key.SetValue(ToggleKeysShortcutValue, ToggleKeysShortcutDefaultFlags, RegistryValueKind.String);
                object? observed = key.GetValue(ToggleKeysShortcutValue, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                return key.GetValueKind(ToggleKeysShortcutValue) == RegistryValueKind.String &&
                       observed is string flags &&
                       string.Equals(flags, ToggleKeysShortcutDefaultFlags, StringComparison.Ordinal);
            },
            ct);
}
