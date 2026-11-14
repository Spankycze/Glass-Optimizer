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
    public static readonly IReadOnlyList<SectionDefinition> Sections = new List<SectionDefinition>
    {
        new("overview", "Přehled", "Stav systému a rychlé profily", "\ue80f"),
        new("power", "Power Plan", "Výkonový plán a pokročilé napájení", "\ue945"),
        new("gpu", "GPU / Display", "Grafika, DWM a zobrazování", "\ue7f8"),
        new("network", "Internet / Síť", "TCP/IP, NIC a síťová latence", "\ue774"),
        new("storage", "Úložiště / NVMe", "SSD/NVMe, TRIM a údržba", "\ue7c3"),
        new("system", "Systémové tweaky", "Windows, boot a virtualizace", "\ue770"),
        new("usb", "USB / HID", "USB zařízení, napájení a čištění", "\uecf0"),
        new("lianli", "Ventilátory", "Lian Li SL-Infinity • RPM • teplota CPU • RGB bez L-Connect", "\ue9ca"),
        new("startup", "Po spuštění", "Programy spouštěné s Windows", "\ue7b8"),
        new("themes", "Personalizace", "Glass Windows, tapety, kurzory a PowerShell", "\ue771"),
        new("ui", "UI / Explorer", "Průzkumník, taskbar a vzhled Windows", "\ue771"),
        new("update", "Windows Update", "Aktualizace a Delivery Optimization", "\ue895"),
        new("spicetify", "Spotify / Spicetify", "Themes, extensions, Marketplace a správa Spotify klienta", "\ue93f"),
        new("apps", "Programy / Launchery", "Launchery, cache a aplikační optimalizace", "\ue71d"),
        new("privacy", "Soukromí", "Telemetry, oprávnění aplikací a doporučený obsah", "\ue72e"),
        new("ai", "AI Debloater", "Copilot, Recall a AI komponenty Windows", "\ue99a"),
        new("ai-runtime", "AI System Runtime", "Lokální AI řízení výkonu, ventilátorů a systémových prostředků", "\ue99a"),
        new("defender", "Defender", "Microsoft Defender na jednom místě", "\uea18"),
        new("debloat", "Debloater", "Volitelné Windows aplikace a komponenty", "\ue74d"),
        new("fixer", "Fixer", "SFC, DISM a opravy Windows", "\ue90f"),
        new("powershell", "PowerShell", "PowerShell 7 a Windows Terminal tweaker", "\ue756"),
        new("activity", "Aktivita", "Procesy a vytížení systému", "\ue9d9"),
        new("restore", "Záloha / Obnova", "Restore Point a návrat změn", "\ue777"),
        new("extensions", "Rozšíření / Nástroje", "Winget, pluginy, Optional Features a upstream zdroje", "\ue74c"),
        new("cursor", "Cursor", "Vlastní systémové kurzory a motivy", "\ue7c9"),
        new("settings", "Nastavení", "Vzhled a chování aplikace", "\ue713")
    };

    private static readonly Dictionary<string, SectionDefinition> SectionIndex = Sections.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<TweakDefinition>> TweakCache = new(StringComparer.OrdinalIgnoreCase);

    // Glass System Tweaks reuses these six definitions across System/UI/Privacy.
    // Keeping them independent from the full UI/Privacy factories avoids constructing ~70
    // unrelated definitions on the first System navigation.
    private static readonly TweakDefinition GlassUiTransparency =
        TweakFactory.Dword("ui.transparency", "ui", "Vzhled", "Vypnout průhlednost", "Méně vizuálních efektů.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "EnableTransparency", 0, 1, false, false, TweakRisk.Optional);
    private static readonly TweakDefinition GlassUiTaskAnimations =
        TweakFactory.Dword("ui.taskanim", "ui", "Vzhled", "Vypnout animace taskbaru", "Omezí animace shellu.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "TaskbarAnimations", 0, 1, false, false, TweakRisk.Optional);
    private static readonly TweakDefinition GlassPrivacyTelemetry =
        TweakFactory.Dword("privacy.telemetry", "privacy", "Telemetry", "Omezit diagnostická data", "AllowTelemetry policy.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "AllowTelemetry", 0, null, deleteOnDisable: true, recommended: true, TweakRisk.Safe, restart: true);
    private static readonly TweakDefinition GlassPrivacyAdvertisingId =
        TweakFactory.Dword("privacy.adid", "privacy", "Privacy", "Advertising ID vypnuté", "Vypne personalizované reklamní ID.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", 0, 1, deleteOnDisable: false, recommended: true);
    private static readonly TweakDefinition GlassPrivacyTailored =
        TweakFactory.Dword("privacy.tailored", "privacy", "Privacy", "Tailored Experiences vypnuté", "Vypne personalizaci z diagnostických dat.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1, deleteOnDisable: false, recommended: true);
    private static readonly TweakDefinition GlassPrivacyActivity =
        TweakFactory.Dword("privacy.activity", "privacy", "Privacy", "Activity History upload vypnutý", "Omezení cloud activity history.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "UploadUserActivities", 0, null, deleteOnDisable: true, recommended: true);

    // Windows 26H2 / modern Windows switches surfaced directly in System Tweaks.
    // They intentionally mirror the UI/Privacy catalog IDs so state/read-back remains consistent.
    private static readonly TweakDefinition Glass26H2EndTask =
        TweakFactory.Dword("ui.endtask", "ui", "Windows 26H2", "End Task v pravém kliku", "Přidá možnost ukončit aplikaci přímo z kontextové nabídky taskbaru.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null, true, false, TweakRisk.Optional, false, "Nízký", null, 22631, "Any", false);
    private static readonly TweakDefinition Glass26H2CopyDetails =
        TweakFactory.Dword("ui.copydetails", "ui", "Windows 26H2", "Detailní dialog kopírování", "File transfer dialog se standardně otevře v detailním režimu.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\OperationStatusManager", "EnthusiastMode", 1, 0, false, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false);
    private static readonly TweakDefinition Glass26H2MergeConflicts =
        TweakFactory.Dword("ui.mergeconflicts", "ui", "Windows 26H2", "Zobrazovat konflikty slučování složek", "Explorer upozorní při slučování složek se stejným názvem.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "HideMergeConflicts", 0, 1, false, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false);
    private static readonly TweakDefinition Glass26H2PersistFolders =
        TweakFactory.Dword("ui.persistfolders", "ui", "Windows 26H2", "Obnovit Explorer okna po přihlášení", "Znovu otevře předchozí okna Průzkumníka po přihlášení.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "PersistBrowsers", 1, 0, false, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false);
    private static readonly TweakDefinition Glass26H2MostUsed =
        TweakFactory.Dword("ui.mostused", "ui", "Windows 26H2", "Skrýt nejpoužívanější aplikace", "Skryje seznam nejpoužívanějších aplikací ve Start.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Start", "ShowFrequentList", 0, null, true, false, TweakRisk.Optional, false, "Nízký", null, 22000, "Any", false);
    private static readonly TweakDefinition Glass26H2LanguageWeb =
        TweakFactory.Dword("privacy.languageweb", "privacy", "Windows 26H2", "Weby bez přístupu k seznamu jazyků", "Omezí používání seznamu jazyků pro lokálně relevantní webový obsah.", RegistryHive.CurrentUser, "Control Panel\\International\\User Profile", "HttpAcceptLanguageOptOut", 1, null, true, true);
    private static readonly TweakDefinition Glass26H2Scoobe =
        TweakFactory.Dword("privacy.scoobe", "privacy", "Windows 26H2", "Vypnout Finish setting up Windows", "Zakáže SCOOBE návrhy na dokončení nastavení zařízení.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1, false, true);

    private static readonly IReadOnlyList<TweakDefinition> GlassSystemSupplementTweaks = new[]
    {
        GlassUiTaskAnimations, GlassUiTransparency, GlassPrivacyTelemetry,
        GlassPrivacyAdvertisingId, GlassPrivacyTailored, GlassPrivacyActivity,
        Glass26H2EndTask, Glass26H2CopyDetails, Glass26H2MergeConflicts,
        Glass26H2PersistFolders, Glass26H2MostUsed, Glass26H2LanguageWeb, Glass26H2Scoobe
    };

    public static IReadOnlyList<TweakDefinition> GetGlassSystemSupplementTweaks() => GlassSystemSupplementTweaks;

    public sealed record TweakCatalogSource(string Section, string? Sub, Func<List<TweakDefinition>> Factory);

    private static readonly TweakCatalogSource[] TweakSources =
    {
        new("system", null, SystemTweaks),
        new("gpu", null, GpuTweaks),
        new("memory", null, MemoryTweaks),
        new("storage", null, StorageTweaks),
        new("network", null, NetworkTweaks),
        new("components", "Služby", ServiceTweaks),
        new("components", "Ovladače", DriverTweaks),
        new("components", "Task Scheduler", TaskTweaks),
        new("ui", null, UiTweaks),
        new("update", null, UpdateTweaks),
        new("privacy", null, PrivacyTweaks),
        new("ai", null, AiTweaks),
        new("defender", null, GetDefenderTweaks)
    };

    private static readonly Dictionary<string, TweakCatalogSource> TweakSourceIndex =
        TweakSources.ToDictionary(
            x => x.Section + "|" + (x.Sub ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TweakCatalogSource> EnumerateTweakSources() => TweakSources;

    public static SectionDefinition GetSection(string key) =>
        SectionIndex.TryGetValue(key, out var section)
            ? section
            : throw new KeyNotFoundException("Unknown section: " + key);

    public static bool TryGetSection(string key, out SectionDefinition? section) => SectionIndex.TryGetValue(key, out section);

    public static List<TweakDefinition> GetTweaks(string section, string? sub = null)
    {
        string effectiveSub = sub ?? string.Empty;
        if (section.Equals("components", StringComparison.OrdinalIgnoreCase) && effectiveSub.Length == 0)
            effectiveSub = "Služby";

        string cacheKey = section + "|" + effectiveSub;
        return TweakCache.GetOrAdd(cacheKey, key =>
        {
            if (!TweakSourceIndex.TryGetValue(key, out var source))
                return new List<TweakDefinition>();
            return source.Factory();
        });
    }

    private static List<TweakDefinition> SystemTweaks()
    {
        var list = new List<TweakDefinition>
        {
            TweakFactory.Dword("sys.gamemode", "system", "Gaming", "Game Mode", "Windows Game Mode.", RegistryHive.CurrentUser, "Software\\Microsoft\\GameBar", "AutoGameModeEnabled", 1, 0, false, true),
            TweakFactory.Dword("sys.gamemode2", "system", "Gaming", "Game Mode povolený systémem", "Druhá hodnota Game Mode používaná Windows.", RegistryHive.CurrentUser, "Software\\Microsoft\\GameBar", "AllowAutoGameMode", 1, 0, false, true),
            TweakFactory.Command("sys.audioenhancements", "system", "Zvuk", "Vypnout Audio Enhancements",
                "Volitelně vypne systémové a výrobní efekty na zvukových výstupech i mikrofonech. Před změnou ukládá původní hodnoty a při vypnutí přepínače je obnoví.",
                AudioEnhancementsTweakService.IsDisabledAsync,
                AudioEnhancementsTweakService.SetDisabledAsync,
                recommended: false, risk: TweakRisk.Optional, restart: false,
                impact: "Nízký", warning: "Může vypnout prostorový zvuk, korekce sluchátek a potlačení šumu mikrofonu. Obnova funguje pouze pro nastavení změněná Optimizerem.",
                requiresAdmin: true, lazyDetect: true),
            TweakFactory.Dword("sys.gamedvr", "system", "Gaming", "Vypnout Game DVR", "Zakáže nahrávání na pozadí.", RegistryHive.CurrentUser, "System\\GameConfigStore", "GameDVR_Enabled", 0, 1, false, true),
            TweakFactory.Dword("sys.capture", "system", "Gaming", "Vypnout App Capture", "Omezí background capture.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR", "AppCaptureEnabled", 0, 1, false, true),
            TweakFactory.Dword("sys.gamebarquiet", "system", "Gaming", "Tichý Game Bar", "Nevynucuje Game Bar panel při startu.", RegistryHive.CurrentUser, "Software\\Microsoft\\GameBar", "ShowStartupPanel", 0, 1, false, true),
            TweakFactory.Dword("sys.faststartup", "system", "Boot", "Vypnout Fast Startup", "Volitelné pro čistší shutdown a boot.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power", "HiberbootEnabled", 0, 1, false, false, TweakRisk.Optional, true),
            TweakFactory.Dword("sys.silentapps", "system", "Background", "Tiché instalace návrhů vypnuté", "Zabrání Windows automaticky přidávat doporučené aplikace.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0, 1, false, true),
            TweakFactory.Dword("sys.restartapps", "system", "Background", "Restart aplikací po přihlášení vypnutý", "Windows znovu neotevře registrované aplikace po přihlášení.", RegistryHive.CurrentUser, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon", "RestartApps", 0, 1, false, false, TweakRisk.Optional),
            TweakFactory.Dword("sys.backgroundapps", "system", "Background", "UWP aplikace na pozadí zakázané", "Force Deny může omezit oznámení Store aplikací.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsRunInBackground", 2, null, true, false, TweakRisk.Advanced, true, "Střední", "Může omezit oznámení a synchronizaci aplikací."),
            TweakFactory.Dword("sys.vbs", "system", "Virtualizace", "VBS vypnuto", "Virtualization Based Security.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, null, true, false, TweakRisk.Advanced, true, "Střední", "Snižuje ochranu systému."),
            TweakFactory.Dword("sys.hvci", "system", "Virtualizace", "HVCI / Memory Integrity vypnuto", "Hypervisor Enforced Code Integrity.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity", "Enabled", 0, null, true, false, TweakRisk.Advanced, true, "Střední", "Snižuje ochranu kernelu."),
            TweakFactory.Dword("sys.credguard", "system", "Virtualizace", "Credential Guard vypnutý", "Volitelná změna zabezpečení.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Lsa", "LsaCfgFlags", 0, null, true, false, TweakRisk.Advanced, true, "Střední", "Snižuje ochranu přihlašovacích údajů."),
            FeatureOff("sys.hyperv", "system", "Virtualizace", "Hyper-V vypnuté", "Microsoft-Hyper-V-All", false),
            FeatureOff("sys.vmp", "system", "Virtualizace", "Virtual Machine Platform vypnutá", "VirtualMachinePlatform", false, "Vypnutí rozbije WSL2 a některé VM scénáře."),
            FeatureOff("sys.whp", "system", "Virtualizace", "Windows Hypervisor Platform vypnutá", "HypervisorPlatform", false, "Může rozbít emulátory/VM."),
            TweakFactory.Command("sys.hypervisor", "system", "Virtualizace", "Microsoft Hypervisor vypnutý", "bcdedit hypervisorlaunchtype Off.", async delegate(CancellationToken ct)
        {
            string output = await BcdEditStateService.GetCurrentForUiAsync(ct).ConfigureAwait(false);
            return Regex.IsMatch(output, @"(?im)^\s*hypervisorlaunchtype\s+Off\s*$");
        }, async delegate(bool on, CancellationToken ct)
        {
            string arguments = (on ? "/set hypervisorlaunchtype off" : "/deletevalue hypervisorlaunchtype");
            ProcessResult processResult = await ProcessRunner.RunAsync("bcdedit.exe", arguments, TimeSpan.FromSeconds(20L), ct).ConfigureAwait(false);
            if (!processResult.Success)
            {
                throw new InvalidOperationException(processResult.StdErr);
            }
            BcdEditStateService.Invalidate();
        }, recommended: false, TweakRisk.Advanced, restart: true, "Střední", "Vypne virtualizační hypervisor.", lazyDetect: true)
        };
        list.AddRange(new[]
        {
            TweakFactory.Dword("sys.longpaths", "system", "Windows", "Win32 dlouhé cesty", "Povolí moderním aplikacím používat cesty delší než 260 znaků.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\FileSystem", "LongPathsEnabled", 1, 0, false, true),
            TweakFactory.Dword("sys.noautorestartsignin", "system", "Přihlášení", "Nevynucovat automatické přihlášení po restartu", "Zakáže automatické použití přihlašovacích údajů po Windows Update restartu.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System", "DisableAutomaticRestartSignOn", 1, 0, false, true),
            TweakFactory.Dword("sys.backgroundaccess", "system", "Background", "Omezit background aktivitu Store aplikací", "Volitelné globální omezení background aplikací; může omezit jejich notifikace.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0, false, false, TweakRisk.Optional),
            TweakFactory.Dword("sys.verboseboot", "system", "Boot", "Podrobné stavové zprávy při startu", "Zobrazí detailnější informace během přihlášení a vypínání.", RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System", "VerboseStatus", 1, 0, false, false, TweakRisk.Optional)
        });
        list.AddRange(new[]
        {
            TweakFactory.Dword("sys.wpbt", "system", "Boot / Firmware", "Blokovat WPBT execution", "Zakáže spuštění OEM programů injektovaných přes Windows Platform Binary Table.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Session Manager", "DisableWpbtExecution", 1, null, true, true, TweakRisk.Safe, true),
            TweakFactory.Command("sys.hibernation", "system", "Napájení", "Hibernace vypnutá", "Vypne hibernaci včetně hiberfil.sys přes powercfg. Na notebooku ponech jako volitelné.", async ct => { using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Power"); return Convert.ToInt32(k?.GetValue("HibernateEnabled") ?? 1) == 0; }, async (on, ct) => { var r = await ProcessRunner.RunAsync("powercfg.exe", on ? "/hibernate off" : "/hibernate on", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); if (!r.Success) throw new InvalidOperationException(r.StdErr + r.StdOut); }, false, TweakRisk.Optional, true),
            TweakFactory.Dword("sys.largesystemcache", "system", "Scheduler / paměť", "Large System Cache = Windows default", "Ponechá desktopový Windows default místo serverového cache režimu.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management", "LargeSystemCache", 0, 1, false, true),
            TweakFactory.Dword("sys.powerthrottle", "system", "Scheduler / napájení", "Vypnout Power Throttling", "Volitelně zabrání Windows omezovat výkon procesů na pozadí; vhodnější pro desktop než notebook.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling", "PowerThrottlingOff", 1, 0, false, false, TweakRisk.Optional, true, "Střední", "Může zvýšit spotřebu a teploty na notebooku."),
            TweakFactory.Dword("sys.autorebootcrash", "system", "Stabilita", "Automatický restart po BSOD vypnutý", "Po pádu nechá chybovou obrazovku viditelnou pro diagnostiku.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\CrashControl", "AutoReboot", 0, 1, false, false, TweakRisk.Optional),

            TweakFactory.Dword("sys.pca", "system", "Kompatibilita", "Program Compatibility Assistant vypnutý", "Advanced: vypne heuristickou kompatibilitu starších aplikací.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppCompat", "DisablePCA", 1, null, true, false, TweakRisk.Advanced, true, "Střední", "Může zhoršit spouštění starších programů a instalátorů."),
            TweakFactory.Dword("sys.inventory", "system", "Background", "Application Inventory vypnutý", "Omezí sběr inventáře kompatibility aplikací.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppCompat", "DisableInventory", 1, null, true, true),
            TweakFactory.Dword("sys.ait", "system", "Background", "Application Impact Telemetry vypnutá", "Omezí AppCompat telemetrii.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\AppCompat", "AITEnable", 0, null, true, true),
            TweakFactory.Dword("sys.devicecompanion", "system", "Zařízení", "Prevent Device Companion Apps", "Brání Windows stahovat doprovodný software jen kvůli připojenému zařízení.", RegistryHive.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\Device Metadata", "PreventDeviceMetadataFromNetwork", 1, null, true, true),
        });
        list.AddRange(GTweakPortCatalog.SystemTweaks());
        list.AddRange(WinhancePortCatalog.SystemTweaks());
        return list;
    }

    private static TweakDefinition FeatureOff(string id, string section, string group, string name, string feature, bool recommended, string? warning = null)
    {
        return TweakFactory.Command(id, section, group, name, "Windows Feature: " + feature + ".", async (CancellationToken ct) => await WindowsFeatureStateService.IsDisabledAsync(feature, ct).ConfigureAwait(false), async delegate (bool on, CancellationToken ct)
        {
            await WorkerClientService.RunAsync<WorkerAck>("feature-set", WorkerPayloads.Encode(new FeatureRequest(feature, on)), TimeSpan.FromMinutes(7), ct).ConfigureAwait(false);
            WindowsFeatureStateService.Invalidate();
        }, recommended, TweakRisk.Advanced, restart: true, "Střední", warning, lazyDetect: true);
    }

    private static List<TweakDefinition> GpuTweaks()
    {
        var list = new List<TweakDefinition>
        {
            TweakFactory.Dword("gpu.hags", "gpu", "Scheduling", "HAGS zapnutý", "Hardware Accelerated GPU Scheduling. Recommended pro tento performance profil.", RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers", "HwSchMode", 2, 1, false, true, TweakRisk.Safe, true),
            DirectXCompositeTweak("gpu.windowed", "Optimizations for windowed games", "SwapEffectUpgradeEnable", "1", true, "Zapne flip-model optimalizace pro kompatibilní DX10/DX11 hry v borderless/windowed režimu."),
            DirectXCompositeTweak("gpu.vrr", "Variable Refresh Rate optimizations", "VRROptimizeEnable", "1", false, "Globální preference VRR pro podporované scénáře."),
            DirectXCompositeTweak("gpu.autohdr", "Auto HDR", "AutoHDREnable", "1", false, "Auto HDR pro podporované HDR displeje.")
        };
        return list;
    }

    private static TweakDefinition DirectXCompositeTweak(string id, string name, string key, string value, bool recommended, string description)
    {
        return TweakFactory.Command(id, "gpu", "Display / Gaming", name, description,
            ct => DirectXGlobalSettingsService.GetAsync(key, value, ct),
            (on, ct) => DirectXGlobalSettingsService.SetAsync(key, value, on, ct),
            recommended, TweakRisk.Safe, false, "Nízký", null, 22000, "Any", false);
    }

    private static List<TweakDefinition> MemoryTweaks()
    {
        var list = new List<TweakDefinition>();
        list.Add(TweakFactory.Command("mem.compression", "memory", "Paměť", "Memory Compression zapnutá", "Ponechá moderní Windows memory compression aktivní místo zbytečného pagefile tlaku.", async ct => (await WindowsActions.GetMemoryCompressionForUiAsync(ct).ConfigureAwait(false)).Contains("True", StringComparison.OrdinalIgnoreCase), async (on, ct) => await WindowsActions.SetMemoryCompressionAsync(on, ct).ConfigureAwait(false), recommended: true, TweakRisk.Safe));
        return list;
    }

    private static List<TweakDefinition> StorageTweaks()
    {
        return new List<TweakDefinition>
        {
        TweakFactory.Dword("storage.sense", "storage", "Údržba", "Storage Sense zapnutý", "Údržba disku; není součástí Recommended performance profilu.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\StorageSense\\Parameters\\StoragePolicy", "01", 1, 0, false, false),
        TweakFactory.Dword("storage.tempcleanup", "storage", "Údržba", "Automatický úklid temp souborů", "Údržba disku; není součástí Recommended performance profilu.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\StorageSense\\Parameters\\StoragePolicy", "04", 1, 0, false, false),
        TweakFactory.Dword("storage.recycle", "storage", "Údržba", "Koš po 30 dnech", "Storage Sense smaže položky v koši starší 30 dnů.", RegistryHive.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\StorageSense\\Parameters\\StoragePolicy", "08", 1, 0, false, false, TweakRisk.Optional),
        TweakFactory.Command("storage.trim", "storage", "NVMe / SSD", "TRIM zapnutý", "DisableDeleteNotify = 0.", ct => Task.FromResult(NativeSystemProbeService.ReadTrimStateCached()), async delegate(bool on, CancellationToken ct)
        {
            ProcessResult processResult = await ProcessRunner.RunAsync("fsutil.exe", $"behavior set DisableDeleteNotify {((!on) ? 1 : 0)}", TimeSpan.FromSeconds(20L), ct).ConfigureAwait(false);
            if (!processResult.Success)
            {
                throw new InvalidOperationException(processResult.StdErr);
            }
            SystemInsightService.InvalidateStorage(
                preserveStableDriverMetadata: true,
                invalidateTemperatures: false,
                invalidateTrim: true);
        }, recommended: true)
        };
    }

    private static List<TweakDefinition> NetworkTweaks()
    {
        return new List<TweakDefinition>
        {
            NetshGlobal("net.autotune", "Auto-Tuning Normal", "autotuninglevel", "normal", "disabled", true),
            NetshGlobal("net.rssglobal", "RSS zapnuté", "rss", "enabled", "disabled", true)
        }.Concat(GTweakPortCatalog.NetworkTweaks()).Concat(WinhancePortCatalog.NetworkTweaks()).ToList();
    }

    private static TweakDefinition NetshGlobal(string id, string name, string key, string onValue, string offValue, bool recommended)
    {
        return TweakFactory.Command(id, "network", "TCP/IP", name, "Nativní nastavení netsh int tcp.",
            async ct => string.Equals(await TcpGlobalStateService.GetValueForUiAsync(key, ct).ConfigureAwait(false), onValue, StringComparison.OrdinalIgnoreCase),
            async (on, ct) => { var r = await ProcessRunner.RunAsync("netsh.exe", $"int tcp set global {key}={(on ? onValue : offValue)}", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); if (!r.Success) throw new InvalidOperationException(r.StdErr + r.StdOut); TcpGlobalStateService.Invalidate(); }, recommended, TweakRisk.Safe, true);
    }

    private static List<TweakDefinition> ComponentTweaks(string sub)
    {
        return sub switch
        {
            "Služby" => ServiceTweaks(),
            "Ovladače" => DriverTweaks(),
            "Task Scheduler" => TaskTweaks(),
            _ => new List<TweakDefinition>(),
        };
    }

    private static List<TweakDefinition> ServiceTweaks()
    {
        var list = new (string, string, int, bool, string)[]
        {
            ("Fax", "Fax", 4, false, "Fax je na gaming PC obvykle nepotřebný; ruční volba, ne performance Recommended."),
            ("MapsBroker", "Downloaded Maps Manager", 4, false, "Offline mapy; ruční volba, ne performance Recommended."),
            ("RetailDemo", "Retail Demo", 4, false, "Retail demo režim; ruční volba."),
            ("RemoteRegistry", "Remote Registry", 4, false, "Vzdálená editace registru; bezpečnostní/administrativní volba, ne performance Recommended."),
            ("Spooler", "Print Spooler", 3, false, "Manual jen pokud netiskneš."),
            ("bthserv", "Bluetooth Support", 3, false, "Manual jen bez Bluetooth zařízení."),
            ("XblAuthManager", "Xbox Live Auth", 3, false, "Manual pokud nepoužíváš Xbox/Game Pass."),
            ("XblGameSave", "Xbox Live Game Save", 3, false, "Manual pokud nepoužíváš Xbox/Game Pass."),
            ("XboxNetApiSvc", "Xbox Networking", 3, false, "Manual pokud nepoužíváš Xbox/Game Pass."),
            ("WbioSrvc", "Windows Biometric Service", 3, false, "Jen bez biometrického zařízení."),
            ("PhoneSvc", "Phone Service", 3, false, "Jen pokud nepoužíváš Phone Link."),
            ("SCardSvr", "Smart Card", 3, false, "Jen bez čtečky Smart Card."),
            ("ScDeviceEnum", "Smart Card Device Enumeration", 3, false, "Jen bez čtečky Smart Card."),
            ("SCPolicySvc", "Smart Card Removal Policy", 3, false, "Jen bez čtečky Smart Card."),
            ("SensorService", "Sensor Service", 3, false, "Jen bez senzorů."),
            ("SensrSvc", "Sensor Monitoring", 3, false, "Jen bez senzorů."),
            ("SensorDataService", "Sensor Data Service", 3, false, "Jen bez senzorů."),
            ("FrameServer", "Windows Camera Frame Server", 3, false, "Jen bez kamery nebo capture zařízení."),
            ("DiagTrack", "Connected User Experiences / Telemetry", 3, false, "Ruční omezení diagnostické telemetrie."),
            ("WerSvc", "Windows Error Reporting", 3, false, "Manual zachová hlášení chyb podle potřeby."),
            ("SysMain", "SysMain", 3, false, "Na moderním SSD není vypnutí univerzálně rychlejší."),
            ("WSearch", "Windows Search", 3, false, "Manual omezí indexing, ale zpomalí hledání."),
            ("wisvc", "Windows Insider Service", 3, false, "Jen mimo Windows Insider program."),
            ("WMPNetworkSvc", "Windows Media Player Network Sharing", 3, false, "Starší síťové sdílení médií."),
            ("WalletService", "Wallet Service", 3, false, "Na desktopu obvykle nemusí běžet automaticky."),
            ("diagnosticshub.standardcollector.service", "Diagnostics Hub Collector", 3, false, "Collector pro vývoj a troubleshooting."),
            ("AJRouter", "AllJoyn Router", 3, false, "Legacy IoT komunikace."),
            ("SSDPSRV", "SSDP Discovery", 3, false, "UPnP / SSDP discovery; ponech pokud používáš chytrá zařízení nebo DLNA."),
            ("upnphost", "UPnP Device Host", 3, false, "Hostování UPnP zařízení; volitelné na běžném gaming PC."),
            ("Wecsvc", "Windows Event Collector", 3, false, "Vzdálený sběr event logů, typicky firemní/admin scénáře."),
            ("WinRM", "Windows Remote Management", 3, false, "PowerShell Remoting / WinRM; neměň pokud jej používáš."),
            ("icssvc", "Windows Mobile Hotspot", 3, false, "Potřebné pro Mobile Hotspot a některé sdílené síťové scénáře."),
            ("SharedAccess", "Internet Connection Sharing", 3, false, "Potřebné pro ICS/hotspot a některé virtualizační scénáře."),
            ("RemoteAccess", "Routing and Remote Access", 3, false, "Routing/VPN server scénáře."),
            ("RasAuto", "Remote Access Auto Connection Manager", 3, false, "Automatická VPN/dial-up připojení."),
            ("lfsvc", "Geolocation Service", 3, false, "Ovlivní aplikace využívající polohu."),
            ("stisvc", "Windows Image Acquisition", 3, false, "Potřebné pro skenery a některé fotoaparáty."),
            ("TrkWks", "Distributed Link Tracking Client", 3, false, "Sleduje odkazy na soubory přes NTFS/doménu; pouze volitelná změna."),
            ("CscService", "Offline Files", 3, false, "Potřebné pro offline síťové soubory."),
            ("WebClient", "WebClient / WebDAV", 3, false, "Potřebné pro WebDAV."),
            ("fdPHost", "Function Discovery Provider Host", 3, false, "Nech při síťovém sdílení a tiskárnách."),
            ("FDResPub", "Function Discovery Resource Publication", 3, false, "Publikuje PC do lokální sítě."),
            ("lmhosts", "TCP/IP NetBIOS Helper", 3, false, "Pomáhá starším NetBIOS/SMB scénářům."),
            ("NcdAutoSetup", "Network Connected Devices Auto-Setup", 3, false, "Automatické nastavení síťových zařízení."),
            ("WpcMonSvc", "Parental Controls", 3, false, "Neměň při používání rodinných funkcí."),
            ("SEMgrSvc", "Payments and NFC", 3, false, "Secure Element a NFC funkce."),
            ("shpamsvc", "Shared PC Account Manager", 3, false, "Funkce režimu Shared PC."),
            ("PrintNotify", "Printer Extensions and Notifications", 3, false, "Jen pokud nepoužíváš tiskárnu."),
            ("BTAGService", "Bluetooth Audio Gateway", 3, false, "Jen bez Bluetooth."),
            ("BthAvctpSvc", "Bluetooth AVCTP Service", 3, false, "Jen bez Bluetooth."),
            ("TabletInputService", "Touch Keyboard and Handwriting", 3, false, "Jen bez dotyku nebo pera."),
            ("TapiSrv", "Telephony", 3, false, "Legacy telephony API."),
            ("TermService", "Remote Desktop Services", 3, false, "Neměň pokud používáš RDP."),
            ("SessionEnv", "Remote Desktop Configuration", 3, false, "Neměň pokud používáš RDP."),
            ("UmRdpService", "Remote Desktop Port Redirector", 3, false, "Neměň pokud používáš RDP."),
            ("RasMan", "Remote Access Connection Manager", 3, false, "Nech pro VPN klienty."),
            ("PcaSvc", "Program Compatibility Assistant", 3, false, "Pomáhá starším aplikacím a instalátorům."),
            ("dmwappushservice", "Device Management WAP Push", 3, false, "Pomocná MDM/WAP Push služba.")
        }.Where(x => WindowsComponentCatalogPolicy.ServiceRegistered(x.Item1))
         .Select(((string, string, int, bool, string) x) => ServiceStartTweak(x.Item1, x.Item2, x.Item3, x.Item4, x.Item5)).ToList();
        list.AddRange(GTweakPortCatalog.ServiceTweaks());
        return list;
    }

    private static TweakDefinition ServiceStartTweak(string service, string label, int target, bool recommended, string reason)
    {
        return TweakFactory.Command("svc." + service, "components", "Služby", label, reason, ct =>
        {
            // DetectAsync already runs on a thread-pool slot (TweakEngine);
            // the inner Task.Run hop added a second scheduler dispatch per service.
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ServiceSafetyService.ReadStartType(service) == target);
        }, async (on, ct) =>
        {
            // Native SCM mutation stays off the UI thread; TweakEngine runs
            // SetAsync on its own Parallel.ForEachAsync worker, so the extra
            // Task.Run wrapping was pure double-scheduling overhead.
            uint desired = on ? (uint)target : 3u;
            int original = ServiceSafetyService.ReadStartType(service) ?? 3;
            if (desired == 4 && !ServiceSafetyService.CanDisable(service))
                throw new InvalidOperationException(ServiceSafetyService.ExplainBlock(service));
            if (!NativeCore.ChangeServiceStartType(service, desired, out int error))
                throw new InvalidOperationException($"SCM error {error} pro {service}.");
            int actual = ServiceSafetyService.ReadStartType(service) ?? -1;
            if (actual != desired)
            {
                NativeCore.ChangeServiceStartType(service, (uint)original, out _);
                throw new InvalidOperationException($"Read-back služby {service} nepotvrdil Start={desired}; původní hodnota byla obnovena.");
            }
        }, recommended, target == 4 ? TweakRisk.Safe : TweakRisk.Optional, restart: true);
    }

    private static List<TweakDefinition> DriverTweaks()
    {
        return new (string, string, string, TweakRisk)[]
        {
            ("Beep", "System Beep (beep.sys)", "Legacy PC speaker driver.", TweakRisk.Safe),
            ("flpydisk", "Floppy disk driver", "Legacy disketová mechanika.", TweakRisk.Safe),
            ("sfloppy", "SCSI Floppy driver", "Legacy SCSI floppy podpora.", TweakRisk.Safe),
            ("Parport", "Parallel Port driver", "Legacy LPT port.", TweakRisk.Optional),
            ("Parvdm", "Parallel VDM driver", "Legacy parallel VDM.", TweakRisk.Optional),
            ("Serial", "Serial / COM port driver", "Jen pokud COM port nepoužíváš.", TweakRisk.Advanced),
            ("Serenum", "Serial Enumerator driver", "Jen bez sériových zařízení.", TweakRisk.Advanced),
            ("BTHUSB", "Bluetooth USB kernel driver", "Jen pokud Bluetooth nepoužíváš.", TweakRisk.Advanced),
            ("BthEnum", "Bluetooth Enumerator driver", "Jen pokud Bluetooth nepoužíváš.", TweakRisk.Advanced),
            ("cdrom", "CD/DVD-ROM driver", "Může rozbít optickou mechaniku i ISO mount.", TweakRisk.Advanced),
            ("RasAcd", "Remote Access Auto Connection", "Může být potřeba pro VPN.", TweakRisk.Advanced),
            ("iScsiPrt", "Microsoft iSCSI transport", "Nevypínej při použití síťového storage.", TweakRisk.Advanced),
            ("lltdio", "Link-Layer Topology Discovery I/O", "Windows Network Map a discovery.", TweakRisk.Optional),
            ("rspndr", "Link-Layer Topology Responder", "LAN discovery.", TweakRisk.Optional),
            ("NetBT", "NetBIOS over TCP/IP", "Může ovlivnit starší SMB zařízení.", TweakRisk.Advanced),
            ("BthLEEnum", "Bluetooth LE Enumerator", "Jen bez Bluetooth.", TweakRisk.Advanced),
            ("BthHFEnum", "Bluetooth Hands-Free Enumerator", "Jen bez Bluetooth.", TweakRisk.Advanced),
            ("BthPan", "Bluetooth PAN driver", "Jen bez Bluetooth.", TweakRisk.Advanced),
            ("RFCOMM", "Bluetooth RFCOMM driver", "Jen bez Bluetooth.", TweakRisk.Advanced),
            ("HidBth", "Bluetooth HID Bus", "Ovlivní BT myši, klávesnice a gamepady.", TweakRisk.Advanced),
            ("BthA2dp", "Bluetooth A2DP", "Ovlivní Bluetooth audio.", TweakRisk.Advanced),
            ("Modem", "Modem driver", "Legacy modem/dial-up.", TweakRisk.Optional),
            ("NdisWan", "NDIS WAN driver", "Může být potřeba pro VPN/PPP.", TweakRisk.Advanced),
            ("wanarp", "Remote Access IP ARP", "Může být potřeba pro VPN.", TweakRisk.Advanced),
            ("vwififlt", "Virtual Wi-Fi Filter", "Potřebné pro hotspot a Miracast.", TweakRisk.Advanced)
        }.Where(x => WindowsComponentCatalogPolicy.DriverRegistered(x.Item1))
         .Select(((string, string, string, TweakRisk) x) => DriverStartTweak(x.Item1, x.Item2, x.Item3, x.Item1 == "Beep", x.Item4)).ToList();
    }
}
