using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed class RegistryBatchScope : IDisposable
{
    private static readonly AsyncLocal<RegistryBatchScope?> CurrentScope = new();
    private readonly Dictionary<(RegistryHive Hive, string Path), RegistryKey> _keys = new();
    private readonly RegistryBatchScope? _previous;
    private bool _disposed;

    private RegistryBatchScope()
    {
        _previous = CurrentScope.Value;
        CurrentScope.Value = this;
    }

    public static RegistryBatchScope Begin() => new();
    public static bool IsActive => CurrentScope.Value != null;

    public static RegistryKey Open(RegistryHive hive, string path, bool writable)
    {
        var scope = CurrentScope.Value;
        if (scope == null)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return writable ? baseKey.CreateSubKey(path, true) : baseKey.OpenSubKey(path, false)!;
        }
        var key = (hive, path);
        if (scope._keys.TryGetValue(key, out var existing)) return existing;
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        RegistryKey opened = writable ? root.CreateSubKey(path, true) : (root.OpenSubKey(path, false) ?? root.CreateSubKey(path, true));
        scope._keys[key] = opened;
        return opened;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in _keys.Values)
        {
            try { key.Dispose(); } catch { }
        }
        _keys.Clear();
        CurrentScope.Value = _previous;
    }
}
