using System;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private int _firmwareReadinessRunning;

    private async Task ShowFirmwareReadinessAsync()
    {
        if (Interlocked.CompareExchange(ref _firmwareReadinessRunning, 1, 0) != 0) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _windowLifetimeCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var snapshot = await FirmwareMemoryReadinessService.InspectAsync(timeout.Token);
            if (_section != "memory" || _windowLifetimeCts.IsCancellationRequested) return;
            await ShowStyledInfoAsync("BIOS / RAM / ReBAR", FirmwareMemoryReadinessService.Format(snapshot));
        }
        catch (OperationCanceledException)
        {
            if (_section == "memory" && !_windowLifetimeCts.IsCancellationRequested)
                await ShowStyledInfoAsync("BIOS / RAM / ReBAR",
                    "Kontrola nebyla dokončena. Žádné systémové nastavení nebylo změněno.");
        }
        catch (Exception ex)
        {
            LogService.Error("hardware", "Firmware readiness check failed", ex);
            if (_section == "memory" && !_windowLifetimeCts.IsCancellationRequested)
                await ShowStyledInfoAsync("BIOS / RAM / ReBAR",
                    "Nelze spolehlivě načíst hardwarové informace. Žádné nastavení nebylo změněno.");
        }
        finally { Interlocked.Exchange(ref _firmwareReadinessRunning, 0); }
    }
}
