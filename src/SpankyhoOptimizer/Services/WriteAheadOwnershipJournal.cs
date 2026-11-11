using System;
using System.IO;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Writes an ownership snapshot durably before privileged OS mutation.
/// CreateNew + atomic rename refuses to overwrite a prior owner's journal.
/// </summary>
internal static class WriteAheadOwnershipJournal
{
    internal static void CreateNew(string path, byte[] contents)
    {
        string? folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Ownership journal requires an absolute directory.", nameof(path));
        Directory.CreateDirectory(folder);
        string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* Never hide an earlier failure. */ }
        }
    }
}
