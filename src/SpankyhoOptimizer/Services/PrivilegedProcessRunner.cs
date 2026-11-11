using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class PrivilegedProcessRunner
{
    private static readonly Regex GuidRegex = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static Task<ProcessResult> RunAsync(
        string file,
        string arguments,
        TimeSpan timeout,
        CancellationToken token = default)
    {
        ValidateRequest(file, arguments);

        if (WorkerExecutionContext.IsWorker || WorkerPrivilegePolicy.IsAdministrator())
            return ProcessRunner.RunAsync(file, arguments, timeout, token);

        var request = new PrivilegedProcessRequest(
            file,
            arguments,
            Math.Clamp((int)Math.Ceiling(timeout.TotalSeconds), 1, 3600));

        return WorkerClientService.RunAsync<ProcessResult>(
            "process-run",
            WorkerPayloads.Encode(request),
            timeout + TimeSpan.FromSeconds(20),
            token);
    }

    internal static void ValidateRequest(string file, string arguments)
    {
        string name = Path.GetFileName(file).ToLowerInvariant();
        if (!IsAllowedExecutable(name))
            throw new InvalidOperationException("Privileged executable is not allowed: " + file);
        if (arguments.IndexOf((char)13) >= 0 || arguments.IndexOf((char)10) >= 0 || arguments.IndexOf((char)0) >= 0)
            throw new InvalidOperationException("Privileged process arguments contain invalid control characters.");
        if (!IsAllowedArguments(name, arguments.Trim()))
            throw new InvalidOperationException($"Privileged arguments are outside the approved contract: {name} {arguments}");
    }

    internal static bool IsAllowedExecutable(string file)
    {
        string name = Path.GetFileName(file).ToLowerInvariant();
        return name is
            "netsh.exe" or
            "powercfg.exe" or
            "pnputil.exe" or
            "dism.exe" or
            "sfc.exe" or
            "ipconfig.exe" or
            "usoclient.exe";
    }

    private static bool IsAllowedArguments(string executable, string args) => executable switch
    {
        "netsh.exe" => IsAllowedNetsh(args),
        "powercfg.exe" => IsAllowedPowerCfg(args),
        "pnputil.exe" => IsAllowedPnpUtil(args),
        "dism.exe" => args.Equals("/Online /Cleanup-Image /RestoreHealth", StringComparison.OrdinalIgnoreCase),
        "sfc.exe" => args.Equals("/scannow", StringComparison.OrdinalIgnoreCase),
        "ipconfig.exe" => args.Equals("/flushdns", StringComparison.OrdinalIgnoreCase),
        "usoclient.exe" => args.Equals("StartScan", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsAllowedNetsh(string args)
    {
        if (args.StartsWith("dnsclient set dnsserver ", StringComparison.OrdinalIgnoreCase) ||
            args.StartsWith("dnsclient add dnsserver ", StringComparison.OrdinalIgnoreCase))
            return args.Contains(" name=\"", StringComparison.OrdinalIgnoreCase) &&
                   args.Contains(" address=", StringComparison.OrdinalIgnoreCase);

        return Regex.IsMatch(
            args,
            "^interface ipv[46] set subinterface \\\"[^\\\"\\r\\n]+\\\" mtu=[0-9]{3,4} store=persistent$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }

    private static bool IsAllowedPowerCfg(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0].Equals("/setactive", StringComparison.OrdinalIgnoreCase))
            return IsPowerSchemeToken(parts[1]);

        if (parts.Length == 2 && parts[0].Equals("/delete", StringComparison.OrdinalIgnoreCase))
            return GuidRegex.IsMatch(parts[1]);

        if (parts.Length == 2 && parts[0].Equals("/duplicatescheme", StringComparison.OrdinalIgnoreCase))
            return parts[1].Equals("SCHEME_BALANCED", StringComparison.OrdinalIgnoreCase);

        if (parts.Length == 5 && parts[0].Equals("/setacvalueindex", StringComparison.OrdinalIgnoreCase))
            return IsPowerSchemeToken(parts[1]) &&
                   IsPowerSettingToken(parts[2]) &&
                   IsPowerSettingToken(parts[3]) &&
                   int.TryParse(parts[4], out _);

        if (args.StartsWith("/changename ", StringComparison.OrdinalIgnoreCase))
        {
            int firstSpace = args.IndexOf(' ');
            int secondSpace = args.IndexOf(' ', firstSpace + 1);
            if (secondSpace <= firstSpace) return false;
            string guid = args[(firstSpace + 1)..secondSpace];
            string planName = args[(secondSpace + 1)..].Trim();
            return GuidRegex.IsMatch(guid) &&
                   planName.Equals("\"Spankyho Performance\"", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsPowerSchemeToken(string token) =>
        GuidRegex.IsMatch(token) ||
        token.Equals("SCHEME_CURRENT", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("SCHEME_BALANCED", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerSettingToken(string token) =>
        GuidRegex.IsMatch(token) ||
        Regex.IsMatch(token, "^[A-Za-z0-9_\\-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool IsAllowedPnpUtil(string args)
    {
        if (Regex.IsMatch(
                args,
                "^/export-driver \\\"oem[0-9]+\\.inf\\\" \\\"[^\\\"\\r\\n]+\\\"$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
            return true;

        return Regex.IsMatch(
            args,
            "^/remove-device \\\"(?:USB|HID)\\\\\\\\[^\\\"\\r\\n]+\\\"$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}
