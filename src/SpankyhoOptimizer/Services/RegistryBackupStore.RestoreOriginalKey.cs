using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    internal enum MissingValueRestoreAction
    {
        AlreadyRestored,
        RemoveOnlyValue,
        RemoveOwnedLeaf,
        Refuse
    }

    // Use a nullable original key-presence field. Historical records lacked it:
    // an unknown original key MUST NOT be deleted as though app-owned.
    internal static MissingValueRestoreAction PlanMissingValueRestore(
        bool? originalKeyExisted, bool currentKeyExisted, bool targetValueExisted,
        int currentValueCount, bool hasSubkeys, bool? valueMatchesExpected = null)
    {
        if (!currentKeyExisted)
            return originalKeyExisted == true
                ? MissingValueRestoreAction.Refuse
                : MissingValueRestoreAction.AlreadyRestored;

        if (currentValueCount < 0 ||
            (targetValueExisted && currentValueCount < 1))
            return MissingValueRestoreAction.Refuse;

        // If the current DWORD differs from the value THIS operation wrote,
        // it may belong to an external writer. Do not remove it.
        if (targetValueExisted && valueMatchesExpected == false)
            return MissingValueRestoreAction.Refuse;

        if (originalKeyExisted == false)
            return targetValueExisted && currentValueCount == 1 && !hasSubkeys &&
                   valueMatchesExpected == true
                ? MissingValueRestoreAction.RemoveOwnedLeaf
                : MissingValueRestoreAction.Refuse;

        return targetValueExisted
            ? MissingValueRestoreAction.RemoveOnlyValue
            : MissingValueRestoreAction.AlreadyRestored;
    }

    internal static bool? MatchesExpectedAppliedDword(
        RegistryValueKind kind, object? current, int? expectedAppliedDword)
    {
        if (expectedAppliedDword is not int expected)
            return null;
        return kind == RegistryValueKind.DWord &&
            current is int actual && actual == expected;
    }

    private static bool? CheckCurrentAppliedDword(
        RegistryKey? key, string valueName, int? expectedAppliedDword)
    {
        if (expectedAppliedDword is null) return null;
        if (key is null ||
            !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
            return false;
        return MatchesExpectedAppliedDword(
            key.GetValueKind(valueName),
            key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            expectedAppliedDword);
    }
    private static bool RestoreOriginallyMissingValue(
        RegistryKey baseKey, RegistryBackupValue original, int? expectedAppliedDword)
    {
        using RegistryKey? existing = baseKey.OpenSubKey(original.Path, writable: false);
        string[] values = existing?.GetValueNames() ?? Array.Empty<string>();
        bool targetPresent = values.Contains(
            original.Name, StringComparer.OrdinalIgnoreCase);
        bool hasSubkeys = existing is not null &&
                          existing.GetSubKeyNames().Length > 0;
        MissingValueRestoreAction action = PlanMissingValueRestore(
            original.KeyExisted, existing is not null,
            targetPresent, values.Length, hasSubkeys,
            targetPresent ? CheckCurrentAppliedDword(existing, original.Name, expectedAppliedDword) : null);

        switch (action)
        {
            case MissingValueRestoreAction.AlreadyRestored:
                return true;

            case MissingValueRestoreAction.Refuse:
                throw new InvalidOperationException(
                    "Původně neexistující registry hodnota nebo klíč byly změněny. " +
                    "Bezpečné obnovení by mohlo odstranit cizí data.");

            case MissingValueRestoreAction.RemoveOnlyValue:
                using (RegistryKey? writable =
                    baseKey.OpenSubKey(original.Path, writable: true))
                {
                    if (writable is null)
                        throw new IOException("Registry klíč zmizel před obnovou.");
                    if (expectedAppliedDword is not null &&
                        CheckCurrentAppliedDword(writable, original.Name, expectedAppliedDword) != true)
                        throw new InvalidOperationException(
                            "Cizí změna DWORD: původní registry hodnota nebude odstraněna.");
                    writable.DeleteValue(original.Name, throwOnMissingValue: false);
                    if (writable.GetValueNames().Contains(
                            original.Name, StringComparer.OrdinalIgnoreCase))
                        throw new IOException(
                            "Windows nepotvrdil odstranění původně neexistující hodnoty.");
                }
                return true;

            case MissingValueRestoreAction.RemoveOwnedLeaf:
                // Parent must have been absent in the original captured state.
                // Do not touch siblings or use recursive tree deletion.
                int split = original.Path.LastIndexOf('\\');
                if (split <= 0 || split == original.Path.Length - 1)
                    throw new InvalidDataException("Neplatná cesta původního registry klíče.");

                // Close the leaf handle before attempting nonrecursive deletion.
                existing?.Close();

                using (RegistryKey? parent =
                    baseKey.OpenSubKey(original.Path[..split], writable: true))
                {
                    if (parent is null)
                        throw new InvalidOperationException(
                            "Nadřazený registry klíč byl změněn.");
                    string leaf = original.Path[(split + 1)..];
                    // Re-check immediately before deletion to refuse an added value
                    // or child (and let DeleteSubKey reject late child subkeys).
                    using (RegistryKey? fresh = parent.OpenSubKey(leaf, writable: false))
                    {
                        if (fresh is null)
                            throw new IOException(
                                "Registry klíč zmizel před ověřením obnovení.");
                        bool oneValue = fresh.GetValueNames().Length == 1 &&
                            fresh.GetValueNames().Contains(
                                original.Name, StringComparer.OrdinalIgnoreCase) &&
                            CheckCurrentAppliedDword(
                                fresh, original.Name, expectedAppliedDword) == true;
                        if (!oneValue || fresh.GetSubKeyNames().Length != 0)
                            throw new InvalidOperationException(
                                "Registry klíč obsahuje cizí data; odstranění zablokováno.");
                    }

                    parent.DeleteSubKey(leaf, throwOnMissingSubKey: false);
                    using RegistryKey? after = parent.OpenSubKey(leaf, writable: false);
                    if (after is not null)
                        throw new IOException(
                            "Windows nepotvrdil obnovení původně nepřítomného klíče.");
                }
                return true;

            default:
                throw new InvalidOperationException("Neznámý plán obnovy registru.");
        }
    }
}
