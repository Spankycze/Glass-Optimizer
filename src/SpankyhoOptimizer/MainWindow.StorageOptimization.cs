using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Border? _storageOptimizationAdvisorCard;
    private Action? _storageOptimizationAdvisorRefresh;

    private void ScheduleStorageOptimizationAdvisor(CancellationToken token)
    {
        // The advisor is part of the cached Storage page. Reattach its real
        // card rather than recreating its controls and subscriptions.
        if (_storageOptimizationAdvisorCard is { } cached)
        {
            if (!ContentStack.Children.Contains(cached)) AttachCachedElement(cached);
            // Returning to an already-expanded card does not raise Expanding.
            // Resume any read canceled by navigation, or refresh stale results.
            if (cached.Child is Expander { IsExpanded: true })
                _storageOptimizationAdvisorRefresh?.Invoke();
            return;
        }
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "storage") return;
            BuildStorageOptimizationAdvisor(generation, token);
        });
    }

    private void BuildStorageOptimizationAdvisor(int generation, CancellationToken token)
    {
        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(new TextBlock
        {
            Text = "Rozbal pro kontrolu skutečného stavu TRIM, NTFS a údržby SSD.",
            Foreground = TextSecondaryBrush()
        });
        Border card = BuildDashboardExpander(
            "Storage Recommended",
            "TRIM • NTFS Last Access • Scheduled Optimize • kompatibilní hodnoty",
            host);
        _storageOptimizationAdvisorCard = card;
        ContentStack.Children.Add(card);
        // Deferred cards may be created after the navigation cache snapshot.
        if (_storagePageCache != null && !_storagePageCache.Contains(card))
            _storagePageCache.Add(card);

        if (card.Child is not Expander expander) return;
        bool loading = false;
        DateTime loadedUtc = default;
        int loadingGeneration = -1;
        void RefreshOnDemand()
        {
            int currentGeneration = _navigationGeneration;
            CancellationToken currentToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
            if (_section != "storage" || currentToken.IsCancellationRequested) return;
            if (loading && loadingGeneration == currentGeneration) return;
            if (DateTime.UtcNow - loadedUtc < TimeSpan.FromSeconds(30)) return;
            loading = true;
            loadingGeneration = currentGeneration;
            host.Children.Clear();
            host.Children.Add(new TextBlock
            {
                Text = "Kontroluji TRIM, NTFS a plán údržby SSD…",
                Foreground = TextSecondaryBrush()
            });
            SafeTaskService.Forget(
                LoadStorageOptimizationAdvisorAsync(host, card, expander, currentGeneration,
                    currentToken,
                    () => loadedUtc = DateTime.UtcNow,
                    () => { if (loadingGeneration == currentGeneration) loading = false; }),
                "storage", "On-demand Storage recommended readback");
        }
        _storageOptimizationAdvisorRefresh = RefreshOnDemand;
        expander.Expanding += (_, _) => RefreshOnDemand();
    }

    private async Task LoadStorageOptimizationAdvisorAsync(
        StackPanel host, Border card, Expander expander, int generation,
        CancellationToken token, Action markLoaded, Action markCompleted)
    {
        try
        {
            // All OS reads stay async. The continuation returns to WinUI only
            // to update a still-expanded card from the current navigation.
            var state = await StorageOptimizationService.ReadAsync(token);
            AiStorageGuardianStatus? guardian = await Task.Run(
                AiStorageGuardianService.ReadLatest, token);
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !expander.IsExpanded ||
                !ReferenceEquals(_storageOptimizationAdvisorCard, card)) return;
            host.Children.Clear();
            RenderStorageOptimizationAdvisor(host, state, guardian);
            markLoaded();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("storage", "Storage optimization advisor failed", ex);
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !expander.IsExpanded ||
                !ReferenceEquals(_storageOptimizationAdvisorCard, card)) return;
            host.Children.Clear();
            host.Children.Add(new TextBlock
            {
                Text = "Aktuální stav úložiště není dostupný: " + ex.Message,
                Foreground = WarningBrush(),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });
        }
        finally { markCompleted(); }
    }

    // Nonmutating UI verification: opening the Storage page itself must not
    // launch fsutil/Task Scheduler readback for a collapsed advisor.
    internal async Task RunStorageAdvisorLazySmokeAsync()
    {
        await ShowSectionAsync("storage");
        for (int i = 0; i < 120 && _storageOptimizationAdvisorCard == null; i++)
            await Task.Delay(25);
        Border card = _storageOptimizationAdvisorCard
            ?? throw new InvalidOperationException("Storage Recommended card never appeared.");
        if (card.Child is not Expander expander ||
            expander.Content is not StackPanel content ||
            expander.IsExpanded || content.Children.Count != 1)
            throw new InvalidOperationException("Collapsed Storage Recommended card performed eager rendering.");

        expander.IsExpanded = true;
        for (int i = 0; i < 240 && content.Children.Count < 5; i++)
            await Task.Delay(50);
        if (content.Children.Count < 5)
            throw new InvalidOperationException("Storage Recommended readback never rendered after expanding.");

        await ShowSectionAsync("overview");
        await ShowSectionAsync("storage");
        // The storage page can finish attaching its deferred shell after
        // ShowSectionAsync returns; wait for that legitimate UI transition.
        for (int i = 0; i < 80 &&
            (_storageOptimizationAdvisorCard == null ||
             !ContentStack.Children.Contains(_storageOptimizationAdvisorCard)); i++)
            await Task.Delay(25);
        if (_storageOptimizationAdvisorCard is not { } restored ||
            !ContentStack.Children.Contains(restored))
            throw new InvalidOperationException(
                $"Storage Recommended card missing after return: cache={_storagePageCache?.Count ?? 0} " +
                $"oldInCache={_storagePageCache?.Contains(card)} originalRef={ReferenceEquals(_storageOptimizationAdvisorCard, card)} " +
                $"contentChildren={ContentStack.Children.Count}.");
        if (_storagePageCache?.Contains(card) == true && !ReferenceEquals(restored, card))
            throw new InvalidOperationException("Storage Recommended unexpectedly rebuilt a cached card.");
        LogService.Info("storage", "STORAGE_ADVISOR_LAZY_SMOKE_OK firstRead=onExpand cachedCard=preserved");
    }

    private void RenderStorageOptimizationAdvisor(
        StackPanel host, StorageOptimizationSnapshot s, AiStorageGuardianStatus? ai)
    {
        // The independent FanAgent writes status separately; UI uses the
        // worker-captured immutable snapshot without opening JSON on thread.
        string aiSummary = ai == null
            ? "AI SSD/NVMe: čekám na klidový režim pro první kontrolu na pozadí."
            : $"AI SSD/NVMe: {ai.NvmeCount} NVMe, {ai.SataSsdCount} SATA SSD • " +
              $"TRIM {(ai.NtfsTrimEnabled == true ? "ON" : ai.NtfsTrimEnabled == false ? "OFF" : "?")} • " +
              $"údržba {(ai.ScheduledOptimizationEnabled == true ? "ON" : ai.ScheduledOptimizationEnabled == false ? "OFF" : "?")} • " +
              (ai.Warnings.Length == 0 ? "Bez zjištěných rizik." : string.Join("; ", ai.Warnings.Take(3)));
        host.Children.Add(new TextBlock
        {
            Text = aiSummary,
            Foreground = ai != null && ai.Warnings.Length > 0 ? WarningBrush() : TextSecondaryBrush(),
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            FontSize = 12
        });
        if (ai != null)
        {
            string disks = string.Join(" • ", ai.Drives.Select(x =>
                x.Name + (x.TemperatureC is double c ? $" {c:0} °C" : " teplota ?")));
            host.Children.Add(new TextBlock
            {
                Text = disks.Length > 0
                    ? disks + " • " + ai.LastAction
                    : "Zatím nebyl rozpoznán fyzický NVMe/SATA SSD.",
                Foreground = TextSecondaryBrush(),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                FontSize = 11
            });
        }

        string trimCurrent = s.TrimEnabled switch
        {
            true => "ON",
            false => "OFF",
            _ => "Nezjištěno"
        };
        host.Children.Add(BuildOptimizationAdvisorRow(
            "TRIM / DeleteNotify",
            trimCurrent,
            "ON",
            "SSD/NVMe má dostávat informace o uvolněných blocích; Optimizer změnu vždy následně ověří.",
            s.TrimEnabled == true
                ? OptimizationAdvisorKind.Ok
                : s.TrimEnabled == false
                    ? OptimizationAdvisorKind.Recommended
                    : OptimizationAdvisorKind.Info));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "NTFS Last Access",
            s.LastAccessText,
            "Disabled",
            "Omezí metadata zápisy; při změně zachová user/system-managed režim NTFS. Úplné uplatnění může vyžadovat restart Windows (Optimizer jej neprovádí).",
            s.LastAccessMode is 1 or 3
                ? OptimizationAdvisorKind.Ok
                : s.LastAccessMode.HasValue
                    ? OptimizationAdvisorKind.Recommended
                    : OptimizationAdvisorKind.Info));

        string scheduled = s.ScheduledOptimizeEnabled switch
        {
            true => "ON",
            false => "OFF",
            _ => "Nezjištěno"
        };
        host.Children.Add(BuildOptimizationAdvisorRow(
            "Scheduled Optimize Drives",
            scheduled,
            "ON",
            "Windows na SSD provádí správnou údržbu/TRIM; vypínat ScheduledDefrag plošně není doporučené.",
            s.ScheduledOptimizeEnabled == true
                ? OptimizationAdvisorKind.Ok
                : s.ScheduledOptimizeEnabled == false
                    ? OptimizationAdvisorKind.Recommended
                    : OptimizationAdvisorKind.Info));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "NTFS 8.3 names",
            s.EightDotThreeText,
            "TEST / kompatibilita",
            "Starší instalátory a aplikace mohou 8.3 názvy potřebovat, proto se tato hodnota nikdy nemění automaticky.",
            OptimizationAdvisorKind.Test));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "Write cache / buffer flushing",
            s.WriteCacheText,
            "Ponechat Windows / driver",
            "Optimizer nebude vypínat write-cache buffer flushing; při výpadku napájení by to mohlo zvýšit riziko ztráty dat.",
            OptimizationAdvisorKind.Info));

        bool needsApply =
            s.TrimEnabled == false ||
            (s.LastAccessMode.HasValue && s.LastAccessMode is not (1 or 3)) ||
            s.ScheduledOptimizeEnabled == false;

        if (needsApply)
        {
            bool applying = false;
            host.Children.Add(PrimaryQuickButton("Použít Storage Recommended", async () =>
            {
                if (applying) return;
                applying = true;
                int actionGeneration = _navigationGeneration;
                CancellationToken actionToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
                try
                {
                    // NTFS Last Access and TRIM are system settings, not just
                    // advisory text. Never change them without explicit consent.
                    if (!await ShowStyledConfirmAsync(
                        "Storage Recommended",
                        "Ověřené doporučené změny upraví TRIM, NTFS Last Access nebo plán Optimalizace jednotek. Některé změny mohou vyžadovat restart Windows, který Optimizer automaticky neprovádí.",
                        "Použít", "Zrušit", "Úložiště / SSD")) return;
                    if (actionToken.IsCancellationRequested ||
                        actionGeneration != _navigationGeneration || _section != "storage") return;
                    var after = await StorageOptimizationService.ApplyRecommendedAsync(actionToken);
                    SystemInsightService.InvalidateStorage(
                        preserveStableDriverMetadata: true,
                        invalidateTemperatures: false,
                        invalidateTrim: true);
                    if (actionToken.IsCancellationRequested ||
                        actionGeneration != _navigationGeneration || _section != "storage") return;
                    AiStorageGuardianStatus? afterGuardian =
                        await Task.Run(AiStorageGuardianService.ReadLatest, actionToken);
                    if (actionToken.IsCancellationRequested ||
                        actionGeneration != _navigationGeneration || _section != "storage") return;
                    host.Children.Clear();
                    RenderStorageOptimizationAdvisor(host, after, afterGuardian);
                    ShowActionFeedback("Storage Recommended", ActionFeedbackKind.Verified,
                        "TRIM • Last Access • Optimize Drives byly ověřeny.");
                }
                catch (OperationCanceledException) when (actionToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    LogService.Error("storage", "Storage Recommended apply failed", ex);
                    if (actionGeneration == _navigationGeneration && _section == "storage")
                    {
                        ShowActionFeedback("Storage Recommended", ActionFeedbackKind.Failed, ex.Message);
                        await ShowMessageAsync("Storage Recommended", ex.Message);
                    }
                }
                finally { applying = false; }
            }));
        }
    }
}
