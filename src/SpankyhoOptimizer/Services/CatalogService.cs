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

public static class CatalogService
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
        new("ui", "UI / Explorer", "Průzkumník, taskbar a vzhled Windows", "\ue771"),
        new("update", "Windows Update", "Aktualizace a Delivery Optimization", "\ue895"),
        new("browsers", "Prohlížeče", "Firefox, Chrome, Edge, Brave, Opera GX a další", "\ue774"),
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
        new("browsers", "Firefox", () => BrowserTweaks("Firefox")),
        new("browsers", "Chrome", () => BrowserTweaks("Chrome")),
        new("browsers", "Edge", () => BrowserTweaks("Edge")),
        new("browsers", "Brave", () => BrowserTweaks("Brave")),
        new("browsers", "Opera", () => BrowserTweaks("Opera")),
        new("browsers", "Opera GX", () => BrowserTweaks("Opera GX")),
        new("browsers", "Vivaldi", () => BrowserTweaks("Vivaldi")),
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
        if (section.Equals("browsers", StringComparison.OrdinalIgnoreCase) && effectiveSub.Length == 0)
            effectiveSub = "Firefox";
        else if (section.Equals("components", StringComparison.OrdinalIgnoreCase) && effectiveSub.Length == 0)
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

    private static (RegistryHive hive, string path) BrowserPolicy(string browser)
    {
        return browser switch
        {
            "Firefox" => (hive: RegistryHive.LocalMachine, path: "SOFTWARE\\Policies\\Mozilla\\Firefox"),
            "Chrome" => (hive: RegistryHive.LocalMachine, path: "SOFTWARE\\Policies\\Google\\Chrome"),
            "Edge" => (hive: RegistryHive.LocalMachine, path: "SOFTWARE\\Policies\\Microsoft\\Edge"),
            "Brave" => (hive: RegistryHive.LocalMachine, path: "SOFTWARE\\Policies\\BraveSoftware\\Brave"),
            "Opera" => (hive: RegistryHive.LocalMachine, path: "SOFTWARE\\Policies\\Opera Software\\Opera Stable"),
            "Opera GX" => (hive: RegistryHive.LocalMachine, path: "SOFTWARE\\Policies\\Opera Software\\Opera GX Stable"),
            "Vivaldi" => (hive: RegistryHive.LocalMachine, path: "SOFTWARE\\Policies\\Vivaldi"),
            _ => (hive: RegistryHive.LocalMachine, path: "SOFTWARE\\Policies\\Google\\Chrome"),
        };
    }

    private static List<TweakDefinition> BrowserTweaks(string browser)
    {
        var (hive, path) = BrowserPolicy(browser);
        if (browser == "Firefox")
        {
            return new List<TweakDefinition>
            {
                TweakFactory.Dword("ff.telemetry", "browsers", "Privacy", "Vypnout telemetry", "Firefox enterprise policy.", hive, path, "DisableTelemetry", 1, null, true, true),
                TweakFactory.Dword("ff.studies", "browsers", "Privacy", "Vypnout Firefox Studies", "Zakáže Studies/Normandy.", hive, path, "DisableFirefoxStudies", 1, null, true, true),
                TweakFactory.Dword("ff.feedback", "browsers", "Privacy", "Vypnout feedback příkazy", "Skryje odesílání feedbacku.", hive, path, "DisableFeedbackCommands", 1, null, true, true),
                TweakFactory.Dword("ff.agent", "browsers", "Background", "Vypnout Default Browser Agent", "Omezí pomocnou úlohu Firefoxu.", hive, path, "DisableDefaultBrowserAgent", 1, null, true, true),
                TweakFactory.Dword("ff.defaultcheck", "browsers", "Background", "Nekontrolovat výchozí prohlížeč", "Nevyvolá kontrolu výchozího browseru.", hive, path, "DontCheckDefaultBrowser", 1, null, true, true),
                TweakFactory.Dword("ff.loginlaunch", "browsers", "Background", "Nespouštět Firefox po přihlášení", "Zakáže launch on login.", hive, path, "DisableLaunchOnLogin", 1, null, true, true),
                TweakFactory.Dword("ff.pocket", "browsers", "UI", "Vypnout Pocket", "Odstraní Pocket integraci.", hive, path, "DisablePocket", 1, null, true, true),
                TweakFactory.Dword("ff.suggest", "browsers", "Privacy", "Vypnout návrhy hledání", "Dotazy se neodesílají při psaní.", hive, path, "SearchSuggestEnabled", 0, null, true, true),
                TweakFactory.Dword("ff.visualsearch", "browsers", "Privacy", "Vypnout Visual Search", "Vypne visual search policy.", hive, path, "VisualSearchEnabled", 0, null, true, true),
                TweakFactory.Dword("ff.background", "browsers", "Background", "Background updates vypnuté", "Zakáže background app update policy.", hive, path, "BackgroundAppUpdate", 0, null, true, false, TweakRisk.Optional),
                TweakFactory.Dword("ff.formhistory", "browsers", "Privacy", "Vypnout historii formulářů", "Zakáže ukládání formulářové historie.", hive, path, "DisableFormHistory", 1, null, true, false, TweakRisk.Optional),
                TweakFactory.Dword("ff.password", "browsers", "Privacy", "Vypnout správce hesel", "Jen pokud používáš externí správce hesel.", hive, path, "PasswordManagerEnabled", 0, null, true, false, TweakRisk.Advanced),
                TweakFactory.Dword("ff.autofilladdress", "browsers", "Privacy", "Vypnout autofill adres", "Vypne ukládání adres.", hive, path, "AutofillAddressEnabled", 0, null, true, false, TweakRisk.Optional),
                TweakFactory.Dword("ff.autofillcard", "browsers", "Privacy", "Vypnout autofill karet", "Vypne ukládání platebních karet.", hive, path, "AutofillCreditCardEnabled", 0, null, true, false, TweakRisk.Optional),
                TweakFactory.Dword("ff.screenshots", "browsers", "UI", "Vypnout Firefox Screenshots", "Volitelné odebrání screenshot nástroje.", hive, path, "DisableFirefoxScreenshots", 1, null, true, false, TweakRisk.Optional),
                TweakFactory.Dword("ff.accounts", "browsers", "Sync", "Vypnout Firefox Accounts", "Zakáže účet a synchronizaci.", hive, path, "DisableFirefoxAccounts", 1, null, true, false, TweakRisk.Advanced),
                TweakFactory.Dword("ff.logins", "browsers", "Privacy", "Nenabízet ukládání přihlášení", "Vhodné s externím password managerem.", hive, path, "OfferToSaveLogins", 0, null, true, false, TweakRisk.Optional),
                TweakFactory.Dword("ff.hometopsites", "browsers", "Firefox Home", "Sponzorované Top Sites vypnuté", "Odstraní sponzorované dlaždice.", hive, path + "\\FirefoxHome", "SponsoredTopSites", 0, null, true, true),
                TweakFactory.Dword("ff.homepocket", "browsers", "Firefox Home", "Sponzorovaný Pocket vypnutý", "Odstraní sponzorovaný obsah Pocket.", hive, path + "\\FirefoxHome", "SponsoredPocket", 0, null, true, true),
                TweakFactory.Dword("ff.snippets", "browsers", "Firefox Home", "Snippets vypnuté", "Odstraní promo zprávy na nové kartě.", hive, path + "\\FirefoxHome", "Snippets", 0, null, true, true),
                TweakFactory.Dword("ff.extensions", "browsers", "Doporučení", "Doporučení rozšíření vypnutá", "Omezí doporučování doplňků.", hive, path + "\\UserMessaging", "ExtensionRecommendations", 0, null, true, true),
                TweakFactory.Dword("ff.features", "browsers", "Doporučení", "Doporučení funkcí vypnutá", "Omezí promo nových funkcí.", hive, path + "\\UserMessaging", "FeatureRecommendations", 0, null, true, true),
                TweakFactory.Dword("ff.urlbar", "browsers", "Doporučení", "Urlbar intervence vypnuté", "Omezí doporučení v adresním řádku.", hive, path + "\\UserMessaging", "UrlbarInterventions", 0, null, true, true),
                TweakFactory.Dword("ff.onboarding", "browsers", "Doporučení", "Přeskočit onboarding", "Nevyvolá úvodní promo obrazovky.", hive, path + "\\UserMessaging", "SkipOnboarding", 1, null, true, true),
                TweakFactory.Dword("ff.moremozilla", "browsers", "Doporučení", "More from Mozilla vypnuté", "Omezí další promo Mozilly.", hive, path + "\\UserMessaging", "MoreFromMozilla", 0, null, true, true)
            };
        }
        string text = browser.Replace(" ", "").ToLowerInvariant();
        List<TweakDefinition> list2 = new List<TweakDefinition>
        {
            TweakFactory.Dword(text + ".background", "browsers", "Performance", "Background mode vypnutý", "Chromium policy BackgroundModeEnabled.", hive, path, "BackgroundModeEnabled", 0, null, deleteOnDisable: true, recommended: true),
            TweakFactory.Dword(text + ".metrics", "browsers", "Privacy", "Metrics reporting vypnutý", "Omezí browser metrics reporting.", hive, path, "MetricsReportingEnabled", 0, null, deleteOnDisable: true, recommended: true),
            TweakFactory.Dword(text + ".hwaccel", "browsers", "Performance", "Hardware acceleration zapnutá", "Nechá rendering na GPU.", hive, path, "HardwareAccelerationModeEnabled", 1, null, deleteOnDisable: true, recommended: true, TweakRisk.Safe, restart: true),
            TweakFactory.Dword(text + ".signin", "browsers", "Privacy", "Browser sign-in vypnutý", "Volitelné — nepoužívej pokud potřebuješ sync.", hive, path, "BrowserSignin", 0, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced),
            TweakFactory.Dword(text + ".sync", "browsers", "Privacy", "Cloud sync vypnutý", "Volitelné — zakáže synchronizaci profilu.", hive, path, "SyncDisabled", 1, null, deleteOnDisable: true, recommended: false, TweakRisk.Advanced)
        };
        list2.AddRange(new[]
        {
            TweakFactory.Dword(text + ".suggest", "browsers", "Privacy", "Návrhy hledání vypnuté", "Při psaní neodesílá návrhy vyhledávači.", hive, path, "SearchSuggestEnabled", 0, null, true, true),
            TweakFactory.Dword(text + ".promo", "browsers", "UI", "Promo karty vypnuté", "Skryje propagační karty prohlížeče.", hive, path, "PromotionalTabsEnabled", 0, null, true, true),
            TweakFactory.Dword(text + ".default", "browsers", "UI", "Kontrola výchozího browseru vypnutá", "Nevyvolá opakované upozornění na výchozí prohlížeč.", hive, path, "DefaultBrowserSettingEnabled", 0, null, true, true),
            TweakFactory.Dword(text + ".dnschecks", "browsers", "Síť", "DNS interception checks vypnuté", "Omezí pomocné DNS kontrolní požadavky.", hive, path, "DNSInterceptionChecksEnabled", 0, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword(text + ".password", "browsers", "Privacy", "Správce hesel vypnutý", "Jen pokud používáš externí správce hesel.", hive, path, "PasswordManagerEnabled", 0, null, true, false, TweakRisk.Advanced),
            TweakFactory.Dword(text + ".autofilladdress", "browsers", "Privacy", "Autofill adres vypnutý", "Vypne ukládání adres.", hive, path, "AutofillAddressEnabled", 0, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword(text + ".autofillcard", "browsers", "Privacy", "Autofill karet vypnutý", "Vypne ukládání platebních údajů.", hive, path, "AutofillCreditCardEnabled", 0, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword(text + ".translate", "browsers", "UI", "Překladač vypnutý", "Volitelné vypnutí nabídek překladu.", hive, path, "TranslateEnabled", 0, null, true, false, TweakRisk.Optional),
            TweakFactory.Dword(text + ".quic", "browsers", "Síť", "QUIC vypnutý", "Jen pro řešení problémů se sítí; HTTP/3 se vypne.", hive, path, "QuicAllowed", 0, null, true, false, TweakRisk.Advanced),
            TweakFactory.Dword(text + ".printing", "browsers", "Privacy", "Background cloud print vypnutý", "Omezí starší cloud print proxy.", hive, path, "CloudPrintProxyEnabled", 0, null, true, false, TweakRisk.Optional)
        });
        if (browser == "Brave")
        {
            list2.Add(TweakFactory.Dword("brave.rewards", "browsers", "Brave", "Brave Rewards vypnuté", "Zakáže Rewards komponentu.", hive, path, "BraveRewardsDisabled", 1, null, true, false, TweakRisk.Optional));
            list2.Add(TweakFactory.Dword("brave.wallet", "browsers", "Brave", "Brave Wallet vypnutá", "Zakáže integrovanou crypto wallet.", hive, path, "BraveWalletDisabled", 1, null, true, false, TweakRisk.Optional));
            list2.Add(TweakFactory.Dword("brave.vpn", "browsers", "Brave", "Brave VPN vypnutá", "Zakáže integrovanou nabídku Brave VPN.", hive, path, "BraveVPNDisabled", 1, null, true, false, TweakRisk.Optional));
            list2.Add(TweakFactory.Dword("brave.leo", "browsers", "Brave", "Brave Leo AI vypnuté", "Zakáže Brave AI Chat / Leo.", hive, path, "BraveAIChatEnabled", 0, null, true, false, TweakRisk.Optional));
            list2.Add(TweakFactory.Dword("brave.news", "browsers", "Brave", "Brave News vypnuté", "Zakáže Brave News.", hive, path, "BraveNewsDisabled", 1, null, true, false, TweakRisk.Optional));
            list2.Add(TweakFactory.Dword("brave.p3a", "browsers", "Privacy", "Brave P3A vypnuté", "Zakáže privacy-preserving product analytics P3A.", hive, path, "BraveP3AEnabled", 0, null, true, true));
        }
        if (browser == "Edge")
        {
            list2.Add(TweakFactory.Dword("edge.startupboost", "browsers", "Performance", "Startup Boost vypnutý", "Zabrání Edge zůstávat přednačtený po startu.", hive, path, "StartupBoostEnabled", 0, null, deleteOnDisable: true, recommended: true));
            list2.Add(TweakFactory.Dword("edge.sidebar", "browsers", "UI", "Edge Sidebar vypnutý", "Zakáže sidebar/hub.", hive, path, "HubsSidebarEnabled", 0, null, deleteOnDisable: true, recommended: true));
            list2.Add(TweakFactory.Dword("edge.shopping", "browsers", "UI", "Shopping Assistant vypnutý", "Skryje nákupní doporučení.", hive, path, "EdgeShoppingAssistantEnabled", 0, null, true, true));
            list2.Add(TweakFactory.Dword("edge.personalization", "browsers", "Privacy", "Personalization reporting vypnutý", "Omezí personalizaci Edge.", hive, path, "PersonalizationReportingEnabled", 0, null, true, true));
            list2.Add(TweakFactory.Dword("edge.recommend", "browsers", "UI", "Edge doporučení vypnutá", "Skryje doporučení v browseru.", hive, path, "ShowRecommendationsEnabled", 0, null, true, true));
            list2.Add(TweakFactory.Dword("edge.webwidget", "browsers", "Background", "Web Widget vypnutý", "Zakáže background Web Widget.", hive, path, "WebWidgetAllowed", 0, null, true, true));
            list2.Add(TweakFactory.Dword("edge.firstrun", "browsers", "UI", "Přeskočit Edge First Run", "Skryje úvodní onboarding Edge.", hive, path, "HideFirstRunExperience", 1, null, true, true));
            list2.Add(TweakFactory.Dword("edge.feedback", "browsers", "Privacy", "Edge feedback vypnutý", "Zakáže feedback prompts a odesílání zpětné vazby.", hive, path, "UserFeedbackAllowed", 0, null, true, true));
            list2.Add(TweakFactory.Dword("edge.dnt", "browsers", "Privacy", "Do Not Track zapnuté", "Zapne Edge Do Not Track request header.", hive, path, "ConfigureDoNotTrack", 1, null, true, false, TweakRisk.Optional));
            list2.Add(TweakFactory.Dword("edge.collections", "browsers", "UI", "Collections vypnuté", "Zakáže Edge Collections.", hive, path, "EdgeCollectionsEnabled", 0, null, true, false, TweakRisk.Optional));
            list2.Add(TweakFactory.Dword("edge.rewards", "browsers", "UI", "Microsoft Rewards v Edge vypnuté", "Skryje Microsoft Rewards integraci v Edge.", hive, path, "ShowMicrosoftRewards", 0, null, true, true));
            list2.Add(TweakFactory.Dword("edge.diagnostic", "browsers", "Privacy", "Edge diagnostic data vypnutá", "Omezí Edge diagnostic data enterprise policy.", hive, path, "DiagnosticData", 0, null, true, true));
            list2.Add(TweakFactory.Dword("edge.assetdelivery", "browsers", "Background", "Edge Asset Delivery vypnutý", "Zakáže background delivery marketing/asset obsahu.", hive, path, "EdgeAssetDeliveryServiceEnabled", 0, null, true, true));
            list2.Add(TweakFactory.Dword("edge.defaultcampaign", "browsers", "UI", "Default browser campaign vypnutá", "Skryje kampaně vyzývající k nastavení Edge jako výchozího browseru.", hive, path, "DefaultBrowserSettingsCampaignEnabled", 0, null, true, true));
        }
        list2.AddRange(GTweakPortCatalog.BrowserTweaks(browser));
        return list2;
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
        }.Select(((string, string, int, bool, string) x) => ServiceStartTweak(x.Item1, x.Item2, x.Item3, x.Item4, x.Item5)).ToList();
        list.AddRange(GTweakPortCatalog.ServiceTweaks());
        return list;
    }

    private static TweakDefinition ServiceStartTweak(string service, string label, int target, bool recommended, string reason)
    {
        return TweakFactory.Command("svc." + service, "components", "Služby", label, reason, async ct => await Task.Run(() =>
        {
            return ServiceSafetyService.ReadStartType(service) == target;
        }, ct).ConfigureAwait(false), async (on, ct) => await Task.Run(() =>
        {
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
        }, ct).ConfigureAwait(false), recommended, target == 4 ? TweakRisk.Safe : TweakRisk.Optional, restart: true);
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
        }.Select(((string, string, string, TweakRisk) x) => DriverStartTweak(x.Item1, x.Item2, x.Item3, x.Item1 == "Beep", x.Item4)).ToList();
    }

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
            RevertAsync = async _ =>
            {
                if (!await TweakFactory.Backups.RestoreAsync(id).ConfigureAwait(false))
                    throw new InvalidOperationException("Pro tento driver tweak není uložená původní hodnota.");
            },
            DetectAsync = async ct => await Task.Run(() =>
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path, false);
                if (key == null) return false;
                return Convert.ToInt32(key.GetValue("Start", -1)) == 4;
            }, ct).ConfigureAwait(false),
            SetAsync = async (on, ct) =>
            {
                if (on) await TweakFactory.Backups.SaveOriginalAsync(id, RegistryHive.LocalMachine, path, "Start").ConfigureAwait(false);
                if (!on && await TweakFactory.Backups.RestoreAsync(id).ConfigureAwait(false)) return;
                await Task.Run(() =>
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(path, true) ?? throw new PlatformNotSupportedException($"Driver service {service} v tomto systému není přítomný.");
                    int original = Convert.ToInt32(key.GetValue("Start", 3));
                    int desired = on ? 4 : 3;
                    key.SetValue("Start", desired, RegistryValueKind.DWord);
                    if (Convert.ToInt32(key.GetValue("Start", -1)) != desired)
                    {
                        key.SetValue("Start", original, RegistryValueKind.DWord);
                        throw new InvalidOperationException($"Read-back driveru {service} nepotvrdil Start={desired}; původní hodnota byla obnovena.");
                    }
                }, ct).ConfigureAwait(false);
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
        }.Select(((string, string, string, bool) x) => TaskDisabledTweak(x.Item1, x.Item2, x.Item3, x.Item4)).ToList();
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
        return TweakFactory.Command(id, section, group, name, description, async ct => await Task.Run(() => { using var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64); using var k = b.OpenSubKey(path); return string.Equals(Convert.ToString(k?.GetValue(valueName)), enabled, StringComparison.OrdinalIgnoreCase); }, ct), async (on, ct) => await Task.Run(() => { using var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64); using var k = b.CreateSubKey(path, true); k.SetValue(valueName, on ? enabled : disabled, RegistryValueKind.String); }, ct), recommended, risk, restart);
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
            TweakFactory.Command("ui.shortcutarrow", "ui", "Plocha / Zástupci", "Odebrat šipku u zástupců", "Skryje overlay šipku pomocí vlastní transparentní ikony; neodstraňuje IsShortcut a nerozbíjí připnuté položky.", async ct => await Task.Run(ShortcutAppearanceService.ShortcutArrowHidden, ct).ConfigureAwait(false), async (on, ct) => await ShortcutAppearanceService.SetShortcutArrowHiddenAsync(on, ct).ConfigureAwait(false), false, TweakRisk.Optional, false, "Nízký", null),
            TweakFactory.Command("ui.shortcutsuffix", "ui", "Plocha / Zástupci", "Nepsat „- Shortcut“ do názvu", "Nově vytvořené zástupce Windows nebude automaticky pojmenovávat s příponou „- Shortcut“.", async ct => await Task.Run(ShortcutAppearanceService.ShortcutSuffixDisabled, ct).ConfigureAwait(false), async (on, ct) => await ShortcutAppearanceService.SetShortcutSuffixDisabledAsync(on, ct).ConfigureAwait(false), false, TweakRisk.Optional, false, "Nízký", null),
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
        const string shell = "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell";
        return TweakFactory.Command("ui.folderdiscovery", "ui", "Explorer", "Vypnout automatické rozpoznávání typu složek", "Vynutí Generic view pro všechny složky. Resetuje uložené Bags/BagMRU pohledy Exploreru.", async ct => await Task.Run(() => { using var k = Registry.CurrentUser.OpenSubKey(shell + "\\Bags\\AllFolders\\Shell"); return string.Equals(k?.GetValue("FolderType")?.ToString(), "NotSpecified", StringComparison.OrdinalIgnoreCase); }, ct).ConfigureAwait(false), async (on, ct) => await Task.Run(() => { Registry.CurrentUser.DeleteSubKeyTree(shell + "\\Bags", false); Registry.CurrentUser.DeleteSubKeyTree(shell + "\\BagMRU", false); if (on) { using var k = Registry.CurrentUser.CreateSubKey(shell + "\\Bags\\AllFolders\\Shell", true); k.SetValue("FolderType", "NotSpecified", RegistryValueKind.String); } }, ct).ConfigureAwait(false), false, TweakRisk.Advanced, true, "Střední", "Resetuje uložené individuální pohledy a seskupení složek Exploreru.");
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
            async ct => await Task.Run(() => { using var k = Registry.CurrentUser.OpenSubKey(path); return k != null && names.All(n => Convert.ToInt32(k.GetValue(n) ?? 1) == 0); }, ct).ConfigureAwait(false),
            async (on, ct) => await Task.Run(() => { using var k = Registry.CurrentUser.CreateSubKey(path, true); foreach (var n in names) k.SetValue(n, on ? 0 : 1, RegistryValueKind.DWord); }, ct).ConfigureAwait(false),
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
