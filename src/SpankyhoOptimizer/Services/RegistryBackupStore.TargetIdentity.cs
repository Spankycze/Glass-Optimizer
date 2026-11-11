using System;
using System.Collections.Generic;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    // A backup ID identifies exactly one registry target. Reusing the ID for
    // another hive/path/value must fail before consulting or altering Windows.
    // Snapshot values themselves stay first-original, never recaptured here.
    internal static void ValidateBackupTargetIdentity(
        string id, RegistryHive hive, string path, string valueName,
        RegistryBackupValue snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(valueName))
            throw new InvalidOperationException(
                "Identifikátor nebo cílová cesta registry tweaku nejsou platné.");

        if (!string.Equals(snapshot.Key, id, StringComparison.OrdinalIgnoreCase) ||
            !Enum.TryParse<RegistryHive>(snapshot.Hive, ignoreCase: true, out var savedHive) ||
            !Enum.IsDefined(savedHive) || savedHive != hive ||
            !SameRegistryTargetPath(snapshot.Path, path) ||
            !string.Equals(snapshot.Name, valueName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "ID registry tweaku '" + id + "' již má zálohu pro jiný klíč či hodnotu. " +
                "Původní záloha zůstala beze změny, zásah byl zastaven.");
    }

    private static bool SameRegistryTargetPath(string? x, string? y) =>
        !string.IsNullOrWhiteSpace(x) && !string.IsNullOrWhiteSpace(y) &&
        string.Equals(x.TrimEnd('\\'), y.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    // Validate the ENTIRE batch before writing any new snapshot. An ID must
    // never be silently skipped if it now points somewhere else, including
    // when two entries in a single request disagree.
    internal static void ValidateBackupBatchIdentities(
        IReadOnlyList<TweakDefinition> items,
        IReadOnlyDictionary<string, RegistryBackupValue> originals)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(originals);
        var seen = new Dictionary<string, (RegistryHive Hive, string Path, string Name)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (TweakDefinition tweak in items)
        {
            if (tweak is null || string.IsNullOrWhiteSpace(tweak.Id) ||
                tweak.RegistryHive is null ||
                string.IsNullOrWhiteSpace(tweak.RegistryPath) ||
                string.IsNullOrWhiteSpace(tweak.RegistryValueName))
                throw new InvalidOperationException(
                    "Dávka obsahuje neplatnou definici registry tweaku.");

            RegistryHive hive = tweak.RegistryHive.Value;
            string path = tweak.RegistryPath;
            string valueName = tweak.RegistryValueName;
            if (seen.TryGetValue(tweak.Id, out var earlier))
            {
                if (earlier.Hive != hive ||
                    !SameRegistryTargetPath(earlier.Path, path) ||
                    !string.Equals(earlier.Name, valueName,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Dávka obsahuje konfliktní cíle pod stejným ID: " + tweak.Id);
            }
            else
            {
                seen.Add(tweak.Id, (hive, path, valueName));
            }

            if (originals.TryGetValue(tweak.Id, out RegistryBackupValue? saved))
                ValidateBackupTargetIdentity(tweak.Id, hive, path, valueName, saved);
        }
    }
}
