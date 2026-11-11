using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using ZstdSharp;

internal static class PayloadPacker
{
    const string Manifest = ".spanky-payload-manifest-v1";

    public static int Main(string[] args)
    {
        try
        {
            if ((args.Length == 4 || args.Length == 5) && args[0] == "pack") { int level = args.Length == 5 ? int.Parse(args[4]) : 5; Pack(args[1], args[2], args[3], level); return 0; }
            if (args.Length == 2 && args[0] == "verify") { Verify(args[1]); return 0; }
            if (args.Length == 3 && args[0] == "compare-tar") { CompareDecompressed(args[1], args[2]); return 0; }
            Console.Error.WriteLine("Usage: pack <publish-dir> <archive> <version> | verify <archive>");
            return 2;
        }
        catch (Exception e) { Console.Error.WriteLine("PAYLOAD_ERROR " + e); return 1; }
    }

    private static void Pack(string source, string destination, string version, int level)
    {
        if (level is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(level));
        source = Path.GetFullPath(source);
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length is < 1 or > 100000) throw new InvalidDataException("Invalid file count");
        long total = 0;
        foreach (var file in files)
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to package link: " + file);
            total = checked(total + new FileInfo(file).Length);
        }
        if (total <= 0 || total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Payload too large");
        string exe = Path.Combine(source, "SpankyhoOptimizer.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Product EXE missing", exe);
        string? actual = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        if (actual != version && (actual == null || !actual.StartsWith(version + "+", StringComparison.Ordinal)))
            throw new InvalidDataException("Version mismatch: " + actual + " != " + version);

        string manifest = $"SPANKY_ZSTD_V1|{version}|{files.Length}|{total}";
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write))
        using (var compressed = new CompressionStream(output, level))
        using (var tar = new TarWriter(compressed, TarEntryFormat.Gnu))
        {
            using var mem = new MemoryStream(Encoding.UTF8.GetBytes(manifest), writable:false);
            tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, Manifest)
            {
                DataStream = mem,
                ModificationTime = DateTimeOffset.UnixEpoch,
                AccessTime = DateTimeOffset.UnixEpoch,
                ChangeTime = DateTimeOffset.UnixEpoch
            });
            foreach (var file in files)
            {
                string name = Path.GetRelativePath(source, file).Replace('\\','/');
                if (name.StartsWith("../", StringComparison.Ordinal) ||
                    name == Manifest) throw new InvalidDataException("Bad filename");
                using var input = File.OpenRead(file);
                tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = input,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    AccessTime = DateTimeOffset.UnixEpoch,
                    ChangeTime = DateTimeOffset.UnixEpoch
                });
            }
        }
        sw.Stop();
        Console.WriteLine($"ZSTD_PACK_OK version={version} level={level} files={files.Length} raw={total} bytes={new FileInfo(destination).Length} ms={sw.ElapsedMilliseconds}");
    }


    private static void CompareDecompressed(string left, string right)
    {
        using var a = new DecompressionStream(File.OpenRead(left));
        using var b = new DecompressionStream(File.OpenRead(right));
        var x = new byte[65536];
        var y = new byte[65536];
        long position = 0;
        for (;;)
        {
            int n = a.ReadAtLeast(x, x.Length, throwOnEndOfStream:false);
            int m = b.ReadAtLeast(y, y.Length, throwOnEndOfStream:false);
            if (n != m) { Console.WriteLine($"TAR_LENGTH_MISMATCH offset={position} a={n} b={m}"); return; }
            if (n == 0) { Console.WriteLine($"TAR_STREAM_IDENTICAL total={position}"); return; }
            for (int i = 0; i < n; i++)
            {
                if (x[i] != y[i])
                {
                    int from = Math.Max(0,i-48);
                    int take = Math.Min(n-from,160);
                    Console.WriteLine($"TAR_FIRST_DIFFERENCE offset={position+i} a={x[i]} b={y[i]}");
                    Console.WriteLine("A_TEXT " + Encoding.UTF8.GetString(x,from,take).Replace((char)0,' '));
                    Console.WriteLine("B_TEXT " + Encoding.UTF8.GetString(y,from,take).Replace((char)0,' '));
                    return;
                }
            }
            position += n;
        }
    }

    private static void Verify(string archive)
    {
        var sw = Stopwatch.StartNew();
        using var input = File.OpenRead(archive);
        using var decoded = new DecompressionStream(input);
        using var tar = new TarReader(decoded);
        TarEntry? first = tar.GetNextEntry();
        if (first == null || first.Name != Manifest || first.DataStream == null || first.Length is < 20 or > 256)
            throw new InvalidDataException("Missing manifest");
        using var reader = new StreamReader(first.DataStream, Encoding.UTF8, leaveOpen:true);
        string[] fields = reader.ReadToEnd().Split('|');
        if (fields.Length != 4 || fields[0] != "SPANKY_ZSTD_V1" ||
            !int.TryParse(fields[2], out int count) || count < 1 ||
            !long.TryParse(fields[3], out long total) || total <= 0 || total > 4L*1024*1024*1024)
            throw new InvalidDataException("Invalid manifest");
        long bytes = 0;
        int files = 0;
        bool exeFound = false;
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) != null)
        {
            if (entry.EntryType != TarEntryType.RegularFile ||
                entry.Name.StartsWith("/", StringComparison.Ordinal) ||
                entry.Name.Contains('\\') ||
                entry.Name.Split('/').Any(s => s is "" or "." or "..") ||
                entry.DataStream == null || entry.Length < 0 || entry.Length > total)
                throw new InvalidDataException("Invalid entry: " + entry.Name);
            exeFound |= entry.Name == "SpankyhoOptimizer.exe";
            entry.DataStream.CopyTo(Stream.Null);
            files++;
            bytes = checked(bytes + entry.Length);
            if (files > count || bytes > total) throw new InvalidDataException("Archive exceeds manifest");
        }
        if (!exeFound || files != count || bytes != total)
            throw new InvalidDataException($"Expected {count}/{total}, received {files}/{bytes}");
        sw.Stop();
        Console.WriteLine($"ZSTD_VERIFY_OK version={fields[1]} files={files} raw={bytes} ms={sw.ElapsedMilliseconds}");
    }
}
