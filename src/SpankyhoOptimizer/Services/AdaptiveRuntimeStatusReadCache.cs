using System;

namespace SpankyhoOptimizer.Services;

// The writable per-user path is preferred but older installs may have a
// ProgramData snapshot. Keep independent caches so fallback/return changes
// never surface a snapshot from the other location.
internal sealed class AdaptiveRuntimeStatusReadCache<T>(string writablePath, string legacyPath)
    where T : class
{
    private readonly ObservedJsonSnapshotCache<T> _writable = new(writablePath);
    private readonly ObservedJsonSnapshotCache<T> _legacy = new(legacyPath);

    internal T? Read(string selectedPath)
    {
        if (string.Equals(selectedPath, writablePath, StringComparison.OrdinalIgnoreCase))
            return _writable.Read();
        if (string.Equals(selectedPath, legacyPath, StringComparison.OrdinalIgnoreCase))
            return _legacy.Read();
        return null; // Unexpected source: do not trust a third-party file path.
    }
}
