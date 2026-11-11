using System;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    internal enum ExistingValueRestoreDecision
    {
        AlreadyOriginal,
        RestoreOriginal,
        RefuseExternalChange
    }

    // This decision is used directly by the real RegistryBackupStore.RestoreAsync
    // and exercised independently with in-memory registry-state simulations.
    // expectedAppliedDwordMatches=null denotes a historical caller which did not
    // supply its applied value. Preserve that API's behavior, but never interpret
    // missing ownership proof as permission to remove the original key.
    internal static ExistingValueRestoreDecision PlanExistingValueRestore(
        bool alreadyEqualsOriginal, bool? expectedAppliedDwordMatches)
    {
        if (alreadyEqualsOriginal)
            return ExistingValueRestoreDecision.AlreadyOriginal;

        return expectedAppliedDwordMatches == false
            ? ExistingValueRestoreDecision.RefuseExternalChange
            : ExistingValueRestoreDecision.RestoreOriginal;
    }

    internal static bool IsExactOriginalValue(
        bool valuePresent, RegistryValueKind actualKind, object? currentValue,
        RegistryValueKind originalKind, object? originalValue) =>
        valuePresent && actualKind == originalKind &&
        RegistryValuesEqual(currentValue, originalValue);
}
