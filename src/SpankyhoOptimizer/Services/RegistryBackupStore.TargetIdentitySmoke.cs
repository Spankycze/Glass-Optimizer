using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    // Pure identity-contract tests; fake definitions and records only.
    // No Windows registry, file, or system setting is read or changed.
    internal static Task RunBackupTargetIdentitySmokeAsync()
    {
        static void Check(bool result, string name)
        {
            if (!result)
                throw new InvalidOperationException(
                    "REGISTRY_BACKUP_IDENTITY_SMOKE_FAIL: " + name);
        }

        static TweakDefinition Tweak(string id, RegistryHive hive,
            string path, string name) => new()
            {
                Id = id,
                Section = "system",
                Group = "Tests",
                Name = "Target identity",
                Description = "In-memory target identity test",
                RegistryHive = hive,
                RegistryPath = path,
                RegistryValueName = name
            };

        const string id = "gt.smoke.identity";
        const string path = @"SOFTWARE\Policies\Example";
        var saved = new RegistryBackupValue(
            id, RegistryHive.LocalMachine.ToString(), path, "Enabled",
            true, "DWord", "AQID", true);

        // 1. Exact previous target remains valid, without changing snapshot.
        ValidateBackupTargetIdentity(
            id, RegistryHive.LocalMachine, path, "Enabled", saved);
        Check(saved.ValueBase64 == "AQID" && saved.Existed, "exact identity preserves original");

        // 2. Case-insensitivity is faithful to the registry namespace.
        ValidateBackupTargetIdentity(
            "GT.SMOKE.IDENTITY", RegistryHive.LocalMachine,
            @"software\POLICIES\example", "enabled", saved);
        Check(true, "case-insensitive target");

        // 3. Trailing path separator does not imply a new registry key.
        ValidateBackupTargetIdentity(
            id, RegistryHive.LocalMachine, path + "\\", "Enabled", saved);
        Check(true, "trailing slash compatibility");

        // 4. An old record without KeyExisted remains identity-compatible.
        ValidateBackupTargetIdentity(
            id, RegistryHive.LocalMachine, path, "Enabled",
            saved with { KeyExisted = null });
        Check(true, "legacy original key presence unaffected");

        static bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (InvalidOperationException) { return true; }
        }

        // 5. The same ID may never be reused in another registry hive.
        Check(Rejected(() => ValidateBackupTargetIdentity(
            id, RegistryHive.CurrentUser, path, "Enabled", saved)), "wrong hive");

        // 6. A changed parent path must not silently reuse an old original.
        Check(Rejected(() => ValidateBackupTargetIdentity(
            id, RegistryHive.LocalMachine, @"SOFTWARE\Policies\Other", "Enabled",
            saved)), "changed path");

        // 7. A changed value name is also a different registry target.
        Check(Rejected(() => ValidateBackupTargetIdentity(
            id, RegistryHive.LocalMachine, path, "Other", saved)), "changed value");

        // 8. Corrupted stored public ID cannot impersonate a new snapshot.
        Check(Rejected(() => ValidateBackupTargetIdentity(
            id, RegistryHive.LocalMachine, path, "Enabled",
            saved with { Key = "wrong" })), "inconsistent stored ID");

        // 9. Unknown stored hive never matches a known caller.
        Check(Rejected(() => ValidateBackupTargetIdentity(
            id, RegistryHive.LocalMachine, path, "Enabled",
            saved with { Hive = "UnknownHive" })), "invalid saved hive");

        // 10. Missing target metadata is rejected rather than defaulted.
        Check(Rejected(() => ValidateBackupTargetIdentity(
            id, RegistryHive.LocalMachine, path, "",
            saved)), "missing target value");

        // 11. Whitespace ID is never an acceptable snapshot key.
        Check(Rejected(() => ValidateBackupTargetIdentity(
            " ", RegistryHive.LocalMachine, path, "Enabled", saved)), "invalid id");

        // 12. Missing snapshot fails without side effects.
        bool missingSnapshotRejected = false;
        try
        {
            ValidateBackupTargetIdentity(
                id, RegistryHive.LocalMachine, path, "Enabled", null!);
        }
        catch (ArgumentNullException) { missingSnapshotRejected = true; }
        Check(missingSnapshotRejected, "null snapshot");

        // 13. A new, conflict-free first-original batch passes.
        var first = new[]
        {
            Tweak(id, RegistryHive.LocalMachine, path, "Enabled"),
            Tweak("gt.smoke.second", RegistryHive.CurrentUser, path, "Other")
        };
        var empty = new Dictionary<string, RegistryBackupValue>(
            StringComparer.OrdinalIgnoreCase);
        ValidateBackupBatchIdentities(first, empty);
        Check(empty.Count == 0, "new batch does not invent backups");

        // 14. A batch hitting its existing first original passes.
        var existing = new Dictionary<string, RegistryBackupValue>(
            StringComparer.OrdinalIgnoreCase) { [id] = saved };
        ValidateBackupBatchIdentities(first, existing);
        Check(existing[id].ValueBase64 == "AQID", "batch keeps first original");

        // 15. Duplicate identical ID/target in one batch is harmless.
        ValidateBackupBatchIdentities(
            new[] { first[0], first[0] }, empty);
        Check(true, "identical duplicate");

        // 16. A duplicate ID with DIFFERENT parent is a conflict.
        Check(Rejected(() => ValidateBackupBatchIdentities(new[]
            {
                first[0], Tweak(id, RegistryHive.LocalMachine,
                    @"SOFTWARE\Policies\Other", "Enabled")
            }, empty)), "batch path collision");

        // 17. A duplicate ID with DIFFERENT value name is a conflict.
        Check(Rejected(() => ValidateBackupBatchIdentities(new[]
            {
                first[0], Tweak(id, RegistryHive.LocalMachine, path, "Flag")
            }, empty)), "batch value name collision");

        // 18. A duplicate ID across HKCU and HKLM is a conflict.
        Check(Rejected(() => ValidateBackupBatchIdentities(new[]
            {
                first[0], Tweak(id, RegistryHive.CurrentUser, path, "Enabled")
            }, empty)), "batch hive collision");

        // 19. Existing historical snapshot mismatch blocks an ENTIRE batch.
        Check(Rejected(() => ValidateBackupBatchIdentities(new[]
            {
                first[1], Tweak(id, RegistryHive.LocalMachine,
                    @"SOFTWARE\Policies\Different", "Enabled")
            }, existing)) && existing.Count == 1, "existing snapshot collision");

        // 20. A case-insensitive duplicate is the same ID, not a second one.
        ValidateBackupBatchIdentities(new[]
            {
                first[0], Tweak("GT.SMOKE.IDENTITY", RegistryHive.LocalMachine,
                    @"software\policies\example", "enabled")
            }, empty);
        Check(true, "duplicate ID normalization");

        // 21. Invalid ID in a batch fails before any registry scan.
        Check(Rejected(() => ValidateBackupBatchIdentities(
            new[] { Tweak(" ", RegistryHive.LocalMachine, path, "Enabled") },
            empty)), "batch blank id");

        // 22. A definition missing its actual target is refused.
        Check(Rejected(() => ValidateBackupBatchIdentities(
            new[] { Tweak("other", RegistryHive.LocalMachine, path, "") },
            empty)), "batch missing target");

        // 23. Null batch argument must be explicitly rejected.
        bool nullBatchRejected = false;
        try { ValidateBackupBatchIdentities(null!, empty); }
        catch (ArgumentNullException) { nullBatchRejected = true; }
        Check(nullBatchRejected, "null batch rejected");

        // 24. Null lookup must not be treated as an empty backup set.
        bool nullLookupRejected = false;
        try { ValidateBackupBatchIdentities(first, null!); }
        catch (ArgumentNullException) { nullLookupRejected = true; }
        Check(nullLookupRejected, "null lookup rejected");

        return Task.CompletedTask;
    }
}
