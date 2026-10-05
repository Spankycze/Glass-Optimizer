using System;
using System.IO;

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
                if (string.Equals(name, CanonicalRootName, StringComparison.OrdinalIgnoreCase) ||
                    !IsRecognizedLegacyRoot(dir, name))
                    continue;

                try
                {
                    var info = new DirectoryInfo(dir);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    Directory.Delete(dir, recursive: true);
                    removed++;
                    LogService.Info("startup", $"DATA_ROOT_CLEANUP removed={dir}");
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
        string root = Path.Combine(Path.GetTempPath(), "SpankyhoOptimizer-cleanup-smoke-" + Guid.NewGuid().ToString("N"));
        string legacyOptimizer = Path.Combine(root, CanonicalRootName + "LegacySmoke");
        string markerLegacy = Path.Combine(root, "SpankyhoLegacyMarkerSmoke");
        string unrelated = Path.Combine(root, "SpankyhoUnrelatedSmoke");

        Directory.CreateDirectory(legacyOptimizer);
        Directory.CreateDirectory(markerLegacy);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(markerLegacy, "terminal-bg.png"), "x");
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "x");

        try
        {
            bool optimizerOk = IsRecognizedLegacyRoot(legacyOptimizer, Path.GetFileName(legacyOptimizer));
            bool markerOk = IsRecognizedLegacyRoot(markerLegacy, Path.GetFileName(markerLegacy));
            bool unrelatedOk = !IsRecognizedLegacyRoot(unrelated, Path.GetFileName(unrelated));

            if (!optimizerOk || !markerOk || !unrelatedOk)
                throw new InvalidOperationException(
                    $"Data root recognition mismatch optimizer={optimizerOk} marker={markerOk} unrelatedSafe={unrelatedOk}");

            return "DATA_ROOT_RECOGNITION_SMOKE_OK optimizer=true marker=true unrelatedSafe=true";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static bool IsRecognizedLegacyRoot(string path, string name)
    {
        if (name.StartsWith(CanonicalRootName, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return File.Exists(Path.Combine(path, "terminal-bg.png"));
        }
        catch
        {
            return false;
        }
    }
}
