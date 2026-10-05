using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed record FirewallPersistentState(bool AllActiveProfilesDisabled);

internal static class FirewallStateService
{
    private const int DomainProfile = 1;
    private const int PrivateProfile = 2;
    private const int PublicProfile = 4;
    private const string PersistentKey = "firewall-state-v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UiStaleTtl = TimeSpan.FromMinutes(10);
    private static readonly object Gate = new();
    private static bool? _sessionValue;
    private static DateTime _sessionAt;
    private static Task? _uiRefreshTask;
    private static int _generation;

    internal static bool AreAllActiveProfilesDisabled()
    {
        lock (Gate)
        {
            if (_sessionValue.HasValue && DateTime.UtcNow - _sessionAt < CacheTtl)
                return _sessionValue.Value;
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<FirewallPersistentState>(
                PersistentKey, CacheTtl, out var persisted) &&
            persisted != null)
        {
            lock (Gate)
            {
                _sessionValue = persisted.AllActiveProfilesDisabled;
                _sessionAt = DateTime.UtcNow;
            }
            LogService.Info("performance", $"FIREWALL_STATE_PERSISTENT_HIT disabled={persisted.AllActiveProfilesDisabled}");
            return persisted.AllActiveProfilesDisabled;
        }

        bool disabled = ReadFresh();
        lock (Gate)
        {
            _sessionValue = disabled;
            _sessionAt = DateTime.UtcNow;
        }
        PersistentHardwareCacheService.WriteMachineStable(
            PersistentKey,
            new FirewallPersistentState(disabled));
        return disabled;
    }

    internal static bool AreAllActiveProfilesDisabledForUi()
    {
        lock (Gate)
        {
            if (_sessionValue.HasValue)
            {
                TimeSpan age = DateTime.UtcNow - _sessionAt;
                if (age < CacheTtl)
                    return _sessionValue.Value;
                if (age < UiStaleTtl)
                {
                    QueueUiRefreshLocked();
                    return _sessionValue.Value;
                }
            }
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<FirewallPersistentState>(
                PersistentKey, UiStaleTtl, out var persisted, out var persistedAge) &&
            persisted != null)
        {
            lock (Gate)
            {
                _sessionValue = persisted.AllActiveProfilesDisabled;
                _sessionAt = DateTime.UtcNow - persistedAge;
                if (persistedAge >= CacheTtl)
                    QueueUiRefreshLocked();
            }
            LogService.Info("performance", $"FIREWALL_UI_PERSISTENT_HIT ageMs={persistedAge.TotalMilliseconds:0}");
            return persisted.AllActiveProfilesDisabled;
        }

        return AreAllActiveProfilesDisabled();
    }

    private static void QueueUiRefreshLocked()
    {
        if (_uiRefreshTask is { IsCompleted: false }) return;
        int generation = _generation;
        _uiRefreshTask = Task.Run(() =>
        {
            try
            {
                bool value = ReadFresh();
                lock (Gate)
                {
                    if (generation != _generation) return;
                    _sessionValue = value;
                    _sessionAt = DateTime.UtcNow;
                }
                PersistentHardwareCacheService.WriteMachineStable(
                    PersistentKey,
                    new FirewallPersistentState(value));
            }
            catch (Exception ex)
            {
                LogService.Info("performance", "FIREWALL_UI_BACKGROUND_REFRESH_FAIL " + ex.Message);
            }
        });
    }

    internal static void Invalidate()
    {
        lock (Gate)
        {
            _generation++;
            _sessionValue = null;
            _sessionAt = default;
            _uiRefreshTask = null;
        }
        PersistentHardwareCacheService.Remove(PersistentKey);
    }

    internal static void RunPersistentCacheSmoke()
    {
        Invalidate();

        var freshSw = Stopwatch.StartNew();
        bool fresh = AreAllActiveProfilesDisabled();
        freshSw.Stop();

        var persistentSw = Stopwatch.StartNew();
        bool persisted = AreAllActiveProfilesDisabled();
        persistentSw.Stop();

        if (fresh != persisted)
            throw new InvalidOperationException($"Firewall persistent cache mismatch fresh={fresh} persisted={persisted}.");
        if (persistentSw.ElapsedMilliseconds > 150)
            throw new InvalidOperationException($"Firewall persistent cache is too slow: {persistentSw.ElapsedMilliseconds} ms.");

        LogService.Info($"FIREWALL_STATE_CACHE_SMOKE_OK freshMs={freshSw.ElapsedMilliseconds} persistentMs={persistentSw.ElapsedMilliseconds} disabled={persisted}");
    }

    private static bool ReadFresh()
    {
        Type? type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (type == null) throw new PlatformNotSupportedException("Windows Firewall COM API není dostupné.");

        object? raw = null;
        try
        {
            raw = Activator.CreateInstance(type);
            if (raw == null) throw new InvalidOperationException("Windows Firewall policy se nepodařilo vytvořit.");
            dynamic policy = raw;
            int active = Convert.ToInt32(policy.CurrentProfileTypes);
            if (active == 0) return false;

            foreach (int profile in new[] { DomainProfile, PrivateProfile, PublicProfile })
            {
                if ((active & profile) == 0) continue;
                if (Convert.ToBoolean(policy.FirewallEnabled[profile]))
                    return false;
            }

            return true;
        }
        finally
        {
            if (raw != null && Marshal.IsComObject(raw))
                Marshal.FinalReleaseComObject(raw);
        }
    }
}
