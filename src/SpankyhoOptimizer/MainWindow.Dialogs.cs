using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow : Window
{
    private async Task ShowMessageAsync(string title, string text)
    {
        await ShowStyledInfoAsync(title, text, "Zavřít");
    }

    private Button QuickButton(string text, Func<Task> action, Func<bool>? canExecute = null)
    {
        Button b = new Button { Content = text };
        ApplyPillButtonAppearance(b);
        b.MinHeight = UseGlassChrome ? 36 : 35;
        b.CornerRadius = new CornerRadius(UseGlassChrome ? 14 : 7);
        b.Padding = new Thickness(UseGlassChrome ? 15 : 13, 6, UseGlassChrome ? 15 : 13, 6);
        b.FontSize = 12.5;
        if (!UseNeutralSectionChrome) AttachHoverMotion(b, 1.012f);
        int actionBusy = 0;
        b.Click += async delegate
        {
            try
            {
                if (Interlocked.Exchange(ref actionBusy, 1) != 0)
                {
                    LogService.Info("ui", $"QUICK_ACTION_DUPLICATE_BLOCKED text={text}");
                    return;
                }

                object normalContent = b.Content;
                // Keep focus while guarded async work runs; actionBusy blocks duplicate activation.
                b.Content = "Pracuji\u2026";
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    LogService.Error($"Quick action failed: {text} | HRESULT=0x{ex.HResult:X8}", ex);
                    ShowActionFeedback(text, ActionFeedbackKind.Failed, ex.Message);
                    await ShowMessageAsync("Chyba", ex.Message);
                }
                finally
                {
                    b.Content = normalContent;
                    b.IsEnabled = canExecute?.Invoke() ?? true;
                    Interlocked.Exchange(ref actionBusy, 0);
                }
            }
            catch (Exception ex)
            {
                // An asynchronous event handler must never let an exception reach
                // the WinUI dispatcher; a failed feedback dialog would otherwise crash.
                Interlocked.Exchange(ref actionBusy, 0);
                LogService.Error($"Quick action handler failed: {text} | HRESULT=0x{ex.HResult:X8}", ex);
            }
        };
        return b;
    }




    private void BuildMemoryActions()
    {
        BuildMemoryState();
        var defaults = new StackPanel { Spacing = 8 };
        defaults.Children.Add(new TextBlock { Text = "Plánování aktivních aplikací — výchozí nastavení Windows", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "Systémová cache — výchozí nastavení Windows", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "Stránkovací soubor — spravuje Windows", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "Doporučené nastavení ponechává plánování, systémovou cache a stránkovací soubor na bezpečných výchozích hodnotách Windows.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        ContentStack.Children.Add(VisualPanel("\uE950", "Paměť a plánování", "Doporučené = bezpečné výchozí hodnoty Windows", defaults));
        var memoryTools = new StackPanel { Spacing = 8 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.0 };
        row.Children.Add(QuickButton("Spustit údržbu paměti", async delegate
        {
            await WindowsActions.RunIdleMaintenanceAsync();
        }));
        row.Children.Add(QuickButton("Uvolnit nepoužívanou paměť", async delegate
        {
            await WindowsActions.TrimWorkingSetsAsync();
        }));
        row.Children.Add(QuickButton("Komprese paměti", async delegate
        {
            var state = await WindowsActions.GetMemoryCompressionAsync();
            var result = await ShowStyledChoiceAsync("Komprese paměti", state + "\n\nVe Windows je doporučené ponechat ji zapnutou.", "Zapnout", "Vypnout");
            if (result == 0) return;
            bool enable = result == 1;
            await WindowsActions.SetMemoryCompressionAsync(enable);
            InvalidateMemoryRecommendedPanel();
            var readback = await WindowsActions.GetMemoryCompressionAsync();
            bool verified = readback.Contains(enable.ToString(), StringComparison.OrdinalIgnoreCase);
            ShowActionFeedback("Komprese paměti", verified ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed, readback);
            if (_section == "memory") await ShowSectionAsync("memory");
        }));
        memoryTools.Children.Add(row);
        memoryTools.Children.Add(BuildGTweakMemoryAdvancedActions());
        ContentStack.Children.Add(BuildDashboardExpander(
            "Paměťové nástroje",
            "Údržba paměti • uvolnění nepoužívané paměti • komprese • pokročilé čištění",
            memoryTools));
    }

    private Border? _storageToolsCard;
    private StackPanel? _storageActionsHost;
    private StackPanel? _storageActionsQueuedHost;
    private int _storageActionsQueuedGeneration;

    private Border EnsureStorageActionsCard()
    {
        if (_storageToolsCard != null)
            return _storageToolsCard;

        var root = new StackPanel { Spacing = 8 };
        _storageToolsCard = BuildDashboardExpander(
            "Storage nástroje",
            "Optimize Drives • NTFS • Windows.old • cache",
            root);

        if (_storageToolsCard.Child is Expander expander)
        {
            bool materialized = false;
            expander.Expanding += (_, _) =>
            {
                if (materialized) return;
                materialized = true;

                var main = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                main.Children.Add(QuickButton("Otevřít Optimize Drives", delegate
                {
                    ProcessRunner.Open("dfrgui.exe");
                    return Task.CompletedTask;
                }));
                root.Children.Add(main);
                root.Children.Add(BuildGTweakStorageActions());
            };
        }

        return _storageToolsCard;
    }

    private void PrewarmStorageActionsCard()
    {
        _ = EnsureStorageActionsCard();
    }

    private void BuildStorageActions(CancellationToken token)
    {
        _storageActionsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(_storageActionsHost);
        ScheduleStorageActions(_storageActionsHost, token);
    }

    private void ScheduleStorageActions(StackPanel host, CancellationToken token)
    {
        int generation = _navigationGeneration;
        if (host.Children.Count > 0 ||
            (ReferenceEquals(_storageActionsQueuedHost, host) &&
             _storageActionsQueuedGeneration == generation)) return;
        _storageActionsQueuedHost = host;
        _storageActionsQueuedGeneration = generation;

        void ReleaseQueueTicket()
        {
            if (ReferenceEquals(_storageActionsQueuedHost, host) &&
                _storageActionsQueuedGeneration == generation)
            {
                _storageActionsQueuedHost = null;
                _storageActionsQueuedGeneration = 0;
            }
        }

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseQueueTicket();
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !ReferenceEquals(_storageActionsHost, host) ||
                host.Children.Count > 0) return;

            var sw = Stopwatch.StartNew();
            host.Children.Add(EnsureStorageActionsCard());
            SchedulePageScrollPolicyRefresh();
            LogService.Info("performance", $"STORAGE_ACTIONS_DEFERRED ms={sw.ElapsedMilliseconds} generation={generation}");
        }))
            ReleaseQueueTicket();
    }

    private Border? _gpuToolsCard;
    private bool _gpuToolsQueued;

    private void ScheduleGpuActions(CancellationToken token)
    {
        if (_gpuToolsCard != null)
        {
            ContentStack.Children.Add(_gpuToolsCard);
            return;
        }
        if (_gpuToolsQueued) return;
        _gpuToolsQueued = true;
        // A rejected low-priority enqueue must release the pending bit, or the
        // GPU tools card could never be scheduled again in this session.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _gpuToolsQueued = false;
            if (token.IsCancellationRequested || _section != "gpu" || _gpuToolsCard != null) return;
            var sw = Stopwatch.StartNew();
            BuildGpuActions();
            LogService.Info("performance", $"GPU_TOOLS_DEFERRED ms={sw.ElapsedMilliseconds}");
        })) _gpuToolsQueued = false;
    }

    private void BuildGpuActions()
    {
        if (_gpuToolsCard != null)
        {
            ContentStack.Children.Add(_gpuToolsCard);
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(QuickButton("Grafické nastavení Windows", delegate
        {
            ProcessRunner.Open("ms-settings:display-advancedgraphics");
            return Task.CompletedTask;
        }));
        row.Children.Add(QuickButton("Vyčistit shader cache", async () =>
        {
            try
            {
                long before = await StorageMaintenanceService.GetShaderCacheSizeAsync();
                if (before <= 0)
                {
                    ShowActionFeedback("GPU shader cache", ActionFeedbackKind.Info, "Shader cache je už prázdná.");
                    await ShowMessageAsync("GPU", "Shader cache je už prázdná.");
                    return;
                }

                if (!await ShowStyledConfirmAsync(
                    "Vyčistit GPU shader cache?",
                    $"Shader cache mají přibližně {before / 1048576d:0.0} MB. Použij jen při konkrétním problému; první start hry po vyčištění může znovu kompilovat shadery a krátce stutterovat.",
                    "Vyčistit",
                    "Zrušit",
                    "GPU / Display")) return;

                await StorageMaintenanceService.ClearShaderCachesAsync();
                long after = await StorageMaintenanceService.GetShaderCacheSizeAsync();
                long freed = Math.Max(0, before - after);
                string detail = $"Uvolněno {freed / 1048576d:0.0} MB • zbývá {after / 1048576d:0.0} MB.";
                ShowActionFeedback(
                    "GPU shader cache",
                    after == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                    detail);
                await ShowMessageAsync("GPU", detail);
            }
            catch (Exception ex)
            {
                LogService.Error("gpu", "Shader cache cleanup failed", ex);
                ShowActionFeedback("GPU shader cache", ActionFeedbackKind.Failed, ex.Message);
                await ShowMessageAsync("GPU", "Vyčištění shader cache selhalo: " + ex.Message);
            }
        }));

        _gpuToolsCard = BuildDashboardExpander(
            "Grafické nástroje",
            "Windows Graphics Settings • shader cache",
            row);
        ContentStack.Children.Add(_gpuToolsCard);
    }

    private Task BuildDriverActionsAsync(CancellationToken token)
    {
        _networkDriverActionToken = token;
        if (_networkDriverCard != null && _networkRealtekSummary != null)
        {
            if (!ContentStack.Children.Contains(_networkDriverCard))
                AttachCachedElement(_networkDriverCard);
            RefreshNetworkDriverSummaryIfExpanded();
            return Task.CompletedTask;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "network") return;

            var summary = new TextBlock { Text = "Rozbal pro načtení stavu ovladače síťové karty.", TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            _networkRealtekSummary = summary;
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(summary);
            stack.Children.Add(QuickButton("Obnovit stav", () =>
            {
                _networkDriverSummaryAt = default;
                RefreshNetworkDriverSummaryIfExpanded();
                return Task.CompletedTask;
            }));
            stack.Children.Add(QuickButton("Zkontrolovat Windows Update", async delegate { await WindowsActions.RealtekScanAsync(_networkDriverActionToken); }));
            _networkDriverCard = BuildDashboardExpander(
                "Driver síťové karty",
                "Realtek • Windows Update • stav aktivního adaptéru",
                stack);
            ContentStack.Children.Add(_networkDriverCard);
            if (_networkPageCache != null && !_networkPageCache.Contains(_networkDriverCard))
                _networkPageCache.Add(_networkDriverCard);
            if (_networkDriverCard.Child is Expander expander)
                expander.Expanding += (_, _) =>
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        RefreshNetworkDriverSummaryIfExpanded);
        });
        return Task.CompletedTask;
    }

    // A collapsed driver card should not probe Windows Update / Realtek on every
    // navigation. An expanded cached card refreshes at most once per 20 seconds,
    // and a manual refresh remains available for immediate read-back.
    private void RefreshNetworkDriverSummaryIfExpanded()
    {
        if (_section != "network" || _networkDriverCard?.Child is not Expander { IsExpanded: true } ||
            _networkRealtekSummary is not { } target) return;
        CancellationToken token = _loadCts?.Token ?? _windowLifetimeCts.Token;
        if (token.IsCancellationRequested) return;
        int generation = _navigationGeneration;
        if (_networkDriverSummaryLoadingGeneration == generation ||
            DateTime.UtcNow - _networkDriverSummaryAt < TimeSpan.FromSeconds(20)) return;

        _networkDriverSummaryLoadingGeneration = generation;
        SafeTaskService.Forget(RunReadbackAsync(), "network", "On-demand network driver readback");
        async Task RunReadbackAsync()
        {
            try
            {
                await LoadDriverSummaryAsync(target, token);
                if (!token.IsCancellationRequested && generation == _navigationGeneration &&
                    _section == "network" && ReferenceEquals(_networkRealtekSummary, target))
                    _networkDriverSummaryAt = DateTime.UtcNow;
            }
            finally
            {
                if (_networkDriverSummaryLoadingGeneration == generation)
                    _networkDriverSummaryLoadingGeneration = -1;
            }
        }
    }

    private async Task LoadDriverSummaryAsync(TextBlock target, CancellationToken token)
    {
        int generation = _navigationGeneration;
        string text;
        Exception? error = null;
        try
        {
            text = await WindowsActions.GetRealtekSummaryAsync(token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            error = ex;
            text = "Realtek kontrolu se nepodařilo načíst: " + ex.Message;
        }

        if (token.IsCancellationRequested ||
            generation != _navigationGeneration ||
            _section != "network" ||
            !ReferenceEquals(_networkRealtekSummary, target)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "network" ||
                !ReferenceEquals(_networkRealtekSummary, target)) return;
            target.Text = text;
            target.Foreground = error == null ? TextSecondaryBrush() : WarningBrush();
            if (error != null)
                ShowActionFeedback("Realtek driver", ActionFeedbackKind.Failed, error.Message);
        });
    }

    private Border ActionCard(string title, string desc, string button, Func<Task> action)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string tag = string.IsNullOrWhiteSpace(_themeSection) ? "system" : _themeSection;
        var icon = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(11), Background = _darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = SectionIcon(tag, 36), VerticalAlignment = VerticalAlignment.Center };
        if (!UseNeutralSectionChrome) AttachHoverMotion(icon, 1.025f); grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 17.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = desc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush(), MaxWidth = 840 });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var actionButton = QuickButton(button, action); Grid.SetColumn(actionButton, 2); grid.Children.Add(actionButton);
        var card = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.25 : 1), CornerRadius = new CornerRadius(13), Padding = new Thickness(15, 13, 15, 13), Margin = new Thickness(0, 0, 0, 10), Child = grid };
        if (!UseNeutralSectionChrome) AttachHoverMotion(card, 1.006f);
        return card;
    }


    private static Brush UiBrush(string name)
    {
        return name switch
        {
            "SpankySuccessBrush" => SuccessBrush(),
            "SpankyWarningBrush" => WarningBrush(),
            "SpankyAccentBrush" => AccentBrush(),
            "SpankyAccentSoftBrush" => AccentSoftBrush(),
            _ => AccentBrush()
        };
    }

    private static void ApplyPillButtonAppearance(Button button)
    {
        button.CornerRadius = new CornerRadius(4);
        button.Padding = new Thickness(13, 6, 13, 6);
        button.MinHeight = 34d;
        button.FontSize = 12d;
        button.Foreground = TextPrimaryBrush();
        // Shared material buttons across every page.
        ApplyMaterialButtonAppearance(button);
        button.BorderThickness = new Thickness(1);
        EnsureAccessibleButton(button);
    }
}
