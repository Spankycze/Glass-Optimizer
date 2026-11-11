using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Optional, user-confirmed cleanup of old files in the current user's TEMP folder.
/// Never follows reparse-point directories, deletes directory trees, or touches files
/// written within the last 24 hours. Does not alter Windows settings.
/// </summary>
public static class SafeTempCleanupService
{
    private const int MaximumFiles = 30000;
    private const int MaximumDirectories = 5000;
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);

    public sealed record Report(
        int VisitedFiles, int EligibleFiles, int RemovedFiles, int SkippedFiles,
        int FailedFiles, long EligibleBytes, long RemovedBytes, bool Truncated);

    public static Task<Report> InspectUserTempAsync(CancellationToken token = default) =>
        Task.Run(() => Scan(ResolveUserTempRoot(), false, MinimumAge, token), token);

    public static Task<Report> CleanUserTempAsync(CancellationToken token = default) =>
        Task.Run(() => Scan(ResolveUserTempRoot(), true, MinimumAge, token), token);

    private static string ResolveUserTempRoot() =>
        ValidateUserTempScope(Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string ValidateUserTempScope(string candidateTemp, string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData) || string.IsNullOrWhiteSpace(candidateTemp))
            throw new InvalidOperationException("User TEMP scope cannot be determined.");
        string local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateTemp));
        // Fail closed for administrator/system-wide or redirected TEMP locations.
        // Never clean Windows\\Temp or a drive root through this UI.
        if (!temp.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TEMP není v uživatelském LocalAppData. Automatický úklid byl odmítnut.");
        return temp;
    }

    // Internal to allow an isolated disposable-fixture regression test without
    // ever cleaning the real user's TEMP directory during tests.
    internal static Report Scan(string root, bool delete, TimeSpan minimumAge, CancellationToken token)
    {
        if (minimumAge < TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(minimumAge), "Do not clean recently modified files.");

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized) ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing a drive root.");
        var baseDirectory = new DirectoryInfo(normalized);
        if (!baseDirectory.Exists || HasUnsafeAttributes(baseDirectory.Attributes) ||
            IsProtectedCacheDirectoryName(normalized))
            throw new InvalidOperationException("TEMP root is missing, protected cache, or is a reparse point.");

        string scope = normalized + Path.DirectorySeparatorChar;
        DateTime cutoff = DateTime.UtcNow - minimumAge;
        var directories = new Stack<string>();
        directories.Push(normalized);
        int dirs = 0, visited = 0, eligible = 0, removed = 0, skipped = 0, failed = 0;
        long candidateBytes = 0, removedBytes = 0;
        bool truncated = false;

        while (directories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (++dirs > MaximumDirectories) { truncated = true; break; }
            string directory = directories.Pop();
            if (!IsInsideScope(directory, normalized, scope)) { skipped++; continue; }

            try
            {
                var current = new DirectoryInfo(directory);
                if (!current.Exists || HasUnsafeAttributes(current.Attributes))
                {
                    skipped++;
                    continue;
                }

                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > MaximumFiles) { truncated = true; break; }
                    if (!IsInsideScope(entry, normalized, scope)) { skipped++; continue; }

                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if (HasUnsafeAttributes(attributes)) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            // Preserve browser, graphics-driver and DirectX shader
                            // caches even when files inside are older than 24h.
                            if (IsProtectedCacheDirectoryName(entry)) { skipped++; continue; }
                            directories.Push(entry);
                            continue;
                        }

                        var file = new FileInfo(entry);
                        if ((attributes & FileAttributes.ReadOnly) != 0 ||
                            file.LastWriteTimeUtc > cutoff)
                        {
                            skipped++;
                            continue;
                        }

                        long size = file.Length;
                        eligible++;
                        candidateBytes = AddSaturated(candidateBytes, size);
                        if (!delete) continue;

                        // Re-evaluate right before deletion to avoid stale scan results.
                        file.Refresh();
                        if (!file.Exists || HasUnsafeAttributes(file.Attributes) ||
                            (file.Attributes & FileAttributes.ReadOnly) != 0 ||
                            file.LastWriteTimeUtc > cutoff)
                        {
                            skipped++;
                            continue;
                        }
                        File.Delete(entry);
                        if (!File.Exists(entry))
                        {
                            removed++;
                            removedBytes = AddSaturated(removedBytes, size);
                        }
                        else failed++;
                    }
                    catch (IOException) { failed++; }
                    catch (UnauthorizedAccessException) { failed++; }
                    catch (System.Security.SecurityException) { failed++; }
                }
            }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
            catch (System.Security.SecurityException) { failed++; }
            if (truncated) break;
        }

        return new Report(visited, eligible, removed, skipped, failed, candidateBytes, removedBytes, truncated);
    }

    // Exact directory names only: do not broadly skip ordinary folders named "cache".
    // Preserve cached shader bytecode, DirectX driver caches and browser GPU caches
    // in both the preview and the actual deletion traversal.
    private static bool IsProtectedCacheDirectoryName(string path)
    {
        ReadOnlySpan<char> name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Equals("ShaderCache", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("GPUCache", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("D3DSCache", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("DXCache", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("GLCache", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NV_Cache", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("DawnCache", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("GrShaderCache", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Code Cache", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasUnsafeAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0;

    private static bool IsInsideScope(string path, string root, string scope)
    {
        string normalized = Path.GetFullPath(path);
        return normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(scope, StringComparison.OrdinalIgnoreCase);
    }

    private static long AddSaturated(long x, long y) => y > 0 && x > long.MaxValue - y ? long.MaxValue : x + y;
}
