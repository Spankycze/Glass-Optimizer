using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string DesktopIconRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    // All six icons use their historical backup IDs and keep registry metadata
    // exposed to the UI. An explicit OFF/Restore means "restore the original",
    // never force Hide=1 or remove an unrelated user's value.
    private static TweakDefinition DesktopIconTweak(string id, string label, string clsid) => new()
    {
        Id = id,
        Section = "ui",
        Group = "Plocha / ikony",
        Name = "Zobrazit " + label + " na ploše",
        Description = "Přepne standardní systémovou ikonu na ploše.",
        Risk = TweakRisk.Optional,
        Recommended = false,
        RestartRequired = false,
        Impact = "Nízký",
        MinWindowsBuild = 22000,
        HardwareScope = "Any",
        RequiresAdmin = false,
        RegistryHive = RegistryHive.CurrentUser,
        RegistryPath = DesktopIconRegistryPath,
        RegistryValueName = clsid,
        RegistryEnabledValue = 0,
        TechnicalTarget = "Registry: CurrentUser\\" + DesktopIconRegistryPath +
            "\\" + clsid + " -> 0",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = token => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(DesktopIconRegistryPath, writable: false);
            object? observed = key?.GetValue(clsid, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return observed is int dword && dword == 0 &&
                   key!.GetValueKind(clsid) == RegistryValueKind.DWord;
        }, token),
        SetAsync = (show, token) => SetDesktopIconAsync(id, clsid, show, token),
        RevertAsync = token => SetDesktopIconAsync(id, clsid, false, token)
    };

    internal static async Task ExecuteDesktopIconMutationAsync(
        bool showIcon,
        Func<CancellationToken, Task> saveOriginal,
        Func<CancellationToken, Task> writeAndVerify,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!showIcon)
        {
            if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Nelze obnovit původní viditelnost ikony: záloha neexistuje.");
            return;
        }

        // Persist the first original before any DWORD write. SaveOriginalAsync
        // deduplicates by ID, so repeated ON cannot overwrite the initial state.
        await saveOriginal(token).ConfigureAwait(false);
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
                if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Po neúspěšném zápisu chybí záloha původního stavu ikony.");
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "Změna viditelnosti ikony i její obnova selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static Task SetDesktopIconAsync(
        string id, string clsid, bool showIcon, CancellationToken token) =>
        ExecuteDesktopIconMutationAsync(
            showIcon,
            ct => TweakFactory.Backups.SaveOriginalAsync(
                id, RegistryHive.CurrentUser, DesktopIconRegistryPath, clsid, ct),
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                // Explorer's NewStartPanel key is allowed to be created for an
                // explicit user ON; a missing original is retained in backup.
                using RegistryKey key = root.CreateSubKey(
                    DesktopIconRegistryPath, writable: true)
                    ?? throw new IOException("Nelze otevřít nastavení ikon plochy.");
                key.SetValue(clsid, 0, RegistryValueKind.DWord);
                ct.ThrowIfCancellationRequested();
                object? readback = key.GetValue(clsid, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (readback is not int value || value != 0 ||
                    key.GetValueKind(clsid) != RegistryValueKind.DWord)
                    throw new IOException("Windows nepotvrdil viditelnost ikony: " + clsid);
                return Task.CompletedTask;
            },
            ct => TweakFactory.Backups.RestoreAsync(id, ct),
            token);
}
