using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class CrashReportService
{
    private static readonly string[] ReportFiles = { "optimizer.log", "operation-journal.jsonl", "section-state.json", "startup-state.json", "tweak-batches.json" };
    public static Task<string> CreateAsync(string? section = null, string? errorId = null,
        Exception? exception = null, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        string dir = Path.Combine(LogService.Root, "crash-reports");
        Directory.CreateDirectory(dir);
        PruneOldReports(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string zipPath = Path.Combine(dir, $"SpankyhoOptimizer_CrashReport_{stamp}.zip");
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        AddText(zip, "report.txt", BuildReport(section, errorId, exception));
        foreach (string name in ReportFiles)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(LogService.Root, name);
            if (File.Exists(path)) zip.CreateEntryFromFile(path, name, CompressionLevel.Fastest);
        }
        return zipPath;
    }, token);
    private static void PruneOldReports(string dir)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(30);
            var files = Directory.EnumerateFiles(dir, "SpankyhoOptimizer_CrashReport_*.zip")
                .Select(path => new FileInfo(path))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToArray();

            for (int i = 0; i < files.Length; i++)
            {
                if (i < 10 && files[i].LastWriteTimeUtc >= cutoff) continue;
                try { files[i].Delete(); } catch { }
            }
        }
        catch (Exception ex) { LogService.Error("crash-report", "Crash report prune failed", ex); }
    }

    private static string BuildReport(string? section, string? errorId, Exception? exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Spankyho Optimizer local crash report");
        sb.AppendLine("Time: " + DateTimeOffset.Now);
        sb.AppendLine("Version: " + AppUpdateService.CurrentVersion);
        sb.AppendLine("OS: " + Environment.OSVersion.VersionString);
        sb.AppendLine("Framework: " + Environment.Version);
        sb.AppendLine("Architecture: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        sb.AppendLine("SafeMode: " + CrashLoopGuardService.SafeMode);
        sb.AppendLine("Section: " + (section ?? "n/a"));
        sb.AppendLine("ErrorId: " + (errorId ?? "n/a"));
        sb.AppendLine("StartupStage: " + StartupDiagnostics.LastStage);
        if (exception != null)
        {
            sb.AppendLine();
            sb.AppendLine(exception.ToString());
        }
        return sb.ToString();
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
