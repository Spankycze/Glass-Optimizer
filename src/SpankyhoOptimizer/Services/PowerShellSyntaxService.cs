using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Parse PS7 source without evaluating it. In-process parsing is preferred; when the
// installed PowerShell 7 parser cannot be loaded (for example a newer runtime), the
// syntax check runs through a read-only pwsh parse instead.
internal static class PowerShellSyntaxService
{
    private const string ParserAssemblyName = "System.Management.Automation.dll";
    private const string ParseScript =
        "$errors = $null; $null = [System.Management.Automation.Language.Parser]::ParseFile(" +
        "$env:SPANKY_PS_PARSE_TARGET, [ref]$null, [ref]$errors); " +
        "if ($errors -and $errors.Count) { $errors[0].Message } else { 'SPANKY_PARSE_OK' }";

    private static string? _resolvedAssemblyPath;

    internal static async Task ValidateFileAsync(string path)
    {
        string? assemblyPath = ResolveParserAssembly();
        if (assemblyPath is not null && TryParseInProcess(assemblyPath, path, out string? syntaxError))
        {
            if (syntaxError is null)
                return;
            throw new InvalidOperationException("PowerShell script syntax invalid: " + syntaxError);
        }

        await ValidateWithPwshAsync(path).ConfigureAwait(false);
    }

    private static bool TryParseInProcess(string assemblyPath, string path, out string? syntaxError)
    {
        syntaxError = null;
        Assembly assembly;
        MethodInfo? method;
        try
        {
            assembly = Assembly.LoadFrom(assemblyPath);
            Type parser = assembly.GetType("System.Management.Automation.Language.Parser", throwOnError: true)!;
            method = parser.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ParseFile" && m.GetParameters().Length == 3);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException
                                       or TypeLoadException or MissingMethodException or TypeInitializationException)
        {
            return false;
        }

        if (method is null)
            return false;

        object?[] arguments = { path, null, null };
        method.Invoke(null, arguments);
        if (arguments[2] is Array errors && errors.Length > 0)
        {
            object? first = errors.GetValue(0);
            syntaxError = first?.GetType().GetProperty("Message")?.GetValue(first)?.ToString()
                ?? "PowerShell syntax error.";
        }

        return true;
    }

    private static async Task ValidateWithPwshAsync(string path)
    {
        string pwsh = FindOnPath("pwsh.exe")
            ?? throw new InvalidOperationException("PowerShell 7 parser assembly is unavailable.");
        string arguments = "-NoLogo -NoProfile -NonInteractive -Command \"" + ParseScript + "\"";
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPANKY_PS_PARSE_TARGET"] = Path.GetFullPath(path),
        };

        ProcessResult result = await ProcessRunner.RunAsync(
            pwsh, arguments, TimeSpan.FromSeconds(30), CancellationToken.None,
            logNonZeroExitAsError: false, caller: nameof(PowerShellSyntaxService),
            environmentVariables: environment).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException("PowerShell 7 parser failed: " + result.StdErr.Trim());
        if (!string.Equals(result.StdOut.Trim(), "SPANKY_PARSE_OK", StringComparison.Ordinal))
            throw new InvalidOperationException("PowerShell script syntax invalid: " + result.StdOut.Trim());
    }

    // PowerShell 7 ships as an MSI (Program Files\PowerShell\7) or as a Store/MSIX package
    // (Program Files\WindowsApps\microsoft.powershell_<version>_*), so probe both.
    private static string? ResolveParserAssembly()
    {
        if (_resolvedAssemblyPath is not null && File.Exists(_resolvedAssemblyPath))
            return _resolvedAssemblyPath;

        foreach (string candidate in EnumerateCandidates())
        {
            if (!File.Exists(candidate))
                continue;

            // Store packages live under WindowsApps, which denies assembly loading in place.
            string? usable = candidate.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                ? Materialize(candidate)
                : candidate;
            if (usable is null)
                continue;

            _resolvedAssemblyPath = usable;
            return usable;
        }

        return null;
    }

    private static string? Materialize(string source)
    {
        try
        {
            string cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpankyhoOptimizer", "Cache", "PowerShellParser");
            Directory.CreateDirectory(cacheRoot);

            string package = Path.GetFileName(Path.GetDirectoryName(source) ?? "powershell");
            string target = Path.Combine(cacheRoot, package + "-" + Path.GetFileName(source));
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
                return target;

            File.Copy(source, target, true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "PowerShell", "7", ParserAssemblyName);

        foreach (string root in StoreRoots(programFiles))
        {
            foreach (string directory in VersionedDirectories(root, "microsoft.powershell*"))
                yield return Path.Combine(directory, ParserAssemblyName);
        }

        // A pwsh.exe on PATH sits next to the parser assembly in MSI and portable installs.
        string? onPath = FindOnPath("pwsh.exe");
        if (onPath is not null)
            yield return Path.Combine(Path.GetDirectoryName(onPath)!, ParserAssemblyName);
    }

    private static IEnumerable<string> StoreRoots(string programFiles)
    {
        yield return Path.Combine(programFiles, "WindowsApps");

        string? modulePath = Environment.GetEnvironmentVariable("PSModulePath");
        if (string.IsNullOrWhiteSpace(modulePath))
            yield break;

        foreach (string entry in modulePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith(Path.Combine(programFiles, "WindowsApps"), StringComparison.OrdinalIgnoreCase))
                continue;
            string? root = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(root))
                yield return root!;
        }
    }

    private static IEnumerable<string> VersionedDirectories(string root, string pattern)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        foreach (string directory in directories.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            yield return directory;
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;
            string candidate;
            try
            {
                candidate = Path.Combine(trimmed, fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
