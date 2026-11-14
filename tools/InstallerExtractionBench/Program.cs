using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SpankyhoOptimizer.ModernSetup
{
    // Exposes the same compile-time version contract as the real installer.
    // The benchmark runs in a separate process without installing the app.
    internal static class Program { internal static string Version { get; set; } = ""; }
}

internal static class InstallerExtractionBench
{
    private static int Main(string[] args)
    {
        if ((args.Length != 4 && args.Length != 6) || !new[] { "zstd", "zip" }.Contains(args[0]))
        {
            Console.Error.WriteLine("Usage: <zstd|zip> <archive> <expected-version> <iteration-id> [copy-buffer] [file-buffer]");
            return 2;
        }
        string format = args[0], source = args[1], iteration = args[3];
        int copyBuffer = args.Length == 6 ? int.Parse(args[4]) : 81920;
        int fileBuffer = args.Length == 6 ? int.Parse(args[5]) : 4096;
        SpankyhoOptimizer.ModernSetup.Program.Version = args[2];
        string root = Path.Combine(Path.GetTempPath(), "SpankyhoInstallerBench-" +
                         Guid.NewGuid().ToString("N"));
        using var process = Process.GetCurrentProcess();
        try
        {
            long beforeAlloc = GC.GetTotalAllocatedBytes(true);
            TimeSpan beforeCpu = process.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            using (var input = File.OpenRead(source))
            {
                if (format == "zstd")
                {
                    SpankyhoOptimizer.ModernSetup.PayloadArchive.Extract(input, root, null, copyBuffer, fileBuffer);
                }
                else
                {
                    using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                    string safeRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                    foreach (var entry in zip.Entries)
                    {
                        string output = Path.GetFullPath(Path.Combine(root, entry.FullName));
                        if (!output.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Unsafe ZIP entry");
                        if (entry.Name.Length == 0) { Directory.CreateDirectory(output); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        entry.ExtractToFile(output, overwrite:true);
                    }
                }
            }
            sw.Stop();
            long alloc = GC.GetTotalAllocatedBytes(true) - beforeAlloc;
            long cpu = (long)(process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
            string exe = Path.Combine(root, "SpankyhoOptimizer.exe");
            if (!File.Exists(exe) || new FileInfo(exe).Length == 0)
                throw new InvalidDataException("Extracted EXE missing");
            int files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
            long bytes = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                                 .Sum(f => new FileInfo(f).Length);
            Console.WriteLine(
                $"EXTRACT_BENCH_OK format={format} iteration={iteration} copy_buffer={copyBuffer} file_buffer={fileBuffer} elapsed_ms={sw.ElapsedMilliseconds} cpu_ms={cpu} allocated_bytes={alloc} peak_workingset_bytes={process.PeakWorkingSet64} files={files} raw_bytes={bytes}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("EXTRACT_BENCH_FAIL " + ex);
            return 1;
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
