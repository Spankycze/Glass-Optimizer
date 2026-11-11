using System;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    // Exercise the production value equality and collision decision WITHOUT
    // opening any HKLM/HKCU key or writing Windows configuration.
    internal static Task RunExistingValueRestoreSmokeAsync()
    {
        static void Check(bool result, string caseName)
        {
            if (!result)
                throw new InvalidOperationException(
                    "EXISTING_VALUE_RESTORE_SMOKE_FAIL: " + caseName);
        }

        // 1-5: Real original-state predicate compares both KIND and value.
        Check(IsExactOriginalValue(true, RegistryValueKind.DWord, 1,
            RegistryValueKind.DWord, 1), "exact original DWORD");
        Check(!IsExactOriginalValue(true, RegistryValueKind.DWord, 0,
            RegistryValueKind.DWord, 1), "different DWORD");
        Check(!IsExactOriginalValue(true, RegistryValueKind.String, "1",
            RegistryValueKind.DWord, 1), "different registry kind");
        Check(!IsExactOriginalValue(false, RegistryValueKind.None, null,
            RegistryValueKind.DWord, 1), "missing current original");
        Check(IsExactOriginalValue(true, RegistryValueKind.String, "",
            RegistryValueKind.String, ""), "empty original string");

        // 6-9: Restore complex old types without losing fidelity.
        Check(IsExactOriginalValue(true, RegistryValueKind.Binary,
            new byte[] { 1, 2, 3 }, RegistryValueKind.Binary,
            new byte[] { 1, 2, 3 }), "original binary exact");
        Check(!IsExactOriginalValue(true, RegistryValueKind.Binary,
            new byte[] { 1, 2, 4 }, RegistryValueKind.Binary,
            new byte[] { 1, 2, 3 }), "binary mismatch");
        Check(IsExactOriginalValue(true, RegistryValueKind.MultiString,
            new[] { "a", "b" }, RegistryValueKind.MultiString,
            new[] { "a", "b" }), "multi-string exact");
        Check(!IsExactOriginalValue(true, RegistryValueKind.MultiString,
            new[] { "b", "a" }, RegistryValueKind.MultiString,
            new[] { "a", "b" }), "multi-string order matters");

        // 10-12: A value that ALREADY matches the original needs NO write.
        Check(PlanExistingValueRestore(true, true) ==
            ExistingValueRestoreDecision.AlreadyOriginal,
            "original restored even if it matches enabled value");
        Check(PlanExistingValueRestore(true, false) ==
            ExistingValueRestoreDecision.AlreadyOriginal,
            "already-original value not overwritten");
        Check(PlanExistingValueRestore(true, null) ==
            ExistingValueRestoreDecision.AlreadyOriginal,
            "legacy already original no-op");

        // 13-15: With known applied DWORD, foreign changes fail CLOSED.
        Check(PlanExistingValueRestore(false, false) ==
            ExistingValueRestoreDecision.RefuseExternalChange,
            "foreign DWORD blocked");
        Check(PlanExistingValueRestore(false,
            MatchesExpectedAppliedDword(RegistryValueKind.String, "1", 1)) ==
            ExistingValueRestoreDecision.RefuseExternalChange,
            "foreign REG_SZ blocked");
        Check(PlanExistingValueRestore(false,
            MatchesExpectedAppliedDword(RegistryValueKind.DWord, 2, 1)) ==
            ExistingValueRestoreDecision.RefuseExternalChange,
            "foreign REG_DWORD changed data blocked");

        // 16: The expected DWORD written by our own tweak may be replaced
        // by its original only when exact ownership is verified.
        Check(PlanExistingValueRestore(false, true) ==
            ExistingValueRestoreDecision.RestoreOriginal,
            "verified applied DWORD may restore original");

        // 17: No previous value in a present original key is NOT a verified
        // applied DWORD, hence known-expected OFF refuses to fabricate it.
        Check(PlanExistingValueRestore(false,
            MatchesExpectedAppliedDword(RegistryValueKind.None, null, 1)) ==
            ExistingValueRestoreDecision.RefuseExternalChange,
            "externally deleted DWORD blocked");

        // 18: Historical clients without applied value remain compatible.
        Check(PlanExistingValueRestore(false, null) ==
            ExistingValueRestoreDecision.RestoreOriginal,
            "legacy unknown expected remains compatible");

        // 19: A policy originally QWORD with an applied DWORD verifies the
        // correct active value and still returns its original QWORD.
        Check(IsExactOriginalValue(true, RegistryValueKind.QWord, 123L,
            RegistryValueKind.QWord, 123L) &&
            PlanExistingValueRestore(false,
                MatchesExpectedAppliedDword(RegistryValueKind.DWord, 0, 0)) ==
            ExistingValueRestoreDecision.RestoreOriginal,
            "QWORD original / DWORD applied");

        // 20: The same DWORD result would not authorize a write if the
        // active value is present with a different registry kind.
        Check(PlanExistingValueRestore(false,
            MatchesExpectedAppliedDword(RegistryValueKind.QWord, 0L, 0)) ==
            ExistingValueRestoreDecision.RefuseExternalChange,
            "QWORD current cannot masquerade as DWORD");

        return Task.CompletedTask;
    }
}
