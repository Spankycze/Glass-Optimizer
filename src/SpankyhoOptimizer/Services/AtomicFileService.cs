using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class AtomicFileService
{
    public static async Task WriteTextAsync(string path, string content, CancellationToken token = default)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
#pragma warning disable CA1849 // Intentional durability barrier: Flush(true) commits file contents/metadata to stable storage.
                stream.Flush(true);
#pragma warning restore CA1849
            }
            Replace(temp, path);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
    public static void WriteText(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(true);
            Replace(temp, path);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static void Replace(string temp, string path)
    {
        if (File.Exists(path))
        {
            string backup = path + ".bak";
            try
            {
                File.Replace(temp, path, backup, true);
                try { File.Delete(backup); } catch { }
                return;
            }
            catch (Exception ex)
            {
                LogService.Error("atomic-file", "File.Replace failed; using overwrite fallback for " + path, ex);
            }
        }
        File.Move(temp, path, true);
    }
}
