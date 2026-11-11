using System;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Text;
using ZstdSharp;

namespace SpankyhoOptimizer.ModernSetup;

// Streaming TAR + Zstandard extraction. No standalone archiver, no extra processes,
// and no buffering the full application in memory.
internal static class PayloadArchive
{
    internal const string ResourceName = "payload.tar.zst";
    internal static bool IsSafeRootAttributes(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) == 0;
    private const string ManifestName = ".spanky-payload-manifest-v1";
    private const long MaxArchiveBytes = 4L * 1024 * 1024 * 1024;

    // Avoid allocating a string[] and one new string per path segment for
    // every entry. This preserves the original checks for empty, dot and
    // dot-dot segments, absolute paths and Windows drive/alternate streams.
    internal static bool IsSafeEntryName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        int start = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && name[i] is '\\' or ':') return false;
            if (i != name.Length && name[i] != '/') continue;
            ReadOnlySpan<char> segment = name.AsSpan(start, i - start);
            if (segment.IsEmpty || segment.SequenceEqual(".".AsSpan()) ||
                segment.SequenceEqual("..".AsSpan())) return false;
            start = i + 1;
        }
        return true;
    }

    internal static void Extract(Stream compressed, string destination, Action<int, string>? progress,
                                 int copyBufferSize = 81920, int fileBufferSize = 4096)
    {
        if (copyBufferSize is < 4096 or > 1048576 || fileBufferSize is < 4096 or > 1048576)
            throw new ArgumentOutOfRangeException(nameof(copyBufferSize), "Invalid streaming buffer sizes");
        ExtractCore(compressed, destination, progress, copyBufferSize, fileBufferSize);
    }

    private static void ExtractCore(Stream compressed, string destination, Action<int, string>? progress,
                                    int copyBufferSize, int fileBufferSize)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
        var rootInfo = Directory.CreateDirectory(root);
        // The staging root itself can be a junction or symbolic link.
        // Checking only child directories would miss this path escape.
        if (!IsSafeRootAttributes(rootInfo.Attributes))
            throw new InvalidDataException("Instalační cílová složka je přesměrovaný odkaz.");
        using var decoded = new DecompressionStream(compressed);
        using var reader = new TarReader(decoded);

        var manifestEntry = reader.GetNextEntry();
        if (manifestEntry == null || manifestEntry.Name != ManifestName ||
            manifestEntry.EntryType != TarEntryType.RegularFile ||
            manifestEntry.DataStream == null || manifestEntry.Length is < 20 or > 256)
            throw new InvalidDataException("Instalační balíček nemá platný manifest.");

        using var manifestReader = new StreamReader(manifestEntry.DataStream, Encoding.UTF8, leaveOpen: true);
        string[] manifest = manifestReader.ReadToEnd().Split('|');
        if (manifest.Length != 4 || manifest[0] != "SPANKY_ZSTD_V1" ||
            !string.Equals(manifest[1], Program.Version, StringComparison.Ordinal) ||
            !int.TryParse(manifest[2], out int expectedFiles) || expectedFiles is < 1 or > 100000 ||
            !long.TryParse(manifest[3], out long expectedBytes) ||
            expectedBytes <= 0 || expectedBytes > MaxArchiveBytes)
            throw new InvalidDataException("Instalační manifest není platný.");

        int files = 0;
        long bytes = 0;
        bool hasMainExecutable = false;
        TarEntry? entry;
        int lastReportedPercent = -1;
        while ((entry = reader.GetNextEntry()) != null)
        {
            // TAR links, alternate streams, device and FIFO entries cannot be safely
            // restored by an installer: fail closed even if they carry no file data.
            if (entry.EntryType != TarEntryType.RegularFile &&
                entry.EntryType != TarEntryType.V7RegularFile)
                throw new InvalidDataException("Nepovolený typ instalačního souboru: " + entry.Name);
            string name = entry.Name;
            // Validate count and declared bytes before creating the file:
            // malformed archives must not populate a staging target first.
            if (!IsSafeEntryName(name) ||
                entry.DataStream == null || entry.Length < 0 ||
                files >= expectedFiles || entry.Length > expectedBytes - bytes)
                throw new InvalidDataException("Neplatná položka v instalačních datech: " + name);

            string fullPath = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Instalační soubor míří mimo cílovou složku: " + name);

            // Disallow reparse points in the destination hierarchy, including an
            // existing staging folder if an interrupted operation left a link behind.
            string? parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent)) throw new InvalidDataException("Invalid target path");
            // Existing directories are the common case after the first file
            // in each folder. Avoid attempting a redundant mkdir each time.
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            for (string? check = parent; check != null && check.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                 check = Path.GetDirectoryName(check))
            {
                if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Odkaz na cizí složku v instalačních datech.");
                if (check.Length < root.Length) break;
            }
            // A fresh transaction uses a unique staging directory. CREATE_NEW is
            // atomic: it refuses any pre-existing file, duplicate TAR entry,
            // hard link or dangling symlink. This also removes the extra
            // File.Exists/GetAttributes probe from the per-file hot path.
            // Keep the parent reparse-point validation above on every entry.
            using (var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, fileBufferSize))
                entry.DataStream.CopyTo(output, copyBufferSize);

            files++;
            bytes = checked(bytes + entry.Length);
            hasMainExecutable |= name.Equals("SpankyhoOptimizer.exe", StringComparison.OrdinalIgnoreCase);
            if (files > expectedFiles || bytes > expectedBytes)
                throw new InvalidDataException("Instalační archiv přesáhl deklarovanou velikost.");

            int percent = 55 + (int)(35L * bytes / expectedBytes);
            if (percent != lastReportedPercent)
            {
                progress?.Invoke(percent, "Instaluji soubory Optimizeru…");
                lastReportedPercent = percent;
            }
        }
        if (!hasMainExecutable || files != expectedFiles || bytes != expectedBytes)
            throw new InvalidDataException("Instalační archiv je neúplný nebo poškozený.");
    }
}
