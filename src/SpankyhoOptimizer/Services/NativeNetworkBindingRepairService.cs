using System;
using System.Collections.Generic;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading;

namespace SpankyhoOptimizer.Services;

// The WMI provider behind Enable-NetAdapterBinding supports Enable/Disable
// directly. Only an explicit repair action may invoke those mutation methods.
internal static class NativeNetworkBindingRepairService
{
    private const string BindingNamespace = @"\\.\root\StandardCimv2";
    // Keep mutation enumeration unfiltered: a provider can change membership
    // while Enable modifies Enabled. Read-only status can use server-side WQL
    // filtering to avoid materializing irrelevant binding objects.
    private const string Query =
        "SELECT Name, ComponentID, Enabled FROM MSFT_NetAdapterBindingSettingData";
    private const string DisabledRelevantQuery =
        Query + " WHERE (ComponentID = 'ms_tcpip6' OR ComponentID = 'ms_pacer') AND Enabled = FALSE";

    private static HashSet<string> ActiveNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            if (adapter.OperationalStatus == OperationalStatus.Up)
                names.Add(adapter.Name);
        return names;
    }

    internal static bool RequiresRepair(string name, string component, bool enabled,
        ISet<string> activeNames) =>
        !enabled && activeNames.Contains(name) &&
        (component.Equals("ms_tcpip6", StringComparison.OrdinalIgnoreCase) ||
         component.Equals("ms_pacer", StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> ReadDisabled(CancellationToken token) =>
        ReadDisabledWithQuery(DisabledRelevantQuery, token);

    private static IReadOnlyList<string> ReadDisabledWithQuery(string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var active = ActiveNames();
        var disabled = new List<string>(4);
        using var searcher = new ManagementObjectSearcher(BindingNamespace, query);
        using var results = searcher.Get();
        foreach (ManagementObject binding in results)
        {
            using (binding)
            {
                token.ThrowIfCancellationRequested();
                string name = binding["Name"]?.ToString() ?? string.Empty;
                string component = binding["ComponentID"]?.ToString() ?? string.Empty;
                if (binding["Enabled"] is bool enabled &&
                    RequiresRepair(name, component, enabled, active))
                    disabled.Add("Binding " + name + ":" + component);
            }
        }
        return disabled;
    }

    internal static int EnableDisabled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var active = ActiveNames();
        var changed = new List<(string Path, string Name, string Component)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(BindingNamespace, Query);
            using var results = searcher.Get();
            foreach (ManagementObject binding in results)
            {
                using (binding)
                {
                    token.ThrowIfCancellationRequested();
                    string name = binding["Name"]?.ToString() ?? string.Empty;
                    string component = binding["ComponentID"]?.ToString() ?? string.Empty;
                    if (binding["Enabled"] is not bool enabled ||
                        !RequiresRepair(name, component, enabled, active))
                        continue;

                    // Record before invocation: if the provider changes state and
                    // then throws, rollback must still restore the original state.
                    changed.Add((binding.Path.Path, name, component));
                    InvokeChecked(binding, "Enable");
                    binding.Get();
                    if (binding["Enabled"] is not bool verified || !verified)
                        throw new InvalidOperationException(
                            "Network binding enable read-back failed: " + name + ":" + component);
                }
            }
            return changed.Count;
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            // Restore only bindings whose initial state was disabled.
            // Rollback is best-effort even when the caller was cancelled.
            for (int i = changed.Count - 1; i >= 0; --i)
            {
                var item = changed[i];
                try
                {
                    using var binding = new ManagementObject(item.Path);
                    binding.Get();
                    if (binding["Enabled"] is bool isEnabled && isEnabled)
                    {
                        InvokeChecked(binding, "Disable");
                        binding.Get();
                        if (binding["Enabled"] is not bool restored || restored)
                            throw new InvalidOperationException(
                                "Network binding rollback read-back failed: " +
                                item.Name + ":" + item.Component);
                    }
                }
                catch (Exception restoreError) { failures.Add(restoreError); }
            }
            if (failures.Count > 0)
            {
                failures.Insert(0, original);
                throw new AggregateException("Network binding repair and rollback failed.", failures);
            }
            throw;
        }
    }

    private static void InvokeChecked(ManagementObject binding, string method)
    {
        using var result = binding.InvokeMethod(method, null, null);
        if (result == null || Convert.ToUInt32(result["ReturnValue"] ?? uint.MaxValue) != 0)
            throw new InvalidOperationException("Network binding " + method +
                " failed: " + binding["Name"] + ":" + binding["ComponentID"] +
                " return=" + (result?["ReturnValue"]?.ToString() ?? "missing"));
    }

    internal static void VerifyReadOnly()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ethernet" };
        if (!RequiresRepair("Ethernet", "ms_tcpip6", false, active) ||
            !RequiresRepair("ethernet", "MS_PACER", false, active) ||
            RequiresRepair("Ethernet", "ms_tcpip6", true, active) ||
            RequiresRepair("Wifi", "ms_pacer", false, active) ||
            RequiresRepair("Ethernet", "ms_tcpip", false, active))
            throw new InvalidOperationException("Native network binding selection regression.");
        using var klass = new ManagementClass(BindingNamespace,
            "MSFT_NetAdapterBindingSettingData", null);
        klass.Get();
        if (klass.Methods["Enable"] == null || klass.Methods["Disable"] == null)
            throw new InvalidOperationException("Network binding native methods missing.");
        // No mutations: the projected WQL result must remain identical to
        // the previous broad enumeration on this machine.
        var filtered = ReadDisabledWithQuery(DisabledRelevantQuery, CancellationToken.None);
        var broad = ReadDisabledWithQuery(Query, CancellationToken.None);
        if (filtered.Count != broad.Count ||
            !new HashSet<string>(filtered, StringComparer.OrdinalIgnoreCase).SetEquals(broad))
            throw new InvalidOperationException("Filtered WMI binding query changed read-only results.");
    }
}
