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
    // Pure memory-only boundary verification. Never uses the actual owner
    // journal. A MemoryStream may have more CAPACITY than stored bytes,
    // especially at 4097 bytes; neither hashes nor UTF-8 decode may see it.
    internal static bool RunBoundedJournalByteSliceSmoke()
    {
        foreach (int length in new[]
            { 1, 4095, 4096, 4097, 8191, 8192, MaxJournalBytes })
        {
            byte[] original = new byte[length];
            Array.Fill(original, (byte)'x');
            original[0] = (byte)'{';
            original[^1] = (byte)'}';
            using var source = new MemoryStream(original, writable: false);
            ArraySegment<byte> slice = ReadBoundedJournalBytes(source);
            if (slice.Count != original.Length ||
                !slice.AsSpan().SequenceEqual(original) ||
                !SHA256.HashData(slice.AsSpan())
                    .AsSpan().SequenceEqual(SHA256.HashData(original)) ||
                DecodeBoundedJournalText(slice) != Encoding.UTF8.GetString(original))
                return false;
        }
        // A seekable source may be positioned partway through a larger
        // stream; initial capacity and integrity must reflect REMAINING
        // bytes, not the total length or stale prefix contents.
        byte[] prefix = new byte[17 + 4097];
        Array.Fill(prefix, (byte)'p', 0, 17);
        Array.Fill(prefix, (byte)'z', 17, 4097);
        using (var positioned = new MemoryStream(prefix, writable: false))
        {
            positioned.Position = 17;
            ArraySegment<byte> slice = ReadBoundedJournalBytes(positioned);
            if (slice.Count != 4097 ||
                !slice.AsSpan().SequenceEqual(prefix.AsSpan(17)) ||
                !SHA256.HashData(slice.AsSpan()).AsSpan()
                    .SequenceEqual(SHA256.HashData(prefix.AsSpan(17))))
                return false;
        }
        using var invalid = new MemoryStream(
            new byte[] { (byte)'{', 0xFF, (byte)'}' }, writable: false);
        try
        {
            DecodeBoundedJournalText(ReadBoundedJournalBytes(invalid));
            return false;
        }
        catch (DecoderFallbackException) { }
        return true;
    }

    // The ownership journal is persisted as UTF-8 without BOM. The exact
    // bytes checked for size are also written and hashed, so multibyte
    // identity characters never get an inconsistent byte-length check.
    private static byte[] SerializeBoundedJournalBytes(Journal journal)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        if (bytes.Length > MaxJournalBytes)
            throw new InvalidDataException(
                "Refusing to persist AI journal larger than safe 256KiB limit");
        return bytes;
    }

    // Pure serialization regression: the new single UTF-8 payload must be
    // byte-for-byte compatible with the previous Serialize+WriteAllText
    // format. No real journal, registry, service, or disk writes occur.
    internal static bool RunUtf8JournalWriteParitySmoke()
    {
        DateTime at = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string id = $"CurrentUser|Registry64|{run}|Česká hudba 😀";
        string ownerKey = Key("startup", id);
        var owner = new Owner("startup", id, "spotify", at, true,
            false, "Česká hudba 😀", "\"C:\\Apps\\Music.exe\"",
            run, 256, false);
        var journal = new Journal(
            new Dictionary<string, Owner> { [ownerKey] = owner },
            new Dictionary<string, FeedbackOutcome>
            {
                [ownerKey] = new FeedbackOutcome(at, "deferred-until-next-startup",
                    0, AppKey: "spotify")
            });
        string legacyText = JsonSerializer.Serialize(journal);
        byte[] newBytes = SerializeBoundedJournalBytes(journal);
        if (!newBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(legacyText)) ||
            newBytes.Length != Encoding.UTF8.GetByteCount(legacyText) ||
            newBytes.Length == 0)
            return false;
        using var uiView = new MemoryStream(newBytes, writable: false);
        if (ReadBoundedJournalText(uiView) != legacyText) return false;
        using var valid = new MemoryStream(newBytes, writable: false);
        var loaded = ReadBoundedJournalPayload(valid);
        if (loaded.Text != legacyText ||
            !MatchesJournalDigest(loaded.Digest, SHA256.HashData(newBytes)) ||
            DeserializeJournalStrict(loaded.Text) is not { Owned.Count: 1 } ||
            !HasValidOwnedJournal(DeserializeJournalStrict(loaded.Text)?.Owned))
            return false;

        // BOM-bearing external legacy journals remain readable while new
        // writes are deterministic UTF-8 without a BOM.
        byte[] withBom = new byte[newBytes.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        newBytes.CopyTo(withBom, 3);
        using var bom = new MemoryStream(withBom, writable: false);
        if (ReadBoundedJournalPayload(bom).Text != legacyText) return false;
        using var bomUi = new MemoryStream(withBom, writable: false);
        if (ReadBoundedJournalText(bomUi) != legacyText) return false;

        var tooLarge = journal with
        {
            Owned = new Dictionary<string, Owner>
            {
                [ownerKey] = owner with
                {
                    StartupCommand = new string('x', MaxJournalBytes)
                }
            }
        };
        try
        {
            _ = SerializeBoundedJournalBytes(tooLarge);
            return false;
        }
        catch (InvalidDataException) { }

        using var oversizedHashOnly =
            new MemoryStream(new byte[MaxJournalBytes + 1]);
        try
        {
            _ = ReadBoundedJournalBytes(oversizedHashOnly);
            return false;
        }
        catch (InvalidDataException) { }
        using var emptyHashOnly = new MemoryStream();
        try
        {
            _ = ReadBoundedJournalBytes(emptyHashOnly);
            return false;
        }
        catch (InvalidDataException) { }
        return true;
    }

    // These are exclusively synthetic JSON inputs; never use or damage
    // the user's actual ProgramData owner journal for fault injection.
    private static bool RunDuplicateJournalPropertySmoke()
    {
        static bool Rejects(string json)
        {
            try { _ = DeserializeJournalStrict(json); return false; }
            catch (InvalidDataException) { return true; }
        }
        try
        {
            return DeserializeJournalStrict(@"{""Owned"":{}}")?.Owned.Count == 0 &&
                Rejects(@"{""Owned"":{},""Owned"":{}}") &&
                Rejects(@"{""Owned"":{},""owned"":{}}") &&
                Rejects(@"{""Owned"":{""service|Steam Client Service"":{},""service|Steam Client Service"":{}}}") &&
                Rejects(@"{""Owned"":{""service|Steam Client Service"":{},""SERVICE|STEAM CLIENT SERVICE"":{}}}") &&
                Rejects(@"{""Owned"":{""s\u0065rvice|Steam Client Service"":{},""service|Steam Client Service"":{}}}") &&
                Rejects(@"{""Owned"":{""service|Steam Client Service"":{""Kind"":""service"",""Kind"":""task""}}}") &&
                Rejects(@"{""Owned"":{""service|Steam Client Service"":{""Kind"":""service"",""kind"":""task""}}}") &&
                Rejects(@"{""Feedback"":{}}") &&
                Rejects(@"[]");
        }
        catch { return false; }
    }

    private static bool RunBoundedJournalSmoke()
    {
        try
        {
            using var valid = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"Owned\":{},\"Feedback\":{},\"References\":{}}"));
            BoundedJournalPayload validPayload = ReadBoundedJournalPayload(valid);
            if (!validPayload.Text.Contains("\"Owned\"", StringComparison.Ordinal) ||
                !MatchesJournalDigest(validPayload.Digest, validPayload.Digest))
                return false;
            // Same byte count and identical simulated file timestamps would
            // pass metadata-only stamp checks, but the SHA-256 must reject
            // replaced content even if a noncompliant external writer
            // preserves those attributes.
            using var tampered = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"Owned\":{},\"Feedback\":{},\"References\":[]}"
            ));
            BoundedJournalPayload other = ReadBoundedJournalPayload(tampered);
            if (validPayload.Text.Length != other.Text.Length ||
                MatchesJournalDigest(validPayload.Digest, other.Digest) ||
                MatchesJournalDigest(null, validPayload.Digest) ||
                MatchesJournalDigest(validPayload.Digest, null))
                return false;
            using var overflow = new MemoryStream(new byte[MaxJournalBytes + 1]);
            try
            {
                _ = ReadBoundedJournalText(overflow);
                return false;
            }
            catch (InvalidDataException) { }
            using var invalidUtf8 = new MemoryStream(new byte[] { 0xFF, 0xFE, 0xFA });
            try
            {
                _ = ReadBoundedJournalText(invalidUtf8);
                return false;
            }
            catch (DecoderFallbackException) { }
            return RunDuplicateJournalPropertySmoke();
        }
        catch { return false; }
    }

    // No live registry/SCM/filesystem calls: both retention modes must
    // evict expired/old outcomes without losing an active owned rollback
    // record or a still-measured passive A2 reference.
    internal static bool RunFeedbackRetentionSmoke()
    {
        DateTime now = new(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        static FeedbackOutcome Feedback(DateTime at) =>
            new(at, "deferred-until-next-startup", 0, AppKey: "spotify");
        var worker = new AiThirdPartyIdleAutomationService();
        const string ownedKey = "startup|protected-owner";
        const string referenceKey = "service|Steam Client Service";
        worker._owned[ownedKey] = new Owner(
            "startup", "protected-owner", "spotify", now, true, false);
        worker._references[referenceKey] = new ReferenceTrial(
            "Steam Client Service", "steam", now, 8, 8192, 7, 8300);
        worker._feedback[ownedKey] = Feedback(now.AddDays(-31));
        worker._feedback[referenceKey] = Feedback(now.AddDays(-31));
        worker._feedback["startup|boundary"] = Feedback(now.AddDays(-30));
        for (int i = 0; i < 130; i++)
            worker._feedback["startup|history-" + i] =
                Feedback(now.AddDays(i < 80 ? -32 : -1));
        worker.PruneFeedback(now);
        if (worker._feedback.Count != 53 ||
            !worker._feedback.ContainsKey(ownedKey) ||
            !worker._feedback.ContainsKey(referenceKey) ||
            !worker._feedback.ContainsKey("startup|boundary") ||
            worker._feedback.ContainsKey("startup|history-0") ||
            !worker._feedback.ContainsKey("startup|history-129"))
            return false;

        var capped = new AiThirdPartyIdleAutomationService();
        capped._owned[ownedKey] = worker._owned[ownedKey];
        capped._references[referenceKey] = worker._references[referenceKey];
        capped._feedback[ownedKey] = Feedback(now.AddDays(-29));
        capped._feedback[referenceKey] = Feedback(now.AddDays(-29));
        for (int i = 0; i < 150; i++)
            capped._feedback["startup|history-" + i] =
                Feedback(now.AddDays(-25).AddMinutes(i));
        capped.PruneFeedback(now);
        return capped._feedback.Count == 96 &&
            capped._feedback.ContainsKey(ownedKey) &&
            capped._feedback.ContainsKey(referenceKey) &&
            !capped._feedback.ContainsKey("startup|history-0") &&
            !capped._feedback.ContainsKey("startup|history-55") &&
            capped._feedback.ContainsKey("startup|history-56") &&
            capped._feedback.ContainsKey("startup|history-149");
    }

    // Oracle parity: the previous stable LINQ pipeline is retained only
    // inside this deterministic in-memory test, never on the UI hot path.
    internal static bool RunFeedbackTopThreeParitySmoke()
    {
        DateTime now = new(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        static FeedbackOutcome Feedback(DateTime at) =>
            new(at, "deferred-until-next-startup", 0, AppKey: "spotify");
        var outcomes = new Dictionary<string, FeedbackOutcome>
        {
            ["startup|a"] = Feedback(now.AddMinutes(-10)),
            ["startup|b"] = Feedback(now.AddMinutes(-2)),
            ["startup|c"] = Feedback(now.AddMinutes(-1)),
            ["startup|d"] = Feedback(now.AddMinutes(-1)),
            ["startup|e"] = Feedback(now.AddMinutes(-3)),
            ["STARTUP|B"] = Feedback(now),
            ["startup|old"] = Feedback(now.AddDays(-15)),
            ["startup|future"] = Feedback(now.AddMinutes(1)),
            ["service|WinDefend"] = Feedback(now)
        };
        var journal = new Journal(new Dictionary<string, Owner>(), outcomes);
        var expected = outcomes
            .Where(x => IsValidPersistedFeedback(x.Key, x.Value) &&
                x.Value.UpdatedUtc <= now &&
                now - x.Value.UpdatedUtc <= TimeSpan.FromDays(14))
            .OrderByDescending(x => x.Value.UpdatedUtc)
            .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3).ToArray();
        var actual = SelectTrustedFeedbackForUi(journal, now);
        if (actual.Length != 3 || expected.Length != actual.Length)
            return false;
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i].Kind + "|" + actual[i].Id != expected[i].Key ||
                actual[i].AppKey != expected[i].Value.AppKey ||
                actual[i].UpdatedUtc != expected[i].Value.UpdatedUtc ||
                actual[i].Verdict != expected[i].Value.Verdict)
                return false;
        }
        if (SelectTrustedFeedbackForUi(journal, now.AddDays(15)).Length != 0 ||
            SelectTrustedFeedbackForUi(new Journal(
                new Dictionary<string, Owner>(),
                new Dictionary<string, FeedbackOutcome>()), now).Length != 0)
            return false;
        var excessive = new Dictionary<string, FeedbackOutcome>();
        for (int i = 0; i < 257; i++)
            excessive["startup|" + i.ToString(CultureInfo.InvariantCulture)] =
                Feedback(now.AddHours(-1));
        return SelectTrustedFeedbackForUi(
            new Journal(new Dictionary<string, Owner>(), excessive), now).Length == 0;
    }

    // UI projection is pure and NEVER reads real Windows resources.
    internal static bool RunFeedbackProjectionSmoke()
    {
        DateTime now = DateTime.UtcNow;
        var owner = new Owner("service", "Steam Client Service", "steam",
            now.AddMinutes(-2), true, true, OriginalServiceStartMode: 3);
        string ownerKey = Key(owner.Kind, owner.Id);
        var feedback = new Dictionary<string, FeedbackOutcome>
        {
            [ownerKey] = new FeedbackOutcome(now.AddMinutes(-1),
                "inconclusive", 2, AppKey: "steam"),
            ["task|\\Mozilla\\Firefox Update Agent 308046B0AF4A39CB"] =
                new FeedbackOutcome(now, "inconclusive", 1, AppKey: "firefox"),
            ["task|\\Mozilla\\Firefox Default Browser Agent 308046B0AF4A39CB"] =
                new FeedbackOutcome(now, "inconclusive", 1, AppKey: "steam"),
            ["service|WinDefend"] =
                new FeedbackOutcome(now, "positive-system-trend", 3,
                    AppKey: "steam")
        };
        var journal = new Journal(
            new Dictionary<string, Owner> { [ownerKey] = owner }, feedback);
        var trusted = SelectTrustedFeedbackForUi(journal, now);
        var invalidOwners = journal with
        {
            Owned = new Dictionary<string, Owner>
            {
                [ownerKey] = owner with { AppKey = "defender" }
            }
        };
        // A verified HKCU Run opt-out can outlive historical 14-day UI
        // retention. Keep showing the pending-next-login verdict ONLY while
        // a matching, completed durable startup owner remains, not after
        // restoration, for pending WAL entries or for ordinary service data.
        var epicOwner = new Owner("startup",
            @"CurrentUser|Registry64|Software\Microsoft\Windows\CurrentVersion\Run|EpicGamesLauncher",
            "epic", now.AddDays(-30), true, false,
            "EpicGamesLauncher", @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
            @"Software\Microsoft\Windows\CurrentVersion\Run", 256, Pending: false);
        string epicKey = Key(epicOwner.Kind, epicOwner.Id);
        var oldFeedback = new Dictionary<string, FeedbackOutcome>
        {
            [epicKey] = new FeedbackOutcome(now.AddDays(-20),
                "deferred-until-next-startup", 0, AppKey: "epic")
        };
        var longOwnedJournal = new Journal(new Dictionary<string, Owner>
        {
            [epicKey] = epicOwner
        }, oldFeedback);
        var longOwned = SelectTrustedFeedbackForUi(longOwnedJournal, now);
        if (longOwned.Length != 1 || longOwned[0].AppKey != "epic" ||
            longOwned[0].Verdict != "deferred-until-next-startup" ||
            SelectTrustedFeedbackForUi(longOwnedJournal with
                { Owned = new Dictionary<string, Owner>() }, now).Length != 0 ||
            SelectTrustedFeedbackForUi(longOwnedJournal with
                {
                    Owned = new Dictionary<string, Owner>
                    {
                        [epicKey] = epicOwner with { Pending = true }
                    }
                }, now).Length != 0 ||
            SelectTrustedFeedbackForUi(longOwnedJournal with
                {
                    Owned = new Dictionary<string, Owner>
                    {
                        [epicKey] = epicOwner with { AppKey = "discord" }
                    }
                }, now).Length != 0)
            return false;
        return trusted.Length == 1 &&
            trusted[0].Kind == "service" &&
            trusted[0].Id == "Steam Client Service" &&
            trusted[0].AppKey == "steam" &&
            SelectTrustedFeedbackForUi(invalidOwners, now).Length == 0 &&
            SelectTrustedFeedbackForUi(journal, now.AddDays(15)).Length == 0 &&
            !MissingJournalRequiresFailClosed(false, false, 0, 0) &&
            MissingJournalRequiresFailClosed(true, true, 0, 0) &&
            MissingJournalRequiresFailClosed(false, false, 1, 0) &&
            MissingJournalRequiresFailClosed(false, false, 0, 1);
    }
}
