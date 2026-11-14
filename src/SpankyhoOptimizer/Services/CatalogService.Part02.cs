using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static partial class CatalogService
{
    private static TweakDefinition DriverStartTweak(string service, string label, string reason, bool recommended, TweakRisk risk)
    {
        string path = "SYSTEM\\CurrentControlSet\\Services\\" + service;
        string id = "drv." + service;
        return new TweakDefinition
        {
            Id = id,
            Section = "components",
            Group = "Ovladače",
            Name = label,
            Description = reason,
            Recommended = recommended,
            Risk = risk,
            RestartRequired = true,
            Impact = "Nízký",
            HardwareScope = DriverHardwareScope(service),
            TechnicalTarget = $"Registry: HKLM\\{path}\\Start -> 4",
            RequiresAdmin = true,
            RegistryHive = RegistryHive.LocalMachine,
            RegistryPath = path,
            RegistryValueName = "Start",
            RegistryEnabledValue = 4,
            RevertMode = "OriginalRegistryValue",
            RevertAsync = async ct =>
            {
                if (!await TweakFactory.Backups.RestoreAsync(id, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Pro tento driver tweak není uložená původní hodnota.");
            },
            DetectAsync = ct =>
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path, false);
                return Task.FromResult(key != null && Convert.ToInt32(key.GetValue("Start", -1)) == 4);
            },
SetAsync = async (on, ct) =>
            {
                if (on) await TweakFactory.Backups.SaveOriginalAsync(id, RegistryHive.LocalMachine, path, "Start", ct).ConfigureAwait(false);
                if (!on && await TweakFactory.Backups.RestoreAsync(id, ct).ConfigureAwait(false)) return;
                ct.ThrowIfCancellationRequested();
                // TweakEngine already executes this on a thread-pool slot, so the
                // extra Task.Run hop only added a second scheduler dispatch per
                // driver toggle (one per page render across ~20 drivers).
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path, true) ?? throw new PlatformNotSupportedException($"Driver service {service} v tomto systemu není přítomný.");
                int original = Convert.ToInt32(key.GetValue("Start", 3));
                int desired = on ? 4 : 3;
                key.SetValue("Start", desired, RegistryValueKind.DWord);
if (Convert.ToInt32(key.GetValue("Start", -1)) != desired)
                {
                    key.SetValue("Start", original, RegistryValueKind.DWord);
                    throw new InvalidOperationException($"Read-back driveru {service} nepotvrdil Start={desired}; původní hodnota byla obnovena.");
                }
            }
        };
    }

    private static string DriverHardwareScope(string service) => service.ToLowerInvariant() switch
    {
        "flpydisk" or "sfloppy" => "Floppy",
        "serial" or "serenum" => "Serial",
        "bthusb" or "bthenum" or "bthleenum" or "bthhfenum" or "bthpan" or "rfcomm" or "hidbth" or "btha2dp" => "Bluetooth",
        "cdrom" => "Optical",
        _ => "Any"
    };

    private static List<TweakDefinition> TaskTweaks()
    {
        var list = new (string, string, string, bool)[]
        {
            ("\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator", "CEIP Consolidator", "Telemetry", true),
            ("\\Microsoft\\Windows\\Customer Experience Improvement Program\\UsbCeip", "USB CEIP", "Telemetry", true),
            ("\\Microsoft\\Windows\\Feedback\\Siuf\\DmClient", "Feedback DmClient", "Feedback", true),
            ("\\Microsoft\\Windows\\Feedback\\Siuf\\DmClientOnScenarioDownload", "Feedback scenario download", "Feedback", true),
            ("\\Microsoft\\Windows\\Maps\\MapsUpdateTask", "Maps Update", "Maps", true),
            ("\\Microsoft\\Windows\\Maps\\MapsToastTask", "Maps Toast", "Maps", true),
            ("\\Microsoft\\Windows\\Customer Experience Improvement Program\\KernelCeipTask", "Kernel CEIP", "Telemetry", true),
            ("\\Microsoft\\Windows\\Customer Experience Improvement Program\\BthSQM", "Bluetooth SQM", "Telemetry", true),
            ("\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser", "Compatibility Appraiser", "Compatibility", false),
            ("\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater", "Program Data Updater", "Compatibility", false),
            ("\\Microsoft\\Windows\\Application Experience\\StartupAppTask", "Startup App Task", "Compatibility", false),
            ("\\Microsoft\\Windows\\Application Experience\\MareBackup", "Mare Backup", "Compatibility", false),
            ("\\Microsoft\\Windows\\Application Experience\\PcaPatchDbTask", "PCA Patch DB", "Compatibility", false),
            ("\\Microsoft\\Windows\\Application Experience\\SdbinstMergeDbTask", "SDB Merge DB", "Compatibility", false),
            ("\\Microsoft\\Windows\\DiskDiagnostic\\Microsoft-Windows-DiskDiagnosticDataCollector", "Disk Diagnostic Collector", "Diagnostics", false),
            ("\\Microsoft\\Windows\\DiskDiagnostic\\Microsoft-Windows-DiskDiagnosticResolver", "Disk Diagnostic Resolver", "Diagnostics", false),
            ("\\Microsoft\\Windows\\DiskFootprint\\Diagnostics", "Disk Footprint", "Diagnostics", false),
            ("\\Microsoft\\Windows\\Power Efficiency Diagnostics\\AnalyzeAll", "Power Efficiency Analyze", "Diagnostics", false),
            ("\\Microsoft\\Windows\\NetTrace\\GatherNetworkInfo", "Gather Network Info", "Diagnostics", false),
            ("\\Microsoft\\Windows\\Windows Error Reporting\\QueueReporting", "WER Queue Reporting", "Diagnostics", false),
            ("\\Microsoft\\Windows\\Autochk\\Proxy", "Autochk Proxy", "Telemetry", false),
            ("\\Microsoft\\Windows\\Location\\Notifications", "Location Notifications", "Location", false),
            ("\\Microsoft\\Windows\\Location\\WindowsActionDialog", "Location Action Dialog", "Location", false),
            ("\\Microsoft\\Windows\\Shell\\FamilySafetyMonitor", "Family Safety Monitor", "Family", false),
            ("\\Microsoft\\Windows\\Shell\\FamilySafetyRefreshTask", "Family Safety Refresh", "Family", false),
            ("\\Microsoft\\Windows\\SettingSync\\BackgroundUploadTask", "Settings Sync Upload", "Sync", false),
            ("\\Microsoft\\Windows\\SettingSync\\NetworkStateChangeTask", "Settings Sync Network", "Sync", false),
            ("\\Microsoft\\Windows\\Application Experience\\AitAgent", "Application Impact Telemetry", "Compatibility", false),
            ("\\Microsoft\\Windows\\PI\\Sqm-Tasks", "SQM Tasks", "Telemetry", false),
            ("\\Microsoft\\Windows\\CloudExperienceHost\\CreateObjectTask", "Cloud Experience Host", "Diagnostics", false),
            ("\\Microsoft\\Windows\\Shell\\FamilySafetyUpload", "Family Safety Upload", "Family", false),
            ("\\Microsoft\\Office\\OfficeTelemetryAgentFallBack2016", "Office Telemetry Fallback", "Office", false),
            ("\\Microsoft\\Office\\OfficeTelemetryAgentLogOn2016", "Office Telemetry Logon", "Office", false),
            ("\\StartAllBack Update", "StartAllBack Update", "Vendor / Updaters", false)
        }.Where(x => WindowsComponentCatalogPolicy.TaskRegistered(x.Item1))
         .Select(((string, string, string, bool) x) => TaskDisabledTweak(x.Item1, x.Item2, x.Item3, x.Item4)).ToList();
        list.AddRange(GTweakTaskCatalog.TaskTweaks());
        return list;
    }

    private static TweakDefinition TaskDisabledTweak(string full, string label, string group, bool recommended)
    {
        return TweakFactory.Command("task." + label.Replace(" ", ""), "components", group, label, "Naplánovaná úloha Windows.",
            async ct => !await new TaskSchedulerService().GetEnabledAsync(full, ct),
            async (on, ct) => await new TaskSchedulerService().SetEnabledAsync(full, !on, ct), recommended);
    }

    private static TweakDefinition StringValueTweak(string id, string section, string group, string name, string description, RegistryHive hive, string path, string valueName, string enabled, string disabled, bool recommended = false, TweakRisk risk = TweakRisk.Optional, bool restart = false)
    {
        return TweakFactory.Command(id, section, group, name, description, ct =>
        {
            // TweakEngine already runs DetectAsync on a thread-pool slot;
            // the inner Task.Run added a second scheduler hop per string tweak.
            using var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = b.OpenSubKey(path);
            return Task.FromResult(string.Equals(Convert.ToString(k?.GetValue(valueName)), enabled, StringComparison.OrdinalIgnoreCase));
        }, async (on, ct) =>
        {
            using var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = b.CreateSubKey(path, true);
            k.SetValue(valueName, on ? enabled : disabled, RegistryValueKind.String);
        }, recommended, risk, restart);
    }

    private static List<TweakDefinition> UiTweaks()
    {
        const string adv = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";
        var list = new List<TweakDefinition>
        {
            GlassUiTransparency,
            TweakFactory.Dword("ui.bluewindowaccent", "ui", "Vzhled", "Accent na titlebarech a okrajích", "Volitelné: použije systémový accent i na titlebar/window border. Není součástí Doporučených.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\DWM", "ColorPrevalence", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.inactiveblue", "ui", "Vzhled", "Tmavší modrý inactive accent", "WindowsModernizer hodnota pro neaktivní titlebar/border.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\DWM", "AccentColorInactive", unchecked((int)0xFF8A1A1E), null, true, true, TweakRisk.Optional),
            GlassUiTaskAnimations,
            TweakFactory.Dword("ui.snap", "ui", "Okna", "Snap Assist zapnutý", "Windows Snap Layouts.", RegistryHive.CurrentUser, adv, "EnableSnapAssistFlyout", 1, null, true, false),
            TweakFactory.Dword("ui.shake", "ui", "Okna", "Vypnout Aero Shake", "Zabrání minimalizaci ostatních oken zatřesením.", RegistryHive.CurrentUser, "Software\\Policies\\Microsoft\\Windows\\Explorer", "NoWindowMinimizingShortcuts", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.extensions", "ui", "Explorer", "Zobrazit přípony souborů", "Explorer ukáže skutečné přípony.", RegistryHive.CurrentUser, adv, "HideFileExt", 0, 1, false, true),
            TweakFactory.Dword("ui.hidden", "ui", "Explorer", "Zobrazit skryté soubory", "Chráněné systémové soubory zůstávají skryté.", RegistryHive.CurrentUser, adv, "Hidden", 1, 2, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.fullpath", "ui", "Explorer", "Plná cesta v titulku", "Ukáže celou cestu v okně Exploreru.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\CabinetState", "FullPath", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.compact", "ui", "Explorer", "Compact View", "Menší rozestupy mezi položkami.", RegistryHive.CurrentUser, adv, "UseCompactMode", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.launchthispc", "ui", "Explorer", "Otevírat Tento počítač", "Explorer místo Home otevře Tento počítač.", RegistryHive.CurrentUser, adv, "LaunchTo", 1, 2, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.navexpand", "ui", "Explorer", "Rozbalit navigaci do aktuální složky", "Levý strom sleduje aktuální umístění.", RegistryHive.CurrentUser, adv, "NavPaneExpandToCurrentFolder", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.navall", "ui", "Explorer", "Zobrazit všechny složky", "Rozšíří strom navigace Exploreru.", RegistryHive.CurrentUser, adv, "NavPaneShowAllFolders", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.separateprocess", "ui", "Explorer", "Explorer okna v odděleném procesu", "Lepší izolace při pádu okna Exploreru.", RegistryHive.CurrentUser, adv, "SeparateProcess", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.checkbox", "ui", "Explorer", "Checkboxy pro výběr souborů", "Zobrazí zaškrtávací políčka při výběru.", RegistryHive.CurrentUser, adv, "AutoCheckSelect", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.statusbar", "ui", "Explorer", "Stavový řádek Exploreru", "Zobrazí stavový řádek.", RegistryHive.CurrentUser, adv, "ShowStatusBar", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.driveletters", "ui", "Explorer", "Písmena disků před názvem", "Upřednostní písmeno jednotky před názvem.", RegistryHive.CurrentUser, adv, "ShowDriveLettersFirst", 4, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.emptydrives", "ui", "Explorer", "Zobrazit prázdné jednotky", "Neukrývá prázdné čtečky médií.", RegistryHive.CurrentUser, adv, "HideDrivesWithNoMedia", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.iconsnotthumbs", "ui", "Explorer", "Náhledy souborů zapnuté", "Povolí thumbnails místo pouze ikon.", RegistryHive.CurrentUser, adv, "IconsOnly", 0, 1, false, false),
            TweakFactory.Dword("ui.infotips", "ui", "Explorer", "InfoTips zapnuté", "Ponechá užitečné popisky souborů a složek.", RegistryHive.CurrentUser, adv, "ShowInfoTip", 1, 0, false, false),
            TweakFactory.Dword("ui.thumbcache", "ui", "Explorer výkon", "Thumbnail cache zapnutá", "Ponechá cache náhledů aktivní, aby Explorer zbytečně negeneroval thumbnails znovu.", RegistryHive.CurrentUser, adv, "DisableThumbnailCache", 0, 1, false, true),
            TweakFactory.Dword("ui.previewhandlers", "ui", "Explorer výkon", "Preview handlery zapnuté", "Povolí náhledové handlery v Exploreru.", RegistryHive.CurrentUser, adv, "ShowPreviewHandlers", 1, 0, false, true),
            TweakFactory.Dword("ui.nonetworkthumbsdb", "ui", "Explorer výkon", "Nevytvářet Thumbs.db na síti", "Na síťových složkách nevytváří lokální Thumbs.db metadata.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer", "DisableThumbsDBOnNetworkFolders", 1, 0, false, true),
            TweakFactory.Dword("ui.hoverfast", "ui", "UI Responsiveness", "Rychlejší taskbar hover", "ExtendedUIHoverTime = 100 ms.", RegistryHive.CurrentUser, adv, "ExtendedUIHoverTime", 100, null, true, true, TweakRisk.Safe),
            TweakFactory.Dword("ui.thumbhoverfast", "ui", "UI Responsiveness", "Rychlejší thumbnail preview", "ThumbnailLivePreviewHoverTime = 100 ms.", RegistryHive.CurrentUser, adv, "ThumbnailLivePreviewHoverTime", 100, null, true, true, TweakRisk.Safe),
            TweakFactory.Dword("ui.syncpromo", "ui", "Explorer", "Skrýt Explorer sync promo", "Skryje reklamní/sync provider bannery.", RegistryHive.CurrentUser, adv, "ShowSyncProviderNotifications", 0, 1, false, true),
            TweakFactory.Command("ui.shortcutarrow", "ui", "Plocha / Zástupci", "Odebrat šipku u zástupců", "Skryje overlay šipku pomocí vlastní transparentní ikony; neodstraňuje IsShortcut a nerozbíjí připnuté položky.", ct =>
            {
                // Pure in-memory read; the Task.Run hop only added a scheduler dispatch.
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(ShortcutAppearanceService.ShortcutArrowHidden());
            }, async (on, ct) => await ShortcutAppearanceService.SetShortcutArrowHiddenAsync(on, ct).ConfigureAwait(false), false, TweakRisk.Optional, false, "Nízký", null),
            TweakFactory.Command("ui.shortcutsuffix", "ui", "Plocha / Zástupci", "Nepsat „- Shortcut“ do názvu", "Nově vytvořené zástupce Windows nebude automaticky pojmenovávat s příponou „- Shortcut“.", ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(ShortcutAppearanceService.ShortcutSuffixDisabled());
            }, async (on, ct) => await ShortcutAppearanceService.SetShortcutSuffixDisabledAsync(on, ct).ConfigureAwait(false), false, TweakRisk.Optional, false, "Nízký", null),
            TweakFactory.Dword("ui.seconds", "ui", "Taskbar", "Sekundy v hodinách", "Zobrazí sekundy, pokud je build podporuje.", RegistryHive.CurrentUser, adv, "ShowSecondsInSystemClock", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.search", "ui", "Taskbar", "Skrýt hledání", "Start hledání zůstává dostupné.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Search", "SearchboxTaskbarMode", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.searchlocation", "ui", "Search", "Search bez polohy", "Windows Search nebude používat polohu uživatele.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Search", "AllowSearchToUseLocation", 0, 1, false, true),
            TweakFactory.Dword("ui.searchfeed", "ui", "Search", "Search feed vypnutý", "Vypne Microsoft Search feed v Search UI.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\SearchSettings", "IsMSFTSearchFeedEnabled", 0, 1, false, true),
            TweakFactory.Dword("ui.searchhistory", "ui", "Search", "Historie hledání zařízení vypnutá", "Neukládá device search history.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\SearchSettings", "IsDeviceSearchHistoryEnabled", 0, 1, false, true),
            TweakFactory.Dword("ui.searchmsa", "ui", "Search", "Microsoft Account cloud search vypnutý", "Omezí cloud výsledky z osobního Microsoft účtu.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\SearchSettings", "IsMSACloudSearchEnabled", 0, 1, false, true),
            TweakFactory.Dword("ui.searchaad", "ui", "Search", "Work/School cloud search vypnutý", "Omezí cloud výsledky z AAD/work účtu.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\SearchSettings", "IsAADCloudSearchEnabled", 0, 1, false, true),
            TweakFactory.Dword("ui.widgets", "ui", "Taskbar", "Skrýt Widgets", "Skryje tlačítko Widgety.", RegistryHive.CurrentUser, adv, "TaskbarDa", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.chat", "ui", "Taskbar", "Skrýt Chat / Teams", "Skryje tlačítko Chat/Teams.", RegistryHive.CurrentUser, adv, "TaskbarMn", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.taskview", "ui", "Taskbar", "Skrýt Task View", "Win+Tab zůstává funkční.", RegistryHive.CurrentUser, adv, "ShowTaskViewButton", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.desktopcorner", "ui", "Taskbar", "Vypnout roh Zobrazit plochu", "Vypne citlivý pravý roh taskbaru.", RegistryHive.CurrentUser, adv, "TaskbarSd", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.taskbarleft", "ui", "Taskbar", "Taskbar zarovnat vlevo", "Zarovná Start a připnuté ikony vlevo.", RegistryHive.CurrentUser, adv, "TaskbarAl", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.badges", "ui", "Taskbar", "Skrýt badge na taskbaru", "Skryje odznaky/počítadla na ikonách.", RegistryHive.CurrentUser, adv, "TaskbarBadges", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.smalltaskbar", "ui", "Taskbar", "Menší ikony taskbaru", "Windows 11 IconSizePreference = 0.", RegistryHive.CurrentUser, adv, "IconSizePreference", 0, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.desktoppeek", "ui", "Taskbar", "Vypnout Aero Peek náhled plochy", "Vypne náhled plochy při najetí na pravý okraj taskbaru.", RegistryHive.CurrentUser, adv, "DisablePreviewDesktop", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.recent", "ui", "Quick Access", "Skrýt nedávné soubory", "Quick Access nebude zobrazovat historii souborů.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer", "ShowRecent", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.frequent", "ui", "Quick Access", "Skrýt časté složky", "Quick Access nebude zobrazovat časté složky.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer", "ShowFrequent", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.trackapps", "ui", "Start", "Vypnout sledování aplikací", "Start nebude řadit programy podle používání.", RegistryHive.CurrentUser, adv, "Start_TrackProgs", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.trackdocs", "ui", "Start", "Vypnout sledování dokumentů", "Omezí historii naposledy otevřených dokumentů.", RegistryHive.CurrentUser, adv, "Start_TrackDocs", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("ui.startrecommend", "ui", "Start", "Omezit doporučení ve Start", "Vypne hlavní přepínač doporučeného obsahu na podporovaných buildech Windows 11.", RegistryHive.CurrentUser, adv, "Start_IrisRecommendations", 0, 1, false, true),
            TweakFactory.Dword("ui.startaccount", "ui", "Start", "Skrýt upozornění účtu ve Start", "Omezí account notification badges ve Start menu.", RegistryHive.CurrentUser, adv, "Start_AccountNotifications", 0, 1, false, true),
            TweakFactory.Dword("ui.endtask", "ui", "Taskbar", "End Task v pravém kliku", "Přidá možnost ukončit aplikaci přímo z kontextové nabídky taskbaru.", RegistryHive.CurrentUser, adv + "\\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null, true, false, TweakRisk.Optional, false, "Nízký", null, 22631, "Any", false),
            TweakFactory.Dword("ui.copydetails", "ui", "Explorer", "Detailní dialog kopírování", "File transfer dialog se standardně otevře v detailním režimu.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\OperationStatusManager", "EnthusiastMode", 1, 0, false, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false),
            TweakFactory.Dword("ui.mergeconflicts", "ui", "Explorer", "Zobrazovat konflikty slučování složek", "Explorer upozorní při slučování složek se stejným názvem.", RegistryHive.CurrentUser, adv, "HideMergeConflicts", 0, 1, false, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false),
            TweakFactory.Dword("ui.persistfolders", "ui", "Explorer", "Obnovit Explorer okna po přihlášení", "Znovu otevře předchozí okna Průzkumníka po přihlášení.", RegistryHive.CurrentUser, adv, "PersistBrowsers", 1, 0, false, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false),
            TweakFactory.Dword("ui.mostused", "ui", "Start", "Skrýt nejpoužívanější aplikace", "Skryje seznam nejpoužívanějších aplikací ve Start.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Start", "ShowFrequentList", 0, null, true, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false),
            TweakFactory.Dword("ui.autoplay", "ui", "Explorer", "AutoPlay vypnutý", "Zakáže automatické nabídky po připojení médií a zařízení.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\AutoplayHandlers", "DisableAutoplay", 1, 0, false, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false),
            TweakFactory.Dword("ui.lockscreen", "ui", "Přihlášení", "Přeskočit Lock Screen", "Přejde rovnou na přihlašovací obrazovku.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\Personalization", "NoLockScreen", 1, null, true, false, TweakRisk.Optional, false),
            StringValueTweak("ui.stickyshift", "ui", "Input", "Vypnout Sticky Keys zkratku", "Zakáže aktivaci Sticky Keys pěti stisky Shift.", RegistryHive.CurrentUser, "Control Panel\\Accessibility\\StickyKeys", "Flags", "506", "510"),
            StringValueTweak("ui.numlock", "ui", "Input", "NumLock při startu", "Zapne NumLock pro přihlašovací obrazovku/default user.", RegistryHive.Users, ".DEFAULT\\Control Panel\\Keyboard", "InitialKeyboardIndicators", "2147483650", "2147483648"),
            StringValueTweak("ui.settingshome", "ui", "Settings", "Skrýt domovskou stránku Nastavení", "Skryje Home v aplikaci Nastavení bez odstranění ostatních stránek.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer", "SettingsPageVisibility", "hide:home", "show:home"),
            ExplorerFolderDiscoveryTweak(),
            TweakFactory.Dword("ui.bsoddetails", "ui", "Diagnostika", "Detailní BSOD parametry", "Zobrazí více technických informací při BSOD.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\CrashControl", "DisplayParameters", 1, 0, false, false, TweakRisk.Optional, true)
        };
        list.AddRange(GTweakPortCatalog.UiTweaks());
        list.AddRange(WinhancePortCatalog.UiTweaks());
        return list;
    }

    private static TweakDefinition ExplorerFolderDiscoveryTweak()
    {
        const string path = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell";
        const string backupId = "ui.folderdiscovery";
        return TweakFactory.Command(
            backupId, "ui", "Explorer",
            "Vypnout automatické rozpoznávání typu složek",
            "Nastaví obecný typ složek bez mazání vlastních pohledů Exploreru.",
            ct =>
            {
                // TweakEngine runs DetectAsync on a thread-pool slot; the inner
                // Task.Run hop only added a second scheduler dispatch.
                ct.ThrowIfCancellationRequested();
                using var key = Registry.CurrentUser.OpenSubKey(path);
                return Task.FromResult(key?.GetValue("FolderType") is string value &&
                    string.Equals(value, "NotSpecified", StringComparison.OrdinalIgnoreCase));
            },
            async (on, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (on)
                {
                    // Old implementation irreversibly erased Bags and BagMRU.
                    // Preserve the original REG_SZ type and its absence instead.
                    await TweakFactory.Backups.SaveOriginalAsync(
                        backupId, RegistryHive.CurrentUser, path, "FolderType", ct)
                        .ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
                    key.SetValue("FolderType", "NotSpecified", RegistryValueKind.String);
                }
                else if (!await TweakFactory.Backups.RestoreAsync(backupId, ct)
                    .ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "Chybí záloha původního typu složek; nebudu mazat nastavení Exploreru.");
                }
            },
            false, TweakRisk.Optional, true, "Nízký",
            "Původní individuální pohledy složek zůstávají zachované. Změna se může projevit až po novém otevření okna Exploreru.");
    }

    private static List<TweakDefinition> UpdateTweaks()
    {
        return new List<TweakDefinition>
        {
            TweakFactory.Dword("wu.drivers", "update", "Drivers", "Neinstalovat drivery přes Windows Update", "Volitelné pokud používáš vlastní GPU/NIC drivery.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, null, true, false, TweakRisk.Advanced, true),
            TweakFactory.Dword("wu.delivery", "update", "Delivery Optimization", "Delivery Optimization P2P vypnuté", "Nepeeruje update data do internetu/LAN.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization", "DODownloadMode", 0, null, true, true),
            TweakFactory.Dword("wu.restarts", "update", "Restart", "No auto restart while users logged on", "Windows Update AU policy.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU", "NoAutoRebootWithLoggedOnUsers", 1, null, true, true),
            TweakFactory.Dword("wu.otherproducts", "update", "Microsoft Update", "Aktualizace dalších Microsoft produktů", "Zapne Microsoft Update pro Office a další podporované produkty.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings", "AllowMUUpdateService", 1, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("wu.restartnotify", "update", "Restart", "Zobrazovat restart notifikace", "Upozorní, když Windows Update vyžaduje restart.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings", "RestartNotificationsAllowed2", 1, 0, false, true),
            TweakFactory.Dword("wu.noexpeditedrestart", "update", "Restart", "Nerestartovat co nejdřív po update", "IsExpedited = 0; Windows nebude tlačit na co nejrychlejší restart po dokončení aktualizace.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings", "IsExpedited", 0, 1, false, true),
            TweakFactory.Dword("wu.latestfeatures", "update", "Feature Updates", "Nové funkce až standardním tempem", "Vypne opt-in na nejnovější funkce hned po vydání; bezpečnostní aktualizace zůstávají.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings", "IsContinuousInnovationOptedIn", 0, 1, false, false, TweakRisk.Optional)
        }.Concat(GTweakPortCatalog.UpdateTweaks()).ToList();
    }

    private static List<TweakDefinition> PrivacyTweaks()
    {
        int num = 14;
        List<TweakDefinition> list = new List<TweakDefinition>(num);
        CollectionsMarshal.SetCount(list, num);
        Span<TweakDefinition> span = CollectionsMarshal.AsSpan(list);
        span[0] = GlassPrivacyTelemetry;
        span[1] = GlassPrivacyAdvertisingId;
        span[2] = GlassPrivacyTailored;
        span[3] = GlassPrivacyActivity;
        span[4] = TweakFactory.Dword("privacy.feedback", "privacy", "Telemetry", "Feedback notifications vypnuté", "DoNotShowFeedbackNotifications.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "DoNotShowFeedbackNotifications", 1, null, deleteOnDisable: true, recommended: true);
        span[5] = TweakFactory.Dword("privacy.consumer", "privacy", "Privacy", "Microsoft Consumer Experiences vypnuté", "CloudContent policy.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsConsumerFeatures", 1, null, deleteOnDisable: true, recommended: true);
        span[6] = TweakFactory.Dword("privacy.clipboard", "privacy", "Search / Cloud", "Cloud Clipboard sync vypnutý", "AllowCrossDeviceClipboard.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "AllowCrossDeviceClipboard", 0, null, deleteOnDisable: true, recommended: true);
        span[7] = TweakFactory.Dword("privacy.websearch", "privacy", "Search / Cloud", "Web search ve Start vypnutý", "DisableSearchBoxSuggestions.", RegistryHive.CurrentUser, "Software\\Policies\\Microsoft\\Windows\\Explorer", "DisableSearchBoxSuggestions", 1, null, deleteOnDisable: true, recommended: true);
        span[8] = TweakFactory.Dword("privacy.searchhl", "privacy", "Search / Cloud", "Search Highlights vypnuté", "Dynamic search highlights.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\SearchSettings", "IsDynamicSearchBoxEnabled", 0, 1, deleteOnDisable: false, recommended: true);
        span[9] = TweakFactory.Dword("sec.llmnr", "privacy", "Network Security", "LLMNR vypnuté", "Multicast name resolution policy.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows NT\\DNSClient", "EnableMulticast", 0, null, deleteOnDisable: true, recommended: true, TweakRisk.Safe, restart: true);
        span[10] = TweakFactory.Dword("sec.guest", "privacy", "Network Security", "SMB insecure guest logons vypnuté", "AllowInsecureGuestAuth = 0.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\LanmanWorkstation\\Parameters", "AllowInsecureGuestAuth", 0, null, deleteOnDisable: true, recommended: true, TweakRisk.Safe, restart: true);
        span[11] = FeatureOff("sec.smb1", "privacy", "Network Security", "SMB1 vypnuté", "SMB1Protocol", recommended: true);
        span[12] = TweakFactory.Dword("sec.vulndrv", "privacy", "Kernel Security", "Vulnerable Driver Blocklist zapnutý", "Doporučeno ponechat zapnuté; ručně lze vypnout.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\CI\\Config", "VulnerableDriverBlocklistEnable", 1, 0, deleteOnDisable: false, recommended: true, TweakRisk.Optional, restart: true, "Nízký", "Vypnutí snižuje ochranu před známými zranitelnými kernel drivery.");
        span[13] = TweakFactory.Dword("sec.lsa", "privacy", "Kernel Security", "LSA Protection zapnutá", "RunAsPPL ochrana LSASS.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Lsa", "RunAsPPL", 1, null, deleteOnDisable: true, recommended: true, TweakRisk.Safe, restart: true);
        list.AddRange(new[]
        {
            TweakFactory.Dword("privacy.activityfeed", "privacy", "Activity History", "Activity Feed vypnutý", "Zakáže activity feed policy.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "EnableActivityFeed", 0, null, true, true),
            TweakFactory.Dword("privacy.activitypublish", "privacy", "Activity History", "Publikování aktivit vypnuté", "Aplikace nepublikují aktivitu do timeline.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "PublishUserActivities", 0, null, true, true),
            TweakFactory.Dword("privacy.tips", "privacy", "Doporučený obsah", "Windows tipy vypnuté", "Omezí tipy a doporučení.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SoftLandingEnabled", 0, 1, false, true),
            TweakFactory.Dword("privacy.tips2", "privacy", "Doporučený obsah", "Suggested content vypnutý", "Vypne jednu z hlavních sad doporučeného obsahu.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0, 1, false, true),
            TweakFactory.Dword("privacy.tips3", "privacy", "Doporučený obsah", "Welcome suggestions vypnuté", "Omezí promo po aktualizaci Windows.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SubscribedContent-310093Enabled", 0, 1, false, true),
            TweakFactory.Dword("privacy.explorerpromo", "privacy", "Doporučený obsah", "Explorer promo vypnuté", "Skryje sync provider promo notifikace.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "ShowSyncProviderNotifications", 0, 1, false, true),
            TweakFactory.Dword("privacy.tailoredpolicy", "privacy", "Privacy", "Tailored Experiences policy vypnutá", "Systémová policy doplňuje uživatelské nastavení.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1, null, true, true)
        });
        list.AddRange(new[]
        {
            TweakFactory.Dword("sec.uac", "privacy", "Windows Security", "UAC zapnuté", "Ponechá User Account Control aktivní.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System", "EnableLUA", 1, 0, false, true, TweakRisk.Safe, true),
            TweakFactory.Dword("sec.securedesktop", "privacy", "Windows Security", "UAC na Secure Desktop", "UAC výzva se zobrazí na zabezpečené ploše.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System", "PromptOnSecureDesktop", 1, 0, false, true),
            TweakFactory.Dword("sec.wdigest", "privacy", "Credential Security", "WDigest plaintext credentials vypnuté", "UseLogonCredential = 0 omezuje ukládání přihlašovacích údajů v paměti.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\SecurityProviders\\WDigest", "UseLogonCredential", 0, 1, false, true),
            TweakFactory.Dword("sec.remoteassist", "privacy", "Remote Access", "Remote Assistance vypnutá", "Zakáže příchozí Windows Remote Assistance. RDP je samostatná funkce.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Remote Assistance", "fAllowToGetHelp", 0, 1, false, true),
            TweakFactory.Dword("sec.alwaysinstallelevated", "privacy", "Installer Security", "AlwaysInstallElevated vypnuté", "Zabrání nebezpečnému spuštění MSI balíčků vždy s vysokými právy.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\Installer", "AlwaysInstallElevated", 0, 1, false, true),
            TweakFactory.Dword("sec.autorun", "privacy", "Device Security", "Autorun na všech discích vypnutý", "Snižuje riziko automatického spuštění obsahu z vyměnitelných médií.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer", "NoDriveTypeAutoRun", 255, 145, false, true),
            TweakFactory.Dword("sec.restrictsam", "privacy", "Network Security", "Anonymní výčet SAM zakázaný", "Omezuje anonymní enumeraci lokálních účtů.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Lsa", "RestrictAnonymousSAM", 1, 0, false, true),
            TweakFactory.Dword("sec.restrictanon", "privacy", "Network Security", "Anonymní přístup omezený", "Omezuje anonymní enumeraci účtů a sdílení.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Lsa", "RestrictAnonymous", 1, 0, false, true),
            TweakFactory.Dword("sec.ntlmv2", "privacy", "Network Security", "NTLMv2 only", "Advanced: odmítne LM/NTLM a může rozbít velmi stará NAS/doménová zařízení.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Lsa", "LmCompatibilityLevel", 5, null, true, false, TweakRisk.Advanced, true, "Střední", "Může rozbít přihlášení ke starým SMB/NAS zařízením."),
            TweakFactory.Dword("sec.smbsignclient", "privacy", "Network Security", "Vyžadovat SMB signing — klient", "Advanced: zvyšuje integritu SMB, může ovlivnit starší NAS nebo výkon.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\LanmanWorkstation\\Parameters", "RequireSecuritySignature", 1, 0, false, false, TweakRisk.Advanced, true),
            TweakFactory.Dword("sec.smbsignserver", "privacy", "Network Security", "Vyžadovat SMB signing — server", "Advanced: vyžaduje podpis SMB pro příchozí sdílení.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\LanmanServer\\Parameters", "RequireSecuritySignature", 1, 0, false, false, TweakRisk.Advanced, true)
        });
        list.AddRange(new[]
        {
            TweakFactory.Dword("privacy.clipboardhistory", "privacy", "Clipboard", "Historie schránky vypnutá", "Vypne lokální historii Win+V; cloud sync je samostatný tweak.", RegistryHive.CurrentUser, "Software\\Microsoft\\Clipboard", "EnableClipboardHistory", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.onlinespeech", "privacy", "Speech / Input", "Online Speech Recognition vypnuté", "Omezí online rozpoznávání řeči pro aktuálního uživatele.", RegistryHive.CurrentUser, "Software\\Microsoft\\Speech_OneCore\\Settings\\OnlineSpeechPrivacy", "HasAccepted", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.implicittext", "privacy", "Speech / Input", "Implicitní sběr psaní vypnutý", "Omezí personalizaci psaní a implicitní text collection.", RegistryHive.CurrentUser, "Software\\Microsoft\\InputPersonalization", "RestrictImplicitTextCollection", 1, 0, false, true),
            TweakFactory.Dword("privacy.implicitink", "privacy", "Speech / Input", "Implicitní sběr rukopisu vypnutý", "Omezí personalizaci handwriting/ink dat.", RegistryHive.CurrentUser, "Software\\Microsoft\\InputPersonalization", "RestrictImplicitInkCollection", 1, 0, false, true),
            TweakFactory.Dword("privacy.location", "privacy", "Location", "Location Services vypnuté", "Volitelné globální vypnutí polohy; může ovlivnit mapy, počasí a automatické časové pásmo.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\LocationAndSensors", "DisableLocation", 1, 0, false, false, TweakRisk.Optional, true),
            TweakFactory.Dword("privacy.finddevice", "privacy", "Location", "Find My Device vypnuté", "Volitelné vypnutí funkce Find My Device.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\FindMyDevice", "AllowFindMyDevice", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.camera", "privacy", "App Permissions", "Přístup aplikací ke kameře zakázaný", "Force Deny pro Store/UWP aplikace; desktop aplikace mohou mít jiné oprávnění.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessCamera", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.microphone", "privacy", "App Permissions", "Přístup aplikací k mikrofonu zakázaný", "Force Deny pro Store/UWP aplikace; může rozbít voice chat v některých aplikacích.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessMicrophone", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.notifications", "privacy", "App Permissions", "Přístup aplikací k oznámením zakázaný", "Force Deny pro aplikace využívající notification access.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessNotifications", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.accountinfo", "privacy", "App Permissions", "Přístup aplikací k informacím účtu zakázaný", "Force Deny pro account-info permission.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessAccountInfo", 2, null, true, false, TweakRisk.Optional),
        });
        list.AddRange(new[]
        {
            TweakFactory.Dword("privacy.contacts", "privacy", "App Permissions", "Přístup aplikací ke kontaktům zakázaný", "Force Deny pro kontakty.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessContacts", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.calendar", "privacy", "App Permissions", "Přístup aplikací ke kalendáři zakázaný", "Force Deny pro kalendář.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessCalendar", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.callhistory", "privacy", "App Permissions", "Přístup aplikací k historii volání zakázaný", "Force Deny pro historii volání.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessCallHistory", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.documents", "privacy", "App Permissions", "Přístup aplikací k dokumentům zakázaný", "Force Deny pro Documents library.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessDocumentsLibrary", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.pictures", "privacy", "App Permissions", "Přístup aplikací k obrázkům zakázaný", "Force Deny pro Pictures library.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessPicturesLibrary", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.videos", "privacy", "App Permissions", "Přístup aplikací k videím zakázaný", "Force Deny pro Videos library.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessVideosLibrary", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.filesystem", "privacy", "App Permissions", "Přístup aplikací k souborovému systému zakázaný", "Force Deny pro broad file-system permission.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessFileSystem", 2, null, true, false, TweakRisk.Optional)
        });
        list.AddRange(new[]
        {
            TweakFactory.Dword("sec.nolmhash", "privacy", "Credential Security", "LM hash ukládání zakázané", "NoLMHash = 1; bezpečnější ukládání lokálních hesel.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Lsa", "NoLMHash", 1, 0, false, true),
            TweakFactory.Dword("sec.blankpassword", "privacy", "Credential Security", "Prázdná hesla jen lokálně", "LimitBlankPasswordUse blokuje vzdálené přihlášení účtů bez hesla.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Lsa", "LimitBlankPasswordUse", 1, 0, false, true),
            TweakFactory.Dword("sec.everyoneanon", "privacy", "Network Security", "Anonymous není součást Everyone", "EveryoneIncludesAnonymous = 0.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Lsa", "EveryoneIncludesAnonymous", 0, 1, false, true),
            TweakFactory.Dword("sec.namerelease", "privacy", "Network Security", "NetBIOS name release protection", "Brání vzdálenému vynucení uvolnění NetBIOS jména.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\NetBT\\Parameters", "NoNameReleaseOnDemand", 1, 0, false, true),
            TweakFactory.Dword("sec.icmpredirect", "privacy", "Network Security", "ICMP Redirect vypnutý", "Omezí přijímání ICMP redirect zpráv.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters", "EnableICMPRedirect", 0, 1, false, true),
            TweakFactory.Dword("sec.sourceroute4", "privacy", "Network Security", "IPv4 source routing zakázaný", "DisableIPSourceRouting = 2.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters", "DisableIPSourceRouting", 2, 0, false, true),
            TweakFactory.Dword("sec.sourceroute6", "privacy", "Network Security", "IPv6 source routing zakázaný", "DisableIPSourceRouting = 2 pro TCPIP6.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters", "DisableIPSourceRouting", 2, 0, false, true),
            TweakFactory.Dword("sec.safedll", "privacy", "Process Security", "Safe DLL Search Mode zapnutý", "Upřednostní bezpečnější pořadí hledání DLL.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Session Manager", "SafeDllSearchMode", 1, 0, false, true),
        });
        list.AddRange(new[]
        {
            TweakFactory.Dword("privacy.email", "privacy", "App Permissions", "Přístup aplikací k e-mailu zakázaný", "Volitelné globální omezení Store aplikací.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessEmail", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.messaging", "privacy", "App Permissions", "Přístup aplikací ke zprávám zakázaný", "Volitelné globální omezení messaging API.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessMessaging", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.motion", "privacy", "App Permissions", "Přístup aplikací k pohybu zakázaný", "Volitelné omezení motion senzorů.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessMotion", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.radios", "privacy", "App Permissions", "Ovládání rádií aplikacemi zakázané", "Volitelné omezení Wi-Fi/Bluetooth radio API.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessRadios", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.tasks", "privacy", "App Permissions", "Přístup aplikací k Tasks zakázaný", "Volitelné omezení úloh a připomínek.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessTasks", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.otherdevices", "privacy", "App Permissions", "Komunikace s nepárovanými zařízeními zakázaná", "Volitelné omezení komunikace aplikací s dalšími zařízeními.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessTrustedDevices", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.syncdevices", "privacy", "App Permissions", "Synchronizace aplikací mezi zařízeními zakázaná", "Volitelné omezení cross-device synchronizace.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsSyncWithDevices", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.phone", "privacy", "App Permissions", "Přístup aplikací k telefonu zakázaný", "Volitelné omezení phone API.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessPhone", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.downloads", "privacy", "App Permissions", "Přístup aplikací ke staženým souborům zakázaný", "Volitelné globální omezení přístupu Store aplikací ke složce Downloads.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessDownloadsFolder", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.appdiagnostics", "privacy", "App Permissions", "Přístup aplikací k diagnostice zakázaný", "Volitelné omezení přístupu aplikací k diagnostickým údajům jiných aplikací.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsGetDiagnosticInfo", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.foregroundtext", "privacy", "App Permissions", "Přístup k textu v popředí zakázaný", "Volitelné omezení čtení textu z aktivního okna Store aplikacemi.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessForegroundText", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.humanpresence", "privacy", "App Permissions", "Detekce přítomnosti uživatele zakázaná", "Volitelné omezení přístupu aplikací k detekci lidské přítomnosti.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessHumanPresence", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.spatial", "privacy", "App Permissions", "Prostorové vnímání na pozadí zakázané", "Volitelné omezení pohybu hlavy a rukou aplikacemi na pozadí; podporuje pouze HoloLens 2.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessBackgroundSpatialPerception", 2, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword("privacy.gaze", "privacy", "App Permissions", "Sledování pohledu zakázané", "Volitelné omezení přístupu aplikací k eye-trackerům.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessGazeInput", 2, null, true, false, TweakRisk.Optional),
        });
        list.AddRange(new[]
        {
            TweakFactory.Dword("privacy.languageweb", "privacy", "Privacy", "Weby bez přístupu k seznamu jazyků", "Omezí používání seznamu jazyků pro lokálně relevantní webový obsah.", RegistryHive.CurrentUser, "Control Panel\\International\\User Profile", "HttpAcceptLanguageOptOut", 1, null, true, true),
            TweakFactory.Dword("privacy.scoobe", "privacy", "Doporučený obsah", "Vypnout Finish setting up Windows", "Zakáže SCOOBE návrhy na dokončení nastavení zařízení.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1, false, true),
            SettingsSuggestedContentTweak(),
            TweakFactory.Dword("privacy.spotlightall", "privacy", "Windows Spotlight", "Windows Spotlight vypnutý", "Zakáže většinu Spotlight obsahu přes systémovou policy.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", 1, null, true, true),
            TweakFactory.Dword("privacy.thirdparty", "privacy", "Windows Spotlight", "Third-party suggestions vypnuté", "Zakáže návrhy aplikací třetích stran.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableThirdPartySuggestions", 1, null, true, true),
            TweakFactory.Dword("privacy.spotlightac", "privacy", "Windows Spotlight", "Spotlight v Action Center vypnutý", "Omezí promo obsah v oznámeních.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightOnActionCenter", 1, null, true, true),
            TweakFactory.Dword("privacy.spotlightsettings", "privacy", "Windows Spotlight", "Spotlight v Nastavení vypnutý", "Omezí doporučený obsah v Settings.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightOnSettings", 1, null, true, true),
            TweakFactory.Dword("privacy.spotlightstart", "privacy", "Windows Spotlight", "Spotlight ve Start vypnutý", "Omezí doporučený obsah ve Start menu.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightOnStart", 1, null, true, true),
            TweakFactory.Dword("privacy.welcome", "privacy", "Windows Spotlight", "Windows Welcome Experience vypnutý", "Zakáže promo obrazovky po aktualizacích a přihlášení.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightWindowsWelcomeExperience", 1, null, true, true),
            TweakFactory.Dword("privacy.cloudsearch", "privacy", "Search / Cloud", "Cloud Search vypnutý", "Zakáže cloudové výsledky ve Windows Search.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "AllowCloudSearch", 0, null, true, true),
            TweakFactory.Dword("privacy.telemetrydevicename", "privacy", "Telemetry", "Název zařízení v telemetrii vypnutý", "Zakáže odesílání názvu zařízení v diagnostických datech.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "AllowDeviceNameInTelemetry", 0, null, true, true),
            TweakFactory.Dword("privacy.diaglogs", "privacy", "Telemetry", "Diagnostické logy omezené", "Omezí kolekci diagnostických logů.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "LimitDiagnosticLogCollection", 1, null, true, true),
            TweakFactory.Dword("privacy.dumps", "privacy", "Telemetry", "Crash dump collection omezená", "Omezí sběr diagnostických dumpů Microsoftem.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "LimitDumpCollection", 1, null, true, true),
        });
        list.AddRange(GTweakPortCatalog.PrivacyTweaks());
        return list.Where(x => !x.Id.StartsWith("sec.", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static TweakDefinition SettingsSuggestedContentTweak()
    {
        const string path = "Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager";
        string[] names = { "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled" };
        return TweakFactory.Command("privacy.settingssuggestions", "privacy", "Doporučený obsah", "Doporučený obsah v Nastavení vypnutý", "Vypne všechny tři Suggested Content přepínače používané aplikací Nastavení.",
            ct =>
            {
                // TweakEngine runs DetectAsync on a thread-pool slot; the inner
                // Task.Run hop only added a second scheduler dispatch, and the
                // LINQ All() enumerator was a per-render allocation.
                ct.ThrowIfCancellationRequested();
                using var k = Registry.CurrentUser.OpenSubKey(path);
                if (k == null) return Task.FromResult(false);
                for (int i = 0; i < names.Length; i++)
                    if (Convert.ToInt32(k.GetValue(names[i]) ?? 1) != 0) return Task.FromResult(false);
                return Task.FromResult(true);
            },
            async (on, ct) =>
            {
                using var k = Registry.CurrentUser.CreateSubKey(path, true);
                foreach (var n in names) k.SetValue(n, on ? 0 : 1, RegistryValueKind.DWord);
            },
            true, TweakRisk.Safe, false, "Nízký", null);
    }
    private static List<TweakDefinition> AiTweaks()
    {
        return new List<TweakDefinition>
        {
            TweakFactory.Dword("ai.recall", "ai", "Recall", "Zakázat Recall snapshots", "WindowsAI policy zakáže ukládání Recall snímků.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis", 1, null, true, true, TweakRisk.Safe, true),
            TweakFactory.Dword("ai.recall2", "ai", "Recall", "Zakázat Recall", "Druhá policy používaná novějšími buildy Windows.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableRecall", 1, null, true, true, TweakRisk.Safe, true),
            TweakFactory.Dword("ai.copilot", "ai", "Copilot", "Zakázat Windows Copilot", "Policy vypne systémový Copilot.", RegistryHive.CurrentUser, "Software\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", 1, null, true, false, TweakRisk.Optional, true),
            TweakFactory.Dword("ai.copilotbutton", "ai", "Copilot", "Skrýt tlačítko Copilot", "Skryje tlačítko v taskbaru.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "ShowCopilotButton", 0, 1, false, false, TweakRisk.Optional)
        }.Concat(GTweakPortCatalog.AiTweaks()).ToList();
    }




    public static List<TweakDefinition> GetDefenderTweaks()
    {
        int num = 12;
        List<TweakDefinition> list = new List<TweakDefinition>(num);
        CollectionsMarshal.SetCount(list, num);
        Span<TweakDefinition> span = CollectionsMarshal.AsSpan(list);
        span[0] = TweakFactory.Dword("def.rt", "defender", "Antivirus", "Real-time Protection vypnutá", "Advanced — Tamper Protection může změnu blokovat.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableRealtimeMonitoring", 1, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced, restart: true, "Střední", "Snižuje ochranu proti malwaru.");
        span[1] = TweakFactory.Dword("def.beh", "defender", "Antivirus", "Behavior Monitoring vypnutý", "Advanced.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableBehaviorMonitoring", 1, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced, restart: true, "Střední", "Snižuje ochranu proti malwaru.");
        span[2] = TweakFactory.Dword("def.ioav", "defender", "Antivirus", "IOAV / download scan vypnutý", "Advanced.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableIOAVProtection", 1, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced, restart: true);
        span[3] = TweakFactory.Dword("def.script", "defender", "Antivirus", "Script scanning vypnutý", "Advanced.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableScriptScanning", 1, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced, restart: true);
        span[4] = TweakFactory.Dword("def.cloud", "defender", "Cloud", "Cloud protection vypnutá", "SpynetReporting = 0.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Spynet", "SpynetReporting", 0, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced, restart: true);
        span[5] = TweakFactory.Dword("def.samples", "defender", "Cloud", "Automatic sample submission vypnuté", "SubmitSamplesConsent = 2.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Spynet", "SubmitSamplesConsent", 2, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced);
        span[6] = TweakFactory.Dword("def.pua", "defender", "Exploit Guard", "PUA Protection vypnutá", "Advanced.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender", "PUAProtection", 0, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced);
        span[7] = TweakFactory.Dword("def.net", "defender", "Exploit Guard", "Network Protection vypnutá", "Advanced.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Windows Defender Exploit Guard\\Network Protection", "EnableNetworkProtection", 0, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced);
        span[8] = TweakFactory.Dword("def.cfa", "defender", "Exploit Guard", "Controlled Folder Access vypnutý", "Advanced.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Windows Defender Exploit Guard\\Controlled Folder Access", "EnableControlledFolderAccess", 0, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced);
        span[9] = TweakFactory.Dword("def.disable", "defender", "Antivirus", "Defender Antivirus policy vypnutý", "Moderní Windows může politiku ignorovat při Tamper Protection.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows Defender", "DisableAntiSpyware", 1, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced, restart: true);
        span[10] = TweakFactory.Dword("def.smartscreen", "defender", "SmartScreen", "SmartScreen vypnutý", "Advanced — snižuje ochranu stažených souborů.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "EnableSmartScreen", 0, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced, restart: false, "Střední", "Snižuje reputační ochranu.");
        span[11] = TweakFactory.Command("def.firewall", "defender", "Firewall", "Defender Firewall vypnutý", "Samostatný přepínač; není součástí kompletního vypnutí Defender AV.", async (CancellationToken ct) => await Task.Run(FirewallStateService.AreAllActiveProfilesDisabledForUi, ct).ConfigureAwait(false), async delegate (bool on, CancellationToken ct)
        {
            ProcessResult processResult = await ProcessRunner.RunAsync("netsh.exe", "advfirewall set allprofiles state " + (on ? "off" : "on"), TimeSpan.FromSeconds(20L), ct).ConfigureAwait(false);
            if (!processResult.Success)
            {
                throw new InvalidOperationException(processResult.StdErr);
            }
            FirewallStateService.Invalidate();
        }, recommended: false, TweakRisk.Advanced, restart: false, "Vysoký", "Vypnutí firewallu výrazně snižuje síťovou ochranu.");
        return list;
    }
}
