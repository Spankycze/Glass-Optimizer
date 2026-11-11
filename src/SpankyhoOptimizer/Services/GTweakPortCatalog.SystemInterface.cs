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
    private static IEnumerable<TweakDefinition> SystemInterfaceSystemTweaks()
    {
        yield return SecurityNotificationsOff();
        yield return BluetoothRadioTweak();
        yield return ToggleKeysShortcutOff();
        yield return FastShutdownAndHungAppTweak();
    }

    private static IEnumerable<TweakDefinition> SystemInterfaceUiTweaks()
    {
        yield return TypeOverlayBadgesTweak();
        yield return DesktopLabelShadowTweak();
        yield return ExplorerHubModeTweak();
        yield return TipsToastOffTweak();
        yield return LockScreenWidgetsOffTweak();
    }

    private static TweakDefinition LockScreenWidgetsOffTweak() =>
        TweakFactory.Dword(
            "ui.lockscreenwidgets", "ui", "Lock Screen",
            "Vypnout Widgety a Počasí na zamykací obrazovce",
            "Zakáže zobrazování widgetů (počasí, finance, sport a doprava) na zamykací obrazovce Windows 11 přes politiku DisableWidgetsOnLockScreen.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh",
            "DisableWidgetsOnLockScreen", 1, null, true, false, TweakRisk.Optional, true);

    private const string FastShutdownTweakId = "sys.fastshutdown";
    private const string DesktopControlPath = @"Control Panel\Desktop";
    private const string FastWaitToKillAppMs = "3000";
    private const string FastHungAppMs = "2500";
    private const string DefaultWaitToKillAppMs = "20000";
    private const string DefaultHungAppMs = "5000";
    private static readonly string[] FastShutdownBackupIds =
    [
        "sys.fastshutdown.waittokillapp",
        "sys.fastshutdown.hungapp"
    ];

    private static TweakDefinition FastShutdownAndHungAppTweak() =>
        TweakFactory.Command(
            FastShutdownTweakId, "system", "Shutdown & Responsiveness",
            "Rychlejší vypínání Windows a ukončení neodpovídajících aplikací",
            "Zkrátí čekání na neodpovídající aplikace při vypínání systému (WaitToKillAppTimeout=3000 ms, HungAppTimeout=2500 ms) v bezpečných mezích se zálohou původních hodnot.",
            async ct => await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return RegistryBatchScope.TryReadString(RegistryHive.CurrentUser, DesktopControlPath, "WaitToKillAppTimeout", out string? waitApp) &&
                       string.Equals(waitApp, FastWaitToKillAppMs, StringComparison.Ordinal) &&
                       RegistryBatchScope.TryReadString(RegistryHive.CurrentUser, DesktopControlPath, "HungAppTimeout", out string? hungApp) &&
                       string.Equals(hungApp, FastHungAppMs, StringComparison.Ordinal);
            }, ct),
            SetFastShutdownAndHungAppAsync, false, TweakRisk.Optional, false, "Nízký",
            null, 19041, "Any", false, false,
            revert: token => SetFastShutdownAndHungAppAsync(false, token));

    private static async Task RestoreFastShutdownTimeoutsAsync(CancellationToken ct)
    {
        bool restoredWait = await TweakFactory.Backups.RestoreAsync(FastShutdownBackupIds[0], ct).ConfigureAwait(false);
        bool restoredHung = await TweakFactory.Backups.RestoreAsync(FastShutdownBackupIds[1], ct).ConfigureAwait(false);
        if (restoredWait && restoredHung)
            return;

        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey key = root.OpenSubKey(DesktopControlPath, writable: true)
            ?? throw new System.IO.IOException("Nelze otevřít klíč Control Panel\\Desktop.");
        if (!restoredWait)
            key.SetValue("WaitToKillAppTimeout", DefaultWaitToKillAppMs, RegistryValueKind.String);
        if (!restoredHung)
            key.SetValue("HungAppTimeout", DefaultHungAppMs, RegistryValueKind.String);

        if ((!restoredWait && !string.Equals(key.GetValue("WaitToKillAppTimeout") as string, DefaultWaitToKillAppMs, StringComparison.Ordinal)) ||
            (!restoredHung && !string.Equals(key.GetValue("HungAppTimeout") as string, DefaultHungAppMs, StringComparison.Ordinal)))
        {
            throw new System.IO.IOException("Windows nepotvrdil obnovení výchozích timeoutů ukončování aplikací.");
        }
    }

    private static async Task SetFastShutdownAndHungAppAsync(bool on, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!on)
        {
            await RestoreFastShutdownTimeoutsAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await TweakFactory.Backups.SaveOriginalAsync(
            FastShutdownBackupIds[0], RegistryHive.CurrentUser, DesktopControlPath, "WaitToKillAppTimeout", ct).ConfigureAwait(false);
        await TweakFactory.Backups.SaveOriginalAsync(
            FastShutdownBackupIds[1], RegistryHive.CurrentUser, DesktopControlPath, "HungAppTimeout", ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey key = root.OpenSubKey(DesktopControlPath, writable: true)
                ?? throw new System.IO.IOException("Nelze otevřít klíč Control Panel\\Desktop.");
            key.SetValue("WaitToKillAppTimeout", FastWaitToKillAppMs, RegistryValueKind.String);
            key.SetValue("HungAppTimeout", FastHungAppMs, RegistryValueKind.String);
            ct.ThrowIfCancellationRequested();

            if (!string.Equals(key.GetValue("WaitToKillAppTimeout") as string, FastWaitToKillAppMs, StringComparison.Ordinal) ||
                !string.Equals(key.GetValue("HungAppTimeout") as string, FastHungAppMs, StringComparison.Ordinal))
            {
                throw new System.IO.IOException("Windows nepotvrdil nastavení timeoutů ukončování aplikací.");
            }
        }
        catch (Exception original)
        {
            try
            {
                await RestoreFastShutdownTimeoutsAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollback)
            {
                throw new AggregateException("Změna timeoutů ukončování aplikací i její rollback selhaly.", original, rollback);
            }
            throw;
        }
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
    private static readonly string[] SecurityNotificationBackupIds =
    [
        "gt.sys.securitynotifications.cu",
        "gt.sys.securitynotifications.lm",
        "gt.sys.securitynotifications.policy"
    ];

    // Restore every independent original even when an earlier restore fails.
    // Do not cancel halfway through recovery: Windows may be partially changed.
    private static async Task RestoreSecurityNotificationsAsync(
        Func<string, System.Threading.CancellationToken, Task<bool>> restore)
    {
        var errors = new List<Exception>();
        foreach (string id in SecurityNotificationBackupIds)
        {
            try
            {
                if (!await restore(id, System.Threading.CancellationToken.None).ConfigureAwait(false))
                    errors.Add(new InvalidOperationException("Chybí původní hodnota pro " + id));
            }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException("Obnova původní hodnoty selhala: " + id, ex));
            }
        }
        if (errors.Count != 0)
            throw new AggregateException("Nelze potvrdit úplné obnovení původních upozornění Windows.", errors);
    }

    // Capture before mutation, roll back all targets if any write, readback or cancellation fails.
    internal static async Task ExecuteSecurityNotificationsMutationAsync(
        bool on,
        Func<System.Threading.CancellationToken, Task> capture,
        Func<System.Threading.CancellationToken, Task> apply,
        Func<string, System.Threading.CancellationToken, Task<bool>> restore,
        System.Threading.CancellationToken ct)
    {
        if (!on)
        {
            await RestoreSecurityNotificationsAsync(restore).ConfigureAwait(false);
            return;
        }

        ct.ThrowIfCancellationRequested();
        await capture(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await apply(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception original)
        {
            try
            {
                await RestoreSecurityNotificationsAsync(restore).ConfigureAwait(false);
            }
            catch (Exception rollback)
            {
                throw new AggregateException("Změna upozornění i její rollback selhaly.", original, rollback);
            }
            throw;
        }
    }

    private static Task SetSecurityNotificationsAsync(bool on, System.Threading.CancellationToken ct)
    {
        const string cu = "Software\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings\\Windows.SystemToast.SecurityAndMaintenance";
        const string lm = "SOFTWARE\\Microsoft\\Windows Defender Security Center\\Notifications";
        const string policy = "SOFTWARE\\Policies\\Microsoft\\Windows Defender Security Center\\Notifications";
        return ExecuteSecurityNotificationsMutationAsync(
            on,
            async token =>
            {
                await TweakFactory.Backups.SaveOriginalAsync(SecurityNotificationBackupIds[0],
                    RegistryHive.CurrentUser, cu, "Enabled", token).ConfigureAwait(false);
                await TweakFactory.Backups.SaveOriginalAsync(SecurityNotificationBackupIds[1],
                    RegistryHive.LocalMachine, lm, "DisableNotifications", token).ConfigureAwait(false);
                await TweakFactory.Backups.SaveOriginalAsync(SecurityNotificationBackupIds[2],
                    RegistryHive.LocalMachine, policy, "DisableNotifications", token).ConfigureAwait(false);
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using var a = Registry.CurrentUser.CreateSubKey(cu, true);
                a.SetValue("Enabled", 0, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                using var b = Registry.LocalMachine.CreateSubKey(lm, true);
                b.SetValue("DisableNotifications", 1, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                using var c = Registry.LocalMachine.CreateSubKey(policy, true);
                c.SetValue("DisableNotifications", 1, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                if (Convert.ToInt32(a.GetValue("Enabled", -1)) != 0 ||
                    Convert.ToInt32(b.GetValue("DisableNotifications", -1)) != 1 ||
                    Convert.ToInt32(c.GetValue("DisableNotifications", -1)) != 1)
                    throw new InvalidOperationException("Windows nepotvrdil změnu všech upozornění.");
                return Task.CompletedTask;
            },
            (id, token) => TweakFactory.Backups.RestoreAsync(id, token),
            ct);
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
            SetExplorerHubModeAsync, false, TweakRisk.Optional, true, "Nízký", null, 22621, "Any", false);
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
            SetTipsToastAsync, false, TweakRisk.Advanced, false, "Střední", "Vypne i běžné toast notifikace aplikací.");
    }
}
