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
        {
            string e = raw.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(e)) throw new InvalidDataException("Archiv obsahuje absolutní cestu.");
            string[] parts = e.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(x => x == "..")) throw new InvalidDataException("Archiv obsahuje zakázanou cestu '..'.");
            if (e.IndexOf('\0') >= 0) throw new InvalidDataException("Archiv obsahuje neplatnou cestu.");
        }
    }

    public static void ValidateExtractedTree(string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long total = 0; int count = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string full = Path.GetFullPath(file);
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Rozbalený soubor opustil cílovou složku.");
            total += new FileInfo(full).Length; count++;
            if (count > 30000) throw new InvalidDataException("Rozbalený balík obsahuje příliš mnoho souborů.");
            if (total > 12L * 1024 * 1024 * 1024) throw new InvalidDataException("Rozbalený balík překročil limit 12 GB.");
        }
    }
}
