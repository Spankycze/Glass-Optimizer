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

    // The batch snapshot is not unconditional permission to overwrite a
    // value that another program changed while the operation was in flight.
    // DWord ON is the only exact post-apply signature known to this engine.
    // OFF may restore an arbitrary backed-up value; when ambiguous, preserve
    // the current value and report a rollback conflict instead.
    internal static bool MayRestoreOwnedValue(
        bool originallyExisted, RegistryValueKind originalKind, object? originalValue,
        bool currentlyExisted, RegistryValueKind currentKind, object? currentValue,
        bool expectedEnabled, long? expectedDword) =>
        MatchesValue(originallyExisted, originalKind, originalValue,
            currentlyExisted, currentKind, currentValue) ||
        (expectedEnabled && expectedDword.HasValue &&
         currentlyExisted && currentKind == RegistryValueKind.DWord &&
         currentValue is int currentDword &&
         (currentDword == expectedDword.Value || (uint)currentDword == (uint)expectedDword.Value));

    private static bool MatchesValue(
        bool aExists, RegistryValueKind aKind, object? aValue,
        bool bExists, RegistryValueKind bKind, object? bValue)
    {
        if (aExists != bExists) return false;
        if (!aExists) return true;
        if (aKind != bKind) return false;
        return aValue switch
        {
            byte[] aBytes when bValue is byte[] bBytes => aBytes.SequenceEqual(bBytes),
            string[] aStrings when bValue is string[] bStrings =>
                aStrings.SequenceEqual(bStrings, StringComparer.Ordinal),
            _ => Equals(aValue, bValue)
        };
    }

    public bool TryRestore(TweakDefinition tweak, bool expectedEnabled)
    {
        if (!_entries.TryGetValue(tweak.Id, out var entry)) return false;
        using var root = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(entry.Path, writable: true);
        bool currentExists = key?.GetValueNames().Contains(
            entry.Name, StringComparer.OrdinalIgnoreCase) == true;
        RegistryValueKind currentKind = currentExists
            ? key!.GetValueKind(entry.Name) : RegistryValueKind.None;
        object? currentValue = currentExists
            ? key!.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            : null;

        if (!MayRestoreOwnedValue(entry.Existed, entry.Kind, entry.Value,
                currentExists, currentKind, currentValue,
                expectedEnabled, tweak.RegistryEnabledValue))
            throw new InvalidOperationException(
                "Registry hodnota byla externě změněna; rollback ji nepřepíše: " + tweak.Id);

        if (MatchesValue(entry.Existed, entry.Kind, entry.Value,
                currentExists, currentKind, currentValue))
            return true; // Original already present: no registry mutation.

        if (key is null || (entry.Existed && entry.Value is null))
            throw new InvalidOperationException(
                "Původní registry hodnotu nelze bezpečně obnovit: " + tweak.Id);

        if (entry.Existed) key.SetValue(entry.Name, Clone(entry.Value)!, entry.Kind);
        else key.DeleteValue(entry.Name, false);

        bool restoredExists = key.GetValueNames().Contains(
            entry.Name, StringComparer.OrdinalIgnoreCase);
        RegistryValueKind restoredKind = restoredExists
            ? key.GetValueKind(entry.Name) : RegistryValueKind.None;
        object? restoredValue = restoredExists
            ? key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            : null;
        if (!MatchesValue(entry.Existed, entry.Kind, entry.Value,
                restoredExists, restoredKind, restoredValue))
            throw new InvalidOperationException(
                "Registry rollback nebyl potvrzen zpětným čtením: " + tweak.Id);
        return true;
    }

    private static object? Clone(object? value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        string[] strings => strings.ToArray(),
        _ => value
    };
}
