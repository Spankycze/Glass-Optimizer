using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal sealed class RegistryAtomicSnapshot
{
    private sealed record Entry(RegistryHive Hive, string Path, string Name, bool Existed,
        RegistryValueKind Kind, object? Value);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static RegistryAtomicSnapshot Capture(IEnumerable<TweakDefinition> tweaks)
    {
        var snapshot = new RegistryAtomicSnapshot();
        var registryTweaks = tweaks
            .Where(x => x.RegistryHive.HasValue && x.RegistryPath != null && x.RegistryValueName != null)
            .GroupBy(x => (Hive: x.RegistryHive!.Value, Path: x.RegistryPath!), x => x);

        foreach (var group in registryTweaks)
        {
            using var root = RegistryKey.OpenBaseKey(group.Key.Hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(group.Key.Path, false);
            var valueNames = key?.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tweak in group)
            {
                string valueName = tweak.RegistryValueName!;
                bool existed = valueNames.Contains(valueName);
                RegistryValueKind kind = existed ? key!.GetValueKind(valueName) : RegistryValueKind.None;
                object? value = existed
                    ? key!.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    : null;
                snapshot._entries[tweak.Id] = new(group.Key.Hive, group.Key.Path, valueName,
                    existed, kind, Clone(value));
            }
        }
        return snapshot;
    }

    public bool TryRestore(TweakDefinition tweak)
    {
        if (!_entries.TryGetValue(tweak.Id, out var entry)) return false;
        using var root = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(entry.Path, true);
        if (!entry.Existed) key.DeleteValue(entry.Name, false);
        else key.SetValue(entry.Name, Clone(entry.Value) ?? string.Empty, entry.Kind);
        return true;
    }

    private static object? Clone(object? value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        string[] strings => strings.ToArray(),
        _ => value
    };
}
