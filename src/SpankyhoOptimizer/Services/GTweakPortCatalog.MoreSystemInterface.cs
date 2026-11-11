using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private static TweakDefinition BluetoothRadioTweak() =>
        TweakFactory.Command("gt.sys.bluetoothradio", "system", "Bluetooth", "Bluetooth radio vypnuté",
            "Přímé zapnutí/vypnutí Bluetooth rádia přes Windows.Devices.Radios bez blokování UI.",
            async ct => !await GTweakBluetoothService.IsEnabledForUiAsync(ct),
            async (on, ct) => await GTweakBluetoothService.SetEnabledAsync(!on, ct),
            false, TweakRisk.Optional, false, "Nízký", "Odpojí všechna Bluetooth zařízení.",
            19041, "Bluetooth", true, true);

    private static IEnumerable<TweakDefinition> SystemInterfacePrivacyTweaks()
    {
        yield return RotatingLockScreenOff();
        yield return EdgeBackgroundAndStartupBoostOff();
        yield return TweakFactory.Dword("gt.privacy.bingsearchmachine", "privacy", "Search / Cloud",
            "Bing návrhy ve Start vypnuté — machine policy",
            "Doplní uživatelskou Search policy o machine-level DisableSearchBoxSuggestions.",
            RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\Explorer",
            "DisableSearchBoxSuggestions", 1, null, true, false, TweakRisk.Optional, true);
    }

    private const string EdgeBoostTweakId = "privacy.edgeboost";
    private const string EdgePolicyPath = @"SOFTWARE\Policies\Microsoft\Edge";
    private static readonly string[] EdgeBoostBackupIds =
    [
        "privacy.edgeboost.startup",
        "privacy.edgeboost.background"
    ];

    private static TweakDefinition EdgeBackgroundAndStartupBoostOff() =>
        TweakFactory.Command(
            EdgeBoostTweakId, "privacy", "Microsoft Edge",
            "Zakázat běh Microsoft Edge na pozadí a Startup Boost",
            "Nastaví politiky StartupBoostEnabled=0 a BackgroundModeEnabled=0 pro Microsoft Edge se zálohou původních hodnot a read-back kontrolou.",
            async ct => await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return RegistryBatchScope.TryReadDword(RegistryHive.LocalMachine, EdgePolicyPath, "StartupBoostEnabled", out long startup) &&
                       startup == 0 &&
                       RegistryBatchScope.TryReadDword(RegistryHive.LocalMachine, EdgePolicyPath, "BackgroundModeEnabled", out long bg) &&
                       bg == 0;
            }, ct),
            SetEdgeBackgroundAndStartupBoostOffAsync, true, TweakRisk.Safe, true, "Nízký",
            null, 19041, "Any", false, false,
            revert: token => SetEdgeBackgroundAndStartupBoostOffAsync(false, token));

    private static async Task RestoreEdgeBoostPoliciesAsync(CancellationToken ct)
    {
        bool restoredStartup = await TweakFactory.Backups.RestoreAsync(EdgeBoostBackupIds[0], ct).ConfigureAwait(false);
        bool restoredBg = await TweakFactory.Backups.RestoreAsync(EdgeBoostBackupIds[1], ct).ConfigureAwait(false);
        if (restoredStartup && restoredBg)
            return;

        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(EdgePolicyPath, writable: true);
        if (key != null)
        {
            if (!restoredStartup)
                key.DeleteValue("StartupBoostEnabled", throwOnMissingValue: false);
            if (!restoredBg)
                key.DeleteValue("BackgroundModeEnabled", throwOnMissingValue: false);
        }

        using RegistryKey? verifyKey = root.OpenSubKey(EdgePolicyPath, writable: false);
        if ((!restoredStartup && verifyKey?.GetValue("StartupBoostEnabled") != null) ||
            (!restoredBg && verifyKey?.GetValue("BackgroundModeEnabled") != null))
        {
            throw new System.IO.IOException("Windows nepotvrdil obnovení původních politik Microsoft Edge.");
        }
    }

    private static async Task SetEdgeBackgroundAndStartupBoostOffAsync(bool on, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!on)
        {
            await RestoreEdgeBoostPoliciesAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await TweakFactory.Backups.SaveOriginalAsync(
            EdgeBoostBackupIds[0], RegistryHive.LocalMachine, EdgePolicyPath, "StartupBoostEnabled", ct).ConfigureAwait(false);
        await TweakFactory.Backups.SaveOriginalAsync(
            EdgeBoostBackupIds[1], RegistryHive.LocalMachine, EdgePolicyPath, "BackgroundModeEnabled", ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey key = root.OpenSubKey(EdgePolicyPath, writable: true) ?? root.CreateSubKey(EdgePolicyPath, writable: true)
                ?? throw new System.IO.IOException("Nelze otevřít klíč politik Microsoft Edge.");
            key.SetValue("StartupBoostEnabled", 0, RegistryValueKind.DWord);
            key.SetValue("BackgroundModeEnabled", 0, RegistryValueKind.DWord);
            ct.ThrowIfCancellationRequested();

            object? rawStartup = key.GetValue("StartupBoostEnabled");
            object? rawBg = key.GetValue("BackgroundModeEnabled");
            if (rawStartup is not int startup || startup != 0 ||
                rawBg is not int bg || bg != 0)
            {
                throw new System.IO.IOException("Windows nepotvrdil nastavení politik Microsoft Edge.");
            }
        }
        catch (Exception original)
        {
            try
            {
                await RestoreEdgeBoostPoliciesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollback)
            {
                throw new AggregateException("Změna politik Microsoft Edge i její rollback selhaly.", original, rollback);
            }
            throw;
        }
    }
    private static TweakDefinition RotatingLockScreenOff()
    {
        const string path = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager";
        string[] names = { "RotatingLockScreenEnabled", "RotatingLockScreenOverlayEnabled", "SubscribedContent-338387Enabled" };
        return TweakFactory.Command("gt.privacy.rotatinglock", "privacy", "Windows Spotlight",
            "Rotating Lock Screen / promo vypnuté",
            "Doplní Spotlight tweaky o lock-screen rotating content a overlay promo hodnoty.",
            async ct => await Task.Run(() =>
            {
                using var k = Registry.CurrentUser.OpenSubKey(path);
                return k != null && Array.TrueForAll(names, n => Convert.ToInt32(k.GetValue(n) ?? 1) == 0);
            }, ct),
            SetRotatingLockScreenAsync, false, TweakRisk.Optional, false, "Nízký",
            null, 19041, "Any", false, false,
            revert: token => SetRotatingLockScreenAsync(false, token));
    }
}
