using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private static IEnumerable<TweakDefinition> SystemInterfaceSystemTweaks()
    {
        yield return SecurityNotificationsOff();
        yield return BluetoothRadioTweak();

    }

    private static IEnumerable<TweakDefinition> SystemInterfaceUiTweaks()
    {
        yield return TweakFactory.Dword("gt.ui.typeoverlay", "ui", "Explorer", "Type overlay badges vypnuté",
            "Doplní Advanced IconOverlayBadges o ShowTypeOverlay = 0.", RegistryHive.CurrentUser,
            ExplorerAdvanced, "ShowTypeOverlay", 0, 1, false, false, TweakRisk.Optional, true);
        yield return ExplorerHubModeTweak();
        yield return TipsToastOffTweak();
    }
    private static TweakDefinition SecurityNotificationsOff()
    {
        const string cu = "Software\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings\\Windows.SystemToast.SecurityAndMaintenance";
        const string lm = "SOFTWARE\\Microsoft\\Windows Defender Security Center\\Notifications";
        const string policy = "SOFTWARE\\Policies\\Microsoft\\Windows Defender Security Center\\Notifications";
        return TweakFactory.Command("gt.sys.securitynotifications", "system", "Security",
            "Security & Maintenance notifikace vypnuté",
            "Advanced ekvivalent: vypne Security and Maintenance toast a Defender Security Center notifikace.",
            async ct => await Task.Run(() =>
            {
                using var a = Registry.CurrentUser.OpenSubKey(cu);
                using var b = Registry.LocalMachine.OpenSubKey(lm);
                using var c = Registry.LocalMachine.OpenSubKey(policy);
                return Convert.ToInt32(a?.GetValue("Enabled") ?? 1) == 0 &&
                       Convert.ToInt32(b?.GetValue("DisableNotifications") ?? 0) == 1 &&
                       Convert.ToInt32(c?.GetValue("DisableNotifications") ?? 0) == 1;
            }, ct), SetSecurityNotificationsAsync, false, TweakRisk.Advanced, true, "Střední",
            "Může skrýt důležitá upozornění Windows Security. Není součástí Recommended.");
    }
    private static async Task SetSecurityNotificationsAsync(bool on, System.Threading.CancellationToken ct)
    {
        const string cu = "Software\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings\\Windows.SystemToast.SecurityAndMaintenance";
        const string lm = "SOFTWARE\\Microsoft\\Windows Defender Security Center\\Notifications";
        const string policy = "SOFTWARE\\Policies\\Microsoft\\Windows Defender Security Center\\Notifications";
        await TweakFactory.Backups.SaveOriginalAsync("gt.sys.securitynotifications.cu", RegistryHive.CurrentUser, cu, "Enabled");
        await TweakFactory.Backups.SaveOriginalAsync("gt.sys.securitynotifications.lm", RegistryHive.LocalMachine, lm, "DisableNotifications");
        await TweakFactory.Backups.SaveOriginalAsync("gt.sys.securitynotifications.policy", RegistryHive.LocalMachine, policy, "DisableNotifications");
        using var a = Registry.CurrentUser.CreateSubKey(cu, true);
        using var b = Registry.LocalMachine.CreateSubKey(lm, true);
        using var c = Registry.LocalMachine.CreateSubKey(policy, true);
        if (on)
        {
            a.SetValue("Enabled", 0, RegistryValueKind.DWord);
            b.SetValue("DisableNotifications", 1, RegistryValueKind.DWord);
            c.SetValue("DisableNotifications", 1, RegistryValueKind.DWord);
        }
        else
        {
            await TweakFactory.Backups.RestoreAsync("gt.sys.securitynotifications.cu");
            await TweakFactory.Backups.RestoreAsync("gt.sys.securitynotifications.lm");
            await TweakFactory.Backups.RestoreAsync("gt.sys.securitynotifications.policy");
        }
        ct.ThrowIfCancellationRequested();
    }
    private static TweakDefinition ExplorerHubModeTweak()
    {
        const string path = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer";
        return TweakFactory.Command("gt.ui.hubmode", "ui", "Explorer", "Explorer Home/Quick Access vypnutý přes HubMode",
            "Pro novější Windows 11 nastaví HubMode = 1, což doplňuje LaunchTo a skrytí Home v navigaci.",
            async ct => await Task.Run(() =>
            {
                using var k = Registry.LocalMachine.OpenSubKey(path);
                return Convert.ToInt32(k?.GetValue("HubMode") ?? 0) == 1;
            }, ct),
            async (on, ct) =>
            {
                await TweakFactory.Backups.SaveOriginalAsync("gt.ui.hubmode", RegistryHive.LocalMachine, path, "HubMode");
                using var k = Registry.LocalMachine.CreateSubKey(path, true);
                if (on) k.SetValue("HubMode", 1, RegistryValueKind.DWord);
                else await TweakFactory.Backups.RestoreAsync("gt.ui.hubmode");
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Optional, true, "Nízký", null, 22621, "Any", false);
    }
    private static TweakDefinition TipsToastOffTweak()
    {
        const string cu = "Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications";
        const string lm = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer";
        return TweakFactory.Command("gt.ui.tipstoast", "ui", "Notifications", "Windows tips/toast notifikace vypnuté",
            "Advanced TipsNotifications ekvivalent. Vypne systémové toasty a online tips; používej jen pokud nechceš Windows oznámení.",
            async ct => await Task.Run(() =>
            {
                using var a = Registry.CurrentUser.OpenSubKey(cu);
                using var b = Registry.LocalMachine.OpenSubKey(lm);
                return Convert.ToInt32(a?.GetValue("ToastEnabled") ?? 1) == 0 &&
                       Convert.ToInt32(b?.GetValue("AllowOnlineTips") ?? 1) == 0;
            }, ct),
            async (on, ct) =>
            {
                await TweakFactory.Backups.SaveOriginalAsync("gt.ui.tipstoast.cu", RegistryHive.CurrentUser, cu, "ToastEnabled");
                await TweakFactory.Backups.SaveOriginalAsync("gt.ui.tipstoast.lm", RegistryHive.LocalMachine, lm, "AllowOnlineTips");
                using var a = Registry.CurrentUser.CreateSubKey(cu, true);
                using var b = Registry.LocalMachine.CreateSubKey(lm, true);
                if (on) { a.SetValue("ToastEnabled", 0, RegistryValueKind.DWord); b.SetValue("AllowOnlineTips", 0, RegistryValueKind.DWord); }
                else { await TweakFactory.Backups.RestoreAsync("gt.ui.tipstoast.cu"); await TweakFactory.Backups.RestoreAsync("gt.ui.tipstoast.lm"); }
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Advanced, false, "Střední", "Vypne i běžné toast notifikace aplikací.");
    }
}
