using System;
using System.Collections.Generic;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

// Broad OBSERVATION inventory, not a new automatic service-stop allowlist.
// Installed/running does not prove that a Windows feature is actually in use.
internal readonly record struct AiOptionalWindowsFeature(string ServiceName, string Area);

internal static class AiOptionalWindowsFeatureCatalog
{
    internal static readonly AiOptionalWindowsFeature[] Entries =
    {
        new("Spooler", "Tiskárny"),
        new("PrintNotify", "Tiskárny"),
        new("StiSvc", "Skenery"),
        new("WiaRpc", "Skenery"),
        new("FrameServer", "Kamery"),
        new("WlanSvc", "Bezdrátová zařízení"),
        new("bthserv", "Bezdrátová zařízení"),
        new("BTAGService", "Bezdrátová zařízení"),
        new("BthAvctpSvc", "Bezdrátová zařízení"),
        new("WFDSConMgrSvc", "Bezdrátová zařízení"),
        new("lfsvc", "Poloha"),
        new("tzautoupdate", "Poloha"),
        new("SCardSvr", "Chytré karty"),
        new("ScDeviceEnum", "Chytré karty"),
        new("CertPropSvc", "Chytré karty"),
        new("TermService", "Vzdálený přístup"),
        new("UmRdpService", "Vzdálený přístup"),
        new("SessionEnv", "Vzdálený přístup"),
        new("RemoteAccess", "Vzdálený přístup"),
        new("RasMan", "Vzdálený přístup"),
        new("SSDPSRV", "Zjišťování a sdílení"),
        new("upnphost", "Zjišťování a sdílení"),
        new("FDResPub", "Zjišťování a sdílení"),
        new("fdPHost", "Zjišťování a sdílení"),
        new("SharedAccess", "Zjišťování a sdílení"),
        new("icssvc", "Zjišťování a sdílení"),
        new("WebClient", "Zjišťování a sdílení"),
        new("WbioSrvc", "Biometrika"),
        new("TabletInputService", "Zadávání"),
        new("TextInputManagementService", "Zadávání"),
        new("WpcMonSvc", "Rodičovská kontrola"),
        new("CscService", "Offline data"),
        new("WorkFoldersSvc", "Offline data"),
        new("PeerDistSvc", "Offline data"),
        new("EntAppSvc", "Podnikové funkce"),
        new("AppVClient", "Podnikové funkce"),
        new("CDPSvc", "Synchronizace"),
        new("DeviceAssociationService", "Synchronizace"),
        new("PushToInstall", "Synchronizace"),
        new("WpnService", "Aplikace a prostředí"),
        new("WSearch", "Aplikace a prostředí"),
        new("SysMain", "Aplikace a prostředí"),
        new("PcaSvc", "Aplikace a prostředí"),
        new("Themes", "Aplikace a prostředí"),
        new("DusmSvc", "Aplikace a prostředí"),
        new("XboxGipSvc", "Xbox a hry"),
        new("XblAuthManager", "Xbox a hry"),
        new("XblGameSave", "Xbox a hry"),
        new("XboxNetApiSvc", "Xbox a hry"),
        new("ADPSvc", "Diagnostika a telemetrie"),
        new("InventorySvc", "Diagnostika a telemetrie"),
        new("diagsvc", "Diagnostika a telemetrie"),
        new("GraphicsPerfSvc", "Diagnostika a telemetrie"),
        new("PerfHost", "Diagnostika a telemetrie"),
        new("pla", "Diagnostika a telemetrie"),
        new("AppReadiness", "Aplikace a prostředí"),
        new("AxInstSV", "Aplikace a prostředí"),
        new("LxpSvc", "Aplikace a prostředí"),
        new("DsSvc", "Synchronizace"),
        new("DmEnrollmentSvc", "Podnikové funkce"),
        new("fhsvc", "Zálohování a obnova"),
        new("SDRSVC", "Zálohování a obnova"),
        new("FrameServerMonitor", "Kamery"),
        new("PrintDeviceConfigurationService", "Tiskárny"),
        new("PrintScanBrokerService", "Tisk a skenování"),
        new("midisrv", "Multimédia"),
        new("QWAVE", "Multimédia"),
        new("SensorDataService", "Senzory"),
        new("SensorService", "Senzory"),
        new("SensrSvc", "Senzory"),
        new("DisplayEnhancementService", "Displej"),
        new("NaturalAuthentication", "Biometrika"),
        new("DevQueryBroker", "Další periferie"),
        new("lltdsvc", "Zjišťování a sdílení"),
        new("SmsRouter", "Telefon a zprávy"),
        new("autotimesvc", "Telefon a zprávy"),
        new("SNMPTrap", "Správa a monitoring"),
        new("RasAuto", "Vzdálený přístup"),
        new("SstpSvc", "Vzdálený přístup"),
        new("DeviceInstall", "Další periferie"),
        new("NcdAutoSetup", "Další periferie"),
    };

    // This output is informational and does not authorize service changes.
    internal static string ObservationReason(AiOptionalWindowsFeature feature,
        ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped =>
            $"Sledování [{feature.Area}]: nyní neběží; dlouhodobé využití není ověřené",
        ServiceControllerStatus.Running =>
            $"Sledování [{feature.Area}]: služba běží; skutečné využití není ověřené",
        _ => $"Sledování [{feature.Area}]: služba mění stav / je pozastavená; využití není ověřené"
    };

    internal static int ObserveInstalled(
        IReadOnlyDictionary<string, ServiceControllerStatus> states,
        ISet<string> automaticCandidates,
        Action<string, string> onObserved)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(automaticCandidates);
        ArgumentNullException.ThrowIfNull(onObserved);
        return ObserveRuntimeStates(states, automaticCandidates,
            (item, status) => onObserved(item.ServiceName, ObservationReason(item, status)));
    }

    internal static int ObserveRuntimeStates(
        IReadOnlyDictionary<string, ServiceControllerStatus> states,
        ISet<string> automaticCandidates,
        Action<AiOptionalWindowsFeature, ServiceControllerStatus> onObserved)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(automaticCandidates);
        ArgumentNullException.ThrowIfNull(onObserved);
        int count = 0;
        foreach (AiOptionalWindowsFeature item in Entries)
        {
            if (automaticCandidates.Contains(item.ServiceName) ||
                !states.TryGetValue(item.ServiceName, out ServiceControllerStatus status))
                continue;
            onObserved(item, status);
            count++;
        }
        return count;
    }
}
