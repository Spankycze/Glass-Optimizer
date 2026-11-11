using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
namespace SpankyhoOptimizer.Services;

public sealed record UserFeatureProfile(string[] KeepFeatures, DateTime UpdatedUtc)
{
    // Runtime-only state: never saved as part of user preferences.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUntrusted { get; init; }

    public bool Keeps(string key) =>
        !IsUntrusted && KeepFeatures != null &&
        KeepFeatures.Contains(key, StringComparer.OrdinalIgnoreCase);
}
public static class UserFeatureProfileService
{
    private static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "State");
    private static string FilePath => Path.Combine(StateRoot, "user-feature-profile.json");
    private static string LegacyFilePath => Path.Combine(LogService.Root, "user-feature-profile.json");
    private static readonly string[] Defaults = { "updates", "notifications", "store" };
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private const int MaxProfileBytes = 262144;
    private static readonly UTF8Encoding StrictProfileUtf8 = new(false, true);
    // A GUI/RuntimeHost reader must not block a second instance's atomic
    // File.Replace of KEEP choices. Allow deletion/rename, not in-place writes.
    private const FileShare ProfileReadShare = FileShare.Read | FileShare.Delete;
    private static readonly object Gate = new();
    private static readonly ReusableFileMetadata ProfileMetadata =
        new(FilePath, trackFileIdentity: true);
    private static UserFeatureProfile? Cache;
    private static FileMetadataStamp CacheStamp;
    private static bool CacheStableInvalid;
    private static bool HadSavedProfile;

    // Only deterministic invalid data may be cached as untrusted. A sharing
    // violation / transient I/O error must retry even with identical metadata.
    private static bool HasVerifiableProfileIdentity(FileMetadataStamp stamp) =>
        !stamp.Exists || stamp.FileIdentity != 0;

    // A cross-process write lease serializes OUR writers, not arbitrary
    // external file replacement. Never commit a user KEEP edit if an
    // existing physical file identity is unknown or changed during reading.
    private static bool HasVerifiedEditSnapshot(
        UserFeatureProfile profile, FileMetadataStamp before,
        FileMetadataStamp after, bool previouslySaved) =>
        !profile.IsUntrusted && before == after &&
        HasVerifiableProfileIdentity(before) &&
        HasVerifiableProfileIdentity(after) &&
        (!previouslySaved || after.Exists);

    private static bool CanReuseProfileCache(UserFeatureProfile? cached,
        FileMetadataStamp observed, FileMetadataStamp saved, bool stableInvalid) =>
        cached != null && HasVerifiableProfileIdentity(observed) &&
        observed == saved && (!cached.IsUntrusted || stableInvalid);

    public static UserFeatureProfile Load()
    {
        lock (Gate)
        {
            // The UI and RuntimeHost are separate processes. A permanently
            // cached trusted profile would miss a freshly saved KEEP choice
            // until restart. FileInfo.Refresh detects atomic replacements
            // without a watcher, timer or full JSON parse on every tick.
            FileMetadataStamp stamp = ProfileMetadata.Read();
            // A failed native file-ID probe cannot establish that an external
            // KEEP update was not missed. Deny until a later healthy read.
            if (!HasVerifiableProfileIdentity(stamp))
            {
                CacheStamp = default;
                CacheStableInvalid = false;
                return Cache = UntrustedProfile();
            }
            if (CanReuseProfileCache(Cache, stamp, CacheStamp, CacheStableInvalid))
                return Cache!;

            // Check disappearance BEFORE legacy migration. Otherwise an old
            // legacy JSON could silently replace a recently deleted current
            // profile and resurrect stale KEEP choices as trusted state.
            if (!stamp.Exists && HadSavedProfile)
            {
                CacheStamp = stamp;
                CacheStableInvalid = false;
                return Cache = UntrustedProfile();
            }
            try { EnsureMigrated(); }
            catch (InvalidOperationException)
            {
                // A broken legacy profile is NOT equivalent to an unused
                // first-run computer. Keep original data and deny writes.
                CacheStamp = default;
                CacheStableInvalid = false;
                return Cache = UntrustedProfile();
            }
            stamp = ProfileMetadata.Read();
            if (stamp.Exists) HadSavedProfile = true;
            if (!HasVerifiableProfileIdentity(stamp))
            {
                CacheStamp = default;
                CacheStableInvalid = false;
                return Cache = UntrustedProfile();
            }

            UserFeatureProfile fresh = ReadProfileFromPath(FilePath, out bool stableInvalid);
            FileMetadataStamp afterRead = ProfileMetadata.Read();
            if (!HasVerifiableProfileIdentity(afterRead) ||
                stamp != afterRead)
            {
                // The other process replaced the file DURING our read.
                // Retry on the next tick rather than caching mixed evidence.
                CacheStamp = default;
                CacheStableInvalid = false;
                return Cache = UntrustedProfile();
            }
            CacheStamp = stamp;
            CacheStableInvalid = stableInvalid;
            return Cache = fresh;
        }
    }

    private static UserFeatureProfile UntrustedProfile() =>
        new(Array.Empty<string>(), DateTime.UtcNow) { IsUntrusted = true };
    // Isolated parser permits testing corrupt and sharing-locked profiles
    // without reading or modifying the user's actual LocalAppData.
    // A user-profile file is a handful of feature keys, never an unbounded
    // document. Read at most the actual 256 KiB of bytes, even if another
    // writer appends after the initial length check. Reject invalid UTF-8
    // without replacing characters in KEEP/UNUSED preference identities.
    private static string ReadBoundedProfileJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, ProfileReadShare);
        if (stream.Length > MaxProfileBytes)
            throw new InvalidDataException("Feature profile exceeds safe byte limit.");
        using var content = new MemoryStream((int)stream.Length);
        byte[] buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (content.Length + read > MaxProfileBytes)
                throw new InvalidDataException("Feature profile grew beyond safe byte limit.");
            content.Write(buffer, 0, read);
        }
        string json = StrictProfileUtf8.GetString(
            content.GetBuffer(), 0, (int)content.Length);
        return json.Length != 0 && json[0] == '\uFEFF' ? json[1..] : json;
    }

    private static UserFeatureProfile ReadProfileFromPath(string path) =>
        ReadProfileFromPath(path, out _);

    private static UserFeatureProfile ReadProfileFromPath(
        string path, out bool stableInvalid)
    {
        stableInvalid = false;
        try
        {
            // File.Exists reports false even for some access-denied failures.
            // Only a genuinely missing file means first-run defaults.
            var parsed = JsonSerializer.Deserialize<UserFeatureProfile>(ReadBoundedProfileJson(path))
                ?? throw new InvalidDataException("Saved feature profile was JSON null.");
            if (parsed.KeepFeatures == null || parsed.KeepFeatures.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Saved feature profile has missing/invalid KeepFeatures.");
            return parsed;
        }
        catch (FileNotFoundException) { return new(Defaults, DateTime.UtcNow); }
        catch (DirectoryNotFoundException) { return new(Defaults, DateTime.UtcNow); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException
                                   or DecoderFallbackException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            stableInvalid = ex is JsonException or InvalidDataException or DecoderFallbackException;
            LogService.Error("profile", "Saved feature preferences unavailable; Recommended and writes blocked", ex);
            // Never turn lost user protections into trusted defaults.
            return new UserFeatureProfile(Array.Empty<string>(), DateTime.UtcNow)
            {
                IsUntrusted = true
            };
        }
    }

    internal static string RunFeatureProfileFailClosedSmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-feature-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "user-feature-profile.json");
            var firstRun = ReadProfileFromPath(path);
            if (firstRun.IsUntrusted ||
                !firstRun.Keeps("updates") || !firstRun.Keeps("store"))
                throw new InvalidOperationException("Missing first-run profile did not retain defaults.");

            var good = new UserFeatureProfile(
                new[] { "updates", "rdp", "keepapp:discord" },
                new DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc));
            string goodJson = JsonSerializer.Serialize(good);
            File.WriteAllText(path, goodJson);
            var loaded = ReadProfileFromPath(path);
            if (loaded.IsUntrusted || !loaded.Keeps("rdp") ||
                !loaded.Keeps("keepapp:discord"))
                throw new InvalidOperationException("Valid used-feature preferences changed.");
            var stableStamp = new FileMetadataStamp(true,
                new DateTime(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc),
                Encoding.UTF8.GetByteCount(goodJson),
                new DateTime(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc),
                FileIdentity: 7);
            if (!CanReuseProfileCache(loaded, stableStamp, stableStamp, false) ||
                !HasVerifiedEditSnapshot(loaded, stableStamp, stableStamp, false) ||
                HasVerifiedEditSnapshot(loaded, stableStamp with { FileIdentity = 0 },
                    stableStamp with { FileIdentity = 0 }, false) ||
                HasVerifiedEditSnapshot(loaded, stableStamp,
                    stableStamp with { FileIdentity = stableStamp.FileIdentity + 1 }, false) ||
                HasVerifiedEditSnapshot(loaded, stableStamp with { Exists = false },
                    stableStamp with { Exists = false }, true) ||
                HasVerifiedEditSnapshot(UntrustedProfile(), stableStamp, stableStamp, false) ||
                HasVerifiableProfileIdentity(stableStamp with { FileIdentity = 0 }) ||
                CanReuseProfileCache(loaded, stableStamp with { FileIdentity = 0 },
                    stableStamp with { FileIdentity = 0 }, false))
                throw new InvalidOperationException(
                    "Unverified physical identity was trusted as a stable KEEP snapshot.");

            // External GUI/RuntimeHost can atomically replace a KEEP choice
            // with a DIFFERENT same-length JSON during a coarse mtime tick.
            // File identity must invalidate the cached trusted profile even
            // when LastWriteUtc and Length remain exactly equal.
            DateTime sharedWriteTime = new(2025, 10, 21, 3, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, sharedWriteTime);
            File.SetCreationTimeUtc(path,
                new DateTime(2025, 10, 20, 3, 0, 0, DateTimeKind.Utc));
            var cachedMetadata = new ReusableFileMetadata(path,
                trackFileIdentity: true);
            FileMetadataStamp beforeReplacement = cachedMetadata.Read();
            string nextJson = goodJson.Replace("rdp", "gpu", StringComparison.Ordinal);
            if (nextJson == goodJson || nextJson.Length != goodJson.Length)
                throw new InvalidOperationException("Metadata collision fixture is invalid.");
            // Hold a real read handle across the atomic replacement. Readers
            // see their coherent old snapshot; writers must not get sharing
            // violations simply because the UI was reading preferences.
            using (var heldReader = new FileStream(path, FileMode.Open,
                FileAccess.Read, ProfileReadShare))
            {
                AtomicFileService.WriteText(path, nextJson);
                using var oldBytes = new MemoryStream();
                heldReader.CopyTo(oldBytes);
                if (!oldBytes.ToArray().SequenceEqual(Encoding.UTF8.GetBytes(goodJson)))
                    throw new InvalidOperationException(
                        "Atomic preference replacement changed bytes in an active reader.");
            }
            File.SetLastWriteTimeUtc(path, sharedWriteTime);
            FileMetadataStamp afterReplacement = cachedMetadata.Read();
            if (!beforeReplacement.Exists ||
                beforeReplacement.Length != afterReplacement.Length ||
                beforeReplacement.LastWriteUtc != afterReplacement.LastWriteUtc ||
                beforeReplacement.FileIdentity == 0 ||
                afterReplacement.FileIdentity == 0 ||
                beforeReplacement.FileIdentity == afterReplacement.FileIdentity ||
                beforeReplacement == afterReplacement ||
                !ReadProfileFromPath(path).Keeps("gpu"))
                throw new InvalidOperationException(
                    "Same-length/same-mtime external preference update was not detected.");
            File.WriteAllText(path, goodJson);
            // An exclusive writer may temporarily prevent the native
            // physical-ID probe without hiding FileInfo metadata. Such a
            // snapshot must NOT validate a cached KEEP consent.
            using (var exclusive = new FileStream(path, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None))
            {
                FileMetadataStamp inaccessible = cachedMetadata.Read();
                if (!inaccessible.Exists || inaccessible.FileIdentity != 0 ||
                    HasVerifiableProfileIdentity(inaccessible) ||
                    HasVerifiedEditSnapshot(loaded, inaccessible, inaccessible, false) ||
                    CanReuseProfileCache(loaded, inaccessible, inaccessible, false))
                    throw new InvalidOperationException(
                        "Unverified native file identity authorized cached KEEP preferences.");
            }
            if (!HasVerifiableProfileIdentity(cachedMetadata.Read()))
                throw new InvalidOperationException(
                    "Native profile identity stayed inaccessible after writer released the file.");

            int rejected = 0;
            foreach (string broken in new[]
            {
                "{broken",
                "null",
                "{\"KeepFeatures\":null,\"UpdatedUtc\":\"2026-10-21T03:00:00Z\"}",
                "{\"KeepFeatures\":[null],\"UpdatedUtc\":\"2026-10-21T03:00:00Z\"}",
                "{\"KeepFeatures\":[\" \"],\"UpdatedUtc\":\"2026-10-21T03:00:00Z\"}"
            })
            {
                File.WriteAllText(path, broken);
                var bad = ReadProfileFromPath(path, out bool stableInvalid);
                var corruptStamp = new FileMetadataStamp(true, stableStamp.LastWriteUtc,
                    Encoding.UTF8.GetByteCount(broken), stableStamp.CreationUtc,
                    stableStamp.FileIdentity);
                if (!stableInvalid ||
                    !CanReuseProfileCache(bad, corruptStamp, corruptStamp, stableInvalid) ||
                    CanReuseProfileCache(bad, corruptStamp with { Length = corruptStamp.Length + 1 },
                        corruptStamp, stableInvalid) ||
                    CanReuseProfileCache(bad, corruptStamp with
                        { LastWriteUtc = corruptStamp.LastWriteUtc.AddSeconds(1) },
                        corruptStamp, stableInvalid) ||
                    !bad.IsUntrusted || !RecommendedEngine.MustBlockFeatureProfile(bad) ||
                    bad.Keeps("rdp") || File.ReadAllText(path) != broken)
                    throw new InvalidOperationException(
                        "Corrupted feature profile silently enabled Recommended or lost original data.");
                bool writeRejected = false;
                try { RequireWritableProfile(path); }
                catch (InvalidOperationException) { writeRejected = true; }
                if (!writeRejected || File.ReadAllText(path) != broken)
                    throw new InvalidOperationException("Corrupt profile was writable or overwritten.");
                rejected++;
            }

            // Boundary admission, valid UTF-8 BOM and malicious binary /
            // oversized profiles all use only the isolated temporary file.
            string atLimit = new string(' ',
                MaxProfileBytes - Encoding.UTF8.GetByteCount(goodJson)) + goodJson;
            File.WriteAllText(path, atLimit);
            if (new FileInfo(path).Length != MaxProfileBytes ||
                ReadProfileFromPath(path).IsUntrusted)
                throw new InvalidOperationException("Bounded valid profile was rejected.");
            byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(Encoding.UTF8.GetBytes(goodJson)).ToArray();
            File.WriteAllBytes(path, withBom);
            if (ReadProfileFromPath(path).IsUntrusted)
                throw new InvalidOperationException("UTF-8 BOM profile was rejected.");
            foreach (byte[] brokenBytes in new[]
            {
                Enumerable.Repeat((byte)' ', MaxProfileBytes + 1).ToArray(),
                new byte[] { 0x7B, 0x22, 0xFF, 0x22, 0x7D }
            })
            {
                File.WriteAllBytes(path, brokenBytes);
                UserFeatureProfile invalid = ReadProfileFromPath(path);
                bool writeRejected = false;
                try { RequireWritableProfile(path); }
                catch (InvalidOperationException) { writeRejected = true; }
                if (!invalid.IsUntrusted ||
                    !RecommendedEngine.MustBlockFeatureProfile(invalid) ||
                    !writeRejected || !File.ReadAllBytes(path).SequenceEqual(brokenBytes))
                    throw new InvalidOperationException(
                        "Oversized or invalid-UTF8 profile authorized a write or changed data.");
                rejected++;
            }

            File.WriteAllText(path, goodJson);
            // A too-large Save used to overwrite a good profile and fail
            // only on its own bounded readback. Reject before replacing it.
            byte[] untouched = File.ReadAllBytes(path);
            var huge = good with
            {
                KeepFeatures = new[] { "rdp", "keepapp:" +
                    new string('x', MaxProfileBytes) }
            };
            bool oversizedWriteRejected = false;
            try { WriteValidatedProfileToPath(path, huge); }
            catch (InvalidOperationException) { oversizedWriteRejected = true; }
            if (!oversizedWriteRejected ||
                !File.ReadAllBytes(path).SequenceEqual(untouched) ||
                ReadProfileFromPath(path).IsUntrusted)
                throw new InvalidOperationException(
                    "Oversized preference Save overwrote a valid user profile.");
            // The writer must allow exactly the same 256 KiB boundary
            // that the read path permits, not reject a valid saved profile.
            var boundarySeed = good with { KeepFeatures = new[] { "keepapp:" } };
            int extra = MaxProfileBytes - StrictProfileUtf8.GetByteCount(
                JsonSerializer.Serialize(boundarySeed, IndentedJson));
            var boundary = good with
            {
                KeepFeatures = new[] { "keepapp:" + new string('x', extra) }
            };
            WriteValidatedProfileToPath(path, boundary);
            if (new FileInfo(path).Length != MaxProfileBytes ||
                ReadProfileFromPath(path).IsUntrusted)
                throw new InvalidOperationException("Exactly bounded profile Save failed.");
            WriteValidatedProfileToPath(path, good);
            if (ReadProfileFromPath(path).IsUntrusted)
                throw new InvalidOperationException("Valid bounded preference Save failed.");
            // Restore the original non-indented fixture bytes for the older
            // locked-file recovery assertion. Production saves use indented
            // JSON; both encodings must load to the same trusted profile.
            File.WriteAllText(path, goodJson);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var unavailable = ReadProfileFromPath(path, out bool stableLock);
                if (stableLock ||
                    CanReuseProfileCache(unavailable, stableStamp, stableStamp, stableLock) ||
                    !unavailable.IsUntrusted ||
                    !RecommendedEngine.MustBlockFeatureProfile(unavailable))
                    throw new InvalidOperationException("Locked feature profile was treated as trusted defaults.");
                bool lockWriteRejected = false;
                try { RequireWritableProfile(path); }
                catch (InvalidOperationException) { lockWriteRejected = true; }
                if (!lockWriteRejected)
                    throw new InvalidOperationException("Locked profile allowed a destructive write.");
            }
            if (ReadProfileFromPath(path).IsUntrusted ||
                File.ReadAllText(path) != goodJson)
                throw new InvalidOperationException("Recovered user feature preferences stayed blocked.");
            RequireWritableProfile(path);

            var fakeTweaks = new[]
            {
                new SpankyhoOptimizer.Models.TweakDefinition
                {
                    Id = "sys.rdp", Section = "system", Group = "fixture",
                    Name = "RDP", Description = "smoke", Current = true, Recommended = true
                },
                new SpankyhoOptimizer.Models.TweakDefinition
                {
                    Id = "sys.store", Section = "system", Group = "fixture",
                    Name = "Store", Description = "smoke", Current = false, Recommended = true
                }
            };
            var plan = RecommendedEngine.BuildBlockedFeatureProfilePlan(fakeTweaks);
            if (plan.EligibleCount != 0 || plan.Decisions.Count != 2 ||
                plan.Decisions.Any(x => x.Eligible || x.Tier != "Blocked") ||
                plan.Decisions[0].Enable != true || plan.Decisions[1].Enable != false)
                throw new InvalidOperationException(
                    "Unknown used features permitted a Recommended tweak mutation.");

            return "USER_FEATURE_FAIL_CLOSED_SMOKE_OK missingProfileDefaults=true " +
                   "validPreferencesKept=3 corruptRejected=" + rejected +
                   " lockedProfileRejected=true writesRejected=8 boundedUtf8=true " +
                   "oversizedSavePreservesOriginal=true " +
                   "invalidMetadataCache=true transientLockRetries=true " +
                   "atomicReplaceDuringRead=true verifiedWriteIdentity=true " +
                   "recoveredWithoutRestart=true " +
                   "recommendedCandidates=0 existingStatesPreserved=2 realUserFilesTouched=0";
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void RequireWritableProfile(string path)
    {
        if (ReadProfileFromPath(path).IsUntrusted)
            throw new InvalidOperationException(
                "Uložené používané funkce nejsou čitelné; profil nebyl přepsán.");
    }

    // Serialize writes across GUI instances and RuntimeHosts. A single
    // background-app preference change must be merged with the CURRENT
    // profile, never with an earlier snapshot from another process.
    private static FileStream AcquireWriteLease()
    {
        Directory.CreateDirectory(StateRoot);
        try
        {
            return new FileStream(FilePath + ".lease", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Uživatelské preference právě ukládá jiná instance. Zkus změnu znovu.", ex);
        }
    }

    // Enforce the same 256 KiB contract BEFORE replacing a good profile.
    // A successful atomic write must always be readable by our bounded loader.
    private static void WriteValidatedProfileToPath(string path, UserFeatureProfile value)
    {
        string json = JsonSerializer.Serialize(value, IndentedJson);
        if (StrictProfileUtf8.GetByteCount(json) > MaxProfileBytes)
            throw new InvalidOperationException(
                "Uživatelské preference překročily bezpečný limit 256 KiB; původní profil zůstal zachován.");
        AtomicFileService.WriteText(path, json);
    }

    private static UserFeatureProfile SaveUnderLease(IEnumerable<string> features)
    {
        string[] chosen = features.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (chosen.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Ukládaný profil obsahuje neplatný klíč.", nameof(features));

        // A formerly known current profile was deleted or became
        // unavailable. Do not migrate an older legacy copy or replace the
        // user's original choices with defaults during a Save.
        if (HadSavedProfile && !ProfileMetadata.Read().Exists)
            throw new InvalidOperationException(
                "Uložený profil už není dostupný. Změny nebyly přepsány.");
        EnsureMigrated();
        // Validate the actual physical file under the cross-process lease.
        // A native ID probe failure or independent external File.Replace is
        // not a verified state from which to overwrite another user's KEEP.
        FileMetadataStamp before = ProfileMetadata.Read();
        UserFeatureProfile current = ReadProfileFromPath(FilePath);
        FileMetadataStamp after = ProfileMetadata.Read();
        if (!HasVerifiedEditSnapshot(current, before, after, HadSavedProfile))
            throw new InvalidOperationException(
                "Uložené uživatelské preference nelze bezpečně ověřit; změna nebyla provedena.");

        var value = new UserFeatureProfile(chosen, DateTime.UtcNow);
        WriteValidatedProfileToPath(FilePath, value);

        UserFeatureProfile persisted = ReadProfileFromPath(FilePath);
        if (persisted.IsUntrusted ||
            !persisted.KeepFeatures.SequenceEqual(value.KeepFeatures,
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Feature profile read-back nepotvrdil uložené preference.");

        Cache = persisted;
        CacheStamp = ProfileMetadata.Read();
        CacheStableInvalid = false;
        HadSavedProfile = true;
        return persisted;
    }

    public static void Save(IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        lock (Gate)
        {
            using FileStream lease = AcquireWriteLease();
            _ = SaveUnderLease(features);
        }
    }

    // Used for per-app Keep/Unused/Default edits: take the shared lease
    // BEFORE loading the profile, then change only that one preference.
    // Multiple GUI instances therefore cannot accidentally replace each
    // other's unrelated user choices.
    public static UserFeatureProfile Update(Func<UserFeatureProfile, string[]> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (Gate)
        {
            using FileStream lease = AcquireWriteLease();
            // Do not use Load()'s metadata-keyed cache for an edit. Another
            // GUI could atomically replace a same-length JSON profile during
            // the same file timestamp resolution and the cache would miss it.
            // The lease is held before reading, merging and writing.
            FileMetadataStamp before = ProfileMetadata.Read();
            if (HadSavedProfile && !before.Exists)
                throw new InvalidOperationException(
                    "Uložený profil už není dostupný. Změna nebyla provedena.");
            EnsureMigrated();
            before = ProfileMetadata.Read();
            UserFeatureProfile current = ReadProfileFromPath(FilePath);
            FileMetadataStamp after = ProfileMetadata.Read();
            if (!HasVerifiedEditSnapshot(current, before, after, HadSavedProfile))
                throw new InvalidOperationException(
                    "Aktuální uživatelské preference nejsou spolehlivě čitelné. Změna byla zablokována.");
            // Updated cache is only an optimization for subsequent display/
            // RuntimeHost reads; the edit itself always used disk evidence.
            Cache = current;
            CacheStamp = after;
            if (after.Exists) HadSavedProfile = true;
            string[] proposed = change(current)
                ?? throw new InvalidOperationException("Změna preferencí vrátila prázdný výsledek.");
            string[] canonical = proposed.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (canonical.AsSpan().SequenceEqual(current.KeepFeatures))
                return current;
            return SaveUnderLease(canonical);
        }
    }

    internal static string RunMigrationSafetySmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-feature-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string oldPath = Path.Combine(root, "legacy", "user-feature-profile.json");
            string newPath = Path.Combine(root, "current", "user-feature-profile.json");
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);

            // Malformed JSON must NEVER be promoted and its only copy deleted.
            File.WriteAllText(oldPath, "{broken-json");
            bool invalidRejected = false;
            try { _ = MigrateLegacyProfile(oldPath, newPath); }
            catch (JsonException) { invalidRejected = true; }
            if (!invalidRejected || !File.Exists(oldPath) || File.Exists(newPath) ||
                File.ReadAllText(oldPath) != "{broken-json")
                throw new InvalidOperationException(
                    "Invalid legacy feature profile was destroyed during migration.");

            // Syntactically valid JSON can still contain an unusable profile.
            File.WriteAllText(oldPath, "{\"KeepFeatures\":null,\"UpdatedUtc\":\"2026-10-21T00:00:00Z\"}");
            bool invalidShapeRejected = false;
            try { _ = MigrateLegacyProfile(oldPath, newPath); }
            catch (InvalidDataException) { invalidShapeRejected = true; }
            if (!invalidShapeRejected || !File.Exists(oldPath) || File.Exists(newPath))
                throw new InvalidOperationException(
                    "Semantically invalid legacy preferences were promoted or destroyed.");

            // An external/legacy profile must not bypass the bounded,
            // lossless decoding rules or lose its only recoverable copy.
            foreach (byte[] raw in new[]
            {
                Enumerable.Repeat((byte)' ', MaxProfileBytes + 1).ToArray(),
                new byte[] { 0x7B, 0x22, 0xFF, 0x22, 0x7D }
            })
            {
                File.WriteAllBytes(oldPath, raw);
                bool denied = false;
                try { EnsureMigratedFromPaths(oldPath, newPath, log: false); }
                catch (InvalidOperationException) { denied = true; }
                if (!denied || File.Exists(newPath) ||
                    !File.ReadAllBytes(oldPath).SequenceEqual(raw) ||
                    Directory.Exists(Path.GetDirectoryName(newPath)!) &&
                    Directory.GetFiles(Path.GetDirectoryName(newPath)!, "*.tmp").Length != 0)
                    throw new InvalidOperationException(
                        "Oversized/invalid-UTF8 legacy profile was altered or migrated.");
            }

            var original = new UserFeatureProfile(new[] { "updates", "store", "keepapp:discord" },
                new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(oldPath, JsonSerializer.Serialize(original));
            bool migrated = MigrateLegacyProfile(oldPath, newPath);
            if (!migrated || File.Exists(oldPath) || !File.Exists(newPath))
                throw new InvalidOperationException("Valid legacy profile did not migrate.");
            var current = JsonSerializer.Deserialize<UserFeatureProfile>(File.ReadAllText(newPath));
            if (current == null ||
                !current.KeepFeatures.SequenceEqual(original.KeepFeatures, StringComparer.Ordinal) ||
                current.UpdatedUtc != original.UpdatedUtc)
                throw new InvalidOperationException("Migrated feature preferences differ from original.");

            // An already-current profile takes precedence even if an old
            // legacy file reappears after a failed cleanup or update.
            File.WriteAllText(oldPath, "{broken-json");
            if (MigrateLegacyProfile(oldPath, newPath) ||
                File.ReadAllText(newPath) != JsonSerializer.Serialize(original) ||
                !File.Exists(oldPath) ||
                Directory.GetFiles(Path.GetDirectoryName(newPath)!, "*.tmp").Length != 0)
                throw new InvalidOperationException(
                    "Existing current profile was overwritten or migration staging leaked.");

            return "FEATURE_PROFILE_MIGRATION_SMOKE_OK invalidOriginalPreserved=true " +
                   "invalidShapePreserved=true boundedLegacyRejected=2 " +
                   "invalidUtf8OriginalBytesPreserved=true validMigrated=true existingCurrentPreserved=true " +
                   "noStagingLeaks=true realUserFilesTouched=0";
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static bool MigrateLegacyProfile(string legacy, string destination)
    {
        if (File.Exists(destination) || !File.Exists(legacy)) return false;

        // Apply the SAME byte and strict-UTF8 bounds during first-run legacy
        // migration as ordinary reads. An oversized/invalid original must
        // stay untouched rather than bypassing the steady-state protection.
        string contents = ReadBoundedProfileJson(legacy);
        UserFeatureProfile profile = JsonSerializer.Deserialize<UserFeatureProfile>(contents)
            ?? throw new InvalidDataException("Legacy feature profile JSON was empty.");
        if (profile.KeepFeatures == null ||
            profile.KeepFeatures.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Legacy feature profile has invalid KeepFeatures.");

        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("Feature profile destination has no directory.");
        Directory.CreateDirectory(directory);
        string staged = Path.Combine(directory,
            "." + Path.GetFileName(destination) + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(staged, contents);
            // Never overwrite a profile created by another process.
            File.Move(staged, destination, overwrite: false);
            UserFeatureProfile persisted =
                JsonSerializer.Deserialize<UserFeatureProfile>(ReadBoundedProfileJson(destination))
                ?? throw new InvalidDataException("Migrated feature profile could not be read back.");
            if (persisted.KeepFeatures == null ||
                !persisted.KeepFeatures.SequenceEqual(profile.KeepFeatures, StringComparer.Ordinal) ||
                persisted.UpdatedUtc != profile.UpdatedUtc)
                throw new InvalidDataException("Migrated feature preferences differ from source.");

            // Delete only after validation and readback. A failure anywhere
            // above leaves the legacy data available for manual recovery.
            File.Delete(legacy);
            return true;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch (IOException) { }
        }
    }

    private static void EnsureMigrated() =>
        EnsureMigratedFromPaths(LegacyFilePath, FilePath, log: true);

    // The isolated migration smoke exercises the SAME production exception
    // propagation without touching the real user's profile or logs.
    private static void EnsureMigratedFromPaths(
        string legacy, string destination, bool log)
    {
        try
        {
            if (MigrateLegacyProfile(legacy, destination) && log)
                LogService.Info("profile", "Validated user feature profile migrated to LocalAppData.");
        }
        catch (Exception ex)
        {
            if (log)
                LogService.Error("profile", "Feature profile migration failed; feature writes blocked", ex);
            throw new InvalidOperationException(
                "Starší uživatelské preference nelze bezpečně převést; automatické změny jsou zablokované.", ex);
        }
    }
}
