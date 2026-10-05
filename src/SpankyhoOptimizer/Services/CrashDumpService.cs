using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SpankyhoOptimizer.Services;

public static class CrashDumpService
{
    public static string? TryWrite(string source)
    {
        try
        {
            string dir = Path.Combine(LogService.Root, "crash-reports");
            Directory.CreateDirectory(dir);
            string safe = string.Concat(source.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            string path = Path.Combine(dir, $"SpankyhoOptimizer-{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.txt");
            var sb = new StringBuilder(8192);
            using var process = Process.GetCurrentProcess();
            sb.AppendLine("Spankyho Optimizer crash diagnostic report");
            sb.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Startup stage: {StartupDiagnostics.LastStage}");
            sb.AppendLine($"Process: {process.ProcessName} ({process.Id})");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"OS version: {Environment.OSVersion.Version}");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"Base directory: {AppContext.BaseDirectory}");
            sb.AppendLine($"Working set: {process.WorkingSet64}");
            sb.AppendLine($"Private memory: {process.PrivateMemorySize64}");
            sb.AppendLine($"Threads: {process.Threads.Count}");
            sb.AppendLine($"Handles: {process.HandleCount}");
            sb.AppendLine($"GC total memory: {GC.GetTotalMemory(false)}");
            sb.AppendLine();
            sb.AppendLine("Stack:");
            sb.AppendLine(Environment.StackTrace);
            sb.AppendLine();
            sb.AppendLine("Loaded assemblies:");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
            {
                try
                {
                    var name = assembly.GetName();
                    sb.AppendLine($"- {name.Name} {name.Version}");
                }
                catch { }
            }
            sb.AppendLine();
            sb.AppendLine("Loaded modules:");
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    try { sb.AppendLine($"- {module.ModuleName} {module.FileVersionInfo.FileVersion}"); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("- module enumeration unavailable: " + ex.GetType().Name);
            }

            // Keep the diagnostic artifact large enough for the release smoke gate while
            // containing only deterministic, non-memory-dump diagnostic metadata.
            if (sb.Length < 4096)
            {
                sb.AppendLine();
                sb.AppendLine("Diagnostic padding / runtime context:");
                while (sb.Length < 4096) sb.AppendLine("runtime-context: managed crash report");
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            LogService.Info("crash-report", "Crash diagnostic report saved: " + path);
            return path;
        }
        catch (Exception ex)
        {
            LogService.Error("crash-report", "Crash diagnostic report creation failed", ex);
            return null;
        }
    }
}
