using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record AiTweakRuntimeItem(string Id, string Section, bool Eligible, bool Managed, string Reason);
public sealed record AiTweakRuntimeHealth(DateTime UpdatedUtc, bool Enabled, int Total, int Eligible,
    int Managed, int Deferred, int Restored, int ManualOverrides, string LastAction, string LastError,
    AiTweakRuntimeItem[] Items, string TrialPhase = "Observation", int MeasuredCandidates = 0,
    int Accepted = 0, int Rejected = 0, string LastEvidence = "");

public static class AiTweakRuntimePolicy
{
    // A stale tick must never start a registry write after the global AI or
    // tweak scope is disabled. This does NOT prevent restoring owned writes.
    internal static bool AllowsNewTweakWrite(AiSystemRuntimeConfig config) =>
        config.Enabled && config.SelfOptimizationEnabled && config.TweaksEnabled;

    // Shared literal safety denylist, evaluated for each catalog candidate.
    // Keep original case-insensitive substring rules and decision order.
    private static readonly string[] ProtectedRegistryFragments =
    {
        @"CurrentControlSet\Services", @"DeviceGuard", @"Windows Defender",
        @"WindowsUpdate", @"PowerSchemes", @"\Winlogon", @"\Policies\System",
        @"Tcpip", @"TCPIP6", @"Control\Class", @"Control\Storage", @"NetworkCards"
    };

    // Recommended catalog policy, not a claim of measured/LLM performance improvements.
    public static string DeferredReason(TweakDefinition t)
    {
        if (t.Id.StartsWith("sec.", StringComparison.OrdinalIgnoreCase))
            return "Security hardening is managed by AI Security Guard";
        if (t.Section.Equals("defender", StringComparison.OrdinalIgnoreCase))
            return "Defender has a dedicated owner; performance AI never weakens protection";
        if (t.Risk != TweakRisk.Safe) return "Optional/advanced: manual decision";
        if (t.RestartRequired) return "Requires restart";
        if (!t.Recommended) return "Not recommended by catalog";
        if (!string.IsNullOrEmpty(t.Warning)) return "Catalog warning: manual decision";
        if (!t.RegistryHive.HasValue || t.RegistryPath == null || t.RegistryValueName == null ||
            !t.RegistryEnabledValue.HasValue || t.RevertMode != "OriginalRegistryValue" || !t.CanRestoreOriginal)
            return "Command/destructive action: exact original-value restore unavailable";
        if (t.RegistryEnabledValue < int.MinValue || t.RegistryEnabledValue > int.MaxValue)
            return "Unsupported registry value";
        if (t.RequiresTweaks.Length != 0 || t.ConflictsWithTweaks.Length != 0)
            return "Dependency/conflict: manual decision";
        if (!t.HardwareScope.Equals("Any", StringComparison.OrdinalIgnoreCase)) return "Hardware-specific: manual decision";
        if (t.Section is "defender" or "update" or "power")
            return "Security/update/device/power policy has a dedicated owner";
        string path = t.RegistryPath;
        foreach (string fragment in ProtectedRegistryFragments)
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return "Critical or independently managed target";
        return string.Empty;
    }
}

public interface IAiTweakRuntimeBackend
{
    string DeferredReason(TweakDefinition tweak) => string.Empty;
    Task<string> CaptureAsync(TweakDefinition tweak, CancellationToken token);
    Task<bool> IsEnabledAsync(TweakDefinition tweak, CancellationToken token);
    string ExpectedSignature(TweakDefinition tweak);
    Task ApplyAsync(TweakDefinition tweak, string backupId, CancellationToken token);
    Task RestoreAsync(TweakDefinition tweak, string backupId, string original, CancellationToken token);
}

public sealed partial class AiTweakRuntimeService
{
    private sealed record Ownership(string Id, string Target, string BackupId, string Original,
        string Applied, bool Managed, bool Suppressed, bool ManualOverride, DateTime ChangedUtc, string Reason);
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer", "ai-tweak-ownership.json");
    private static readonly string HealthPath = Path.Combine(Path.GetDirectoryName(DefaultPath)!, "ai-tweak-health.json");
    private static readonly SemaphoreSlim DefaultGate = new(1, 1);
    private static readonly ObservedJsonSnapshotCache<AiTweakRuntimeHealth> HealthSnapshot = new(HealthPath);
    private readonly SemaphoreSlim _gate;
    private readonly Func<IEnumerable<TweakDefinition>> _catalog;
    private readonly IAiTweakRuntimeBackend _backend;
    private readonly string _path;
    private TweakDefinition[]? _definitions;
    private readonly Dictionary<string, long> _observed = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Ownership> _owned = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxOwnershipJournalBytes = 1024 * 1024;
    private byte[]? _trustedJournalDigest;
    private bool _trustedJournalKnown;
    private static bool SameTweakId(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // Registry hives, paths and value names are case-insensitive on Windows.
    // A casing-only catalog change must not orphan an AI-owned restoration,
    // while any real target/value change remains blocked.
    private static bool SameOwnedTarget(TweakDefinition tweak, string target) =>
        string.Equals(Target(tweak), target, StringComparison.OrdinalIgnoreCase);
    private int _cursor, _restored;
    private DateTime _cooldown;
    private string _lastAction = "Catalog policy observation", _lastError = "";
    public AiTweakRuntimeHealth Health { get; private set; } = new(DateTime.MinValue, false, 0, 0, 0, 0, 0, 0, "", "", Array.Empty<AiTweakRuntimeItem>());

    public AiTweakRuntimeService() : this(
        () => CatalogService.EnumerateTweakSources()
            .SelectMany(s => s.Factory()).GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()),
        new RegistryBackend(), DefaultPath, feedbackRequired: true) { }

    public AiTweakRuntimeService(Func<IEnumerable<TweakDefinition>> catalog, IAiTweakRuntimeBackend backend, string ownershipPath, bool feedbackRequired = false, Func<DateTime>? utcNow = null)
    {
        _catalog = catalog; _backend = backend; _path = ownershipPath; _feedbackRequired = feedbackRequired; _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _gate = ownershipPath == DefaultPath ? DefaultGate : new SemaphoreSlim(1, 1);
    }

    public static AiTweakRuntimeHealth? ReadHealth() => HealthSnapshot.TryRead();

    private void RequireLiveTweakWriteScope()
    {
        // Fake/test backends use an isolated journal and explicit test config.
        // Only the real Windows backend consults the current UI-owned config.
        if (!_feedbackRequired || _path != DefaultPath) return;
        if (!AiTweakRuntimePolicy.AllowsNewTweakWrite(
                AiSystemResourceManagerService.LoadConfig()))
            throw new InvalidOperationException(
                "AI or registry-tweak automation was disabled before native write.");
    }

    public Task RestoreAllAsync(CancellationToken token = default) =>
        TickAsync(new AiSystemRuntimeConfig(Enabled: false), true, 0, false, token);

    public Task TickAsync(AiSystemRuntimeConfig config, bool realIdle, double load, bool gameActive, CancellationToken token) =>
        TickCoreAsync(config, realIdle, load, gameActive, token);

    public Task TickMeasuredAsync(AiSystemRuntimeConfig config, bool realIdle, double load, bool gameActive, AiSystemPerformanceSample sample, CancellationToken token) =>
        TickCoreAsync(config, realIdle, load, gameActive, token, sample);

    private async Task TickCoreAsync(AiSystemRuntimeConfig config, bool realIdle, double load, bool gameActive, CancellationToken token, AiSystemPerformanceSample? suppliedSample = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var ownershipLease = await AcquireOwnershipLeaseAsync(token).ConfigureAwait(false);
            // A previous lease is a finished transaction. A different
            // cooperating instance may have committed a newer trial journal
            // before THIS lease was obtained, so its digest is not a valid
            // starting point for this cycle. Load a fresh trusted snapshot
            // under the new lease; keep strict checks within the cycle.
            _trustedPerformanceJournalKnown = false;
            _trustedPerformanceJournalDigest = null;
            // Both in-process gate and cross-process lease protect ownership+trial state.
            // Capture the digest of the EXACT bytes validated by this tick;
            // a non-compliant external writer may ignore our .lease entirely.
            byte[]? journalBytes = await ReadOwnershipJournalBytesAsync(token).ConfigureAwait(false);
            if (journalBytes != null)
            {
                // System tweak IDs are case-insensitive in the live catalog.
                // JSON deserialization creates an ordinary case-sensitive
                // dictionary, which can orphan a previously owned restore
                // after a catalog casing change. Reject case-colliding keys
                // rather than choosing either owner silently.
                RejectDuplicateJournalJsonProperties(journalBytes);
                var stored = JsonSerializer.Deserialize<Dictionary<string, Ownership>>(journalBytes)
                    ?? throw new InvalidDataException("AI tweak ownership journal is null.");
                // Never restore using a journal key that points to a
                // different embedded owner. Otherwise a successful restore
                // updates a NEW key while the original managed entry stays
                // live and may be retried after restart.
                foreach (var entry in stored)
                    if (entry.Value is null ||
                        string.IsNullOrWhiteSpace(entry.Key) ||
                        string.IsNullOrWhiteSpace(entry.Value.Id) ||
                        !SameTweakId(entry.Key, entry.Value.Id) ||
                        (entry.Value.Managed &&
                            (string.IsNullOrWhiteSpace(entry.Value.Target) ||
                             string.IsNullOrWhiteSpace(entry.Value.BackupId) ||
                             string.IsNullOrWhiteSpace(entry.Value.Original) ||
                             string.IsNullOrWhiteSpace(entry.Value.Applied))))
                        throw new InvalidDataException(
                            "AI tweak ownership journal has invalid active owner identity or backup evidence.");
                _owned = new Dictionary<string, Ownership>(stored, StringComparer.OrdinalIgnoreCase);
            }
            else if (_owned.Count != 0 || (_trustedJournalKnown && _trustedJournalDigest != null))
            {
                // A deleted journal invalidates earlier in-memory ownership.
                // No restore or new native write may rely on an owner which
                // is no longer durably recorded. A new process with no prior
                // ownership may still initialize an empty journal normally.
                throw new InvalidDataException(
                    "AI tweak ownership journal disappeared during an active session.");
            }
            _trustedJournalDigest = journalBytes is null ? null : SHA256.HashData(journalBytes);
            _trustedJournalKnown = true;
            var defs = _definitions ??= _catalog().ToArray();
            // The central self-optimization switch also controls whether
            // new automatic registry tweaks are admitted. On AI-off we still
            // enter the existing owned-value restoration branch below.
            bool enabled = AiTweakRuntimePolicy.AllowsNewTweakWrite(config);
            if (_feedbackRequired && enabled)
                await EvaluateMeasuredSafeAsync(config, defs, realIdle, load, gameActive, suppliedSample, token).ConfigureAwait(false);
            else if (!enabled)
            {
                if (_feedbackRequired) await EndMeasuredTrialAsync(defs, "AI scope disabled", token).ConfigureAwait(false);
                _observed.Clear();
                foreach (var row in _owned.Values.Where(x => x.Managed).ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    var t = defs.FirstOrDefault(x => SameTweakId(x.Id, row.Id) && SameOwnedTarget(x, row.Target));
                    if (t == null) { _lastError = "Catalog target unavailable: " + row.Id; continue; }
                    try { await RestoreOwnedAsync(t, row, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _lastError = "Restore " + t.Id + ": " + ex.GetBaseException().Message; }
                }
            }
            else if (!gameActive && (realIdle || load <= 5) && DateTime.UtcNow >= _cooldown)
            {
                // Both reads and mutations are bounded: scan up to 12, apply up to 3 each maintenance cycle.
                int actions = 0;
                for (int i = 0; i < Math.Min(12, defs.Length); i++)
                {
                    token.ThrowIfCancellationRequested();
                    var t = defs[_cursor++ % defs.Length];
                    if (AiTweakRuntimePolicy.DeferredReason(t).Length != 0 || _backend.DeferredReason(t).Length != 0) continue;
                    try
                    {
                    if (_owned.TryGetValue(t.Id, out var row))
                    {
                        if (row.Suppressed) continue;
                        if (!SameOwnedTarget(t, row.Target)) { _lastError = "Catalog target changed: " + t.Id; continue; }
                        if (row.Managed)
                        {
                            if (await _backend.CaptureAsync(t, token).ConfigureAwait(false) != row.Applied)
                                await PreserveOverrideAsync(row, token).ConfigureAwait(false);
                            continue;
                        }
                    }
                    DateTime now = DateTime.UtcNow; // journal record only
                    long observationStamp = Stopwatch.GetTimestamp();
                    if (!_observed.TryGetValue(t.Id, out var since))
                        _observed[t.Id] = since = observationStamp;
                    if (!AiTweakMonotonicTimingPolicy.HasElapsed(since, observationStamp,
                        Stopwatch.Frequency, TimeSpan.FromSeconds(Math.Clamp(
                            config.ObservationSeconds > 0 ? config.ObservationSeconds : 180, 0, 3600))))
                        continue;
                    if (await _backend.IsEnabledAsync(t, token).ConfigureAwait(false)) continue; // never claim pre-existing manual settings
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                    budget.CancelAfter(TimeSpan.FromSeconds(20));
                    string original = await _backend.CaptureAsync(t, budget.Token).ConfigureAwait(false);
                    row = new(t.Id, Target(t), "ai." + t.Id + "." + Guid.NewGuid().ToString("N"),
                        original, _backend.ExpectedSignature(t), true, false, false, now, "Pending transaction");
                    _owned.TryGetValue(t.Id, out var precedingOwner);
                    _owned[t.Id] = row;
                    try
                    {
                        await PersistAsync(budget.Token).ConfigureAwait(false); // ownership BEFORE backend mutation
                    }
                    catch
                    {
                        // No backend write was attempted. A failed ownership
                        // journal cannot authorize an in-memory managed row.
                        if (precedingOwner is null) _owned.Remove(t.Id);
                        else _owned[t.Id] = precedingOwner;
                        throw;
                    }
                    try
                    {
                        RequireLiveTweakWriteScope();
                        await EnsureOwnershipJournalUnchangedAsync(budget.Token).ConfigureAwait(false);
                        await _backend.ApplyAsync(t, row.BackupId, budget.Token).ConfigureAwait(false);
                        if (await _backend.CaptureAsync(t, budget.Token).ConfigureAwait(false) != row.Applied)
                            throw new InvalidOperationException("AI tweak read-back failed: " + t.Id);
                        _owned[t.Id] = row with { Reason = "Catalog recommended policy", ChangedUtc = DateTime.UtcNow };
                        _lastAction = "Applied " + t.Id + " (catalog policy)";
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.GetBaseException().Message;
                        using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        try
                        {
                            string current = await _backend.CaptureAsync(t, rollback.Token).ConfigureAwait(false);
                            // AI only owns its exact applied signature. A
                            // different value indicates a user/external write,
                            // not an invitation to overwrite it on failure.
                            if (current != row.Applied && current != row.Original)
                            {
                                await PreserveOverrideAsync(row, rollback.Token).ConfigureAwait(false);
                                throw new InvalidOperationException(
                                    "AI rollback skipped: externally changed tweak preserved");
                            }
                            if (current == row.Applied && current != row.Original)
                            {
                                await EnsureOwnershipJournalUnchangedAsync(rollback.Token).ConfigureAwait(false);
                                await _backend.RestoreAsync(t, row.BackupId, row.Original, rollback.Token).ConfigureAwait(false);
                            }
                            if (await _backend.CaptureAsync(t, rollback.Token).ConfigureAwait(false) != row.Original)
                                throw new InvalidOperationException("Rollback read-back failed");
                            _owned[t.Id] = row with { Managed = false, Suppressed = true, Reason = "Apply failed; restored; manual review" };
                        }
                        catch (Exception restoreError) { _lastError += "; " + restoreError.Message; }
                        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
                        if (ex is OperationCanceledException) throw;
                    }
                    await PersistAsync(CancellationToken.None).ConfigureAwait(false);
                    if (++actions >= 3) break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _lastError = t.Id + ": " + ex.GetBaseException().Message;
                        if (!_owned.TryGetValue(t.Id, out var failed) || !failed.Managed)
                        {
                            _owned[t.Id] = new(t.Id, Target(t), "", "", "", false, true, false,
                                DateTime.UtcNow, "Deferred after probe/preflight failure: " + ex.GetBaseException().Message);
                            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                if (actions > 0) _cooldown = DateTime.UtcNow.AddSeconds(Math.Clamp(config.ActionCooldownSeconds, 0, 3600));
            }
            if (_path == DefaultPath && !enabled)
            {
                Health = new(
                    DateTime.UtcNow,
                    false,
                    0,
                    0,
                    _owned.Values.Count(x => x.Managed),
                    0,
                    _restored,
                    _owned.Values.Count(x => x.ManualOverride),
                    string.IsNullOrWhiteSpace(_lastAction)
                        ? "Production registry-tweak AI retired; manual sections preserved"
                        : _lastAction,
                    _lastError,
                    Array.Empty<AiTweakRuntimeItem>(),
                    "Retired/manual-only",
                    0,
                    _performance.History.Count(x => x.Verdict == "Accepted"),
                    _performance.History.Count(x => x.Verdict != "Accepted"),
                    "System/UI/Explorer/Security registry automation is disabled by policy");
            }
            else
            {
                var items = defs.Select(t =>
                {
                    string reason = AiTweakRuntimePolicy.DeferredReason(t);
                    if (reason.Length == 0) reason = _backend.DeferredReason(t);
                    if (_feedbackRequired && reason.Length == 0)
                        reason = AiSystemPerformancePolicy.CandidateDeferredReason(
                            t, _latestSample?.ActiveComponents ?? "");
                    _owned.TryGetValue(t.Id, out var row);
                    return new AiTweakRuntimeItem(
                        t.Id,
                        t.Section,
                        reason.Length == 0 && row?.Suppressed != true,
                        row?.Managed == true,
                        reason.Length != 0
                            ? reason
                            : row?.Reason ?? "Observed; waits for quiet maintenance window");
                }).ToArray();

                Health = new(
                    DateTime.UtcNow,
                    enabled,
                    items.Length,
                    items.Count(x => x.Eligible),
                    _owned.Values.Count(x => x.Managed),
                    items.Count(x => !x.Eligible),
                    _restored,
                    _owned.Values.Count(x => x.ManualOverride),
                    _lastAction,
                    _lastError,
                    items,
                    _feedbackRequired
                        ? _performance.Trial?.Phase ?? "Observation"
                        : "Catalog policy (test backend)",
                    defs.Count(AiSystemPerformancePolicy.IsMeasuredCandidate),
                    _performance.History.Count(x => x.Verdict == "Accepted"),
                    _performance.History.Count(x => x.Verdict != "Accepted"),
                    _performance.LastEvidence);
            }
            if (_path == DefaultPath)
            {
                await AtomicFileService.WriteTextAsync(HealthPath, JsonSerializer.Serialize(Health), token).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task PreserveOverrideAsync(Ownership row, CancellationToken token)
    {
        _owned[row.Id] = row with { Managed = false, Suppressed = true, ManualOverride = true, Reason = "Manual override preserved" };
        _lastAction = "Preserved manual override " + row.Id;
        await PersistAsync(token).ConfigureAwait(false);
    }

    private async Task RestoreOwnedAsync(TweakDefinition t, Ownership row, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string current = await _backend.CaptureAsync(t, timeout.Token).ConfigureAwait(false);
        if (_feedbackRequired && _performance.Trial != null)
            await EnsurePerformanceJournalUnchangedAsync(timeout.Token).ConfigureAwait(false);
        if (current == row.Original)
        {
            // The original is already present before OUR restore. This may be
            // an external/manual reversal or crash recovery after an earlier
            // native restore. Relinquish ownership and conservatively suppress
            // future automatic reapplication instead of silently re-enabling.
            _owned[row.Id] = row with
            {
                Managed = false, Suppressed = true, ManualOverride = true,
                Reason = "Original already present; external change or recovered restore preserved"
            };
            _lastAction = "Preserved already-original value " + row.Id;
        }
        else if (current != row.Applied) { await PreserveOverrideAsync(row, timeout.Token).ConfigureAwait(false); return; }
        else
        {
            await EnsureOwnershipJournalUnchangedAsync(timeout.Token).ConfigureAwait(false);
            await _backend.RestoreAsync(t, row.BackupId, row.Original, timeout.Token).ConfigureAwait(false);
            if (await _backend.CaptureAsync(t, timeout.Token).ConfigureAwait(false) != row.Original)
                throw new InvalidOperationException("AI restore read-back failed: " + t.Id);
            _owned[row.Id] = row with { Managed = false, Reason = "Original value restored" };
            _restored++; _lastAction = "Restored " + t.Id;
        }
        await PersistAsync(timeout.Token).ConfigureAwait(false);
    }
    private static string Target(TweakDefinition t) => $"{t.RegistryHive}|{t.RegistryPath}|{t.RegistryValueName}|{t.RegistryEnabledValue}";
    private static void RejectDuplicateJournalJsonProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AI journal root must be a JSON object.");

        // Dictionary deserialization silently keeps the last exact duplicate.
        // Validate the raw JSON tree first so no ambiguous owner/backup fields
        // are discarded before our ownership checks can inspect them.
        static void Walk(JsonElement element, bool isRoot = false)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                // Root case aliases retain the established dictionary
                // collision guard. An OWNERSHIP FIELD differs only in case
                // must be rejected too, including across serializer versions.
                var names = new HashSet<string>(
                    isRoot ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException(
                            "AI journal contains duplicate JSON properties.");
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Walk(item);
            }
        }

        Walk(document.RootElement, isRoot: true);
    }

    private async Task<byte[]?> ReadOwnershipJournalBytesAsync(CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
            // A corrupted or hostile journal must never allocate unbounded RAM.
            if (stream.Length > MaxOwnershipJournalBytes)
                throw new InvalidDataException("AI tweak ownership journal exceeds safe size limit.");
            using var buffer = new MemoryStream((int)stream.Length);
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaxOwnershipJournalBytes)
                    throw new InvalidDataException("AI tweak ownership journal grew beyond safe size limit.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task EnsureOwnershipJournalUnchangedAsync(CancellationToken token)
    {
        if (!_trustedJournalKnown)
            throw new IOException("AI tweak ownership journal has no validated snapshot.");
        byte[]? current = await ReadOwnershipJournalBytesAsync(token).ConfigureAwait(false);
        bool unchanged = _trustedJournalDigest is null
            ? current is null
            : current is not null &&
              CryptographicOperations.FixedTimeEquals(
                  _trustedJournalDigest, SHA256.HashData(current));
        if (!unchanged)
            throw new IOException(
                "AI tweak ownership journal changed since validated lease read; refusing stale write.");
    }

    private async Task PersistAsync(CancellationToken token)
    {
        // Detect writers which ignore our exclusive cross-process .lease.
        // Hash verification before replacement is optimistic, not filesystem CAS.
        await EnsureOwnershipJournalUnchangedAsync(token).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(_owned);
        await AtomicFileService.WriteTextAsync(_path, json, token).ConfigureAwait(false);
        _trustedJournalDigest = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        _trustedJournalKnown = true;
    }

    private sealed class RegistryBackend : IAiTweakRuntimeBackend
    {
        private readonly TweakEngine _engine = new();
        private static readonly bool IsElevated = ReadElevation();
        private static bool ReadElevation()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        public string DeferredReason(TweakDefinition t) =>
            t.RequiresAdmin && t.RegistryHive != RegistryHive.CurrentUser && !IsElevated
                ? "Requires administrator; waits for elevated runtime" : string.Empty;
        private void CheckAdmin(TweakDefinition t)
        {
            string reason = DeferredReason(t);
            if (reason.Length != 0) throw new UnauthorizedAccessException(reason);
        }
        public Task<string> CaptureAsync(TweakDefinition t, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            CheckAdmin(t);
            using var root = RegistryKey.OpenBaseKey(t.RegistryHive!.Value, RegistryView.Registry64);
            using var key = root.OpenSubKey(t.RegistryPath!);
            if (key == null || !key.GetValueNames().Contains(t.RegistryValueName!, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult("missing");
            if (key.GetValueKind(t.RegistryValueName!) != RegistryValueKind.DWord)
                throw new InvalidOperationException("Non-DWORD original: manual review");
            return Task.FromResult("DWord:" + Convert.ToInt32(key.GetValue(t.RegistryValueName!)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        public Task<bool> IsEnabledAsync(TweakDefinition t, CancellationToken token) => t.DetectAsync(token);
        public string ExpectedSignature(TweakDefinition t) => "DWord:" + t.RegistryEnabledValue!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static TweakDefinition Owned(TweakDefinition t, string id) => TweakFactory.Dword(
            id, t.Section, t.Group, t.Name, t.Description, t.RegistryHive!.Value, t.RegistryPath!, t.RegistryValueName!,
            (int)t.RegistryEnabledValue!.Value, recommended: true, minWindowsBuild: t.MinWindowsBuild, requiresAdmin: t.RequiresAdmin && t.RegistryHive != RegistryHive.CurrentUser);
        public async Task ApplyAsync(TweakDefinition t, string backupId, CancellationToken token)
        {
            var owned = Owned(t, backupId);
            await _engine.DetectAsync(new[] { owned }, token).ConfigureAwait(false);
            if (!owned.IsLoaded) throw new InvalidOperationException("AI compatibility/detection failed: " + t.Id);
            owned.Pending = true;
            var errors = await _engine.ApplyAsync(new[] { owned }, token).ConfigureAwait(false);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        }
        public async Task RestoreAsync(TweakDefinition t, string backupId, string original, CancellationToken token)
        {
            var errors = await _engine.RestoreOriginalAsync(new[] { Owned(t, backupId) }, token).ConfigureAwait(false);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        }
    }
}
