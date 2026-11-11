using System;
using System.Net.NetworkInformation;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Read-only wireless-adapter evidence. Hardware presence never implies usage;
/// adapter inactivity never authorizes disabling WLAN or a network setting.
/// </summary>
internal static class AiOptionalWirelessUsageProbe
{
    internal static AiOptionalFeatureUsage Classify(
        bool probeSucceeded, int installedWifiAdapters, int activeWifiAdapters)
    {
        if (!probeSucceeded || installedWifiAdapters < 0 || activeWifiAdapters < 0 ||
            activeWifiAdapters > installedWifiAdapters || installedWifiAdapters == 0)
            return AiOptionalFeatureUsage.Unknown;
        return activeWifiAdapters > 0
            ? AiOptionalFeatureUsage.InUse
            : AiOptionalFeatureUsage.CandidateForObservation;
    }

    internal static AiOptionalFeatureUsage Read()
    {
        try
        {
            int installed = 0;
            int active = 0;
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                    continue;
                installed++;
                if (adapter.OperationalStatus == OperationalStatus.Up)
                    active++;
            }
            return Classify(true, installed, active);
        }
        catch
        {
            return AiOptionalFeatureUsage.Unknown;
        }
    }

    internal static string Describe(AiOptionalFeatureUsage usage) => usage switch
    {
        AiOptionalFeatureUsage.InUse => "Wi-Fi adaptér je připojený; zachovat síťové funkce",
        AiOptionalFeatureUsage.CandidateForObservation =>
            "Wi-Fi adaptér momentálně není připojený; další využití není ověřené",
        _ => "stav Wi-Fi adaptéru se nepodařilo spolehlivě určit"
    };
}
