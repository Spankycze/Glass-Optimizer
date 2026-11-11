using System;
using System.IO;

namespace SpankyhoOptimizer.Services;

internal static class WorkerIpcSecurity
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "worker-ipc");

    public static string CreateOutputPath()
    {
        Directory.CreateDirectory(Root);
        return ValidateOutputPath(Path.Combine(Root, Guid.NewGuid().ToString("N") + ".json"));
    }

    public static string ValidateOutputPath(string path) => ValidateOutputPathInRoot(Root, path);

    // The elevated worker only needs the single, random, direct-child output
    // path created by WorkerClientService. Never accept nested paths, arbitrary
    // filenames or existing files that an attacker could make us overwrite.
    internal static string ValidateOutputPathInRoot(string rootPath, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string full = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(full);
        string fileName = Path.GetFileName(full);
        bool generatedName = fileName.Length == 37 &&
            fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(fileName[..32], "N", out _);

        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) || !generatedName)
            throw new InvalidOperationException("Worker IPC output path must be a direct, unique JSON file.");

        // Refuse redirected IPC root directories. CreateNew in WorkerHost is
        // the final no-clobber check even if another process races this read.
        RequireSafeRoot(root);
        if (File.Exists(full) || Directory.Exists(full))
            throw new InvalidOperationException("Worker IPC output file already exists.");
        return full;
    }

    // Also run before stale IPC cleanup: cleanup must never traverse a
    // junction/symlinked root and delete unrelated JSON files.
    internal static void RequireSafeRoot(string rootPath)
    {
        var directory = new DirectoryInfo(rootPath);
        if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Worker IPC root is a reparse point.");
    }

    internal static string RunOutputPathSmoke()
    {
        string temp = Path.Combine(Path.GetTempPath(), "spanky-worker-out-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(temp, "worker-ipc");
        Directory.CreateDirectory(root);
        int rejected = 0;
        bool rootLinkTested = false;
        try
        {
            string good = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
            if (!string.Equals(ValidateOutputPathInRoot(root, good), Path.GetFullPath(good), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Valid IPC output was rejected.");
            string[] unsafePaths =
            {
                Path.Combine(root, "nested", Guid.NewGuid().ToString("N") + ".json"),
                Path.Combine(root + "-prefix", Guid.NewGuid().ToString("N") + ".json"),
                Path.Combine(root, "overwrite-target.json"),
                Path.Combine(root, Guid.NewGuid().ToString("N") + ".txt"),
                Path.Combine(root, "not-a-guid-value".PadRight(32, 'a') + ".json"),
                Path.Combine(temp, Guid.NewGuid().ToString("N") + ".json"),
                Path.Combine(root, ".json")
            };
            foreach (string unsafePath in unsafePaths)
            {
                bool denied = false;
                try { ValidateOutputPathInRoot(root, unsafePath); }
                catch (InvalidOperationException) { denied = true; }
                if (!denied) throw new InvalidOperationException("Unsafe worker IPC output accepted: " + unsafePath);
                rejected++;
            }

            File.WriteAllText(good, "{\"do-not-overwrite\":true}");
            bool existingDenied = false;
            try { ValidateOutputPathInRoot(root, good); }
            catch (InvalidOperationException) { existingDenied = true; }
            if (!existingDenied) throw new InvalidOperationException("Existing IPC output was accepted.");
            rejected++;
            if (File.ReadAllText(good) != "{\"do-not-overwrite\":true}")
                throw new InvalidOperationException("IPC output smoke unexpectedly changed existing data.");

            string redirect = Path.Combine(temp, "linked-ipc-root");
            try
            {
                Directory.CreateSymbolicLink(redirect, root);
                rootLinkTested = true;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
            if (rootLinkTested)
            {
                bool denied = false;
                try { ValidateOutputPathInRoot(redirect, Path.Combine(redirect, Guid.NewGuid().ToString("N") + ".json")); }
                catch (InvalidOperationException) { denied = true; }
                if (!denied) throw new InvalidOperationException("Reparse-point IPC root was accepted.");
                rejected++;
            }
            return $"WORKER_IPC_OUTPUT_PATH_SMOKE_OK accepted=1 rejected={rejected} rootLinkTested={rootLinkTested} workerProcesses=0 userFiles=0";
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
