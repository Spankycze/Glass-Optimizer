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
        yield return PcHealthTweak();
        yield return LockScreenTimeoutTweak();
        yield return StoreAutoUpdateTweak();
        yield return ExeLaunchWarningsOff();
        foreach (var tweak in SystemInterfaceSystemTweaks()) yield return tweak;
    }

    public static IEnumerable<TweakDefinition> PrivacyTweaks()
    {
        yield return SettingSyncOff();
        yield return CeipTweak();
        yield return TweakFactory.Dword("gt.privacy.experiments", "privacy", "Telemetry",
            "Windows experimenty vypnuté", "Zakáže experimentační policy používanou pro A/B funkce Windows.",
            RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\PolicyManager\\current\\device\\System", "AllowExperimentation", 0, null,
            true, true, TweakRisk.Safe, true);
        yield return SpeechModelTweak();
        yield return UarTweak();
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
            "Pouze pokročilé ruční nastavení: zakáže šablonu CDPUserSvc. Uživatelská instance se vytváří při přihlášení, takže pouhá změna zpět nemusí ihned obnovit její chod.",
            RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\CDPUserSvc", "Start", 4, 2,
            false, false, TweakRisk.Advanced, true, "Vysoké",
            "NEVYPÍNAT při řešení pádů ctfmon.exe: mohou selhat související systémové služby zadávání a propojení zařízení. Obnovení může vyžadovat nové přihlášení.");

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
        yield return ThemeModeTweak("gt.ui.darkapps", "Tmavý režim aplikací",
            "Přepne systémové AppsUseLightTheme na tmavý režim.", "AppsUseLightTheme");
        yield return ThemeModeTweak("gt.ui.darksystem", "Tmavý režim systému",
            "Přepne taskbar/Start/system shell na tmavý režim.", "SystemUsesLightTheme");
        yield return TweakFactory.Dword("gt.ui.systemfiles", "ui", "Explorer", "Zobrazit chráněné systémové soubory",
            "Explorer zobrazí i protected operating system files. Používej jen při diagnostice.", RegistryHive.CurrentUser,
            ExplorerAdvanced, "ShowSuperHidden", 1, 0, false, false, TweakRisk.Advanced, false, "Střední",
            "Může zpřístupnit kritické systémové soubory běžnému uživateli.", 22000, "Any", false);
        yield return ClassicContextMenu();
        yield return WallpaperQualityTweak();
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
            if (WindowsComponentCatalogPolicy.ServiceRegistered(service))
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
            if (WindowsComponentCatalogPolicy.ServiceRegistered(service))
                yield return ServiceDisableTweak(service, ServiceLabel(service), TweakRisk.Advanced);
    }
    private static TweakDefinition StringTweak(string id, string section, string group, string name, string desc,
        RegistryHive hive, string path, string valueName, string enabledValue, string? disabledValue,
        bool recommended, TweakRisk risk, bool restart)
    {
        // The old disabledValue parameter is retained for existing callers.
        // OFF deliberately restores the exact original rather than applying
        // a synthetic default or deleting the user's previous value.
        _ = disabledValue;
        return TweakFactory.Command(id, section, group, name, desc,
            ct => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(path, writable: false);
                object? actual = key?.GetValue(valueName, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                return actual is string observed && key is not null &&
                    key.GetValueKind(valueName) == RegistryValueKind.String &&
                    string.Equals(observed, enabledValue, StringComparison.Ordinal);
            }, ct),
            (on, ct) => SetStringTweakAsync(on, id, hive, path, valueName, enabledValue, ct),
            recommended, risk, restart, "Nízký", null, 19041, "Any",
            hive == RegistryHive.LocalMachine, false,
            revert: ct => SetStringTweakAsync(false, id, hive, path, valueName, enabledValue, ct));
    }
    private static TweakDefinition PointerPrecisionOff()
    {
        const string path = "Control Panel\\Mouse";
        return TweakFactory.Command("gt.sys.pointerprecision", "system", "Input", "Enhance Pointer Precision vypnuté",
            "Vypne akceleraci myši a ponechá lineární odezvu kurzoru.",
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(path);
                bool disabled = k != null && PointerPrecisionNames.All(n => string.Equals(k.GetValue(n)?.ToString(), "0", StringComparison.Ordinal));
                return Task.FromResult(disabled);
            },
            SetPointerPrecisionAsync, false, TweakRisk.Optional, false, "Nízký",
            null, 19041, "Any", false, false,
            revert: token => SetPointerPrecisionAsync(false, token));
    }

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
            SetRealtekAudioIdlePowerAsync, false, TweakRisk.Optional, true, "Nízký",
            "Použije se jen pokud je nalezen Realtek HDA driver.",
            19041, "Any", true, true,
            revert: token => SetRealtekAudioIdlePowerAsync(false, token));
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
    private static TweakDefinition SettingSyncOff() =>
        TweakFactory.Command(SettingSyncTweakId, "privacy", "Cloud / Sync",
            "Synchronizace Windows nastavení vypnutá",
            "Zakáže synchronizaci vybraných skupin Windows nastavení mezi zařízeními.",
            async ct => await Task.Run(() => SettingSyncGroups.All(group =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    SettingSyncRoot + "\\" + group);
                object? value = key?.GetValue("Enabled");
                return value is not null &&
                    key!.GetValueKind("Enabled") == RegistryValueKind.DWord &&
                    Convert.ToInt64(value) == 0;
            }), ct),
            SetSettingSyncAsync, false, TweakRisk.Optional, false, "Nízký",
            "Vypne synchronizaci některých osobních nastavení.",
            19041, "Any", false, false,
            revert: token => SetSettingSyncAsync(false, token));
    private static TweakDefinition LocationProviderExtras() =>
        TweakFactory.Command(LocationExtrasId, "privacy", "Location",
            "Location provider a scripting vypnuté",
            "Doplní globální Location tweak o starší Location Provider a scripting policies.",
            ct => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(LocationExtrasPath, writable: false);
                if (key is null) return false;
                return LocationExtrasNames.All(name =>
                {
                    ct.ThrowIfCancellationRequested();
                    object? value = key.GetValue(name);
                    return value is not null &&
                           key.GetValueKind(name) == RegistryValueKind.DWord &&
                           Convert.ToInt64(value) == 1;
                });
            }, ct),
            SetLocationExtrasAsync, false, TweakRisk.Optional, true, "Nízký",
            "Může ovlivnit mapy, počasí a automatické časové pásmo.",
            19041, "Any", true, false,
            revert: token => SetLocationExtrasAsync(false, token));
    private static TweakDefinition ClassicContextMenu() =>
        TweakFactory.Command(ClassicMenuId, "ui", "Explorer",
            "Klasické kontextové menu Windows 10",
            "Na Windows 11 zobrazí plné klasické pravé menu bez mezikroku „Zobrazit další možnosti“.",
            DetectClassicMenuAsync,
            SetClassicMenuAsync, false, TweakRisk.Optional, true, "Nízký",
            "Odstraní pouze vlastní klíče; existující cizí nastavení zachovává.",
            22000, "Any", false, false,
            revert: token => SetClassicMenuAsync(false, token));
    private static TweakDefinition AdaptiveBrightnessOff()
    {
        const string path = "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";
        return TweakFactory.Command("gt.ui.cabc", "ui", "Display", "Content Adaptive Brightness vypnutý",
            "Zakáže CABC/adaptive content brightness na podporovaných zařízeních.",
            async ct => await Task.Run(() =>
            {
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(path);
                return Convert.ToInt32(k?.GetValue("DisableCABC") ?? 0) == 1;
            }, ct), SetCabcAsync, false, TweakRisk.Optional, true, "Nízký", null, 22000, "Any", true, false,
            revert: token => SetCabcAsync(false, token));
    }
    private static TweakDefinition StartMenuLayoutTweak() =>
        TweakFactory.Command("gt.ui.startlayout", "ui", "Start",
            "Skrýt Recommended sekci ve Start",
            "Advanced kompatibilní Start layout: omezí Recommended sekci na podporovaných edicích Windows 11.",
            DetectStartLayoutAsync,
            SetStartLayoutAsync, false, TweakRisk.Optional, true, "Nízký",
            "Policy HideRecommendedSection nemusí být respektována ve Windows Home.",
            22000, "Any", true, false,
            revert: token => SetStartLayoutAsync(false, token));
    private static TweakDefinition ScrollbarMetricsPreset() =>
        MultiStringTweak("gt.ui.scrollbarsize", "ui", "Vzhled Windows", "Kompaktnější scrollbar",
            "Advanced WindowMetrics preset ScrollHeight/ScrollWidth = -210.", RegistryHive.CurrentUser,
            "Control Panel\\Desktop\\WindowMetrics", new[] { "ScrollHeight", "ScrollWidth" }, "-210", false, TweakRisk.Optional, true);

    private static TweakDefinition WindowMetricsPreset() =>
        MultiStringTweak("gt.ui.windowmetrics", "ui", "Vzhled Windows", "Kompaktnější titulkové tlačítko",
            "Advanced WindowMetrics preset CaptionHeight/CaptionWidth = -270.", RegistryHive.CurrentUser,
            "Control Panel\\Desktop\\WindowMetrics", new[] { "CaptionHeight", "CaptionWidth" }, "-270", false, TweakRisk.Optional, true);
    private static TweakDefinition MultiStringTweak(string id, string section, string group, string name, string desc,
        RegistryHive hive, string path, string[] names, string enabledValue, bool recommended, TweakRisk risk, bool restart)
    {
        return TweakFactory.Command(id, section, group, name, desc,
            ct => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(path, writable: false);
                if (key is null) return false;
                return names.All(valueName =>
                {
                    ct.ThrowIfCancellationRequested();
                    object? observed = key.GetValue(valueName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return observed is string text &&
                           key.GetValueKind(valueName) == RegistryValueKind.String &&
                           string.Equals(text, enabledValue, StringComparison.Ordinal);
                });
            }, ct),
            (on, ct) => SetMultiStringsAsync(id, hive, path, names, enabledValue, on, ct),
            recommended, risk, restart, "Nízký", null, 19041, "Any",
            hive == RegistryHive.LocalMachine, false,
            revert: ct => SetMultiStringsAsync(id, hive, path, names, enabledValue, false, ct));
    }
    private static string ServiceLabel(string service) => service switch
    {
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
