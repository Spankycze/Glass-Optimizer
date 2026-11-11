using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static class WinhancePortCatalog
{
    internal static IEnumerable<TweakDefinition> SystemTweaks()
    {
        yield return TweakFactory.Dword("wh.sys.devmode", "system", "Vývojář", "Developer Mode", "Povolí Windows Developer Mode pro vývoj a sideloading aplikací.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock", "AllowDevelopmentWithoutDevLicense", 1, 0, false, false, TweakRisk.Optional, false, "Nízký", "Zapínej jen pokud vývojářské funkce skutečně používáš.");
        yield return PowerShellRemoteSigned();
        yield return TweakFactory.Dword("wh.sys.coinstallers", "system", "Ovladače", "Blokovat Driver Co-Installers", "Brání PnP driverům automaticky spouštět doplňkové vendor instalátory.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Device Installer", "DisableCoInstallers", 1, 0, false, false, TweakRisk.Optional, true, "Střední", "Může zabránit automatické instalaci doprovodných aplikací k některým zařízením.");
        yield return TweakFactory.Dword("wh.sys.restore", "system", "Obnova systému", "System Restore povolený", "Ponechá System Restore dostupný přes System Protection.", RegistryHive.LocalMachine,
            "SOFTWARE\\Policies\\Microsoft\\Windows NT\\SystemRestore", "DisableSR", 0, 1, false, true, TweakRisk.Safe, false);
        yield return TweakFactory.Dword("wh.sys.sacoff", "system", "Zabezpečení (Advanced)", "Smart App Control vypnutý", "Vypne Smart App Control policy state. Používej jen pokud SAC blokuje legitimní software.", RegistryHive.LocalMachine,
            "SYSTEM\\CurrentControlSet\\Control\\CI\\Policy", "VerifiedAndReputablePolicyState", 0, 2, false, false, TweakRisk.Advanced, true, "Vysoký", "Snižuje ochranu před nedůvěryhodnými aplikacemi; návrat nemusí být na všech instalacích plně vratný bez obnovy/reinstalace.", 22621);
    }
    internal static IEnumerable<TweakDefinition> NetworkTweaks()
    {
        yield break;
    }

    internal static IEnumerable<TweakDefinition> UiTweaks()
    {
        yield return TweakFactory.Dword("wh.ui.autoplaynonvolume", "ui", "Explorer", "AutoPlay vypnutý pro non-volume zařízení", "Zakáže AutoPlay pro zařízení bez klasického volume.", RegistryHive.CurrentUser,
            "Software\\Policies\\Microsoft\\Windows\\Explorer", "NoAutoplayfornonVolume", 1, 0, false, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("wh.ui.officefiles", "ui", "Quick Access", "Skrýt soubory z Office.com", "Omezí cloudové Office soubory v Explorer Home/Quick Access.", RegistryHive.CurrentUser,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer", "ShowCloudFilesInQuickAccess", 0, 1, false, false, TweakRisk.Optional);
        yield return StringRegistryTweak("wh.ui.iconcachesize", "ui", "Explorer", "Icon cache = 4096", "Zvětší maximální Windows icon cache, což může omezit opakované rebuildy a chybné ikony.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer", "MaxCachedIcons", "4096", false, TweakRisk.Optional, true);
        yield return ThumbnailAutoCleanupTweak(); yield return TweakFactory.Dword("wh.ui.allappslist", "ui", "Start", "All apps = seznam", "Na novém Start menu zobrazí All apps jako klasický seznam místo kategorií/gridu.", RegistryHive.CurrentUser,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Start", "AllAppsViewMode", 2, null, true, false, TweakRisk.Optional, true, "Nízký", null, 26100, "Any", false);
        yield return TweakFactory.Command("wh.ui.trayshowall", "ui", "Taskbar", "Zobrazit všechny tray ikony",
            "Zobrazí zaregistrované ikony, ponechá nabídku skrytých pro nově spuštěné aplikace. Bez restartu Průzkumníka.",
            ct => WinhanceUiService.AreAllTrayIconsShownAsync(ct),
            (on, ct) => WinhanceUiService.SetAllTrayIconsAsync(on, ct),
            false, TweakRisk.Optional, false, "Nízký",
            "Vypnuté aplikace nemají aktivní tray ikonu. Nové ikony mohou být zpočátku v nabídce skrytých ikon.", 22000, "Any", false, true);
    }
    private static TweakDefinition PowerShellRemoteSigned() => TweakFactory.Command(
        "wh.sys.psremote", "system", "Vývojář", "PowerShell policy = RemoteSigned",
        "Povolí lokální skripty a vyžaduje podpis u skriptů označených jako stažené z internetu.",
        async ct => await Task.Run(() =>
        {
            using var key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Policies\\Microsoft\\Windows\\PowerShell");
            return Convert.ToInt32(key?.GetValue("EnableScripts") ?? 0) == 1 &&
                   string.Equals(Convert.ToString(key?.GetValue("ExecutionPolicy")), "RemoteSigned", StringComparison.OrdinalIgnoreCase);
        }, ct),
        async (on, ct) => await Task.Run(() =>
        {
            using var key = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Policies\\Microsoft\\Windows\\PowerShell", true);
            if (on) { key.SetValue("EnableScripts", 1, RegistryValueKind.DWord); key.SetValue("ExecutionPolicy", "RemoteSigned", RegistryValueKind.String); }
            else { key.DeleteValue("EnableScripts", false); key.DeleteValue("ExecutionPolicy", false); }
        }, ct), false, TweakRisk.Optional, false, "Nízký", "Group Policy může mít vyšší prioritu než uživatelské nastavení PowerShellu.");
    private static TweakDefinition StringRegistryTweak(string id, string section, string group, string name, string description,
        RegistryHive hive, string path, string valueName, string enabledValue, bool recommended, TweakRisk risk, bool restart)
    {
        return TweakFactory.Command(id, section, group, name, description,
            async ct => await Task.Run(() =>
            {
                using var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = b.OpenSubKey(path);
                return string.Equals(Convert.ToString(k?.GetValue(valueName)), enabledValue, StringComparison.OrdinalIgnoreCase);
            }, ct),
            async (on, ct) => await Task.Run(async () =>
            {
                await TweakFactory.Backups.SaveOriginalAsync(id, hive, path, valueName);
                if (on)
                {
                    using var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var k = b.CreateSubKey(path, true);
                    k.SetValue(valueName, enabledValue, RegistryValueKind.String);
                }
                else await TweakFactory.Backups.RestoreAsync(id);
                ct.ThrowIfCancellationRequested();
            }, ct), recommended, risk, restart);
    }
    private static TweakDefinition ThumbnailAutoCleanupTweak()
    {
        const string path64 = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VolumeCaches\\Thumbnail Cache";
        const string path32 = "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VolumeCaches\\Thumbnail Cache";
        return TweakFactory.Command("wh.ui.thumbnailautorun", "ui", "Explorer", "Zachovat thumbnail cache", "Vypne automatické mazání thumbnail cache přes SilentCleanup, aby Explorer nemusel zbytečně znovu generovat náhledy.",
            async ct => await Task.Run(() =>
            {
                using var a = Registry.LocalMachine.OpenSubKey(path64); using var b = Registry.LocalMachine.OpenSubKey(path32);
                int va = Convert.ToInt32(a?.GetValue("Autorun") ?? 3); int vb = Convert.ToInt32(b?.GetValue("Autorun") ?? 3);
                return va == 0 && vb == 0;
            }, ct),
            async (on, ct) => await Task.Run(() =>
            {
                using var a = Registry.LocalMachine.CreateSubKey(path64, true); using var b = Registry.LocalMachine.CreateSubKey(path32, true);
                a.SetValue("Autorun", on ? 0 : 3, RegistryValueKind.DWord); b.SetValue("Autorun", on ? 0 : 3, RegistryValueKind.DWord); ct.ThrowIfCancellationRequested();
            }, ct), true, TweakRisk.Safe, false, "Nízký");
    }
}
