using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string WindowsThemePath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly SemaphoreSlim ThemeModeGate = new(1, 1);

    private static TweakDefinition ThemeModeTweak(
        string id, string name, string description, string registryValue)
    {
        return new TweakDefinition
        {
            Id = id,
            Section = "ui",
            Group = "Vzhled Windows",
            Name = name,
            Description = description,
            Recommended = false,
            Risk = TweakRisk.Optional,
            RestartRequired = false,
            Impact = "Nízký",
            MinWindowsBuild = 19041,
            HardwareScope = "Any",
            RequiresAdmin = false,
            RegistryHive = RegistryHive.CurrentUser,
            RegistryPath = WindowsThemePath,
            RegistryValueName = registryValue,
            RegistryEnabledValue = 0,
            TechnicalTarget =
                "Registry: CurrentUser\\" + WindowsThemePath + "\\" + registryValue + " -> 0",
            RevertMode = "OriginalRegistryValue",
            DetectAsync = ct => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(WindowsThemePath, writable: false);
                return IsDarkThemeValue(key, registryValue);
            }, ct),
            SetAsync = (dark, ct) => SetThemeModeAsync(id, registryValue, dark, ct),
            RevertAsync = ct => SetThemeModeAsync(id, registryValue, false, ct)
        };
    }

    private static bool IsDarkThemeValue(RegistryKey? key, string valueName)
    {
        if (key is null || !key.GetValueNames().Contains(
                valueName, StringComparer.OrdinalIgnoreCase))
            return false;
        object? raw = key.GetValue(valueName, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return raw is int number && number == 0 &&
               key.GetValueKind(valueName) == RegistryValueKind.DWord;
    }

    // Shared production fault boundary. Tests inject fake backups, reads and
    // writes, leaving the user's actual HKCU themes untouched.
    internal static async Task ExecuteThemeModeMutationAsync(
        bool dark,
        Func<CancellationToken, Task> captureOriginal,
        Func<CancellationToken, Task> applyAndVerify,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!dark)
        {
            if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Původní nastavení motivu Windows nemá uloženou zálohu.");
            return;
        }

        // Always persist exact original kind/presence BEFORE changing a DWORD.
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
                        "Nelze obnovit původní motiv Windows: původní záloha chybí.");
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Změna motivu Windows i rollback původního stavu selhaly.",
                    mutationError, restoreError);
            }
            throw;
        }
    }

    private static async Task SetThemeModeAsync(
        string id, string valueName, bool dark, CancellationToken ct)
    {
        await ThemeModeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteThemeModeMutationAsync(
                dark,
                token => TweakFactory.Backups.SaveOriginalAsync(
                    id, RegistryHive.CurrentUser, WindowsThemePath, valueName, token),
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    using RegistryKey root = RegistryKey.OpenBaseKey(
                        RegistryHive.CurrentUser, RegistryView.Registry64);
                    // Do not synthesize a missing Windows Personalize subtree.
                    using RegistryKey key = root.OpenSubKey(WindowsThemePath, writable: true)
                        ?? throw new IOException(
                            "Původní Windows Personalize klíč není dostupný.");
                    key.SetValue(valueName, 0, RegistryValueKind.DWord);
                    token.ThrowIfCancellationRequested();
                    if (!IsDarkThemeValue(key, valueName))
                        throw new IOException(
                            "Windows nepotvrdil nastavení tmavého režimu " + valueName + ".");
                    return Task.CompletedTask;
                },
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    using RegistryKey root = RegistryKey.OpenBaseKey(
                        RegistryHive.CurrentUser, RegistryView.Registry64);
                    using RegistryKey? key = root.OpenSubKey(WindowsThemePath, writable: false);
                    if (key is null)
                        throw new InvalidOperationException(
                            "Klíč Personalize byl odstraněn mimo Optimizer; nebude vytvořen znovu.");
                    if (!IsDarkThemeValue(key, valueName))
                        throw new InvalidOperationException(
                            "Motiv Windows byl mezitím změněn mimo Optimizer; " +
                            "nebudu přepisovat cizí hodnotu " + valueName + ".");
                    return TweakFactory.Backups.RestoreAsync(id, token);
                },
                ct).ConfigureAwait(false);
        }
        finally { ThemeModeGate.Release(); }
    }
}
