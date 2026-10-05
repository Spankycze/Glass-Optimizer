using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace SpankyhoOptimizer.Services;

internal static class GTweakBluetoothService
{
    private sealed record BluetoothPersistentState(bool Enabled);
    private const string PersistentKey = "bluetooth-radio-state-v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object CacheLock = new();
    private static readonly TimeSpan RadioCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UiStateTtl = TimeSpan.FromMinutes(10);
    private static Radio? _cachedRadio;
    private static DateTime _cachedRadioAt;
    private static Task<Radio?>? _radioFlight;
    private static bool? _cachedState;
    private static DateTime _cachedStateAt;
    private static Task? _uiRefreshTask;
    private static int _generation;

    public static async Task<bool> IsEnabledAsync(CancellationToken token = default)
    {
        Radio? radio = await GetBluetoothAsync(token).ConfigureAwait(false);
        if (radio == null) throw new PlatformNotSupportedException("Bluetooth radio nebylo nalezeno nebo aplikace nemá přístup.");
        bool enabled = radio.State == RadioState.On;
        lock (CacheLock)
        {
            _cachedState = enabled;
            _cachedStateAt = DateTime.UtcNow;
        }
        PersistentHardwareCacheService.WriteMachineStable(PersistentKey, new BluetoothPersistentState(enabled));
        return enabled;
    }

    public static async Task<bool> IsEnabledForUiAsync(CancellationToken token = default)
    {
        lock (CacheLock)
        {
            if (_cachedState.HasValue && DateTime.UtcNow - _cachedStateAt < UiStateTtl)
            {
                QueueUiRefreshLocked();
                return _cachedState.Value;
            }
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<BluetoothPersistentState>(
                PersistentKey, UiStateTtl, out var persisted) &&
            persisted != null)
        {
            lock (CacheLock)
            {
                _cachedState = persisted.Enabled;
                _cachedStateAt = DateTime.UtcNow;
                QueueUiRefreshLocked();
            }
            LogService.Info("performance", $"BLUETOOTH_UI_PERSISTENT_HIT enabled={persisted.Enabled}");
            return persisted.Enabled;
        }

        return await IsEnabledAsync(token).ConfigureAwait(false);
    }

    public static async Task SetEnabledAsync(bool enabled, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Radio? radio = await GetBluetoothAsync(token).ConfigureAwait(false);
            if (radio == null) throw new PlatformNotSupportedException("Bluetooth radio nebylo nalezeno nebo aplikace nemá přístup.");
            RadioState desired = enabled ? RadioState.On : RadioState.Off;
            if (radio.State != desired)
                await radio.SetStateAsync(desired).AsTask(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (radio.State != desired)
                throw new InvalidOperationException("Bluetooth read-back nepotvrdil nový stav.");

            lock (CacheLock)
            {
                _generation++;
                _cachedState = enabled;
                _cachedStateAt = DateTime.UtcNow;
                _uiRefreshTask = null;
            }
            PersistentHardwareCacheService.WriteMachineStable(PersistentKey, new BluetoothPersistentState(enabled));
        }
        finally { Gate.Release(); }
    }

    private static void QueueUiRefreshLocked()
    {
        if (_uiRefreshTask is { IsCompleted: false }) return;
        int generation = _generation;
        _uiRefreshTask = Task.Run(async () =>
        {
            try
            {
                Radio? radio = await GetBluetoothAsync(CancellationToken.None).ConfigureAwait(false);
                if (radio == null) return;
                bool enabled = radio.State == RadioState.On;
                lock (CacheLock)
                {
                    if (generation != _generation) return;
                    _cachedState = enabled;
                    _cachedStateAt = DateTime.UtcNow;
                }
                PersistentHardwareCacheService.WriteMachineStable(PersistentKey, new BluetoothPersistentState(enabled));
            }
            catch (Exception ex)
            {
                LogService.Info("performance", "BLUETOOTH_UI_BACKGROUND_REFRESH_FAIL " + ex.Message);
            }
        });
    }

    private static async Task<Radio?> GetBluetoothAsync(CancellationToken token)
    {
        Task<Radio?> flight;
        lock (CacheLock)
        {
            if (_cachedRadio != null && DateTime.UtcNow - _cachedRadioAt < RadioCacheTtl)
                return _cachedRadio;

            _radioFlight ??= LoadBluetoothAsync();
            flight = _radioFlight;
        }

        try
        {
            Radio? radio = await flight.WaitAsync(token).ConfigureAwait(false);
            lock (CacheLock)
            {
                _cachedRadio = radio;
                _cachedRadioAt = DateTime.UtcNow;
                if (ReferenceEquals(_radioFlight, flight))
                    _radioFlight = null;
            }
            return radio;
        }
        catch
        {
            lock (CacheLock)
                if (ReferenceEquals(_radioFlight, flight))
                    _radioFlight = null;
            throw;
        }
    }

    private static async Task<Radio?> LoadBluetoothAsync()
    {
        RadioAccessStatus access = await Radio.RequestAccessAsync().AsTask().ConfigureAwait(false);
        if (access != RadioAccessStatus.Allowed) return null;
        var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
        return radios.FirstOrDefault(x => x.Kind == RadioKind.Bluetooth);
    }
}
