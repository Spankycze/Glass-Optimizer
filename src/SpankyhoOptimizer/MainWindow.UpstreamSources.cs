using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task ShowUpstreamSourcesAsync(CancellationToken token, bool force)
    {
        try
        {
        var progressResult = await RunCancellableWithProgressAsync(
            "Source Auto-Updater",
            force ? "Kontroluji GitHub zdroje…" : "Načítám stav zdrojů…",
            async progressToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progressToken);
                var data = await UpstreamSourceUpdateService.CheckAllAsync(force, linked.Token);
                return data.Select(x => x.Error == null ? string.Empty : $"{x.Name}: {x.Error}").Where(x => x.Length > 0).ToArray();
            },
            result => result.Length == 0,
            result => result.Length == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("upstream", "Source Auto-Updater cancelled by user/navigation.");
            return;
        }
        var statuses = progressResult.Result;
        var current = UpstreamSourceUpdateService.ReadCachedStatuses();
        var root = new StackPanel { Spacing = 9 };

        bool HasError(UpstreamSourceStatus item) =>
            statuses.Any(x => x.StartsWith(item.Name + ":", StringComparison.OrdinalIgnoreCase));
        int failed = current.Count(HasError);
        int missing = current.Count(x => !HasError(x) && (!x.UpdateDownloaded || x.Commit == "-"));
        int ready = current.Count - missing - failed;
        root.Children.Add(new TextBlock
        {
            Text = $"Ověřené archivy: {ready}/{current.Count}  •  neověřeno/chybí: {missing}  •  chyby: {failed}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = failed > 0 ? WarningBrush() : SuccessBrush(),
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        });

        var autoCheck = new ToggleSwitch { Header = "Automaticky kontrolovat aktualizace zdrojů", IsOn = UpstreamSourceUpdateService.AutoCheckEnabled, OnContent = "Zapnuto", OffContent = "Vypnuto" };
        NormalizeToggleAppearance(autoCheck);
        autoCheck.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyVerifiedImmediateToggleAsync(autoCheck, UpstreamSourceUpdateService.SetAutoCheckEnabled,
                () => UpstreamSourceUpdateService.AutoCheckEnabled, "upstream", "Automatická kontrola zdrojů"),
            "upstream", "Verified upstream auto-check toggle");
        root.Children.Add(autoCheck);
        foreach (var item in current)
        {
            string sha = item.Commit == "-" ? "-" : item.Commit[..Math.Min(10, item.Commit.Length)];
            string checkedAt = item.LastCheckedUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "nikdy";
            string updatedAt = item.LastUpdatedUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "nikdy";
            string? error = statuses.FirstOrDefault(x => x.StartsWith(item.Name + ":", StringComparison.OrdinalIgnoreCase));
            string state = error != null ? "CHYBA" : item.NewerSourceAvailable ? "NOVĚJŠÍ ZDROJE" : !item.UpdateDownloaded ? "KOPIE NEOVĚŘENA / CHYBÍ" : "ARCHIV OVĚŘEN";
            string detail = error != null
                ? error
                                : $"Stažený commit {sha}  •  kontrola {checkedAt}  •  archiv {updatedAt}\n" +
                  $"Poslední zjištěný commit: {item.LatestCommit}\n" +
                  $"Způsob integrace: {item.IntegrationMode}\n{UpstreamUpdatePolicy.IntegrationDetail(item.Id)}\n" +
                  $"Aktivní verze: {UpstreamUpdatePolicy.ActiveRevision(item.Id)}";
            root.Children.Add(new TextBlock
            {
                Text = $"{item.Name}  •  {state}\n{detail}",
                FontWeight = FontWeights.SemiBold,
                Foreground = error != null ? WarningBrush() : item.UpdateDownloaded ? SuccessBrush() : TextSecondaryBrush(),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });
        }
        await ShowStyledContentAsync("Source Auto-Updater", $"Sleduji {current.Count} GitHub zdrojů. Stažený archiv není nainstalovaný program. WinUtil poskytuje ověřená data pro podporované služby; ostatní komponenty mají vlastní integrační cestu popsanou níže. Automatická kontrola při startu má hodinový odstup; ruční kontrola je okamžitá. Upstream kód se při kontrole nespouští.", root, "Zavřít");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogService.Info("upstream", "Source Auto-Updater cancelled by navigation.");
        }
    }

    private async Task CheckUpstreamUpdatesOnStartupAsync()
    {
        if (_windowLifetimeCts.IsCancellationRequested) return;
        if (Interlocked.Exchange(ref _startupUpstreamCheckStarted, 1) != 0) return;
        _upstreamUpdateCts?.Cancel();
        _upstreamUpdateCts?.Dispose();
        _upstreamUpdateCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        CancellationToken token = _upstreamUpdateCts.Token;

        try
        {
            // Isolated smoke/benchmark runs must remain network-quiescent, otherwise the GitHub
            // sockets/HTTP handles would contaminate retention and leak measurements.
            if (Environment.GetCommandLineArgs().Any(x =>
                x.Contains("smoke", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("perf-run", StringComparison.OrdinalIgnoreCase)))
                return;

            // Keep GitHub network I/O away from first paint and the initial Overview warm path.
            await Task.Delay(TimeSpan.FromSeconds(8), token);
            if (!UpstreamSourceUpdateService.AutoCheckEnabled) return;

            var updates = await UpstreamSourceUpdateService.ProbeUpdatesAsync(force: false, token);
            if (updates.Count == 0)
            {
                LogService.Info("upstream", "STARTUP_GITHUB_UPDATE_CHECK_OK updates=0 popup=false");
                return;
            }

            string names = string.Join(", ", updates.Select(x => x.Name));
            Action<string> liveStatus = _ => { };
            var progress = await RunCancellableWithProgressAsync(
                "Aktualizace GitHub komponent",
                $"Nalezeno {updates.Count} aktualizací • {names}",
                async progressToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progressToken);
                    return await UpstreamSourceUpdateService.UpdateSourcesAsync(
                        updates.Select(x => x.Id),
                        linked.Token,
                        (index, total, name) => liveStatus($"{index}/{total} • Stahuji {name}…"));
                },
                result => result.All(x => x.Error == null),
                result =>
                {
                    var errors = result.Where(x => x.Error != null).Select(x => $"{x.Name}: {x.Error}").Take(2);
                    return string.Join(" • ", errors);
                },
                liveStatusBinder: setter => liveStatus = setter);

            if (!progress.Completed) return;

            int failed = progress.Result.Count(x => x.Error != null);
            LogService.Info("upstream",
                $"STARTUP_GITHUB_UPDATE_APPLY_OK requested={updates.Count} updated={progress.Result.Count - failed} failed={failed} popup=true");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("upstream", "Startup GitHub update check failed", ex);
        }
    }
}
