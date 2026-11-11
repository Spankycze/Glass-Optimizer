using System;
using System.IO;
using System.Linq;

namespace SpankyhoOptimizer.Services;

public static class DataRootCleanupService
{
    private const string CanonicalRootName = "SpankyhoOptimizer";

    public static int CleanupUserDataRoots()
    {
        int removed = 0;
        foreach (Environment.SpecialFolder folder in new[]
        {
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData
        })
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            foreach (string dir in Directory.EnumerateDirectories(root, "Spankyho*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(dir);
                try
                {
                    if (!TryRemoveSafeLegacyDirectory(dir, name))
                        continue;

                    removed++;
                    LogService.Info("startup", $"DATA_ROOT_CLEANUP emptyLegacyRemoved={dir}");
                }
                catch (Exception ex)
                {
                    LogService.Error("startup", $"Data root cleanup failed path={dir}", ex);
                }
            }
        }

        return removed;
    }

    internal static string RunRecognitionSmoke()
    {
        // Only disposable temp fixtures. The test must NEVER invoke cleanup
        // against the caller's actual AppData or alter user-owned directories.
        string root = Path.Combine(Path.GetTempPath(),
            "SpankyhoOptimizer-cleanup-smoke-" + Guid.NewGuid().ToString("N"));
        string emptyLegacy = Path.Combine(root, "SpankyhoOptimizerLegacy");
        string nonEmptyLegacy = Path.Combine(root, "SpankyhoOptimizerOld");
        string prefixedUnrelated = Path.Combine(root, "SpankyhoOptimizerPhotos");
        string markedUnrelated = Path.Combine(root, "SpankyhoOtherApp");
        string canonical = Path.Combine(root, CanonicalRootName);
        Directory.CreateDirectory(emptyLegacy);
        Directory.CreateDirectory(nonEmptyLegacy);
        Directory.CreateDirectory(prefixedUnrelated);
        Directory.CreateDirectory(markedUnrelated);
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(nonEmptyLegacy, "preferences.json"), "{\"keep\":true}");
        File.WriteAllText(Path.Combine(prefixedUnrelated, "private.txt"), "never delete");
        File.WriteAllText(Path.Combine(markedUnrelated, "terminal-bg.png"), "user artwork");
        File.WriteAllText(Path.Combine(canonical, "settings.json"), "keep");
        try
        {
            bool emptyAllowed = IsRecognizedLegacyRoot(emptyLegacy, Path.GetFileName(emptyLegacy));
            bool nonEmptyRejected = !IsRecognizedLegacyRoot(nonEmptyLegacy, Path.GetFileName(nonEmptyLegacy));
            bool prefixRejected = !IsRecognizedLegacyRoot(prefixedUnrelated, Path.GetFileName(prefixedUnrelated));
            bool markerRejected = !IsRecognizedLegacyRoot(markedUnrelated, Path.GetFileName(markedUnrelated));
            bool canonicalRejected = !IsRecognizedLegacyRoot(canonical, Path.GetFileName(canonical));

            if (!emptyAllowed || !nonEmptyRejected || !prefixRejected ||
                !markerRejected || !canonicalRejected)
                throw new InvalidOperationException(
                    $"Unsafe AppData root recognition empty={emptyAllowed} nonEmptySafe={nonEmptyRejected} " +
                    $"prefixSafe={prefixRejected} markerSafe={markerRejected} canonicalSafe={canonicalRejected}");

            bool emptyRemoved = TryRemoveSafeLegacyDirectory(emptyLegacy, Path.GetFileName(emptyLegacy));
            bool foreignRemoved = TryRemoveSafeLegacyDirectory(prefixedUnrelated, Path.GetFileName(prefixedUnrelated)) ||
                                  TryRemoveSafeLegacyDirectory(markedUnrelated, Path.GetFileName(markedUnrelated)) ||
                                  TryRemoveSafeLegacyDirectory(nonEmptyLegacy, Path.GetFileName(nonEmptyLegacy)) ||
                                  TryRemoveSafeLegacyDirectory(canonical, Path.GetFileName(canonical));
            if (!emptyRemoved || Directory.Exists(emptyLegacy) || foreignRemoved ||
                !File.Exists(Path.Combine(prefixedUnrelated, "private.txt")) ||
                !File.Exists(Path.Combine(markedUnrelated, "terminal-bg.png")) ||
                !File.Exists(Path.Combine(nonEmptyLegacy, "preferences.json")) ||
                !File.Exists(Path.Combine(canonical, "settings.json")))
                throw new InvalidOperationException(
                    "Safe cleanup deleted non-empty or unrelated application data.");

            return "DATA_ROOT_CLEANUP_SAFETY_SMOKE_OK emptyLegacyRemoved=true " +
                   "nonEmptyLegacyPreserved=true arbitraryPrefixPreserved=true " +
                   "markerOnlyPreserved=true canonicalPreserved=true " +
                   "recursiveDeletes=0 realUserFilesTouched=0";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static bool IsRecognizedLegacyRoot(string path, string name)
    {
        // This executes automatically on app startup. Never infer ownership of
        // a user directory from a broad prefix or a single common file name.
        if (!string.Equals(name, "SpankyhoOptimizerLegacy", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "SpankyhoOptimizerOld", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                   !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryRemoveSafeLegacyDirectory(string path, string name)
    {
        if (!IsRecognizedLegacyRoot(path, name)) return false;
        // A nonrecursive delete cannot remove user data even if another
        // process creates a file after the emptiness check (TOCTOU).
        Directory.Delete(path, recursive: false);
        return true;
    }
}
