using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed class RegistryBatchScope : IDisposable
{
    private static readonly AsyncLocal<RegistryBatchScope?> CurrentScope = new();
    private static readonly RegistryCacheKeyComparer KeyComparer = new();
    // Windows registry paths are case-insensitive; read and write handles remain distinct.
    private readonly Dictionary<(RegistryHive Hive, string Path, bool Writable), RegistryKey> _keys = new(KeyComparer);
    private readonly HashSet<(RegistryHive Hive, string Path, bool Writable)> _missingReadOnlyKeys = new(KeyComparer);
    private readonly Dictionary<RegistryHive, RegistryKey> _roots = new();
    private readonly object _gate = new();
    private readonly RegistryBatchScope? _previous;
    private bool _disposed;

    private RegistryBatchScope()
    {
        _previous = CurrentScope.Value;
        if (_previous is not null)
        {
            // A child task can inherit an already disposed ambient scope.
            // Refuse to chain a fresh scope to a released registry-handle cache.
            lock (_previous._gate)
                ObjectDisposedException.ThrowIf(_previous._disposed, _previous);
        }
        CurrentScope.Value = this;
    }

    public static RegistryBatchScope Begin() => new();
    public static bool IsActive => CurrentScope.Value != null;

    // Detection must never create a missing key or require write access.
    // Scope-owned handles live until Dispose; unscoped callers own their returned handle.
    public static RegistryKey? OpenReadOnly(RegistryHive hive, string path)
    {
        var scope = CurrentScope.Value;
        if (scope is null)
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return root.OpenSubKey(path, writable: false);
        }

        // AsyncLocal can flow to child tasks: synchronize cache lookup, insert and disposal.
        lock (scope._gate)
        {
            ObjectDisposedException.ThrowIf(scope._disposed, scope);
            return scope.OpenReadOnlyUnlocked(hive, path);
        }
    }

    private RegistryKey GetOrOpenRootUnlocked(RegistryHive hive)
    {
        if (!_roots.TryGetValue(hive, out RegistryKey? root))
        {
            root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            _roots[hive] = root;
        }
        return root;
    }

    private RegistryKey? OpenReadOnlyUnlocked(RegistryHive hive, string path)
    {
        var cacheKey = (hive, path, Writable: false);
        if (_keys.TryGetValue(cacheKey, out var cached))
            return cached;
        if (_missingReadOnlyKeys.Contains(cacheKey))
            return null;

        RegistryKey root = GetOrOpenRootUnlocked(hive);
        RegistryKey? opened = root.OpenSubKey(path, writable: false);
        if (opened != null)
            _keys[cacheKey] = opened;
        else
            _missingReadOnlyKeys.Add(cacheKey);
        return opened;
    }

    public static bool TryReadDword(RegistryHive hive, string path, string valueName, out long value)
    {
        value = 0;
        var scope = CurrentScope.Value;
        if (scope is not null)
        {
            lock (scope._gate)
            {
                ObjectDisposedException.ThrowIf(scope._disposed, scope);
                RegistryKey? scopedKey = scope.OpenReadOnlyUnlocked(hive, path);
                return TryReadDwordFromKey(scopedKey, valueName, out value);
            }
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(path, writable: false);
        return TryReadDwordFromKey(key, valueName, out value);
    }

    public static bool TryReadString(
        RegistryHive hive,
        string path,
        string valueName,
        out string? value,
        RegistryValueOptions options = RegistryValueOptions.DoNotExpandEnvironmentNames)
    {
        value = null;
        var scope = CurrentScope.Value;
        if (scope is not null)
        {
            lock (scope._gate)
            {
                ObjectDisposedException.ThrowIf(scope._disposed, scope);
                RegistryKey? scopedKey = scope.OpenReadOnlyUnlocked(hive, path);
                return TryReadStringFromKey(scopedKey, valueName, options, out value);
            }
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(path, writable: false);
        return TryReadStringFromKey(key, valueName, options, out value);
    }

    public static bool TryReadBinary(RegistryHive hive, string path, string valueName, out byte[]? value)
    {
        value = null;
        var scope = CurrentScope.Value;
        if (scope is not null)
        {
            lock (scope._gate)
            {
                ObjectDisposedException.ThrowIf(scope._disposed, scope);
                RegistryKey? scopedKey = scope.OpenReadOnlyUnlocked(hive, path);
                return TryReadBinaryFromKey(scopedKey, valueName, out value);
            }
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(path, writable: false);
        return TryReadBinaryFromKey(key, valueName, out value);
    }

    private static bool TryReadDwordFromKey(RegistryKey? key, string valueName, out long value)
    {
        value = 0;
        if (key is null) return false;
        try
        {
            object? raw = key.GetValue(valueName);
            if (raw is null || key.GetValueKind(valueName) != RegistryValueKind.DWord)
                return false;
            value = raw switch
            {
                int i => i,
                uint u => u,
                long l => l,
                _ => Convert.ToInt64(raw)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadStringFromKey(
        RegistryKey? key,
        string valueName,
        RegistryValueOptions options,
        out string? value)
    {
        value = null;
        if (key is null) return false;
        try
        {
            object? raw = key.GetValue(valueName, null, options);
            if (raw is not string text || key.GetValueKind(valueName) != RegistryValueKind.String)
                return false;
            value = text;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBinaryFromKey(RegistryKey? key, string valueName, out byte[]? value)
    {
        value = null;
        if (key is null) return false;
        try
        {
            object? raw = key.GetValue(valueName);
            if (raw is not byte[] bytes || key.GetValueKind(valueName) != RegistryValueKind.Binary)
                return false;
            value = bytes;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static RegistryKey Open(RegistryHive hive, string path, bool writable)
    {
        if (!writable)
            return OpenReadOnly(hive, path) ??
                throw new InvalidOperationException("Požadovaný registry klíč neexistuje.");

        var scope = CurrentScope.Value;
        if (scope is null)
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return root.CreateSubKey(path, writable: true);
        }

        lock (scope._gate)
        {
            ObjectDisposedException.ThrowIf(scope._disposed, scope);

            var cacheKey = (hive, path, Writable: true);
            scope._missingReadOnlyKeys.Remove((hive, path, Writable: false));
            if (scope._keys.TryGetValue(cacheKey, out var cached))
                return cached;

            RegistryKey root = scope.GetOrOpenRootUnlocked(hive);
            RegistryKey opened = root.CreateSubKey(path, writable: true);
            scope._keys[cacheKey] = opened;
            return opened;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Nested scopes must unwind in LIFO order. Closing an outer scope
            // first would replace the active child and later restore a disposed parent.
            if (!ReferenceEquals(CurrentScope.Value, this))
                throw new InvalidOperationException("Registry batch scope musí být ukončen v opačném pořadí, než byl otevřen.");
            _disposed = true;
            foreach (var key in _keys.Values)
            {
                try { key.Dispose(); } catch { }
            }
            _keys.Clear();
            _missingReadOnlyKeys.Clear();
            foreach (var root in _roots.Values)
            {
                try { root.Dispose(); } catch { }
            }
            _roots.Clear();
        }
        CurrentScope.Value = _previous;
    }

    private sealed class RegistryCacheKeyComparer : IEqualityComparer<(RegistryHive Hive, string Path, bool Writable)>
    {
        public bool Equals(
            (RegistryHive Hive, string Path, bool Writable) left,
            (RegistryHive Hive, string Path, bool Writable) right) =>
            left.Hive == right.Hive && left.Writable == right.Writable &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path);

        public int GetHashCode((RegistryHive Hive, string Path, bool Writable) key) =>
            HashCode.Combine(key.Hive, key.Writable, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path));
    }
}
