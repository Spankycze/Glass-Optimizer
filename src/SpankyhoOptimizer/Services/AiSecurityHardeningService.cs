using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record AiSecurityHardeningHealth(
    DateTime UpdatedUtc, bool Enabled, int Total, int Compliant, int Managed,
    int ManualOverrides, string LastAction, string LastError);

public sealed class AiSecurityHardeningService
{
    public static bool AutomaticMutationAllowed => false;
    private sealed record Target(string Id, RegistryHive Hive, string Path, string Name, int Value);
    private sealed record Ownership(string Id, bool OriginalExists, int OriginalValue, int AppliedValue,
        bool Managed, bool Suppressed, int ManualOverrides);

    private static readonly Target[] Targets =
    {
        new("sec.uac", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1),
        new("sec.securedesktop", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "PromptOnSecureDesktop", 1),
        new("sec.wdigest", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecurityProviders\WDigest", "UseLogonCredential", 0),
        new("sec.alwaysinstallelevated", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Installer", "AlwaysInstallElevated", 0),
        new("sec.restrictsam", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "RestrictAnonymousSAM", 1),
        new("sec.restrictanon", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "RestrictAnonymous", 1),
        new("sec.nolmhash", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "NoLMHash", 1),
        new("sec.blankpassword", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LimitBlankPasswordUse", 1),
        new("sec.everyoneanon", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "EveryoneIncludesAnonymous", 0),
        new("sec.safedll", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "SafeDllSearchMode", 1),
        new("sec.autorun", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", 255)
    };

    public static IReadOnlyList<string> ManagedTargetIds =>
        Targets.Select(x => x.Id).ToArray();

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer");
    private static readonly string StatePath = Path.Combine(Root, "ai-security-ownership.json");
    private static readonly string HealthPath = Path.Combine(Root, "ai-security-health.json");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ObservedJsonSnapshotCache<AiSecurityHardeningHealth> HealthSnapshot = new(HealthPath);
    private Dictionary<string, Ownership> _owned = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextAuditUtc;
    private string _lastAction = "Security guard observing";
    private string _lastError = "";

    public static AiSecurityHardeningHealth? ReadHealth() => HealthSnapshot.TryRead();

    public Task RestoreAllAsync(CancellationToken token = default) => TickAsync(false, token);

    public async Task TickAsync(bool enabled, CancellationToken token)
    {
        // Security is manual-only. Older configs may still request this scope;
        // in that case restore any prior AI-owned values instead of mutating security.
        enabled &= AutomaticMutationAllowed;
        if (enabled && DateTime.UtcNow < _nextAuditUtc) return;
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _lastError = string.Empty;
            Directory.CreateDirectory(Root);
            if (File.Exists(StatePath))
                _owned = JsonSerializer.Deserialize<Dictionary<string, Ownership>>(
                    await File.ReadAllTextAsync(StatePath, token).ConfigureAwait(false))
                    ?? new(StringComparer.OrdinalIgnoreCase);

            if (!enabled)
            {
                foreach (Target target in Targets)
                    if (_owned.TryGetValue(target.Id, out var own) && own.Managed && !own.Suppressed)
                        await RestoreOwnedAsync(target, own, token).ConfigureAwait(false);
                _lastAction = "Security scope disabled; AI-owned values restored";
                await PublishAsync(false, token).ConfigureAwait(false);
                return;
            }

            if (!IsElevated())
            {
                _lastAction = "Security guard waits for elevated runtime";
                _nextAuditUtc = DateTime.UtcNow.AddMinutes(2);
                await PublishAsync(true, token).ConfigureAwait(false);
                return;
            }

            int actions = 0;
            foreach (Target target in Targets)
            {
                token.ThrowIfCancellationRequested();
                var current = Read(target);
                if (_owned.TryGetValue(target.Id, out var own))
                {
                    if (own.Suppressed) continue;
                    if (own.Managed && (!current.Exists || current.Value != own.AppliedValue))
                    {
                        _owned[target.Id] = own with
                        {
                            Managed = false,
                            Suppressed = true,
                            ManualOverrides = own.ManualOverrides + 1
                        };
                        _lastAction = "Preserved manual security override: " + target.Id;
                        await PersistAsync(token).ConfigureAwait(false);
                        continue;
                    }
                }

                if (current.Exists && current.Value == target.Value) continue;
                if (actions >= 2) break;

                own = new(target.Id, current.Exists, current.Value, target.Value, true, false,
                    own?.ManualOverrides ?? 0);
                _owned[target.Id] = own;
                await PersistAsync(token).ConfigureAwait(false);

                try
                {
                    Write(target, target.Value);
                    var verify = Read(target);
                    if (!verify.Exists || verify.Value != target.Value)
                        throw new InvalidOperationException("Security read-back failed: " + target.Id);
                    _lastAction = "Hardened " + target.Id;
                    actions++;
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetBaseException().Message;
                    try { await RestoreOwnedAsync(target, own, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception rollback) { _lastError += "; rollback: " + rollback.GetBaseException().Message; }
                }
            }

            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            _nextAuditUtc = DateTime.UtcNow.AddMinutes(actions > 0 ? 1 : 5);
            await PublishAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async Task RestoreOwnedAsync(Target target, Ownership own, CancellationToken token)
    {
        var current = Read(target);
        if (!current.Exists || current.Value != own.AppliedValue)
        {
            _owned[target.Id] = own with
            {
                Managed = false,
                Suppressed = true,
                ManualOverrides = own.ManualOverrides + 1
            };
            _lastAction = "Preserved manual security override: " + target.Id;
            await PersistAsync(token).ConfigureAwait(false);
            return;
        }

        using RegistryKey root = RegistryKey.OpenBaseKey(target.Hive, RegistryView.Registry64);
        using RegistryKey key = root.CreateSubKey(target.Path, writable: true)
            ?? throw new InvalidOperationException("Cannot open security target: " + target.Id);
        if (own.OriginalExists)
            key.SetValue(target.Name, own.OriginalValue, RegistryValueKind.DWord);
        else
            key.DeleteValue(target.Name, throwOnMissingValue: false);

        _owned[target.Id] = own with { Managed = false };
        await PersistAsync(token).ConfigureAwait(false);
    }

    private static (bool Exists, int Value) Read(Target target)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(target.Hive, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(target.Path);
        if (key == null || !key.GetValueNames().Contains(target.Name, StringComparer.OrdinalIgnoreCase))
            return (false, 0);
        return (true, Convert.ToInt32(key.GetValue(target.Name, 0)));
    }

    private static void Write(Target target, int value)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(target.Hive, RegistryView.Registry64);
        using RegistryKey key = root.CreateSubKey(target.Path, writable: true)
            ?? throw new InvalidOperationException("Cannot open security target: " + target.Id);
        key.SetValue(target.Name, value, RegistryValueKind.DWord);
    }

    private Task PersistAsync(CancellationToken token) =>
        AtomicFileService.WriteTextAsync(StatePath, JsonSerializer.Serialize(_owned), token);

    private async Task PublishAsync(bool enabled, CancellationToken token)
    {
        int compliant = 0;
        foreach (Target t in Targets)
        {
            try { var current = Read(t); if (current.Exists && current.Value == t.Value) compliant++; }
            catch { }
        }

        var health = new AiSecurityHardeningHealth(DateTime.UtcNow, enabled, Targets.Length, compliant,
            _owned.Values.Count(x => x.Managed), _owned.Values.Sum(x => x.ManualOverrides),
            _lastAction, _lastError);
        await AtomicFileService.WriteTextAsync(HealthPath, JsonSerializer.Serialize(health), token).ConfigureAwait(false);
    }

    private static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
