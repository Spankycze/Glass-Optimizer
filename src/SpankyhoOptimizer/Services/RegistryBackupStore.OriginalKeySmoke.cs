using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    // The real production restore policy is exercised without touching HKLM
    // or HKCU. Also round-trip both current and historical JSON shapes.
    internal static Task RunOriginalKeySmokeAsync()
    {
        static void Check(bool ok, string test)
        {
            if (!ok) throw new InvalidOperationException(
                "REGISTRY_ORIGINAL_KEY_SMOKE_FAIL: " + test);
        }

        const string path = @"SOFTWARE\Policies\Microsoft\Example";
        const string id = "smoke.registry-key";

        // 1-3: Old snapshots can carry unknown parent existence (null);
        // freshly captured original states are explicitly true/false.
        var legacy = new RegistryBackupValue(
            id, "LocalMachine", path, "Enabled", false, "None", null);
        Check(legacy.KeyExisted is null, "legacy missing metadata");
        var present = legacy with { KeyExisted = true };
        Check(present.KeyExisted == true, "present original key");
        var missing = legacy with { KeyExisted = false };
        Check(missing.KeyExisted == false, "absent original key");

        // 4-5: New metadata is durably round-trippable through the actual
        // source-generated RegistryBackupStore JSON serializer.
        var records = new Dictionary<string, RegistryBackupValue>
        {
            ["present"] = present,
            ["missing"] = missing
        };
        string json = JsonSerializer.Serialize(
            records, typeof(Dictionary<string, RegistryBackupValue>),
            RegistryBackupJsonContext.Default);
        var roundtrip = JsonSerializer.Deserialize(
            json, typeof(Dictionary<string, RegistryBackupValue>),
            RegistryBackupJsonContext.Default) as Dictionary<string, RegistryBackupValue>;
        Check(roundtrip is not null && roundtrip["present"].KeyExisted == true,
            "JSON records present key");
        Check(roundtrip is not null && roundtrip["missing"].KeyExisted == false,
            "JSON records absent key");

        // 6: A historical JSON record that never had this property must
        // remain readable without incorrectly pretending it was absent.
        const string legacyJson =
            "{\"legacy\":{\"Key\":\"legacy\",\"Hive\":\"LocalMachine\"," +
            "\"Path\":\"SOFTWARE\\\\Policies\\\\Example\"," +
            "\"Name\":\"Enabled\",\"Existed\":false,\"Kind\":\"None\"," +
            "\"ValueBase64\":null}}";
        var old = JsonSerializer.Deserialize(
            legacyJson, typeof(Dictionary<string, RegistryBackupValue>),
            RegistryBackupJsonContext.Default) as Dictionary<string, RegistryBackupValue>;
        Check(old is not null && old["legacy"].KeyExisted is null,
            "backwards-compatible JSON");

        // 7. An originally missing leaf which remains missing is restored.
        Check(PlanMissingValueRestore(
            false, false, false, 0, false) ==
            MissingValueRestoreAction.AlreadyRestored, "absent key unchanged");

        // 8. Missing original key metadata (legacy) must not be guessed.
        Check(PlanMissingValueRestore(
            null, false, false, 0, false) ==
            MissingValueRestoreAction.AlreadyRestored, "legacy no key now");

        // 9. A key known to exist originally must not be recreated if
        // another application has removed it.
        Check(PlanMissingValueRestore(
            true, false, false, 0, false) ==
            MissingValueRestoreAction.Refuse, "original key removed externally");

        // 10. Our app-owned singleton value in an originally absent key
        // allows nonrecursive removal of only that exact leaf.
        Check(PlanMissingValueRestore(
            false, true, true, 1, false, true) ==
            MissingValueRestoreAction.RemoveOwnedLeaf, "owned absent leaf");

        // 11. An extra value means the created leaf now belongs to others too.
        Check(PlanMissingValueRestore(
            false, true, true, 2, false) ==
            MissingValueRestoreAction.Refuse, "foreign value protection");

        // 12. Child keys must be preserved, never recursively removed.
        Check(PlanMissingValueRestore(
            false, true, true, 1, true) ==
            MissingValueRestoreAction.Refuse, "foreign child protection");

        // 13. Empty external leaf cannot be proven ours; refuse deletion.
        Check(PlanMissingValueRestore(
            false, true, false, 0, false) ==
            MissingValueRestoreAction.Refuse, "unknown empty leaf");

        // 14. A single value of another name cannot justify deleting a key.
        Check(PlanMissingValueRestore(
            false, true, false, 1, false) ==
            MissingValueRestoreAction.Refuse, "foreign singleton protection");

        // 15. Invalid registry counts are rejected.
        Check(PlanMissingValueRestore(
            false, true, true, 0, false) ==
            MissingValueRestoreAction.Refuse, "inconsistent count protection");

        // 16. When parent originally existed, remove ONLY the owned value.
        Check(PlanMissingValueRestore(
            true, true, true, 1, false) ==
            MissingValueRestoreAction.RemoveOnlyValue, "original parent retained");

        // 17. Values added alongside the target must be left alone.
        Check(PlanMissingValueRestore(
            true, true, true, 2, true) ==
            MissingValueRestoreAction.RemoveOnlyValue, "original parent with foreign content");

        // 18. An originally present parent with no target needs no work.
        Check(PlanMissingValueRestore(
            true, true, false, 3, true) ==
            MissingValueRestoreAction.AlreadyRestored, "already absent target");

        // 19. Legacy records may remove the target but NEVER the parent.
        Check(PlanMissingValueRestore(
            null, true, true, 1, false) ==
            MissingValueRestoreAction.RemoveOnlyValue, "legacy preserve parent");

        // 20. Even with other values or subtrees a legacy restore must not
        // delete the parent; it can only target its own value.
        Check(PlanMissingValueRestore(
            null, true, true, 4, true) ==
            MissingValueRestoreAction.RemoveOnlyValue, "legacy foreign subtrees preserved");

        return Task.CompletedTask;
    }
}
