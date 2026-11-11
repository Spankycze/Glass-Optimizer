using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class ArchiveSafetyService
{
    private static readonly string[] Allowed = { ".zip", ".rar", ".7z", ".exe" };
    private static readonly char[] LineSeparators = { '\r', '\n' };

    public static async Task ValidateAsync(string archive, CancellationToken token = default)
    {
        if (!File.Exists(archive)) throw new FileNotFoundException("Archiv nebyl nalezen.", archive);
        string ext = Path.GetExtension(archive);
        if (!Allowed.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Nepodporovaný typ archivu: " + ext);
        long size = new FileInfo(archive).Length;
        if (size < 1024) throw new InvalidDataException("Archiv je příliš malý nebo poškozený.");
        if (size > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Archiv překračuje bezpečnostní limit 8 GB.");
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return;

        var list = await ProcessRunner.RunAsync("tar.exe", $"-tf \"{archive}\"", TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
        if (!list.Success) throw new InvalidDataException("Archiv nelze přečíst: " + (list.StdErr + " " + list.StdOut).Trim());
        string[] entries = list.StdOut.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0) throw new InvalidDataException("Archiv neobsahuje žádné soubory.");
        if (entries.Length > 20000) throw new InvalidDataException("Archiv obsahuje příliš mnoho položek.");
        foreach (string raw in entries)
            ValidateListedEntry(raw);

        // Pre-extraction safeguard: an archive link can redirect a later
        // extracted member outside the destination BEFORE postvalidation runs.
        var verbose = await ProcessRunner.RunAsync("tar.exe", $"-tvf \"{archive}\"",
            TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
        if (!verbose.Success)
            throw new InvalidDataException("Nelze ověřit typy položek archivu.");
        ValidateVerboseEntryTypes(verbose.StdOut);
    }

    private static void ValidateVerboseEntryTypes(string listing)
    {
        string[] lines = listing.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines.Length > 20000)
            throw new InvalidDataException("Archiv má neplatný seznam typů položek.");
        foreach (string line in lines)
        {
            // bsdtar/Windows tar -tvf begins with the POSIX entry type.
            // Accept only ordinary files and directories; reject links,
            // devices, FIFOs and any unrecognized type before extraction.
            if (line.Length == 0 || line[0] is not ('-' or 'd'))
                throw new InvalidDataException("Archiv obsahuje odkaz nebo nepodporovaný typ položky.");
        }
    }

    private static void ValidateListedEntry(string raw)
    {
        // Do not Trim() file names: leading/trailing whitespace is part of
        // the archive entry and may be interpreted differently on Windows.
        string entry = raw.Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry))
            throw new InvalidDataException("Archiv obsahuje neplatnou nebo absolutní cestu.");

        string[] parts = entry.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new InvalidDataException("Archiv obsahuje prázdnou cestu.");
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            // tar commonly prefixes root-relative members with "./".
            if (i == 0 && part == ".") continue;
            if (part == ".." || part == "." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => c < 32 || c is ':' or '<' or '>' or '"' or '|' or '?' or '*'))
                throw new InvalidDataException("Archiv obsahuje nebezpečný název: " + part);

            string device = part.Split('.')[0];
            if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (device.Length == 4 &&
                 ((device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                   device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                  // Win32 reserves COM/LPT aliases with superscript 1, 2 and 3 too.
                  device[3] is (>= '1' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3')))
                throw new InvalidDataException("Archiv obsahuje rezervovaný název Windows: " + part);
        }
    }

    internal static string RunArchiveEntrySmoke()
    {
        string[] allowed = { "drivers/setup.exe", "./drivers/setup.exe", "assets/config.ini" };
        foreach (string entry in allowed) ValidateListedEntry(entry);
        string[] unsafeEntries =
        {
            "drivers/payload:stream", "CON", "drivers/NUL.txt",
            "drivers/COM1.dll", "drivers/trailing.", "drivers/trailing ",
            "drivers/pipe|name", "drivers/../escape.exe",
            "C:relative\\setup.exe", "/absolute/setup.exe",
            // Windows console devices and Unicode COM/LPT digit aliases.
            "CONIN$", "system/CONOUT$.txt", "drivers/COM\u00B9.sys",
            "drivers/LPT\u00B2.dll", "drivers/COM\u00B3", "drivers/LPT\u00B3.txt"
        };
        foreach (string entry in unsafeEntries)
        {
            bool rejected = false;
            try { ValidateListedEntry(entry); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Archive entry accepted unsafe Windows path: " + entry);
        }
        ValidateVerboseEntryTypes("-rw-r--r-- 0 0 12 Jan 1 00:00 drivers/setup.exe\n" +
                                  "drwxr-xr-x 0 0 0 Jan 1 00:00 drivers/\n");
        foreach (string mode in new[] { "lrwxrwxrwx", "hrw-r--r--", "crw-r--r--" })
        {
            bool rejected = false;
            try { ValidateVerboseEntryTypes(mode + " 0 0 12 Jan 1 00:00 unsafe-entry\n"); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Archive accepted unsafe entry type: " + mode);
        }
        return $"ARCHIVE_ENTRY_SMOKE_OK allowed={allowed.Length} rejected={unsafeEntries.Length} linkModesRejected=3 fileWrites=0";
    }

    internal static string RunExtractedTreeSmoke()
    {
        string parent = Path.Combine(Path.GetTempPath(), "spanky-archive-smoke-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "Extracted");
        Directory.CreateDirectory(Path.Combine(root, "drivers"));
        try
        {
            File.WriteAllText(Path.Combine(root, "drivers", "setup.exe"), "safe test fixture");
            ValidateExtractedTree(root);
            // Symbolic-link creation may be restricted by Windows policy.
            // Exercise the real filesystem reparse guard when permitted.
            bool linkTested = false;
            string outside = Path.Combine(parent, "outside-fixture.txt");
            File.WriteAllText(outside, "outside the extraction root");
            string link = Path.Combine(root, "link-to-outside.txt");
            try
            {
                File.CreateSymbolicLink(link, outside);
                linkTested = true;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }

            if (linkTested)
            {
                bool rejected = false;
                try { ValidateExtractedTree(root); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected)
                    throw new InvalidOperationException("Extracted archive followed a file symlink outside its root.");
            }
            return "ARCHIVE_TREE_SMOKE_OK normalTree=true symlinkChecked=" + linkTested;
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    public static void ValidateExtractedTree(string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // Never use AllDirectories here: it can follow junctions or symlinked
        // directories before the caller has a chance to reject them.
        var pending = new System.Collections.Generic.Stack<string>();
        pending.Push(Path.GetFullPath(root));
        long total = 0;
        int count = 0;
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Rozbalený balík obsahuje přesměrovanou složku.");
            foreach (string directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                string path = Path.GetFullPath(directory);
                if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Rozbalená složka odkazuje mimo cílový adresář.");
                if (++count > 30000)
                    throw new InvalidDataException("Rozbalený balík obsahuje příliš mnoho položek.");
                pending.Push(path);
            }
            foreach (string file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                string full = Path.GetFullPath(file);
                if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Rozbalený soubor obsahuje nepovolený odkaz.");
                if (++count > 30000)
                    throw new InvalidDataException("Rozbalený balík obsahuje příliš mnoho položek.");
                total += new FileInfo(full).Length;
                if (total > 12L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Rozbalený balík překročil limit 12 GB.");
            }
        }
    }
}
