using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

// Bridge JSON is normally written every ~2 seconds; hot consumers often poll
// much faster than that. Cache a tiny metadata-stat window to avoid paying a
// native filesystem metadata query on every caller's refresh request.
internal sealed class HardwareTelemetrySnapshotReadCache(string path)
{
    private readonly object _gate = new();
    private readonly FileInfo _fileInfo = new(path);
    private static readonly long MetadataRefreshTicks = Math.Max(1, Stopwatch.Frequency / 5); // 200 ms
    private long _nextMetadataRefresh;
    private bool _metadataChecked;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private long _lastLength = -1;
    private HardwareTelemetryBridgeSnapshot? _cached;

    internal HardwareTelemetryBridgeSnapshot? Read()
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_metadataChecked && now < _nextMetadataRefresh)
                return _cached;

            _fileInfo.Refresh();
            // Set only after successful metadata lookup so transient IO errors
            // are retried on the next call instead of being cached as success.
            _nextMetadataRefresh = Stopwatch.GetTimestamp() + MetadataRefreshTicks;
            _metadataChecked = true;
            if (!_fileInfo.Exists)
            {
                _cached = null;
                _lastWriteUtc = DateTime.MinValue;
                _lastLength = -1;
                return null;
            }

            DateTime writeUtc = _fileInfo.LastWriteTimeUtc;
            long length = _fileInfo.Length;
            if (_cached != null && writeUtc == _lastWriteUtc && length == _lastLength)
                return _cached;

            // A replacement can race with the read. Never cache a failed JSON
            // parse as fresh; the outer caller can fall back to direct telemetry.
            HardwareTelemetryBridgeSnapshot? snapshot;
            try
            {
                // Parse UTF-8 directly from the file; avoid an intermediate UTF-16 JSON string.
                using var snapshotStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                snapshot = JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(snapshotStream);
            }
            catch
            {
                // Failed I/O or malformed JSON is not a successfully observed snapshot.
                // Retry immediately after the writer finishes its replacement.
                _metadataChecked = false;
                throw;
            }
            if (snapshot is null)
            {
                _metadataChecked = false;
                return null;
            }
            _cached = snapshot;
            _lastWriteUtc = writeUtc;
            _lastLength = length;
            return snapshot;
        }
    }

    // Real temporary-file exercise of the production metadata cache:
    // atomically replaced snapshots, the 200ms stat window, malformed JSON
    // retry, and an unplugged/removed bridge file. No live sensor or registry.
    internal static string RunAtomicFreshnessSmoke()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "SpankyTelemetryCacheSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "bridge.json");
        string temp = path + ".tmp";
        try
        {
            var cache = new HardwareTelemetrySnapshotReadCache(path);
            if (cache.Read() != null)
                throw new InvalidOperationException("Missing bridge cache returned data.");
            var original = new HardwareTelemetryBridgeSnapshot(
                new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc),
                123, "v1", true, 51.2, "CPU", "Ryzen", 12, 4200, 60, 1);
            File.WriteAllText(temp, JsonSerializer.Serialize(original));
            File.Move(temp, path);
            System.Threading.Thread.Sleep(235);
            if (cache.Read() != original || !ReferenceEquals(cache.Read(), cache.Read()))
                throw new InvalidOperationException("Initial bridge snapshot cache did not stabilize.");
            var next = original with { Version = "a-different-version", CpuTempC = 61.8 };
            File.WriteAllText(temp, JsonSerializer.Serialize(next));
            File.Move(temp, path, overwrite: true);
            if (cache.Read() != original)
                throw new InvalidOperationException("Telemetry metadata throttle did not reuse cached snapshot.");
            System.Threading.Thread.Sleep(235);
            if (cache.Read() != next)
                throw new InvalidOperationException("Telemetry cache missed atomic writer replacement.");
            File.WriteAllText(temp, "{");
            File.Move(temp, path, overwrite: true);
            System.Threading.Thread.Sleep(235);
            bool invalidDetected = false;
            try { _ = cache.Read(); }
            catch (JsonException) { invalidDetected = true; }
            if (!invalidDetected)
                throw new InvalidOperationException("Corrupt bridge snapshot was mistakenly cached as current.");
            File.WriteAllText(temp, JsonSerializer.Serialize(next));
            File.Move(temp, path, overwrite: true);
            if (cache.Read() != next)
                throw new InvalidOperationException("Bridge JSON parser failed immediate recovery.");
            File.Delete(path);
            System.Threading.Thread.Sleep(235);
            if (cache.Read() != null)
                throw new InvalidOperationException("Bridge cache retained disconnected hardware snapshot.");
            return "HARDWARE_TELEMETRY_READ_CACHE_SMOKE_OK atomicReplace=true " +
                "metadataThrottle=200ms corruptRetry=true disconnectedCleared=true liveWrites=0";
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch { /* diagnostic temp cleanup best effort */ }
        }
    }
}
