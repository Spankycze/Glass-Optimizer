using System;
using System.Collections.Generic;
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
        yield return TweakFactory.Dword("gt.privacy.bingsearchmachine", "privacy", "Search / Cloud",
            "Bing návrhy ve Start vypnuté — machine policy",
            "Doplní uživatelskou Search policy o machine-level DisableSearchBoxSuggestions.",
            RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\Explorer",
            "DisableSearchBoxSuggestions", 1, null, true, false, TweakRisk.Optional, true);
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
            async (on, ct) =>
            {
                for (int i = 0; i < names.Length; i++)
                    await TweakFactory.Backups.SaveOriginalAsync($"gt.privacy.rotatinglock.{i}", RegistryHive.CurrentUser, path, names[i]);
                using var k = Registry.CurrentUser.CreateSubKey(path, true);
                if (on) foreach (string n in names) k.SetValue(n, 0, RegistryValueKind.DWord);
                else for (int i = 0; i < names.Length; i++) await TweakFactory.Backups.RestoreAsync($"gt.privacy.rotatinglock.{i}");
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Optional, false, "Nízký");
    }
}
