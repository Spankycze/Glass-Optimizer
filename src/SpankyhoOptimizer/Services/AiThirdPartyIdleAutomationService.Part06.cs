using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed partial class AiThirdPartyIdleAutomationService
{
    private async Task<bool> RestoreAsync(Owner owner, CancellationToken token)
    {
        try
        {
            if (owner.Kind == "startup")
            {
                // The shared tick inventory can already be minutes old. Even
                // a DISABLED row may have been enabled, removed or repointed
                // while other asynchronous recovery work was running. Always
                // use fresh native HKCU Run evidence before confirming success
                // OR issuing a restorative write to this exact owned entry.
                IReadOnlyList<StartupEntry> latest = await StartupManagerService
                    .GetAiUserRunAsync(token).ConfigureAwait(false);
                if (IsStartupRestoreConfirmed(owner, latest,
                        requireNativeAdmission: true))
                    return true; // Verified existing user restoration.
                if (IsExternallyResolvedStartupWithoutDisabledSlot(owner, latest,
                        requireNativeAdmission: true))
                {
                    // The user replaced the Run command and the AI-owned
                    // disabled slot no longer exists. Nothing remains for AI
                    // to restore; relinquish ownership without ANY Windows
                    // write. A surviving disabled twin always blocks release.
                    LogService.Info("ai-system",
                        $"AI_APP_IDLE_EXTERNAL_STARTUP_RESOLVED app={owner.AppKey} " +
                        "disabledSlotAbsent=true windowsWrites=0");
                    return true;
                }
                StartupEntry? current = FindAdmissibleOwnedRestore(
                    owner, latest, requireNativeAdmission: true);
                if (current is null) return false;
                await StartupManagerService.SetEnabledAsync(current, true, token)
                    .ConfigureAwait(false);
                // Do not discard durable ownership until the actual HKCU
                // Run entry is observed enabled again. SetEnabledAsync may
                // complete despite a later native readback contradiction.
                var confirmed = await StartupManagerService
                    .GetAiUserRunAsync(token).ConfigureAwait(false);
                return IsStartupRestoreConfirmed(owner, confirmed,
                    requireNativeAdmission: true);
            }
            if (owner.Kind == "service")
            {
                using var service = new ServiceController(owner.Id);
                if (service.Status == ServiceControllerStatus.Running) return true;
                if (!AiThirdPartyIdlePolicy.MayRestoreServiceStart(
                        owner.OriginallyRunning, service.Status,
                        (int)service.StartType, owner.OriginalServiceStartMode))
                    return false;
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running,
                    TimeSpan.FromSeconds(8));
                return service.Status == ServiceControllerStatus.Running;
            }
            if (owner.Kind == "task")
            {
                var current = TaskSchedulerComService.GetRuntimeState(owner.Id, token);
                if (current.Enabled) return true;
                // Another host can change task state between inventory and
                // recovery. Reject unrecognized or actively running tasks.
                if (!AiThirdPartyIdlePolicy.MayRestoreFirefoxTask(
                        owner.Id, owner.OriginalEnabled, current.Running))
                    return false;
                TaskSchedulerComService.SetEnabled(owner.Id, true, token);
                return TaskSchedulerComService.GetRuntimeState(owner.Id, token).Enabled;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Manual cancellation cannot be treated as an ordinary blocked
            // candidate. The owned journal remains durable and the caller
            // must report that restoration was interrupted.
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system",
                $"AI app idle restore blocked kind={owner.Kind} id={owner.Id}", ex);
        }
        return false;
    }

    // Registry value names are case-insensitive slots. A different command
    // in the SAME name/hive/view/Run key still occupies that slot. Refuse an
    // AI disable if its reversible SpankyhoDisabled destination is already
    // occupied, rather than persisting a spurious owner and failing only at
    // the native collision gate. Different registry views remain independent.
    private static bool HasRegistrySlot(
        StartupEntry entry, IReadOnlyList<StartupEntry> rows, bool enabled)
    {
        foreach (StartupEntry row in rows)
            if (row.Enabled == enabled &&
                string.Equals(row.SourceKind, "Registry", StringComparison.OrdinalIgnoreCase) &&
                row.Hive == entry.Hive && row.View == entry.View &&
                string.Equals(row.RunPath, entry.RunPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Only one optional HKCU user Run entry can be admitted per AI cycle.
    // Select the first policy-allowed candidate whose reversible destination
    // is EMPTY; never award a disabled value already owned by another writer.
    // Actual auto-writes additionally demand canonical native admission;
    // keep legacy pure policy parity for read-only synthetic fixtures.
    private static StartupEntry? FindAdmissibleStartup(
        string app, IReadOnlyList<StartupEntry> entries,
        bool requireNativeAdmission = false)
    {
        foreach (StartupEntry entry in entries)
            if (AiThirdPartyIdlePolicy.CanManageStartup(app, entry) &&
                (!requireNativeAdmission || CanCommitOwnedStartup(entry)) &&
                !HasRegistrySlot(entry, entries, enabled: false))
                return entry;
        return null;
    }

    // The pre-WAL admission must prove the startup inventory identity is
    // bound to this EXACT native HKCU Run slot. ReadKey builds Id from
    // hive/view/path/name; a stale or malformed mismatched Id must never
    // become a durable owner even if the displayed executable looks valid.
    // Native mutation and the fresh prewrite readback retain their own gates.
    internal static bool CanCommitOwnedStartup(StartupEntry? entry) =>
        entry is { Enabled: true, Hive: Microsoft.Win32.RegistryHive.CurrentUser } &&
        string.Equals(entry.RunPath,
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            StringComparison.OrdinalIgnoreCase) &&
        StartupManagerService.IsAdmissibleRegistryStartupMove(entry) &&
        string.Equals(entry.Id,
            $"{entry.Hive}|{entry.View}|{entry.RunPath}|{entry.Name}",
            StringComparison.OrdinalIgnoreCase);

    // The second lookup happens against a FRESH native snapshot immediately
    // before a persisted startup mutation. Matching the journal identity is
    // mandatory; merely finding some other allowed launcher is insufficient.
    private static bool HasCurrentAdmissibleStartup(
        string app, Owner owner, IReadOnlyList<StartupEntry> entries,
        bool requireNativeAdmission = false)
    {
        foreach (StartupEntry entry in entries)
            if (IsSameStartup(entry, owner) &&
                AiThirdPartyIdlePolicy.CanManageStartup(app, entry) &&
                (!requireNativeAdmission || CanCommitOwnedStartup(entry)) &&
                !HasRegistrySlot(entry, entries, enabled: false))
                return true;
        return false;
    }

    // A postwrite disabled row is not sufficient if Run was concurrently
    // repopulated under the same name (even with a changed command).
    // Preserve the durable owner when readback is not unambiguous.
    private static bool IsStartupDisableConfirmed(
        Owner owner, StartupEntry original, IReadOnlyList<StartupEntry> rows,
        bool requireNativeAdmission = false) =>
        rows.Any(row => !row.Enabled && IsSameStartup(row, owner) &&
            (!requireNativeAdmission || CanRestoreOwnedStartup(owner, row))) &&
        !HasRegistrySlot(original, rows, enabled: true);

    // These direct loops avoid allocating a captured LINQ predicate on
    // every owned-startup recovery. Ordering is identical to FirstOrDefault
    // and the acceptance criteria are deliberately unchanged.
    private static StartupEntry? FindMatchingStartup(
        Owner owner, IReadOnlyList<StartupEntry> current)
    {
        foreach (StartupEntry row in current)
            if (IsSameStartup(row, owner)) return row;
        return null;
    }

    // A fresh restore candidate must be the exact persisted owner identity,
    // still disabled, and an explicitly allowed end-user launcher. Never
    // operate on a stale StartupEntry captured before awaited native work.
    private static StartupEntry? FindAdmissibleOwnedRestore(
        Owner owner, IReadOnlyList<StartupEntry> current,
        bool requireNativeAdmission = false)
    {
        // Registry value slots are identified by name, hive, view and path,
        // NOT command. A user-modified Run value occupies the destination
        // even if its command no longer equals our durable owner identity.
        // Do not attempt a native move into that occupied destination.
        if (HasOwnedStartupSlot(owner, current, enabled: true))
            return null;
        StartupEntry? entry = FindMatchingStartup(owner, current);
        return entry is { Enabled: false } &&
            (!requireNativeAdmission || CanRestoreOwnedStartup(owner, entry)) &&
            AiThirdPartyIdlePolicy.CanManageStartup(
                owner.AppKey, entry with { Enabled = true }) ? entry : null;
    }

    // Disabled registry inventory IDs refer to the PHYSICAL backup path,
    // unlike the active Run ID persisted in our owner. Never use the
    // enabled-entry CanCommitOwnedStartup gate for a legitimate restore.
    // ReadKey emits CurrentUser|View|RunPath\SpankyhoDisabled|Name.
    private static bool CanRestoreOwnedStartup(Owner owner, StartupEntry? entry) =>
        owner.Kind == "startup" && owner.OriginalEnabled &&
        entry is { Enabled: false, Hive: Microsoft.Win32.RegistryHive.CurrentUser } &&
        string.Equals(owner.StartupRunPath,
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.RunPath, owner.StartupRunPath,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Name, owner.StartupName,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Command, owner.StartupCommand,
            StringComparison.Ordinal) &&
        (int)entry.View == owner.StartupView &&
        string.Equals(owner.Id,
            $"CurrentUser|{entry.View}|{entry.RunPath}|{entry.Name}",
            StringComparison.OrdinalIgnoreCase) &&
        StartupManagerService.IsAdmissibleRegistryStartupMove(entry) &&
        string.Equals(entry.Id,
            $"CurrentUser|{entry.View}|{entry.RunPath}\\SpankyhoDisabled|{entry.Name}",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasMatchingStartupState(
        Owner owner, IReadOnlyList<StartupEntry> current, bool enabled)
    {
        foreach (StartupEntry row in current)
            if (row.Enabled == enabled && IsSameStartup(row, owner)) return true;
        return false;
    }

    private static bool HasEnabledMatchingStartup(
        Owner owner, IReadOnlyList<StartupEntry> current) =>
        HasMatchingStartupState(owner, current, enabled: true);

    private static bool HasOwnedStartupSlot(
        Owner owner, IReadOnlyList<StartupEntry> current, bool enabled)
    {
        foreach (StartupEntry row in current)
            if (row.Enabled == enabled && IsSameStartupSlot(row, owner))
                return true;
        return false;
    }

    // Release an obsolete startup journal ONLY when a user-controlled Run
    // value occupies the original physical slot, its command has changed,
    // and the ENTIRE corresponding disabled slot is absent. Otherwise the
    // AI may still own a disabled value and must retain recovery ownership.
    // This is a pure read-only decision; it does not authorize a registry write.
    private static bool IsExternallyResolvedStartupWithoutDisabledSlot(
        Owner owner, IReadOnlyList<StartupEntry> current,
        bool requireNativeAdmission = false) =>
        owner.Kind == "startup" && owner.OriginalEnabled &&
        // A present-but-empty Run string is not evidence of a legitimate
        // user replacement. Never discard durable AI restore ownership
        // based on a blank/unusable enabled command.
        current.Any(row => row.Enabled && IsSameStartupSlot(row, owner) &&
            (!requireNativeAdmission || CanCommitOwnedStartup(row)) &&
            !string.IsNullOrWhiteSpace(row.Command)) &&
        !HasEnabledMatchingStartup(owner, current) &&
        !HasOwnedStartupSlot(owner, current, enabled: false);

    private static bool IsStartupRestoreConfirmed(
        Owner owner, IReadOnlyList<StartupEntry> current,
        bool requireNativeAdmission = false)
    {
        // A restored Run value is not enough if ANY hidden value still
        // occupies the same disabled slot, even after a user edited its
        // command. Keep the durable journal on ambiguous native evidence.
        bool enabled = false;
        foreach (StartupEntry row in current)
        {
            if (!IsSameStartupSlot(row, owner)) continue;
            if (!row.Enabled) return false;
            if (IsSameStartup(row, owner) &&
                (!requireNativeAdmission || CanCommitOwnedStartup(row)) &&
                AiThirdPartyIdlePolicy.CanManageStartup(owner.AppKey, row))
                enabled = true;
        }
        return enabled;
    }

    private static bool IsSameStartupSlot(StartupEntry row, Owner owner) =>
        string.Equals(row.SourceKind, "Registry", StringComparison.OrdinalIgnoreCase) &&
        row.Hive == Microsoft.Win32.RegistryHive.CurrentUser &&
        string.Equals(row.Name, owner.StartupName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.RunPath, owner.StartupRunPath, StringComparison.OrdinalIgnoreCase) &&
        (int)row.View == owner.StartupView;

    private static bool IsSameStartup(StartupEntry row, Owner owner) =>
        IsSameStartupSlot(row, owner) &&
        // Registry command values are bytes/characters, not an executable
        // name search. Even casing drift may indicate an external user edit.
        string.Equals(row.Command, owner.StartupCommand, StringComparison.Ordinal);

    private static string Key(string kind, string id) => kind + "|" + id;

    private const int MaxJournalBytes = 262144;

    // A FileInfo.Length check followed by File.ReadAllText has a TOCTOU
    // window if an external writer doesn't honor our cross-host lease.
    // Bound the ACTUAL bytes consumed, and reject invalid UTF-8 rather than
    // silently replacing malformed journal identity characters.
    private readonly record struct BoundedJournalPayload(
        string Text, byte[] Digest);

    // JsonSerializer's Dictionary behavior can silently overwrite duplicate
    // JSON property names BEFORE HasValidOwnedJournal sees them. Reject all
    // ambiguous owner identity at the JSON-token boundary, including exact,
    // case-only and escaped-equivalent duplicates. Parsing is performed only
    // on bounded journal loads/UI navigation, never on hot metadata probes.
    private static Journal? DeserializeJournalStrict(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 32,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AI owner journal root is not an object");

        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool ownedPresent = false;
        foreach (JsonProperty prop in document.RootElement.EnumerateObject())
        {
            if (!rootNames.Add(prop.Name))
                throw new InvalidDataException("Duplicate AI journal root property");
            if (!prop.NameEquals("Owned")) continue;
            ownedPresent = true;
            if (prop.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("AI owner journal entries must be an object");
            var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty entry in prop.Value.EnumerateObject())
            {
                if (owners.Count >= 64 || !owners.Add(entry.Name))
                    throw new InvalidDataException(
                        "Duplicate or excessive AI owner identity in JSON");
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("AI owner value must be an object");
                // Duplicate/case-variant fields within a single owner could
                // also change e.g. Id/Kind/Pending during deserialization.
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty field in entry.Value.EnumerateObject())
                    if (!fields.Add(field.Name))
                        throw new InvalidDataException(
                            "Duplicate field in AI owner identity JSON");
            }
        }
        if (!ownedPresent)
            throw new InvalidDataException("AI owner journal has no Owned section");
        return document.RootElement.Deserialize<Journal>();
    }

    // Read-only AI page does not consume a durable SHA-256 journal stamp.
    // Keep the SAME byte bound and strict UTF-8 decoding as the leased
    // recovery path but skip the unused digest during UI navigation.
    private static string ReadBoundedJournalText(Stream source) =>
        DecodeBoundedJournalText(ReadBoundedJournalBytes(source));

    private static string DecodeBoundedJournalText(ArraySegment<byte> bytes) =>
        StrictJournalUtf8.GetString(bytes.AsSpan()).TrimStart('\uFEFF');

    // One bounded byte read serves both strict JSON decode on normal load
    // and hash-only conflict checking before an atomic journal swap.
    // A hash-only check must not decode another UTF-8 string per commit.
    private static ArraySegment<byte> ReadBoundedJournalBytes(Stream source)
    {
        // Journal files are seekable: allocate their exact remaining length
        // once instead of doubling MemoryStream's byte[] several times.
        // Keep the bounded streaming loop below: the file may grow after
        // checking its length or an input stream may not be seekable.
        int initialCapacity = 0;
        if (source.CanSeek)
        {
            long remaining = source.Length - source.Position;
            if (remaining > MaxJournalBytes)
                throw new InvalidDataException(
                    "AI app owner journal exceeded safe byte limit");
            if (remaining > 0)
                initialCapacity = checked((int)remaining);
        }
        using var output = new MemoryStream(initialCapacity);
        Span<byte> buffer = stackalloc byte[4096];
        while (true)
        {
            int n = source.Read(buffer);
            if (n == 0) break;
            if (output.Length + n > MaxJournalBytes)
                throw new InvalidDataException("AI app owner journal exceeded safe byte limit");
            output.Write(buffer[..n]);
        }
        if (output.Length == 0)
            throw new InvalidDataException("Empty AI app owner journal");
        // MemoryStream's exposable backing buffer survives Dispose.
        // Retain only the actual byte count, never its larger capacity:
        // otherwise a SHA digest could silently include uninitialized
        // capacity bytes and misidentify a valid ownership journal.
        return new ArraySegment<byte>(
            output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static BoundedJournalPayload ReadBoundedJournalPayload(Stream source)
    {
        ArraySegment<byte> bytes = ReadBoundedJournalBytes(source);
        return new BoundedJournalPayload(
            DecodeBoundedJournalText(bytes),
            SHA256.HashData(bytes.AsSpan()));
    }
}
