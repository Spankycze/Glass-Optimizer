using System;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

// Read-only JSON snapshots (often from another process) may be requested more
// often than they are updated. Reuse metadata and parsed data, but never cache
// missing or malformed files or extend a health snapshot's own freshness.
internal sealed class ObservedJsonSnapshotCache<T>(string path) where T : class
{
    private readonly object _gate = new();
    private readonly FileInfo _file = new(path);
    private DateTime _lastWriteUtc;
    private long _lastLength = -1;
    private T? _cached;

    internal T? Read()
    {
        lock (_gate)
        {
            _file.Refresh();
            if (!_file.Exists)
            {
                Invalidate();
                return null;
            }

            DateTime writtenUtc = _file.LastWriteTimeUtc;
            long length = _file.Length;
            if (_cached != null && _lastWriteUtc == writtenUtc && _lastLength == length)
                return _cached;

            // An interrupted or malformed replacement must be read again next
            // time even if file metadata has not subsequently changed.
            Invalidate();
            T? snapshot = ReadSnapshotFromFile(path);
            if (snapshot is null) return null;
            _cached = snapshot;
            _lastWriteUtc = writtenUtc;
            _lastLength = length;
            return snapshot;
        }
    }

    // Cross-process status files are normally persisted as UTF-8 JSON.
    // Deserialize those bytes directly without a temporary UTF-16 string.
    // The previous File.ReadAllText recognized UTF-16/32 BOMs; retain that
    // decoder for legacy snapshots rather than silently dropping them.
    internal static T? ReadSnapshotFromFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xff && bytes[1] == 0xfe) ||
             (bytes[0] == 0xfe && bytes[1] == 0xff) ||
             (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 &&
              bytes[2] == 0xfe && bytes[3] == 0xff)))
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));

        ReadOnlySpan<byte> utf8 = bytes;
        if (utf8.Length >= 3 && utf8[0] == 0xef &&
            utf8[1] == 0xbb && utf8[2] == 0xbf)
            utf8 = utf8[3..];
        return JsonSerializer.Deserialize<T>(utf8);
    }

    // Health and diagnostic consumers must not claim stale evidence if the
    // file disappears or an atomic replacement temporarily contains bad JSON.
    // Unlike Read(), this is intentionally fail-closed on expected I/O errors.
    internal T? TryRead()
    {
        try { return Read(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Security.SecurityException) { return null; }
        catch (JsonException) { return null; }
    }

    // When this process successfully replaces a snapshot, the OS may retain
    // identical timestamp and length; force the next read to inspect the new
    // file even when its metadata happens to match the previous version.
    internal void InvalidateAfterWrite()
    {
        lock (_gate) Invalidate();
    }

    private void Invalidate()
    {
        _cached = null;
        _lastWriteUtc = default;
        _lastLength = -1;
    }
}
