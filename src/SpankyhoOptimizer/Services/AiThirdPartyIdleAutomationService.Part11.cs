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
    // Lossy recovery applies ONLY to auxiliary outcomes. Active ownership
    // must be validated separately and carried across unchanged. Keep at
    // most the newest 128 valid feedback rows; an optional bad A2 sample
    // is dropped rather than keeping a real native restoration hostage.
    private static Journal RecoverValidLearning(Journal rows, out bool degraded)
    {
        // Most journals are already normalized from our last recovery. If
        // all surviving rows are newest-first with unique resource identity
        // and <=128 entries, avoid sorting and building LINQ iterators on
        // every leased AI scan. On ANY disorder/case collision/overflow,
        // use the unchanged stable legacy pipeline: latest duplicate wins,
        // equal timestamps preserve original order, oldest entries drop.
        Dictionary<string, FeedbackOutcome>? feedback = null;
        if (rows.Feedback is null)
            feedback = new(StringComparer.OrdinalIgnoreCase);
        else if (rows.Feedback.Count <= 128)
        {
            var candidate = new Dictionary<string, FeedbackOutcome>(
                rows.Feedback.Count, StringComparer.OrdinalIgnoreCase);
            DateTime previousUtc = DateTime.MaxValue;
            bool alreadyOrdered = true;
            foreach (var pair in rows.Feedback)
            {
                if (!IsValidPersistedFeedback(pair.Key, pair.Value)) continue;
                if (pair.Value.UpdatedUtc > previousUtc ||
                    !candidate.TryAdd(pair.Key, pair.Value))
                {
                    alreadyOrdered = false;
                    break;
                }
                previousUtc = pair.Value.UpdatedUtc;
            }
            if (alreadyOrdered) feedback = candidate;
        }
        feedback ??= (rows.Feedback ?? new Dictionary<string, FeedbackOutcome>())
            .Where(x => IsValidPersistedFeedback(x.Key, x.Value))
            .OrderByDescending(x => x.Value.UpdatedUtc)
            .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToDictionary(x => x.Key, x => x.Value,
                StringComparer.OrdinalIgnoreCase);

        // A2 references have no ordering or top-N rule: the former
        // Where/DistinctBy/ToDictionary pipeline simply accepted the first
        // valid case-insensitive identity. TryAdd does exactly that in one
        // bounded pass, including invalid-before-valid duplicate cases.
        var references = new Dictionary<string, ReferenceTrial>(
            rows.References?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
        if (rows.References is not null)
            foreach (var pair in rows.References)
                if (IsValidPersistedReference(pair.Key, pair.Value))
                    references.TryAdd(pair.Key, pair.Value);

        degraded = feedback.Count != (rows.Feedback?.Count ?? 0) ||
                   references.Count != (rows.References?.Count ?? 0);
        return rows with { Feedback = feedback, References = references };
    }

    // JSON dictionaries deserialize with ordinal case-sensitive keys,
    // whereas Windows resource identity and our runtime owner maps use
    // OrdinalIgnoreCase. Refuse rather than silently overwrite colliding
    // owners when transferring JSON into the authoritative runtime map.
    // Loaded JSON dictionaries can contain case-only duplicate identities,
    // whereas the live owner map already uses OrdinalIgnoreCase. Validate
    // every owner once and only allocate a duplicate-key set for an untrusted
    // dictionary comparer. Keep the original <=64 owner hard bound.
    private static bool HasValidOwnedJournal(Dictionary<string, Owner>? owned)
    {
        if (owned is null || owned.Count > 64) return false;
        HashSet<string>? identities =
            ReferenceEquals(owned.Comparer, StringComparer.OrdinalIgnoreCase)
                ? null : new HashSet<string>(owned.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in owned)
        {
            if ((identities != null && !identities.Add(pair.Key)) ||
                !IsValidPersistedOwner(pair.Key, pair.Value))
                return false;
        }
        return true;
    }

    // Pure oracle parity against the old LINQ validation: case-only
    // duplicates in untrusted JSON, original count bound and actual owner
    // allowlist must all behave identically. No real journal/SCM/registry.
    internal static bool RunOwnedJournalValidationParitySmoke()
    {
        DateTime at = new(2026, 10, 22, 13, 0, 0, DateTimeKind.Utc);
        static bool Legacy(Dictionary<string, Owner>? owners) =>
            owners is { Count: <= 64 } &&
            owners.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                owners.Count &&
            owners.All(x => IsValidPersistedOwner(x.Key, x.Value));
        var ordinal = new Dictionary<string, Owner>(StringComparer.Ordinal);
        var insensitive = new Dictionary<string, Owner>(
            StringComparer.OrdinalIgnoreCase);
        if (!HasValidOwnedJournal(ordinal) || !HasValidOwnedJournal(insensitive) ||
            HasValidOwnedJournal(null))
            return false;
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string IdFor(int i) =>
            $"CurrentUser|Registry64|Software\\Microsoft\\Windows\\CurrentVersion\\Run|Spotify{i}";
        for (int i = 0; i < 64; i++)
        {
            string id = IdFor(i);
            var owner = new Owner("startup", id, "spotify", at,
                true, false, "Spotify" + i.ToString(CultureInfo.InvariantCulture),
                @"C:\Apps\Spotify.exe", run, 256, false);
            string key = Key(owner.Kind, owner.Id);
            ordinal[key] = owner;
            insensitive[key] = owner;
            if (HasValidOwnedJournal(ordinal) != Legacy(ordinal) ||
                HasValidOwnedJournal(insensitive) != Legacy(insensitive))
                return false;
        }
        if (!HasValidOwnedJournal(ordinal) || !HasValidOwnedJournal(insensitive))
            return false;
        var extended = new Dictionary<string, Owner>(ordinal,
            StringComparer.Ordinal);
        extended[Key("startup", IdFor(64))] = ordinal[Key("startup", IdFor(0))] with
        {
            Id = IdFor(64), StartupName = "Spotify64"
        };
        if (HasValidOwnedJournal(extended) || Legacy(extended)) return false;

        var colliding = new Dictionary<string, Owner>(StringComparer.Ordinal)
        {
            [Key("startup", IdFor(0))] = ordinal[Key("startup", IdFor(0))],
            [Key("STARTUP", IdFor(0)).ToUpperInvariant()] =
                ordinal[Key("startup", IdFor(0))]
        };
        if (HasValidOwnedJournal(colliding) || Legacy(colliding)) return false;

        var malformed = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase)
        {
            [Key("startup", IdFor(0))] = ordinal[Key("startup", IdFor(0))] with
            {
                AppKey = "windows"
            }
        };
        return !HasValidOwnedJournal(malformed) &&
            !Legacy(malformed) &&
            !HasValidOwnedJournal(new Dictionary<string, Owner>
            {
                [Key("startup", IdFor(0))] = ordinal[Key("startup", IdFor(0))] with
                {
                    AppliedUtc = at.ToLocalTime()
                }
            });
    }

    // Owner telemetry is part of the durable recovery record, not trusted
    // optional feedback: NaN/Infinity/out-of-range samples must fail the
    // owner journal closed rather than influence native service recovery.
    private static bool HasValidOwnerTelemetry(Owner o) =>
        o.FeedbackSamples is >= 0 and <= 3 &&
        double.IsFinite(o.SumCpuPercent) && o.SumCpuPercent >= 0 &&
        double.IsFinite(o.SumRamMiB) && o.SumRamMiB >= 0 &&
        (!o.BaselineCpuPercent.HasValue ||
            (double.IsFinite(o.BaselineCpuPercent.Value) &&
             o.BaselineCpuPercent.Value is >= 0 and <= 100)) &&
        (!o.BaselineRamMiB.HasValue ||
            (double.IsFinite(o.BaselineRamMiB.Value) &&
             o.BaselineRamMiB.Value > 0)) &&
        (!o.MinCpuPercent.HasValue ||
            (double.IsFinite(o.MinCpuPercent.Value) &&
             o.MinCpuPercent.Value is >= 0 and <= 100)) &&
        (!o.MaxCpuPercent.HasValue ||
            (double.IsFinite(o.MaxCpuPercent.Value) &&
             o.MaxCpuPercent.Value is >= 0 and <= 100)) &&
        (!o.MinRamMiB.HasValue ||
            (double.IsFinite(o.MinRamMiB.Value) && o.MinRamMiB.Value > 0)) &&
        (!o.MaxRamMiB.HasValue ||
            (double.IsFinite(o.MaxRamMiB.Value) && o.MaxRamMiB.Value > 0)) &&
        (o.LastFeedbackSampleUtc == default ||
            (o.LastFeedbackSampleUtc.Kind == DateTimeKind.Utc &&
             o.LastFeedbackSampleUtc >= o.AppliedUtc &&
             o.LastFeedbackSampleUtc <= DateTime.UtcNow.AddMinutes(5))) &&
        (o.FeedbackSamples == 0 || !o.Pending);

    // Fail closed on malformed persisted owners: a corrupted/misplaced
    // journal must never authorize arbitrary service start or task enable.
    // Only IDs that this implementation can itself create are acceptable.
    private static bool IsValidPersistedOwner(string key, Owner? owner)
    {
        if (owner is null || string.IsNullOrWhiteSpace(owner.Id) ||
            string.IsNullOrWhiteSpace(owner.AppKey) || !owner.OriginalEnabled ||
            owner.AppliedUtc.Kind != DateTimeKind.Utc ||
            owner.AppliedUtc.Ticks == 0 ||
            owner.AppliedUtc > DateTime.UtcNow.AddMinutes(5) ||
            !key.Equals(Key(owner.Kind, owner.Id), StringComparison.OrdinalIgnoreCase) ||
            !HasValidOwnerTelemetry(owner))
            return false;

        if (owner.Kind == "service")
            return owner.Id.Equals("Steam Client Service",
                       StringComparison.OrdinalIgnoreCase) &&
                   owner.AppKey.Equals("steam", StringComparison.OrdinalIgnoreCase) &&
                   owner.OriginallyRunning &&
                   (owner.OriginalServiceStartMode is null or 2 or 3);
        if (owner.Kind == "task")
            return owner.AppKey.Equals("firefox", StringComparison.OrdinalIgnoreCase) &&
                   AiThirdPartyIdlePolicy.MatchesOptionalFirefoxTask(owner.Id);
        if (owner.Kind == "startup")
            return IsManagedJournalApp(owner.AppKey) &&
                   !string.IsNullOrWhiteSpace(owner.StartupName) &&
                   !string.IsNullOrWhiteSpace(owner.StartupCommand) &&
                   // JSON may explicitly contain null despite non-null C#
                   // annotations. Reject it, never crash journal recovery.
                   string.Equals(owner.StartupRunPath,
                       @"Software\Microsoft\Windows\CurrentVersion\Run",
                       StringComparison.OrdinalIgnoreCase) &&
                   owner.StartupView is 256 or 512 &&
                   string.Equals(owner.Id,
                       $"CurrentUser|{(Microsoft.Win32.RegistryView)owner.StartupView}|{owner.StartupRunPath}|{owner.StartupName}",
                       StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private void CaptureJournalStamp(byte[]? validatedDigest = null)
    {
        var file = new FileInfo(JournalPath);
        file.Refresh();
        _journalStampExists = file.Exists;
        _journalStampLength = file.Exists ? file.Length : 0;
        _journalStampWriteUtc = file.Exists ? file.LastWriteTimeUtc : default;
        _journalDigest = file.Exists ? validatedDigest : null;
        _journalStampKnown = true;
    }

    // Read-only FileInfo metadata every 10s, never JSON parsing, service
    // enumeration or new background task. A stamp change still goes through
    // the same exclusive lease and full journal validation.
    internal static bool HasJournalStampChanged(
        bool knownExists, long knownLength, DateTime knownWriteUtc,
        bool currentExists, long currentLength, DateTime currentWriteUtc) =>
        knownExists != currentExists ||
        (currentExists && (knownLength != currentLength ||
                           knownWriteUtc != currentWriteUtc));

    private bool ExternalJournalChangeDue(DateTime now)
    {
        if (_lastJournalMetadataProbeUtc.Kind == DateTimeKind.Utc &&
            now >= _lastJournalMetadataProbeUtc &&
            now - _lastJournalMetadataProbeUtc < TimeSpan.FromSeconds(10))
            return false;
        _lastJournalMetadataProbeUtc = now;
        if (!_journalStampKnown) return true;
        try
        {
            var file = new FileInfo(JournalPath);
            file.Refresh();
            return HasJournalStampChanged(
                _journalStampExists, _journalStampLength, _journalStampWriteUtc,
                file.Exists, file.Exists ? file.Length : 0,
                file.Exists ? file.LastWriteTimeUtc : default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // Indeterminate metadata must not hide recovery.
        }
    }

    // Best-effort optimistic guard against EXTERNAL writers which ignore
    // our cross-Host .lease: reject an overwrite if the source changed
    // after our last complete, validated journal read or atomic write.
    // This is intentionally not claimed as a filesystem CAS guarantee.
    internal static bool CanReplaceJournalStamp(
        bool stampKnown, bool oldExists, long oldLength, DateTime oldUtc,
        bool diskExists, long diskLength, DateTime diskUtc) =>
        stampKnown && !HasJournalStampChanged(
            oldExists, oldLength, oldUtc, diskExists, diskLength, diskUtc);

    private static bool MatchesJournalDigest(byte[]? expected, byte[]? actual) =>
        expected is { Length: 32 } && actual is { Length: 32 } &&
        CryptographicOperations.FixedTimeEquals(expected, actual);

    private bool JournalUnchangedSinceLeaseRead()
    {
        if (!_journalStampKnown) return false;
        var file = new FileInfo(JournalPath);
        file.Refresh();
        if (!CanReplaceJournalStamp(_journalStampKnown,
            _journalStampExists, _journalStampLength, _journalStampWriteUtc,
            file.Exists, file.Exists ? file.Length : 0,
            file.Exists ? file.LastWriteTimeUtc : default))
            return false;
        if (!file.Exists) return _journalDigest is null;
        if (_journalDigest is null) return false;
        // Avoid a second JSON deserialization. Under the exclusive lease
        // hash the bounded on-disk bytes only for real durable commits.
        // External writers changing data AFTER this read are not atomic-CAS
        // protected; this is intentionally a best-effort conflict guard.
        using var stream = File.OpenRead(JournalPath);
        return MatchesJournalDigest(_journalDigest,
            SHA256.HashData(ReadBoundedJournalBytes(stream).AsSpan()));
    }

    // A previously observed durable journal disappearing is NOT evidence
    // that its owned Windows resources were restored. Keep in-memory owners
    // and fail closed instead of silently forgetting service/task changes.
    private static bool MissingJournalRequiresFailClosed(
        bool stampKnown, bool previousFileExists, int activeOwners,
        int activeReferences) =>
        (stampKnown && previousFileExists) ||
        activeOwners > 0 || activeReferences > 0;

    private void ReloadJournalUnderLease()
    {
        try
        {
            if (!File.Exists(JournalPath))
            {
                if (MissingJournalRequiresFailClosed(
                    _journalStampKnown, _journalStampExists,
                    _owned.Count, _references.Count))
                    throw new InvalidDataException(
                        "Previously trusted AI owner journal disappeared; ownership cannot be discarded");
                _owned.Clear();
                _feedback.Clear();
                _references.Clear();
                _learningRepairPending = false;
                _journalHealthy = true;
                _lastJournalErrorUtc = default;
                CaptureJournalStamp();
                return;
            }
            var info = new FileInfo(JournalPath);
            if (info.Length is <= 0 or > 262144)
                throw new InvalidDataException("AI app owner journal size outside safe bounds");
            using var stream = File.OpenRead(JournalPath);
            var payload = ReadBoundedJournalPayload(stream);
            var rows = DeserializeJournalStrict(payload.Text);
            if (!HasValidOwnedJournal(rows?.Owned))
                throw new InvalidDataException("Invalid AI app OWNER journal");
            // Learning can be quarantined WITHOUT losing ownership of an
            // already stopped service or disabled startup/task. Invalid
            // OWNERS remain fail-closed and must never be guessed/repaired.
            Journal safe = RecoverValidLearning(rows, out bool degraded);
            _owned.Clear();
            foreach (var pair in safe.Owned) _owned[pair.Key] = pair.Value;
            _feedback.Clear();
            if (safe.Feedback != null)
                foreach (var pair in safe.Feedback) _feedback[pair.Key] = pair.Value;
            _references.Clear();
            if (safe.References != null)
                foreach (var pair in safe.References) _references[pair.Key] = pair.Value;
            _learningRepairPending = degraded;
            _journalHealthy = true;
            _lastJournalErrorUtc = default;
            CaptureJournalStamp(payload.Digest);
        }
        catch (Exception ex)
        {
            _journalHealthy = false;
            _lastJournalErrorUtc = DateTime.UtcNow;
            LogService.Error("ai-system", "Third party AI owner journal unreadable", ex);
        }
    }

    // Keep persisted AI learning small even after years of app/task cycles.
    // Never discard an active transaction or an A2 reference, and never
    // discard a feedback row that could still affect the 7-day cooldown.
    private void PruneFeedback(DateTime now)
    {
        if (_feedback.Count <= 96) return;
        // .NET 10 Dictionary.Remove does not invalidate its enumerator.
        // Walk the existing validated map in place instead of allocating a
        // full KeyValuePair snapshot whenever durable learning is pruned.
        // Active owners/A2 references are still protected from eviction.
        foreach (var pair in _feedback)
        {
            if (now - pair.Value.UpdatedUtc <= TimeSpan.FromDays(30) ||
                _owned.ContainsKey(pair.Key) || _references.ContainsKey(pair.Key))
                continue;
            _feedback.Remove(pair.Key);
        }
        if (_feedback.Count <= 128) return;
        foreach (var key in _feedback
            .Where(x => now - x.Value.UpdatedUtc > TimeSpan.FromDays(7) &&
                !_owned.ContainsKey(x.Key) && !_references.ContainsKey(x.Key))
            .OrderBy(x => x.Value.UpdatedUtc)
            .Take(_feedback.Count - 96)
            .Select(x => x.Key).ToArray())
            _feedback.Remove(key);
    }

    // After ANY journal write failure, the host's pending owner/feedback
    // dictionaries are no longer proof of what's durable on disk. Never
    // start another native action from them: force a leased re-read next
    // retry window, preserving the last known owners until then.
    private void InvalidateJournalAfterFailedWrite(DateTime atUtc)
    {
        _journalHealthy = false;
        _lastJournalErrorUtc = atUtc;
        _lastScanUtc = DateTime.MinValue;
        _lastFailedOffRestoreUtc = default;
    }
}
