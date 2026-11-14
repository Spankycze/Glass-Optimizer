using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class AiSystemResourceManagerService
{
    internal static IReadOnlyDictionary<string, ServiceControllerStatus>
        GetFastServiceStatusSnapshot(IReadOnlyList<string>? managedServices,
            ISet<string>? unreadableNames = null)
    {
        var states = new Dictionary<string, ServiceControllerStatus>(
            StringComparer.OrdinalIgnoreCase);

        static void ReadStatus(
            Dictionary<string, ServiceControllerStatus> result, string name,
            ISet<string>? unreadableNames)
        {
            // Missing/denied services have no invented Running/Stopped value;
            // failed reads are recorded separately and never authorize writes.
            if (result.ContainsKey(name))
                return;
            try
            {
                using var controller = new ServiceController(name);
                result[name] = controller.Status;
            }
            catch (Exception)
            {
                // Fast SCM status query cannot establish non-installation.
                unreadableNames?.Add(name);
            }
        }

        foreach (string name in FastDemandServiceNames)
            ReadStatus(states, name, unreadableNames);
        if (managedServices != null)
            foreach (string name in managedServices)
                ReadStatus(states, name, unreadableNames);
        return states;
    }

    private static IReadOnlyDictionary<string, ServiceControllerStatus> GetServiceStatusSnapshot(
        ISet<string>? unreadableNames = null)
    {
        var result = new Dictionary<string, ServiceControllerStatus>(StringComparer.OrdinalIgnoreCase);
        ServiceController[] controllers = Array.Empty<ServiceController>();
        try
        {
            controllers = ServiceController.GetServices();
            foreach (ServiceController controller in controllers)
            {
                try
                {
                    result[controller.ServiceName] = controller.Status;
                }
                catch
                {
                    // The controller exists in SCM, but reading its status
                    // failed. Keep it separate from absent service names.
                    unreadableNames?.Add(controller.ServiceName);
                }
            }
            return result;
        }
        finally
        {
            foreach (ServiceController controller in controllers)
                controller.Dispose();
        }
    }

    private bool HasRunningDependents(ServiceController controller, string serviceName)
    {
        string cacheKey = "service-dependents|" + serviceName;
        if (_boolProbeCache.TryGetValue(cacheKey, out var cached) &&
            IsMonotonicProbeFresh(cached.CollectedStamp, Stopwatch.GetTimestamp(),
                Stopwatch.Frequency, TimeSpan.FromSeconds(30)))
            return cached.Value;

        bool running;
        try
        {
            running = AiDependentServiceProbe.HasRunningOrUnknown(
                controller.DependentServices,
                dependent => dependent.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending);
        }
        catch
        {
            running = true;
        }

        StoreBoundedProbe(_boolProbeCache, cacheKey, running, Stopwatch.GetTimestamp());
        return running;
    }

    private static DriverRegistrySnapshot ReadDriverRegistrySnapshot(string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name);
            if (key == null)
                return new(false, false, 0, -1, false);

            string imagePath = Convert.ToString(key.GetValue("ImagePath", string.Empty))?.Trim() ?? string.Empty;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // A missing driver ImagePath has a Windows fallback at load time,
            // but that alone is NOT proof of origin for automatic disabling.
            // Share the conservative binary path policy with service trials.
            bool windowsOwned = !string.IsNullOrWhiteSpace(imagePath) &&
                IsWindowsKernelDriverBinaryPath(Environment.ExpandEnvironmentVariables(imagePath), windows);

            int type = Convert.ToInt32(key.GetValue("Type", 0));
            int start = AiMaintenanceServiceStartPolicy.NormalizeStartValue(
                key.GetValue("Start"));
            using RegistryKey? enumKey = key.OpenSubKey("Enum");
            bool attached = Convert.ToInt32(enumKey?.GetValue("Count", 0) ?? 0) > 0;
            return new(true, windowsOwned, type, start, attached);
        }
        catch
        {
            return new(false, false, 0, -1, true);
        }
    }

    // Path provenance is a conservative prerequisite, NOT proof of a
    // Microsoft signature. Final service mutation also checks SCM state,
    // live dependencies, service start, quiet workload and owned journal.
    // Never treat a Win32 .exe/.com as a kernel-driver image, or a .sys
    // image as a Win32 service binary. Both callers share path normalization,
    // but the last-write gate always requires the correct binary kind.
    internal static bool IsWindowsKernelDriverBinaryPath(
        string? commandLine, string? windowsDirectory) =>
        IsWindowsServiceBinaryPath(commandLine, windowsDirectory, kernelDriver: true);

    internal static bool IsWindowsServiceBinaryPath(
        string? commandLine, string? windowsDirectory, bool kernelDriver = false)
    {
        if (string.IsNullOrWhiteSpace(commandLine) ||
            string.IsNullOrWhiteSpace(windowsDirectory)) return false;
        string text = commandLine.Trim().Replace('/', '\\');
        string executable;
        bool hasArguments;
        if (text.StartsWith('"'))
        {
            int endQuote = text.IndexOf('"', 1);
            if (endQuote < 2) return false;
            executable = text.Substring(1, endQuote - 1);
            if (endQuote + 1 < text.Length && !char.IsWhiteSpace(text[endQuote + 1]))
                return false;
            hasArguments = endQuote + 1 < text.TrimEnd().Length;
        }
        else
        {
            int whitespace = text.IndexOfAny([' ', '\t', '\r', '\n']);
            executable = whitespace >= 0 ? text[..whitespace] : text;
            hasArguments = whitespace >= 0 && text[whitespace..].Length > 0;
        }
        if (executable.Length == 0 || executable.Contains('"') ||
            (kernelDriver && hasArguments) ||
            executable.Split('\\').Any(part => part is "." or ".."))
            return false;

        string windows = windowsDirectory.Trim().Replace('/', '\\').TrimEnd('\\');
        string prefix = windows + @"\System32\";
        string suffix;
        if (executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            suffix = executable[prefix.Length..];
        else if (executable.StartsWith(@"\SystemRoot\System32\", StringComparison.OrdinalIgnoreCase))
            suffix = executable[@"\SystemRoot\System32\".Length..];
        else if (executable.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            suffix = executable[@"System32\".Length..];
        else return false;

        // Refuse drive streams, empty final filename and unexpected binaries.
        // A service that cannot be attributed safely is left untouched.
        if (suffix.Contains(':')) return false;
        string[] segments = suffix.Split('\\');
        if (segments.Any(static part => string.IsNullOrWhiteSpace(part)))
            return false;
        string filename = segments[^1];
        if (filename.Length < 5 || filename.EndsWith(' ')) return false;
        string extension = Path.GetExtension(filename);
        // Executables must be directly in System32; accepting arbitrary
        // System32 subfolders would classify untrusted Temp payloads as OS.
        if (segments.Length == 1)
            return !kernelDriver &&
                   (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".com", StringComparison.OrdinalIgnoreCase));
        // Optional kernel drivers must reside directly in System32\drivers,
        // and a Win32 service cannot pass with a driver .sys image.
        return kernelDriver && segments.Length == 2 &&
               segments[0].Equals("drivers", StringComparison.OrdinalIgnoreCase) &&
               extension.Equals(".sys", StringComparison.OrdinalIgnoreCase);
    }

    // Only own-process (0x10) and shared-process (0x20) Win32 services are
    // candidates. A kernel driver must use the separate driver-specific gate.
    internal static bool IsEligibleWin32ServiceType(int type) => type is 0x10 or 0x20;

    // svchost.exe is only a host; its Parameters\\ServiceDll is the code it
    // actually loads. Keep this conservative and limited to a direct
    // System32 DLL (no Temp, nested folders, ADS, traversal or arguments).
    internal static bool IsWindowsServiceDllPath(string? rawPath, string? windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath) ||
            string.IsNullOrWhiteSpace(windowsDirectory)) return false;
        string path = rawPath.Trim().Replace('/', '\\');
        if (path.StartsWith('"'))
        {
            if (path.Length < 3 || path[^1] != '"') return false;
            path = path[1..^1];
        }
        if (path.IndexOfAny(['"', ' ', '\t', '\r', '\n', ':']) >= 0)
        {
            // A drive colon is valid only in the canonical drive prefix.
            bool drivePrefix = path.Length > 3 && path[1] == ':' &&
                char.IsLetter(path[0]) && path[2] == '\\';
            if (!drivePrefix || path[3..].IndexOfAny(['"', ' ', '\t', '\r', '\n', ':']) >= 0)
                return false;
        }
        if (path.Split('\\').Any(part => part is "" or "." or ".."))
        {
            // NT \\SystemRoot begins with one separator, which is expected.
            if (!path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ||
                path[1..].Split('\\').Any(part => part is "" or "." or ".."))
                return false;
        }
        string root = windowsDirectory.Trim().Replace('/', '\\').TrimEnd('\\');
        string prefix = root + @"\System32\";
        string dllName;
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            dllName = path[prefix.Length..];
        else if (path.StartsWith(@"\SystemRoot\System32\", StringComparison.OrdinalIgnoreCase))
            dllName = path[@"\SystemRoot\System32\".Length..];
        else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            dllName = path[@"System32\".Length..];
        else return false;
        return dllName.Length > 4 && !dllName.Contains('\\') &&
            dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !dllName.EndsWith(" .dll", StringComparison.OrdinalIgnoreCase);
    }

    // Parse ONLY the executable token, not the arguments. A service with
    // "svchost.exe" inside an unrelated flag must not be misclassified as
    // the svchost host, nor may an argument masquerade as an unsupported
    // DLL loader. The path admission guard separately validates location.
    internal static string GetServiceExecutableName(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return string.Empty;
        string text = imagePath.Trim().Replace('/', '\\');
        string executable;
        if (text.StartsWith('"'))
        {
            int closing = text.IndexOf('"', 1);
            if (closing <= 1 ||
                (closing + 1 < text.Length && !char.IsWhiteSpace(text[closing + 1])))
                return string.Empty;
            executable = text[1..closing];
        }
        else
        {
            int boundary = text.IndexOfAny([' ', '\t', '\r', '\n']);
            executable = boundary < 0 ? text : text[..boundary];
        }
        return executable.Contains('"') ? string.Empty : Path.GetFileName(executable);
    }

    internal static bool RequiresServiceDllReadback(string imagePath) =>
        string.Equals(GetServiceExecutableName(imagePath), "svchost.exe",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnsupportedDllLauncher(string imagePath)
    {
        string fileName = GetServiceExecutableName(imagePath);
        return fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dllhost.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWindowsServiceAdmissionPath(
        string imagePath, string? serviceDll, string windowsDirectory, int serviceType) =>
        IsEligibleWin32ServiceType(serviceType) &&
        IsWindowsServiceBinaryPath(imagePath, windowsDirectory) &&
        !IsUnsupportedDllLauncher(imagePath) &&
        (!RequiresServiceDllReadback(imagePath) ||
         IsWindowsServiceDllPath(serviceDll, windowsDirectory));

    internal static string RunServiceModuleAdmissionSmoke()
    {
        const string root = @"C:\Windows";
        const string svchost = @"C:\Windows\System32\svchost.exe -k netsvcs";
        const string normal = @"C:\Windows\System32\ExampleService.exe";
        const string goodDll = @"C:\Windows\System32\ExampleService.dll";
        // Named assertions make the first rejected/unsafe case actionable
        // without weakening the regression or touching real service state.
        var cases = new (string Label, bool Expected, bool Actual)[]
        {
            ("valid svchost", true, IsWindowsServiceAdmissionPath(svchost, goodDll, root, 0x20)),
            ("valid NT DLL", true, IsWindowsServiceAdmissionPath(svchost,
                @"\SystemRoot\System32\ExampleService.dll", root, 0x20)),
            ("own process", true, IsWindowsServiceAdmissionPath(normal, null, root, 0x10)),
            ("shared exe", true, IsWindowsServiceAdmissionPath(normal, null, root, 0x20)),
            ("quoted svchost executable", true, IsWindowsServiceAdmissionPath(
                "\"C:\\Windows\\System32\\svchost.exe\" -k netsvcs", goodDll, root, 0x20)),
            ("svchost word only in arguments", true, IsWindowsServiceAdmissionPath(
                normal + " --description=svchost.exe", null, root, 0x10)),
            ("loader word only in arguments", true, IsWindowsServiceAdmissionPath(
                normal + " --description=rundll32.exe", null, root, 0x10)),
            ("quoted regular executable", true, IsWindowsServiceAdmissionPath(
                "\"C:\\Windows\\System32\\ExampleService.exe\" --note=dllhost.exe",
                null, root, 0x10)),
            ("empty DLL", false, IsWindowsServiceAdmissionPath(svchost, string.Empty, root, 0x20)),
            ("driver as regular executable", false, IsWindowsServiceAdmissionPath(normal, null, root, 1)),
            ("missing DLL", false, IsWindowsServiceAdmissionPath(svchost, null, root, 0x20)),
            ("temp payload", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\Temp\hijack.dll", root, 0x20)),
            ("nested payload", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\Temp\hijack.dll", root, 0x20)),
            ("module traversal", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\..\Temp\hijack.dll", root, 0x20)),
            ("alternate stream", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\hijack.dll:alternate", root, 0x20)),
            ("wrong extension", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\hijack.exe", root, 0x20)),
            ("DLL arguments", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\hijack.dll -x", root, 0x20)),
            ("driver type", false, IsWindowsServiceAdmissionPath(svchost, goodDll, root, 1)),
            ("rundll32", false, IsWindowsServiceAdmissionPath(
                @"C:\Windows\System32\rundll32.exe payload.dll,X", null, root, 0x10)),
            ("dllhost", false, IsWindowsServiceAdmissionPath(
                @"C:\Windows\System32\dllhost.exe /Processid:123", null, root, 0x10))
        };
        foreach (var test in cases)
            if (test.Actual != test.Expected)
                throw new InvalidOperationException(
                    $"AI service module {test.Label}: expected={test.Expected}, actual={test.Actual}");
        return "AI_SERVICE_MODULE_ADMISSION_SMOKE_OK hostDllMandatory=true " +
               "injectionBlocked=true serviceTypes=true windowsWrites=0";
    }

    private bool IsWindowsOwnedServiceEntry(string name, bool requireFresh = false)
    {
        string cacheKey = "windows-owned|" + name;
        if (MayReuseProbeCache(requireFresh) &&
            _boolProbeCache.TryGetValue(cacheKey, out var cached) &&
            IsMonotonicProbeFresh(cached.CollectedStamp, Stopwatch.GetTimestamp(),
                Stopwatch.Frequency, TimeSpan.FromMinutes(10)))
            return cached.Value;

        bool owned = false;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name);
            if (key != null)
            {
                string imagePath = Convert.ToString(key.GetValue("ImagePath", string.Empty))?.Trim() ?? string.Empty;
                // Owning a service is not proven by merely living somewhere
                // under C:\Windows. At the last write boundary, require a
                // plausible System32 binary path and reject path traversal.
                // Blank ImagePath is not sufficient authorization to stop a
                // service, even when its name matches a known driver.
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    string expanded = Environment.ExpandEnvironmentVariables(imagePath);
                    string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    int serviceType = Convert.ToInt32(key.GetValue("Type", 0));
                    string library = string.Empty;
                    if (RequiresServiceDllReadback(expanded))
                    {
                        using RegistryKey? parameters = key.OpenSubKey("Parameters");
                        library = Environment.ExpandEnvironmentVariables(
                            Convert.ToString(parameters?.GetValue(
                                "ServiceDll", string.Empty))?.Trim() ?? string.Empty);
                    }
                    owned = IsWindowsServiceAdmissionPath(
                        expanded, library, windows, serviceType);
                }
            }
        }
        catch
        {
            owned = false;
        }

        StoreBoundedProbe(_boolProbeCache, cacheKey, owned, Stopwatch.GetTimestamp());
        return owned;
    }

    private static int ReadServiceStartType(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + name);
        return AiMaintenanceServiceStartPolicy.NormalizeStartValue(
            key?.GetValue("Start"));
    }

    private static void WriteServiceStartType(string name, int value)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + name, writable: true)
            ?? throw new InvalidOperationException("Nelze otevĹ™Ă­t konfiguraci sluĹľby " + name);
        key.SetValue("Start", value, RegistryValueKind.DWord);
        int readback = Convert.ToInt32(key.GetValue("Start", -1));
        if (readback != value)
            throw new InvalidOperationException($"{name} nepotvrdil Start={value}.");
    }
}
