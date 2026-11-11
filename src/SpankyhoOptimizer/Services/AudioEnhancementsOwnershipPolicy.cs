namespace SpankyhoOptimizer.Services;

// Pure decision rules for the explicit Audio Enhancements registry transaction.
// Distinguish a value written by us from a removed/changed device property.
internal enum AudioOwnedWriteDecision { Apply, AlreadyApplied, Conflict }
internal enum AudioOwnedRestoreDecision { RestoreOriginal, LeaveExternalChange }

internal static class AudioEnhancementsOwnershipPolicy
{
    // Reject tampered ProgramData rollback paths before opening ANY writable
    // registry key. Audio endpoints have one GUID segment and FxProperties.
    internal static bool IsTrustedEndpointPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        const string render = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\";
        const string capture = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\";
        string remaining = path.StartsWith(render, System.StringComparison.OrdinalIgnoreCase)
            ? path[render.Length..] :
            path.StartsWith(capture, System.StringComparison.OrdinalIgnoreCase)
            ? path[capture.Length..] : string.Empty;
        int separator = remaining.IndexOf('\\');
        return separator > 0 &&
            remaining[(separator + 1)..].Equals("FxProperties",
                System.StringComparison.OrdinalIgnoreCase) &&
            System.Guid.TryParse(remaining[..separator], out _);
    }

    // An endpoint attached while Audio Enhancements is ON needs its own
    // durable original only if it was not disabled independently already.
    internal static bool NeedsNewEndpointSnapshot(
        bool isAlreadyRecorded, bool propertyExists, int currentValue) =>
        !isAlreadyRecorded && (!propertyExists || currentValue != 1);

    internal static AudioOwnedWriteDecision SelectApply(
        bool originallyExisted, int originalValue, bool currentlyExisted, int currentValue)
    {
        if (currentlyExisted && currentValue == 1)
            return AudioOwnedWriteDecision.AlreadyApplied;
        if (originallyExisted == currentlyExisted &&
            (!originallyExisted || currentValue == originalValue))
            return AudioOwnedWriteDecision.Apply;
        return AudioOwnedWriteDecision.Conflict;
    }

    // For an endpoint captured in THIS transaction, a change to disabled (1)
    // after the original snapshot cannot be attributed to this app: we have
    // not written it yet. Existing journal entries may legitimately be 1 from
    // an earlier interrupted apply, so retain SelectApply for those entries.
    internal static AudioOwnedWriteDecision SelectApplyForNewCapture(
        bool originallyExisted, int originalValue, bool currentlyExisted, int currentValue)
    {
        if (currentlyExisted && currentValue == 1 &&
            (!originallyExisted || originalValue != 1))
            return AudioOwnedWriteDecision.Conflict;
        return SelectApply(originallyExisted, originalValue, currentlyExisted, currentValue);
    }

    internal static AudioOwnedRestoreDecision SelectRestore(
        bool currentlyExisted, bool isDword, int currentValue) =>
        currentlyExisted && isDword && currentValue == 1
            ? AudioOwnedRestoreDecision.RestoreOriginal
            : AudioOwnedRestoreDecision.LeaveExternalChange;
}
