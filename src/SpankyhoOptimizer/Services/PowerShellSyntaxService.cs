using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SpankyhoOptimizer.Services;

// Parse PS7 source in-process without launching powershell.exe or pwsh.exe.
// Does not evaluate the script or run its commands.
internal static class PowerShellSyntaxService
{
    internal static void ValidateFile(string path)
    {
        string assemblyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "System.Management.Automation.dll");
        if (!File.Exists(assemblyPath))
            throw new InvalidOperationException("PowerShell 7 parser assembly is unavailable.");
        var assembly = Assembly.LoadFrom(assemblyPath);
        var parser = assembly.GetType("System.Management.Automation.Language.Parser", throwOnError: true)!;
        var method = parser.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "ParseFile" && m.GetParameters().Length == 3);
        object?[] args = { path, null, null };
        method.Invoke(null, args);
        if (args[2] is Array errors && errors.Length > 0)
        {
            var first = errors.GetValue(0);
            string message = first?.GetType().GetProperty("Message")?.GetValue(first)?.ToString()
                ?? "PowerShell syntax error.";
            throw new InvalidOperationException("PowerShell script syntax invalid: " + message);
        }
    }
}
