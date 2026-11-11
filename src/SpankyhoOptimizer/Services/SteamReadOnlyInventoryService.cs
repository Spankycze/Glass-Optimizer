using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// On-demand Steam presence and process snapshot only. No Steam launch,
/// configuration edits, cache deletion, or library traversal.
/// </summary>
internal static class SteamReadOnlyInventoryService
{
    internal sealed record Snapshot(
        bool Installed, string? InstallationDirectory, bool LibraryIndexPresent,
        int ProcessCount, long WorkingSetBytes, long PrivateBytes,
        int ThreadCount, int HandleCount);

    internal static Task<Snapshot> InspectAsync(CancellationToken token = default) =>
        Task.Run(() => Inspect(token), token);

    private static Snapshot Inspect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        string? machinePath = null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = root.OpenSubKey(@"SOFTWARE\Valve\Steam");
            machinePath = key?.GetValue("InstallPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(steamPath)) paths.Add(steamPath);
        if (!string.IsNullOrWhiteSpace(machinePath)) paths.Add(machinePath);
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(x86)) paths.Add(Path.Combine(x86, "Steam"));
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) paths.Add(Path.Combine(programFiles, "Steam"));

        var installation = FindExistingInstall(paths);
        int count = 0, threads = 0, handles = 0;
        long workingSet = 0, privateBytes = 0;
        foreach (string name in new[] { "steam", "steamwebhelper" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        count++;
                        workingSet = SaturatedAdd(workingSet, process.WorkingSet64);
                        privateBytes = SaturatedAdd(privateBytes, process.PrivateMemorySize64);
                        threads += process.Threads.Count;
                        handles += Math.Max(0, process.HandleCount);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }
        return new Snapshot(
            installation != null, installation,
            installation != null && File.Exists(Path.Combine(installation, "steamapps", "libraryfolders.vdf")),
            count, workingSet, privateBytes, threads, handles);
    }

    // Accept only installed steam.exe in a local absolute directory.
    // Treat paths from the registry as untrusted; never open UNC shares here.
    internal static string? FindExistingInstall(IEnumerable<string> candidateDirectories)
    {
        foreach (string candidate in candidateDirectories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    !Path.IsPathFullyQualified(candidate) ||
                    candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                    candidate.StartsWith(@"//", StringComparison.Ordinal))
                    continue;
                string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                if (!File.Exists(Path.Combine(directory, "steam.exe"))) continue;
                return directory;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }
        return null;
    }

    private static long SaturatedAdd(long existing, long next)
    {
        if (next <= 0) return existing;
        return existing > long.MaxValue - next ? long.MaxValue : existing + next;
    }
}
