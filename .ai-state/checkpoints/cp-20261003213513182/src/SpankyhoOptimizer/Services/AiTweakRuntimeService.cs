using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record AiTweakRuntimeItem(string Id, string Section, bool Eligible, bool Managed, string Reason);
public sealed record AiTweakRuntimeHealth(DateTime UpdatedUtc, bool Enabled, int Total, int Eligible,
    int Managed, int Deferred, int Restored, int ManualOverrides, string LastAction, string LastError,
    AiTweakRuntimeItem[] Items);

public static class AiTweakRuntimePolicy
{
    // Recommended catalog policy, not a claim of measured/LLM performance improvements.
    public static string DeferredReason(TweakDefinition t)
    {
        if (t.Section.Equals("browsers", StringComparison.OrdinalIgnoreCase)) return "Browser managed by RAMGuard";
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
        if (new[] { @"CurrentControlSet\Services", @"DeviceGuard", @"Windows Defender",
            @"WindowsUpdate", @"PowerSchemes", @"\Winlogon", @"\Policies\System",
            @"Tcpip", @"TCPIP6", @"Control\Class", @"Control\Storage", @"NetworkCards" }
            .Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)))
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

public sealed class AiTweakRuntimeService
{
    private sealed record Ownership(string Id, string Target, string BackupId, string Original,
        string Applied, bool Managed, bool Suppressed, bool ManualOverride, DateTime ChangedUtc, string Reason);
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer", "ai-tweak-ownership.json");
    private static readonly string HealthPath = Path.Combine(Path.GetDirectoryName(DefaultPath)!, "ai-tweak-health.json");
    private static readonly SemaphoreSlim DefaultGate = new(1, 1);
    private static AiTweakRuntimeHealth? _cachedHealth;
    private readonly SemaphoreSlim _gate;
    private readonly Func<IEnumerable<TweakDefinition>> _catalog;
    private readonly IAiTweakRuntimeBackend _backend;
    private readonly string _path;
    private TweakDefinition[]? _definitions;
    private readonly Dictionary<string, DateTime> _observed = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Ownership> _owned = new(StringComparer.OrdinalIgnoreCase);
    private int _cursor, _restored;
    private DateTime _cooldown;
    private string _lastAction = "Catalog policy observation", _lastError = "";
    public AiTweakRuntimeHealth Health { get; private set; } = new(DateTime.MinValue, false, 0, 0, 0, 0, 0, 0, "", "", Array.Empty<AiTweakRuntimeItem>());

    public AiTweakRuntimeService() : this(
        () => CatalogService.EnumerateTweakSources().Where(s => s.Section != "browsers")
            .SelectMany(s => s.Factory()).GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()),
        new RegistryBackend(), DefaultPath) { }

    public AiTweakRuntimeService(Func<IEnumerable<TweakDefinition>> catalog, IAiTweakRuntimeBackend backend, string ownershipPath)
    {
        _catalog = catalog; _backend = backend; _path = ownershipPath;
        _gate = ownershipPath == DefaultPath ? DefaultGate : new SemaphoreSlim(1, 1);
    }

    public static AiTweakRuntimeHealth? ReadHealth()
    {
        try
        {
            if (File.Exists(HealthPath))
                _cachedHealth = JsonSerializer.Deserialize<AiTweakRuntimeHealth>(File.ReadAllText(HealthPath));
        }
        catch { }
        return _cachedHealth;
    }

    public Task RestoreAllAsync(CancellationToken token = default) =>
        TickAsync(new AiSystemRuntimeConfig(Enabled: false), true, 0, false, token);

    public async Task TickAsync(AiSystemRuntimeConfig config, bool realIdle, double load, bool gameActive, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Reload under the shared gate so explicit RestoreAllNow and the resident agent agree.
            if (File.Exists(_path))
                _owned = JsonSerializer.Deserialize<Dictionary<string, Ownership>>(await File.ReadAllTextAsync(_path, token).ConfigureAwait(false))
                    ?? new(StringComparer.OrdinalIgnoreCase);
            var defs = _definitions ??= _catalog().ToArray();
            bool enabled = config.Enabled && config.TweaksEnabled;
            if (!enabled)
            {
                _observed.Clear();
                foreach (var row in _owned.Values.Where(x => x.Managed).ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    var t = defs.FirstOrDefault(x => x.Id == row.Id && Target(x) == row.Target);
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
                        if (row.Target != Target(t)) { _lastError = "Catalog target changed: " + t.Id; continue; }
                        if (row.Managed)
                        {
                            if (await _backend.CaptureAsync(t, token).ConfigureAwait(false) != row.Applied)
                                await PreserveOverrideAsync(row, token).ConfigureAwait(false);
                            continue;
                        }
                    }
                    DateTime now = DateTime.UtcNow;
                    if (!_observed.TryGetValue(t.Id, out var since)) _observed[t.Id] = since = now;
                    if (now - since < TimeSpan.FromSeconds(Math.Clamp(config.ObservationSeconds, 0, 3600))) continue;
                    if (await _backend.IsEnabledAsync(t, token).ConfigureAwait(false)) continue; // never claim pre-existing manual settings
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                    budget.CancelAfter(TimeSpan.FromSeconds(20));
                    string original = await _backend.CaptureAsync(t, budget.Token).ConfigureAwait(false);
                    row = new(t.Id, Target(t), "ai." + t.Id + "." + Guid.NewGuid().ToString("N"),
                        original, _backend.ExpectedSignature(t), true, false, false, now, "Pending transaction");
                    _owned[t.Id] = row;
                    await PersistAsync(budget.Token).ConfigureAwait(false); // ownership BEFORE backend mutation
                    try
                    {
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
                            if (current != row.Original)
                                await _backend.RestoreAsync(t, row.BackupId, row.Original, rollback.Token).ConfigureAwait(false);
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
            var items = defs.Select(t =>
            {
                string reason = AiTweakRuntimePolicy.DeferredReason(t);
                if (reason.Length == 0) reason = _backend.DeferredReason(t);
                _owned.TryGetValue(t.Id, out var row);
                return new AiTweakRuntimeItem(t.Id, t.Section, reason.Length == 0 && row?.Suppressed != true, row?.Managed == true,
                    reason.Length != 0 ? reason : row?.Reason ?? "Observed; waits for quiet maintenance window");
            }).ToArray();
            Health = new(DateTime.UtcNow, enabled, items.Length, items.Count(x => x.Eligible),
                _owned.Values.Count(x => x.Managed), items.Count(x => !x.Eligible), _restored,
                _owned.Values.Count(x => x.ManualOverride), _lastAction, _lastError, items);
            if (_path == DefaultPath)
            {
                _cachedHealth = Health;
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
        if (current == row.Original) { _owned[row.Id] = row with { Managed = false, Reason = "Already original" }; }
        else if (current != row.Applied) { await PreserveOverrideAsync(row, timeout.Token).ConfigureAwait(false); return; }
        else
        {
            await _backend.RestoreAsync(t, row.BackupId, row.Original, timeout.Token).ConfigureAwait(false);
            if (await _backend.CaptureAsync(t, timeout.Token).ConfigureAwait(false) != row.Original)
                throw new InvalidOperationException("AI restore read-back failed: " + t.Id);
            _owned[row.Id] = row with { Managed = false, Reason = "Original value restored" };
            _restored++; _lastAction = "Restored " + t.Id;
        }
        await PersistAsync(timeout.Token).ConfigureAwait(false);
    }
    private static string Target(TweakDefinition t) => $"{t.RegistryHive}|{t.RegistryPath}|{t.RegistryValueName}|{t.RegistryEnabledValue}";
    private Task PersistAsync(CancellationToken token) => AtomicFileService.WriteTextAsync(_path, JsonSerializer.Serialize(_owned), token);

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
