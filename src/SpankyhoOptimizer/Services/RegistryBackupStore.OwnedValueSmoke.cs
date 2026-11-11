using System;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    // Pure tests of actual restore ownership predicate. All cases run in
    // memory: no HKLM/HKCU reads, writes, restarts or Windows configuration.
    internal static Task RunOwnedValueSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException(
                "REGISTRY_OWNED_VALUE_SMOKE_FAIL: " + scenario);
        }

        // 1. A DWORD with exact type and value is ours for restore purposes.
        Check(MatchesExpectedAppliedDword(RegistryValueKind.DWord, 1, 1) == true,
            "matching enabled 1");
        // 2. The feature may have enabled DWORD 0 instead.
        Check(MatchesExpectedAppliedDword(RegistryValueKind.DWord, 0, 0) == true,
            "matching enabled 0");
        // 3. Custom numbers are not hardcoded to 0/1.
        Check(MatchesExpectedAppliedDword(RegistryValueKind.DWord, 100, 100) == true,
            "matching enabled 100");
        // 4. Same key/name but different current data is not proof of ownership.
        Check(MatchesExpectedAppliedDword(RegistryValueKind.DWord, 2, 1) == false,
            "external DWORD mismatch");
        // 5. Same digits encoded as REG_SZ are not the original DWORD write.
        Check(MatchesExpectedAppliedDword(RegistryValueKind.String, "1", 1) == false,
            "foreign REG_SZ kind");
        // 6. Wrong kind with boxed int is equally foreign.
        Check(MatchesExpectedAppliedDword(RegistryValueKind.QWord, 1, 1) == false,
            "foreign QWORD kind");
        // 7. Absent value cannot pass ownership.
        Check(MatchesExpectedAppliedDword(RegistryValueKind.None, null, 1) == false,
            "absent value not owned");
        // 8. Legacy clients with no expected postwrite DWORD remain unknown.
        Check(MatchesExpectedAppliedDword(RegistryValueKind.DWord, 1, null) is null,
            "unknown legacy ownership");

        // 9. New originally absent key with verified DWORD can be removed.
        Check(PlanMissingValueRestore(false, true, true, 1, false, true) ==
            MissingValueRestoreAction.RemoveOwnedLeaf,
            "new single owned leaf removable");
        // 10. Never remove an originally absent key just for matching name.
        Check(PlanMissingValueRestore(false, true, true, 1, false, null) ==
            MissingValueRestoreAction.Refuse,
            "unknown ownership refuses leaf removal");
        // 11. Different current DWORD blocks entire-leaf removal.
        Check(PlanMissingValueRestore(false, true, true, 1, false, false) ==
            MissingValueRestoreAction.Refuse,
            "changed DWORD blocks leaf removal");
        // 12. Foreign sibling value blocks removal, even with owned DWORD.
        Check(PlanMissingValueRestore(false, true, true, 2, false, true) ==
            MissingValueRestoreAction.Refuse,
            "additional registry values retained");
        // 13. Foreign subkey blocks nonrecursive leaf removal.
        Check(PlanMissingValueRestore(false, true, true, 1, true, true) ==
            MissingValueRestoreAction.Refuse,
            "child registry key retained");
        // 14. A key that existed originally is never removed as a leaf.
        Check(PlanMissingValueRestore(true, true, true, 1, false, true) ==
            MissingValueRestoreAction.RemoveOnlyValue,
            "existing original key keeps parent");
        // 15. A foreign current DWORD may not be removed from original key.
        Check(PlanMissingValueRestore(true, true, true, 1, false, false) ==
            MissingValueRestoreAction.Refuse,
            "changed DWORD in original parent preserved");
        // 16. Old snapshots without an expected DWORD only remove target
        // value in originally existing keys, not the entire parent.
        Check(PlanMissingValueRestore(null, true, true, 1, false, null) ==
            MissingValueRestoreAction.RemoveOnlyValue,
            "legacy parent preserved");
        // 17. A value that has already vanished needs no mutation.
        Check(PlanMissingValueRestore(true, true, false, 0, false, false) ==
            MissingValueRestoreAction.AlreadyRestored,
            "target already absent");
        // 18. Missing previously present key is an error, never fabricated.
        Check(PlanMissingValueRestore(true, false, false, 0, false, null) ==
            MissingValueRestoreAction.Refuse,
            "missing original parent fail closed");

        return Task.CompletedTask;
    }
}
