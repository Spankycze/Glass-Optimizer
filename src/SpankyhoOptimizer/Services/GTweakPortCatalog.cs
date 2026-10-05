using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Functional equivalents of useful GTweak controls, adapted to the existing
/// Spankyho Optimizer catalog/UI. UI layout is intentionally untouched.
/// Source reference: Greedeks/GTweak (BSD-3-Clause).
/// </summary>
internal static partial class GTweakPortCatalog
{
    private const string ExplorerAdvanced = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";

    public static IEnumerable<TweakDefinition> SystemTweaks()
    {
        yield return PointerPrecisionOff();
        yield return StickyFilterShortcutsOff();
        yield return RealtekAudioIdlePowerOff();
        yield return TweakFactory.Dword("gt.sys.pchealth", "system", "Windows",
            "PC Health Check nepropagovat", "Omezí návrat/propagaci PC Health Check komponenty.",
            RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\PCHC", "PreviousUninstall", 1, 0,
            false, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.sys.lockscreenpower", "system", "Napájení",
            "Zobrazit Console lock timeout", "Odkryje skryté nastavení vypnutí displeje na zamčené obrazovce.",
            RegistryHive.LocalMachine,
            "SYSTEM\\CurrentControlSet\\Control\\Power\\PowerSettings\\7516b95f-f776-4464-8c53-06167f40cc99\\8EC4B3A5-6868-48c2-BE75-4F3044BE88A7",
            "Attributes", 2, 1, false, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.sys.storeautoupdate", "system", "Microsoft Store",
            "Store automatické aktualizace vypnuté", "Advanced: vypne automatické aktualizace Store aplikací.",
            RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\WindowsStore", "AutoDownload", 2, null,
            true, false, TweakRisk.Advanced, false, "Střední", "Aplikace z Microsoft Store se nebudou aktualizovat automaticky.");
        yield return ExeLaunchWarningsOff();
        foreach (var tweak in SystemInterfaceSystemTweaks()) yield return tweak;
    }

    public static IEnumerable<TweakDefinition> PrivacyTweaks()
    {
        yield return SettingSyncOff();
        yield return TweakFactory.Dword("gt.privacy.ceip", "privacy", "Telemetry",
            "Windows CEIP vypnutý", "Vypne Customer Experience Improvement Program policy.",
            RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\SQMClient\\Windows", "CEIPEnable", 0, null,
            true, true, TweakRisk.Safe, true);
        yield return TweakFactory.Dword("gt.privacy.experiments", "privacy", "Telemetry",
            "Windows experimenty vypnuté", "Zakáže experimentační policy používanou pro A/B funkce Windows.",
            RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\PolicyManager\\current\\device\\System", "AllowExperimentation", 0, null,
            true, true, TweakRisk.Safe, true);
        yield return TweakFactory.Dword("gt.privacy.speechmodel", "privacy", "Speech / Input",
            "Aktualizace speech modelu vypnuté", "Zakáže automatické aktualizace online speech modelu.",
            RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Speech", "AllowSpeechModelUpdate", 0, null,
            true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.privacy.uar", "privacy", "Activity / Usage",
            "User Activity Recording vypnutý", "Zakáže User Activity Recording přes AppCompat policy.",
            RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppCompat", "DisableUAR", 1, null,
            true, true, TweakRisk.Safe, true);
        yield return TweakFactory.Dword("gt.privacy.lockcamera", "privacy", "Activity / Usage",
            "Kamera na lock screenu vypnutá", "Zakáže kameru z uzamčené obrazovky.",
            RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\Personalization", "NoLockScreenCamera", 1, null,
            true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.privacy.offlinemaps", "privacy", "Background",
            "Offline mapy bez auto-update", "Zakáže automatické stahování a aktualizaci offline map.",
            RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\Maps", "AutoDownloadAndUpdateMapData", 0, null,
            true, true, TweakRisk.Safe);
        yield return TweakFactory.Dword("gt.privacy.nvtelemetry", "privacy", "Vendor telemetry",
            "NVIDIA Telemetry Container vypnutý", "Vypne službu NvTelemetryContainer, pokud je nainstalovaná.",
            RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\NvTelemetryContainer", "Start", 4, 2,
            false, false, TweakRisk.Optional, true, "Nízký", null, 19041, "nvidia");
        yield return TweakFactory.Dword("gt.privacy.inteltelemetry", "privacy", "Vendor telemetry",
            "Intel Telemetry service vypnutá", "Pokud existuje služba Telemetry od Intel komponenty, nastaví Disabled.",
            RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\Telemetry", "Start", 4, 2,
            false, false, TweakRisk.Optional, true, "Nízký", null, 19041, "intel");
        yield return LocationProviderExtras();
        yield return CdpUserServiceOff();
        yield return HostsBlockTweak();
        foreach (var tweak in SystemInterfacePrivacyTweaks()) yield return tweak;
    }

    private static TweakDefinition CdpUserServiceOff() =>
        TweakFactory.Dword("gt.privacy.cdpusersvc", "privacy", "Background", "Connected Devices user service vypnutá",
            "Advanced Hidden System Monitoring ekvivalent. Vypne základní CDPUserSvc template; per-user instance se projeví po restartu/přihlášení.",
            RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\CDPUserSvc", "Start", 4, 2,
            false, false, TweakRisk.Optional, true, "Střední", "Může omezit Nearby Sharing a cross-device funkce.");

    public static IEnumerable<TweakDefinition> UiTweaks()
    {
        yield return StringTweak("gt.ui.menudelay", "ui", "Odezva shellu", "Rychlejší context menu",
            "MenuShowDelay = 20 ms místo Windows defaultu 400 ms.", RegistryHive.CurrentUser,
            "Control Panel\\Desktop", "MenuShowDelay", "20", "400", false, TweakRisk.Optional, true);
        yield return StringTweak("gt.ui.taskbarpreview", "ui", "Odezva shellu", "Rychlejší taskbar preview",
            "MouseHoverTime = 20 ms pro rychlejší náhledy taskbaru.", RegistryHive.CurrentUser,
            "Control Panel\\Mouse", "MouseHoverTime", "20", "400", false, TweakRisk.Optional, true);
        yield return StringTweak("gt.ui.cursorblink", "ui", "Odezva shellu", "Rychlejší blikání kurzoru",
            "CursorBlinkRate = 200 ms.", RegistryHive.CurrentUser, "Control Panel\\Desktop", "CursorBlinkRate",
            "200", "530", false, TweakRisk.Optional, false);
        yield return TweakFactory.Dword("gt.ui.darkapps", "ui", "Vzhled Windows", "Tmavý režim aplikací",
            "Přepne systémové AppsUseLightTheme na tmavý režim.", RegistryHive.CurrentUser,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "AppsUseLightTheme", 0, 1,
            false, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.ui.darksystem", "ui", "Vzhled Windows", "Tmavý režim systému",
            "Přepne taskbar/Start/system shell na tmavý režim.", RegistryHive.CurrentUser,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "SystemUsesLightTheme", 0, 1,
            false, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.ui.systemfiles", "ui", "Explorer", "Zobrazit chráněné systémové soubory",
            "Explorer zobrazí i protected operating system files. Používej jen při diagnostice.", RegistryHive.CurrentUser,
            ExplorerAdvanced, "ShowSuperHidden", 1, 0, false, false, TweakRisk.Advanced, false, "Střední",
            "Může zpřístupnit kritické systémové soubory běžnému uživateli.", 22000, "Any", false);
        yield return ClassicContextMenu();
        yield return TweakFactory.Dword("gt.ui.wallpaperquality", "ui", "Vzhled Windows", "JPEG wallpaper bez komprese",
            "JPEGImportQuality = 100 pro maximální kvalitu tapety.", RegistryHive.CurrentUser,
            "Control Panel\\Desktop", "JPEGImportQuality", 100, null, true, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false);
        yield return AdaptiveBrightnessOff();
        yield return DesktopIconTweak("gt.ui.iconpc", "Tento počítač", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        yield return DesktopIconTweak("gt.ui.iconnetwork", "Síť", "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}");
        yield return DesktopIconTweak("gt.ui.iconrecycle", "Koš", "{645FF040-5081-101B-9F08-00AA002F954E}");
        yield return DesktopIconTweak("gt.ui.iconcontrol", "Ovládací panely", "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}");
        yield return DesktopIconTweak("gt.ui.iconuser", "Uživatelské soubory", "{59031A47-3F72-44A7-89C5-5595FE6B30EE}");
        yield return DesktopIconTweak("gt.ui.icononedrive", "OneDrive", "{018D5C66-4533-4307-9B53-224DE2ED1FE6}");
        yield return ThisPcFolderTweak("gt.ui.folder3d", "3D Objects", "{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}");
        yield return ThisPcFolderTweak("gt.ui.folderdesktop", "Plocha", "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}");
        yield return ThisPcFolderTweak("gt.ui.folderdownloads", "Stažené soubory", "{088e3905-0323-4b02-9826-5d99428e115f}");
        yield return ThisPcFolderTweak("gt.ui.folderdocuments", "Dokumenty", "{d3162b92-9365-467a-956b-92703aca08af}");
        yield return ThisPcFolderTweak("gt.ui.folderpictures", "Obrázky", "{24ad3ad4-a569-4530-98e1-ab02f9417aa8}");
        yield return ThisPcFolderTweak("gt.ui.foldermusic", "Hudba", "{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}");
        yield return ThisPcFolderTweak("gt.ui.foldervideos", "Videa", "{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}");
        yield return ExplorerGalleryTweak();
        yield return ExplorerOneDriveNavTweak();
        yield return ExplorerHomeTweak();
        yield return WindowMetricsPreset();
        yield return ScrollbarMetricsPreset();
        yield return StartMenuLayoutTweak();
        foreach (var tweak in SystemInterfaceUiTweaks()) yield return tweak;
    }

    public static IEnumerable<TweakDefinition> NetworkTweaks()
    {
        yield break;
    }
    public static IEnumerable<TweakDefinition> UpdateTweaks()
    {
        yield break;
    }

    public static IEnumerable<TweakDefinition> ServiceTweaks()
    {
        string[] optional =
        {
            "WdiSystemHost", "WdiServiceHost", "TroubleshootingSvc",
            "DPS", "workfolderssvc", "dot3svc", "DevQueryBroker", "AppMgmt", "PushToInstall",
            "WwanSvc", "wlpasvc", "DusmSvc", "autotimesvc", "p2pimsvc",
            "WPDBusEnum", "WiaRpc", "BDESVC", "EFS", "LxpSvc", "WarpJITSvc", "AssignedAccessManagerSvc",
            "WFDSConMgrSvc", "CertPropSvc", "PerfHost", "pla", "wmiApSrv",
            "fhsvc", "wercplsupport", "diagsvc"
        };
        foreach (string service in optional)
            yield return ServiceDisableTweak(service, ServiceLabel(service), TweakRisk.Optional);

        string[] advanced =
        {
            "vmicvmsession", "vmictimesync", "vmicshutdown", "vmicrdv", "vmickvpexchange",
            "vmicheartbeat", "vmicguestinterface", "HvHost", "vmicvss", "McpManagementService", "VacSvc",
            "spectrum", "SharedRealitySvc", "perceptionsimulation", "MixedRealityOpenXRSvc", "EntAppSvc",
            "embeddedmode", "wlidsvc", "WEPHOSTSVC",
            "DmEnrollmentSvc", "Netlogon", "DsSvc",
            "diagnosticshub.standardcollector.service"
        };
        foreach (string service in advanced)
            yield return ServiceDisableTweak(service, ServiceLabel(service), TweakRisk.Advanced);
    }
    private static TweakDefinition StringTweak(string id, string section, string group, string name, string desc,
        RegistryHive hive, string path, string valueName, string enabledValue, string? disabledValue,
        bool recommended, TweakRisk risk, bool restart)
    {
        return TweakFactory.Command(id, section, group, name, desc,
            async ct => await Task.Run(() =>
            {
                using RegistryKey b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? k = b.OpenSubKey(path);
                return string.Equals(k?.GetValue(valueName)?.ToString(), enabledValue, StringComparison.OrdinalIgnoreCase);
            }, ct),
            async (on, ct) =>
            {
                await TweakFactory.Backups.SaveOriginalAsync(id, hive, path, valueName);
                using RegistryKey b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey k = b.CreateSubKey(path, true);
                if (on) k.SetValue(valueName, enabledValue, RegistryValueKind.String);
                else if (!await TweakFactory.Backups.RestoreAsync(id))
                {
                    if (disabledValue == null) k.DeleteValue(valueName, false);
                    else k.SetValue(valueName, disabledValue, RegistryValueKind.String);
                }
                ct.ThrowIfCancellationRequested();
            }, recommended, risk, restart, "Nízký");
    }
    private static TweakDefinition PointerPrecisionOff()
    {
        const string path = "Control Panel\\Mouse";
        string[] names = { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" };
        return TweakFactory.Command("gt.sys.pointerprecision", "system", "Input", "Enhance Pointer Precision vypnuté",
            "Vypne akceleraci myši a ponechá lineární odezvu kurzoru.",
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(path);
                bool disabled = k != null && names.All(n => string.Equals(k.GetValue(n)?.ToString(), "0", StringComparison.Ordinal));
                return Task.FromResult(disabled);
            },
            async (on, ct) =>
            {
                for (int i = 0; i < names.Length; i++)
                    await TweakFactory.Backups.SaveOriginalAsync($"gt.sys.pointerprecision.{i}", RegistryHive.CurrentUser, path, names[i]);
                using RegistryKey k = Registry.CurrentUser.CreateSubKey(path, true);
                if (on) foreach (string n in names) k.SetValue(n, "0", RegistryValueKind.String);
                else for (int i = 0; i < names.Length; i++) await TweakFactory.Backups.RestoreAsync($"gt.sys.pointerprecision.{i}");
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Optional, false, "Nízký", null, 19041, "Any", false);
    }

    private static TweakDefinition StickyFilterShortcutsOff() =>
        StringTweak("gt.ui.filterkeys", "ui", "Input", "Vypnout Filter Keys zkratku",
            "Zakáže nechtěnou aktivaci Filter Keys klávesovou zkratkou.", RegistryHive.CurrentUser,
            "Control Panel\\Accessibility\\Keyboard Response", "Flags", "26", null, false, TweakRisk.Optional, false);
    private static TweakDefinition RealtekAudioIdlePowerOff()
    {
        return TweakFactory.Command("gt.sys.realtekaudioidle", "system", "Audio",
            "Realtek audio power delay vypnutý", "Omezí uspávání Realtek HDA kodeku, které může způsobovat prodlevu/prasknutí při probuzení.",
            async ct => await Task.Run(() =>
            {
                string? path = FindRealtekPowerSettingsPath();
                if (path == null) return false;
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(path);
                return IsFfff(k?.GetValue("ConservationIdleTime") as byte[]) &&
                       IsFfff(k?.GetValue("PerformanceIdleTime") as byte[]) &&
                       IsZero(k?.GetValue("IdlePowerState") as byte[]);
            }, ct),
            async (on, ct) =>
            {
                string path = FindRealtekPowerSettingsPath() ?? throw new PlatformNotSupportedException("Realtek HDA PowerSettings nebyly nalezeny.");
                string[] names = { "ConservationIdleTime", "PerformanceIdleTime", "IdlePowerState" };
                for (int i = 0; i < names.Length; i++)
                    await TweakFactory.Backups.SaveOriginalAsync($"gt.sys.realtekaudioidle.{i}", RegistryHive.LocalMachine, path, names[i]);
                using RegistryKey k = Registry.LocalMachine.CreateSubKey(path, true);
                if (on)
                {
                    k.SetValue(names[0], new byte[] { 255, 255, 255, 255 }, RegistryValueKind.Binary);
                    k.SetValue(names[1], new byte[] { 255, 255, 255, 255 }, RegistryValueKind.Binary);
                    k.SetValue(names[2], new byte[] { 0, 0, 0, 0 }, RegistryValueKind.Binary);
                }
                else for (int i = 0; i < names.Length; i++) await TweakFactory.Backups.RestoreAsync($"gt.sys.realtekaudioidle.{i}");
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Optional, true, "Nízký", "Použije se jen pokud je nalezen Realtek HDA driver.", 19041, "Any", true, true);
    }
    private static string? FindRealtekPowerSettingsPath()
    {
        const string rootPath = "SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e96c-e325-11ce-bfc1-08002be10318}";
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(rootPath);
        if (root == null) return null;
        foreach (string sub in root.GetSubKeyNames())
        {
            if (sub.Equals("Properties", StringComparison.OrdinalIgnoreCase)) continue;
            using RegistryKey? k = root.OpenSubKey(sub);
            string desc = k?.GetValue("DriverDesc")?.ToString() ?? string.Empty;
            if (desc.Contains("Realtek", StringComparison.OrdinalIgnoreCase) &&
                k?.OpenSubKey("PowerSettings") is RegistryKey power)
            {
                power.Dispose();
                return rootPath + "\\" + sub + "\\PowerSettings";
            }
        }
        return null;
    }

    private static bool IsFfff(byte[]? value) => value is { Length: > 0 } && value.All(b => b == 255);
    private static bool IsZero(byte[]? value) => value is { Length: > 0 } && value.All(b => b == 0);
    private static TweakDefinition SettingSyncOff()
    {
        string[] groups = { "Accessibility", "BrowserSettings", "Credentials", "Language", "Personalization", "Windows" };
        const string root = "Software\\Microsoft\\Windows\\CurrentVersion\\SettingSync\\Groups";
        return TweakFactory.Command("gt.privacy.settingsync", "privacy", "Cloud / Sync", "Synchronizace Windows nastavení vypnutá",
            "Zakáže synchronizaci vybraných skupin Windows nastavení mezi zařízeními.",
            async ct => await Task.Run(() => groups.All(g =>
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(root + "\\" + g);
                return Convert.ToInt32(k?.GetValue("Enabled") ?? 1) == 0;
            }), ct),
            async (on, ct) =>
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    string path = root + "\\" + groups[i];
                    string id = $"gt.privacy.settingsync.{i}";
                    await TweakFactory.Backups.SaveOriginalAsync(id, RegistryHive.CurrentUser, path, "Enabled");
                    using RegistryKey k = Registry.CurrentUser.CreateSubKey(path, true);
                    if (on) k.SetValue("Enabled", 0, RegistryValueKind.DWord);
                    else await TweakFactory.Backups.RestoreAsync(id);
                }
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Optional, false, "Nízký", "Vypne synchronizaci některých osobních nastavení.", 19041, "Any", false);
    }
    private static TweakDefinition LocationProviderExtras()
    {
        const string path = "SOFTWARE\\Policies\\Microsoft\\Windows\\LocationAndSensors";
        string[] names = { "DisableLocationScripting", "DisableWindowsLocationProvider" };
        return TweakFactory.Command("gt.privacy.locationextras", "privacy", "Location", "Location provider a scripting vypnuté",
            "Doplní globální Location tweak o starší Location Provider a scripting policies.",
            async ct => await Task.Run(() =>
            {
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(path);
                return k != null && names.All(n => Convert.ToInt32(k.GetValue(n) ?? 0) == 1);
            }, ct),
            async (on, ct) =>
            {
                for (int i = 0; i < names.Length; i++)
                {
                    string id = $"gt.privacy.locationextras.{i}";
                    await TweakFactory.Backups.SaveOriginalAsync(id, RegistryHive.LocalMachine, path, names[i]);
                    using RegistryKey k = Registry.LocalMachine.CreateSubKey(path, true);
                    if (on) k.SetValue(names[i], 1, RegistryValueKind.DWord);
                    else await TweakFactory.Backups.RestoreAsync(id);
                }
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Optional, true, "Nízký", "Může ovlivnit mapy, počasí a automatické časové pásmo.");
    }
    private static TweakDefinition ClassicContextMenu()
    {
        const string root = "Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        const string path = root + "\\InprocServer32";
        return TweakFactory.Command("gt.ui.classicmenu", "ui", "Explorer", "Klasické kontextové menu Windows 10",
            "Na Windows 11 zobrazí plné klasické pravé menu bez mezikroku „Zobrazit další možnosti“.",
            async ct => await Task.Run(() => Registry.CurrentUser.OpenSubKey(path) != null, ct),
            async (on, ct) => await Task.Run(() =>
            {
                if (on)
                {
                    using RegistryKey k = Registry.CurrentUser.CreateSubKey(path, true);
                    k.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
                }
                else Registry.CurrentUser.DeleteSubKeyTree(root, false);
                ct.ThrowIfCancellationRequested();
            }, ct), false, TweakRisk.Optional, true, "Nízký", null, 22000, "Any", false);
    }

    private static TweakDefinition AdaptiveBrightnessOff()
    {
        const string path = "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";
        return TweakFactory.Command("gt.ui.cabc", "ui", "Display", "Content Adaptive Brightness vypnutý",
            "Zakáže CABC/adaptive content brightness na podporovaných zařízeních.",
            async ct => await Task.Run(() =>
            {
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(path);
                return Convert.ToInt32(k?.GetValue("DisableCABC") ?? 0) == 1;
            }, ct), SetCabcAsync, false, TweakRisk.Optional, true, "Nízký", null, 22000);
    }
    private static async Task SetCabcAsync(bool on, CancellationToken ct)
    {
        const string path = "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";
        await TweakFactory.Backups.SaveOriginalAsync("gt.ui.cabc.disable", RegistryHive.LocalMachine, path, "DisableCABC");
        await TweakFactory.Backups.SaveOriginalAsync("gt.ui.cabc.option", RegistryHive.LocalMachine, path, "CABCOption");
        using RegistryKey k = Registry.LocalMachine.CreateSubKey(path, true);
        if (on)
        {
            k.SetValue("DisableCABC", 1, RegistryValueKind.DWord);
            if (Environment.OSVersion.Version.Build >= 22000) k.SetValue("CABCOption", 0, RegistryValueKind.DWord);
        }
        else
        {
            await TweakFactory.Backups.RestoreAsync("gt.ui.cabc.disable");
            await TweakFactory.Backups.RestoreAsync("gt.ui.cabc.option");
        }
        ct.ThrowIfCancellationRequested();
    }

    private static TweakDefinition DesktopIconTweak(string id, string label, string clsid) =>
        TweakFactory.Dword(id, "ui", "Plocha / ikony", "Zobrazit " + label + " na ploše",
            "Přepne standardní systémovou ikonu na ploše.", RegistryHive.CurrentUser,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\HideDesktopIcons\\NewStartPanel", clsid,
            0, 1, false, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false);
    private static TweakDefinition ThisPcFolderTweak(string id, string label, string clsid)
    {
        string path = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\MyComputer\\NameSpace\\" + clsid;
        return TweakFactory.Command(id, "ui", "Tento počítač", "Skrýt " + label + " z Tento počítač",
            "Odebere pouze položku navigace Tento počítač; skutečná uživatelská složka se nemaže.",
            async ct => await Task.Run(() => Registry.LocalMachine.OpenSubKey(path) == null, ct),
            async (on, ct) => await Task.Run(() =>
            {
                if (on) Registry.LocalMachine.DeleteSubKeyTree(path, false);
                else using (RegistryKey _ = Registry.LocalMachine.CreateSubKey(path, true)) { }
                ct.ThrowIfCancellationRequested();
            }, ct), false, TweakRisk.Optional, true, "Nízký");
    }

    private static TweakDefinition ExplorerGalleryTweak()
    {
        const string clsid = "{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
        string[] paths =
        {
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Desktop\\NameSpace\\" + clsid,
            "SOFTWARE\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Desktop\\NameSpace\\" + clsid
        };
        return KeyPairPresenceTweak("gt.ui.gallery", "Skrýt Gallery z Exploreru", paths, true);
    }
    private static TweakDefinition ExplorerOneDriveNavTweak()
    {
        string[] paths =
        {
            "CLSID\\{018D5C66-4533-4307-9B53-224DE2ED1FE6}",
            "Wow6432Node\\CLSID\\{018D5C66-4533-4307-9B53-224DE2ED1FE6}"
        };
        return TweakFactory.Command("gt.ui.onedrivenav", "ui", "Explorer", "Skrýt OneDrive v navigaci Exploreru",
            "Skryje OneDrive namespace položku, samotný OneDrive neodinstaluje.",
            async ct => await Task.Run(() => paths.All(p =>
            {
                using RegistryKey? k = Registry.ClassesRoot.OpenSubKey(p);
                return Convert.ToInt32(k?.GetValue("System.IsPinnedToNameSpaceTree") ?? 1) == 0;
            }), ct),
            async (on, ct) => await Task.Run(() =>
            {
                foreach (string p in paths)
                {
                    using RegistryKey k = Registry.ClassesRoot.CreateSubKey(p, true);
                    k.SetValue("System.IsPinnedToNameSpaceTree", on ? 0 : 1, RegistryValueKind.DWord);
                }
                ct.ThrowIfCancellationRequested();
            }, ct), false, TweakRisk.Optional, true, "Nízký");
    }

    private static TweakDefinition ExeLaunchWarningsOff()
    {
        const string lmPath = "SOFTWARE\\Microsoft\\Internet Explorer\\Security";
        const string cuPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\\Zones\\3";
        return TweakFactory.Command("gt.sys.exelaunchwarnings", "system", "Security / Compatibility",
            "EXE launch warnings vypnuté", "Advanced Advanced kompatibilita: vypne Internet-zone security warning pro spouštění souborů.",
            async ct => await Task.Run(() =>
            {
                using var lm = Registry.LocalMachine.OpenSubKey(lmPath);
                using var cu = Registry.CurrentUser.OpenSubKey(cuPath);
                return Convert.ToInt32(lm?.GetValue("DisableSecuritySettingsCheck") ?? 0) == 1 &&
                       Convert.ToInt32(cu?.GetValue("1806") ?? 3) == 0;
            }, ct),
            async (on, ct) =>
            {
                await TweakFactory.Backups.SaveOriginalAsync("gt.sys.exelaunchwarnings.lm", RegistryHive.LocalMachine, lmPath, "DisableSecuritySettingsCheck");
                await TweakFactory.Backups.SaveOriginalAsync("gt.sys.exelaunchwarnings.cu", RegistryHive.CurrentUser, cuPath, "1806");
                using var lm = Registry.LocalMachine.CreateSubKey(lmPath, true);
                using var cu = Registry.CurrentUser.CreateSubKey(cuPath, true);
                if (on) { lm.SetValue("DisableSecuritySettingsCheck", 1, RegistryValueKind.DWord); cu.SetValue("1806", 0, RegistryValueKind.DWord); }
                else { await TweakFactory.Backups.RestoreAsync("gt.sys.exelaunchwarnings.lm"); await TweakFactory.Backups.RestoreAsync("gt.sys.exelaunchwarnings.cu"); }
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Advanced, false, "Vysoký", "Snižuje varování Windows při spouštění souborů stažených z internetu.");
    }

    private static TweakDefinition ExplorerHomeTweak()
    {
        const string clsid = "{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
        string userPath = "Software\\Classes\\CLSID\\" + clsid;
        return TweakFactory.Command("gt.ui.explorerhome", "ui", "Explorer", "Skrýt Home v Exploreru",
            "Skryje Home z navigačního stromu Exploreru bez odstranění uživatelských souborů.",
            async ct => await Task.Run(() =>
            {
                using var k = Registry.CurrentUser.OpenSubKey(userPath);
                return Convert.ToInt32(k?.GetValue("System.IsPinnedToNameSpaceTree") ?? 1) == 0;
            }, ct),
            async (on, ct) => await Task.Run(() =>
            {
                using var k = Registry.CurrentUser.CreateSubKey(userPath, true);
                if (on) k.SetValue("System.IsPinnedToNameSpaceTree", 0, RegistryValueKind.DWord);
                else k.DeleteValue("System.IsPinnedToNameSpaceTree", false);
                ct.ThrowIfCancellationRequested();
            }, ct), false, TweakRisk.Optional, true, "Nízký");
    }

    private static TweakDefinition StartMenuLayoutTweak()
    {
        const string cuPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";
        const string policyPath = "SOFTWARE\\Policies\\Microsoft\\Windows\\Explorer";
        return TweakFactory.Command("gt.ui.startlayout", "ui", "Start", "Skrýt Recommended sekci ve Start",
            "Advanced kompatibilní Start layout: omezí Recommended sekci na podporovaných edicích Windows 11.",
            async ct => await Task.Run(() =>
            {
                using var cu = Registry.CurrentUser.OpenSubKey(cuPath);
                using var lm = Registry.LocalMachine.OpenSubKey(policyPath);
                return Convert.ToInt32(cu?.GetValue("Start_Layout") ?? 0) == 1 ||
                       Convert.ToInt32(lm?.GetValue("HideRecommendedSection") ?? 0) == 1;
            }, ct),
            async (on, ct) =>
            {
                await TweakFactory.Backups.SaveOriginalAsync("gt.ui.startlayout.cu", RegistryHive.CurrentUser, cuPath, "Start_Layout");
                await TweakFactory.Backups.SaveOriginalAsync("gt.ui.startlayout.lm", RegistryHive.LocalMachine, policyPath, "HideRecommendedSection");
                using var cu = Registry.CurrentUser.CreateSubKey(cuPath, true);
                using var lm = Registry.LocalMachine.CreateSubKey(policyPath, true);
                if (on) { cu.SetValue("Start_Layout", 1, RegistryValueKind.DWord); lm.SetValue("HideRecommendedSection", 1, RegistryValueKind.DWord); }
                else { await TweakFactory.Backups.RestoreAsync("gt.ui.startlayout.cu"); await TweakFactory.Backups.RestoreAsync("gt.ui.startlayout.lm"); }
                ct.ThrowIfCancellationRequested();
            }, false, TweakRisk.Optional, true, "Nízký", "Policy HideRecommendedSection nemusí být respektována ve Windows Home.", 22000, "Any", false);
    }

    private static TweakDefinition ScrollbarMetricsPreset() =>
        MultiStringTweak("gt.ui.scrollbarsize", "ui", "Vzhled Windows", "Kompaktnější scrollbar",
            "Advanced WindowMetrics preset ScrollHeight/ScrollWidth = -210.", RegistryHive.CurrentUser,
            "Control Panel\\Desktop\\WindowMetrics", new[] { "ScrollHeight", "ScrollWidth" }, "-210", false, TweakRisk.Optional, true);

    private static TweakDefinition WindowMetricsPreset() =>
        MultiStringTweak("gt.ui.windowmetrics", "ui", "Vzhled Windows", "Kompaktnější titulkové tlačítko",
            "Advanced WindowMetrics preset CaptionHeight/CaptionWidth = -270.", RegistryHive.CurrentUser,
            "Control Panel\\Desktop\\WindowMetrics", new[] { "CaptionHeight", "CaptionWidth" }, "-270", false, TweakRisk.Optional, true);
    private static TweakDefinition KeyPairPresenceTweak(string id, string name, string[] paths, bool hideWhenOn)
    {
        return TweakFactory.Command(id, "ui", "Explorer", name, "Přepíná systémovou namespace položku Exploreru.",
            async ct => await Task.Run(() => hideWhenOn
                ? paths.All(p => Registry.LocalMachine.OpenSubKey(p) == null)
                : paths.All(p => Registry.LocalMachine.OpenSubKey(p) != null), ct),
            async (on, ct) => await Task.Run(() =>
            {
                foreach (string path in paths)
                {
                    bool remove = hideWhenOn ? on : !on;
                    if (remove) Registry.LocalMachine.DeleteSubKeyTree(path, false);
                    else using (RegistryKey _ = Registry.LocalMachine.CreateSubKey(path, true)) { }
                }
                ct.ThrowIfCancellationRequested();
            }, ct), false, TweakRisk.Optional, true, "Nízký");
    }

    private static TweakDefinition MultiStringTweak(string id, string section, string group, string name, string desc,
        RegistryHive hive, string path, string[] names, string enabledValue, bool recommended, TweakRisk risk, bool restart)
    {
        return TweakFactory.Command(id, section, group, name, desc,
            async ct => await Task.Run(() =>
            {
                using RegistryKey b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? k = b.OpenSubKey(path);
                return k != null && names.All(n => string.Equals(k.GetValue(n)?.ToString(), enabledValue, StringComparison.OrdinalIgnoreCase));
            }, ct),
            async (on, ct) => await SetMultiStringsAsync(id, hive, path, names, enabledValue, on, ct),
            recommended, risk, restart, "Nízký");
    }
    private static async Task SetMultiStringsAsync(string id, RegistryHive hive, string path, string[] names,
        string enabledValue, bool on, CancellationToken ct)
    {
        for (int i = 0; i < names.Length; i++)
            await TweakFactory.Backups.SaveOriginalAsync($"{id}.{i}", hive, path, names[i]);
        using RegistryKey b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey k = b.CreateSubKey(path, true);
        if (on) foreach (string name in names) k.SetValue(name, enabledValue, RegistryValueKind.String);
        else for (int i = 0; i < names.Length; i++) await TweakFactory.Backups.RestoreAsync($"{id}.{i}");
        ct.ThrowIfCancellationRequested();
    }


    private static TweakDefinition ServiceDisableTweak(string service, string label, TweakRisk risk)
    {
        return TweakFactory.Command("gt.svc." + service, "components", "Služby", label + " — vypnout",
            "Položka převzatá z Advanced service groups. Je volitelná a má read-back přes SCM.",
            async ct => await Task.Run(() => ServiceSafetyService.ReadStartType(service) == 4, ct),
            async (on, ct) => await Task.Run(() => SetServiceStart(service, on ? 4u : 3u), ct),
            false, risk, true, risk == TweakRisk.Advanced ? "Střední" : "Nízký",
            risk == TweakRisk.Advanced ? "Může vypnout systémovou funkci, kterou některé aplikace používají." : null,
            19041, "Any", true, true);
    }
    private static void SetServiceStart(string service, uint desired)
    {
        int original = ServiceSafetyService.ReadStartType(service) ??
            throw new InvalidOperationException($"Služba {service} na tomto systému není dostupná.");
        if (desired == 4 && !ServiceSafetyService.CanDisable(service))
            throw new InvalidOperationException(ServiceSafetyService.ExplainBlock(service));
        if (!NativeCore.ChangeServiceStartType(service, desired, out int error))
            throw new InvalidOperationException($"SCM error {error} pro {service}.");
        int actual = ServiceSafetyService.ReadStartType(service) ?? -1;
        if (actual != desired)
        {
            NativeCore.ChangeServiceStartType(service, (uint)original, out _);
            throw new InvalidOperationException($"Read-back služby {service} nepotvrdil Start={desired}; původní stav byl obnoven.");
        }
        ServiceCatalogService.InvalidateCache();
    }

    private static string ServiceLabel(string service) => service switch
    {
        "edgeupdate" => "Microsoft Edge Update",
        "edgeupdatem" => "Microsoft Edge Update (Machine)",
        "MMCSS" => "Multimedia Class Scheduler",
        "DPS" => "Diagnostic Policy Service",
        "WpnService" => "Windows Push Notifications",
        "WiaRpc" => "Windows Image Acquisition / Scanner",
        "BDESVC" => "BitLocker Drive Encryption",
        "EFS" => "Encrypting File System",
        "WPDBusEnum" => "Portable Device Enumerator",
        "WwanSvc" => "WWAN AutoConfig",
        _ => service
    };
}
