using System;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private int _gameReadyCompatibilityRunning;

    private async Task CheckGameReadyCompatibilityAsync()
    {
        if (Interlocked.CompareExchange(ref _gameReadyCompatibilityRunning, 1, 0) != 0)
            return;
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
            cancel.CancelAfter(TimeSpan.FromSeconds(12));
            var result = await GameReadyCompatibilityService.ScanAsync(cancel.Token);
            if (_section == "fixer" && !_windowLifetimeCts.IsCancellationRequested)
            {
                await ShowStyledInfoAsync("GameReadyX – kompatibilita",
                    GameReadyCompatibilityService.Format(result));
                foreach (var repair in GameReadyCompatibilityService.ManualRepairOptions(result))
                {
                    if (_section != "fixer" || _windowLifetimeCts.IsCancellationRequested)
                        break;
                    if (!await ShowStyledConfirmAsync("GameReadyX – " + repair.Title,
                        "Byla nalezena chybějící volitelná herní závislost. " +
                        "Otevřít oficiální návod Microsoftu? Žádná instalace ani změna Windows " +
                        "se nespustí automaticky.",
                        "Otevřít Microsoft", "Přeskočit", "Pouze na vyžádání"))
                        continue;
                    if (GameReadyCompatibilityService.IsApprovedManualRepairUrl(repair.Url))
                        ProcessRunner.Open(repair.Url);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_section == "fixer" && !_windowLifetimeCts.IsCancellationRequested)
                await ShowStyledInfoAsync("GameReadyX", "Kontrola nebyla dokončena. Žádné nastavení se nezměnilo.");
        }
        catch (Exception ex)
        {
            LogService.Error("fixer", "GameReadyX dependency check failed", ex);
            if (_section == "fixer" && !_windowLifetimeCts.IsCancellationRequested)
                await ShowStyledInfoAsync("GameReadyX",
                    "Kompatibilitu herních závislostí se nepodařilo ověřit. Žádné nastavení se nezměnilo.");
        }
        finally { Interlocked.Exchange(ref _gameReadyCompatibilityRunning, 0); }
    }
}
