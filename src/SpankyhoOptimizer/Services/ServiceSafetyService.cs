using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public enum ServiceSafetyLevel { Safe, Conditional, Critical }
public sealed record ServiceSafetyInfo(ServiceSafetyLevel Level, string Reason);

public static class ServiceSafetyService
{
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "PlugPlay", "Power", "EventLog", "Winmgmt",
        "Dhcp", "Dnscache", "NlaSvc", "NSI", "LanmanWorkstation", "ProfSvc",
        "UserManager", "Schedule", "CryptSvc", "Appinfo", "BFE", "SamSs", "LSM",
        "BrokerInfrastructure", "CoreMessagingRegistrar", "StateRepository", "SystemEventsBroker", "gpsvc"
    };

    private static readonly HashSet<string> Conditional = new(StringComparer.OrdinalIgnoreCase)
    {
        "WSearch", "SysMain", "Spooler", "BluetoothUserService", "bthserv", "XboxGipSvc",
        "XblAuthManager", "XblGameSave", "WpnService", "WpnUserService", "WbioSrvc", "TabletInputService",
        "OneSyncSvc", "CDPUserSvc", "UserDataSvc", "UnistoreSvc", "cbdhsvc", "DevicesFlowUserSvc",
        "Audiosrv", "AudioEndpointBuilder"
    };

    private static readonly ServiceSafetyInfo CriticalInfo = new(ServiceSafetyLevel.Critical, "Kritická Windows služba; Optimizer blokuje její zakázání.");
    private static readonly ServiceSafetyInfo ConditionalInfo = new(ServiceSafetyLevel.Conditional, "Volitelná služba navázaná na konkrétní Windows funkci nebo hardware.");
    private static readonly ServiceSafetyInfo SafeInfo = new(ServiceSafetyLevel.Safe, "Služba není v interní databázi kritických komponent.");

    public static ServiceSafetyInfo Classify(string name)
    {
        string normalized = NormalizeServiceName(name);
        if (Critical.Contains(normalized)) return CriticalInfo;
        if (Conditional.Contains(normalized)) return ConditionalInfo;
        return SafeInfo;
    }

    private static string NormalizeServiceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        int split = name.LastIndexOf('_');
        if (split > 0 && split + 1 < name.Length)
        {
            string suffix = name[(split + 1)..];
            if (suffix.Length is >= 4 and <= 8 && suffix.All(Uri.IsHexDigit)) return name[..split];
        }
        return name;
    }

    public static bool CanDisable(string name) => Classify(name).Level != ServiceSafetyLevel.Critical;

    public static int? ReadStartType(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        object? value = key?.GetValue("Start");
        return value == null ? null : Convert.ToInt32(value);
    }

    public static string ExplainBlock(string name) =>
        $"Služba {name} je chráněná jako kritická pro stabilitu Windows a Optimizer ji nedovolí zakázat.";
}
