using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow : Window
{
    private void ScheduleRenderFirstBackground(string sectionKey, string cacheKey, TweakDefinition[] tweaks, bool staleDetection, CancellationToken token)
    {
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;

            var immediate = tweaks.Where(x => !x.LazyDetect).ToArray();
            var lazy = tweaks.Where(x => x.LazyDetect).ToArray();

            if (staleDetection && immediate.Length > 0)
                SafeTaskService.Forget(
                    DetectSectionTweaksAndRefreshAsync(sectionKey, cacheKey, immediate, token, generation),
                    "tweaks",
                    "Background immediate detect " + sectionKey);

            if (lazy.Length > 0)
                SafeTaskService.Forget(
                    DetectLazyTweaksAfterSettleAsync(sectionKey, lazy, token, generation),
                    "tweaks",
                    "Background lazy detect " + sectionKey);

            if (sectionKey is "network" or "memory")
                SafeTaskService.Forget(BuildHardwareRecommendedPanelAsync(sectionKey, tweaks, token), sectionKey, "Lazy hardware recommended");
        });
    }

    private object? _sectionDetectGateStore;
    private HashSet<string>? _sectionDetectInFlightStore;
    private object _sectionDetectGate => _sectionDetectGateStore ??= new object();
    private HashSet<string> _sectionDetectInFlight =>
        _sectionDetectInFlightStore ??= new(StringComparer.OrdinalIgnoreCase);

    private async Task DetectSectionTweaksAndRefreshAsync(string sectionKey, string cacheKey, TweakDefinition[] tweaks, CancellationToken token, int generation = -1)
    {
        if (generation < 0) generation = _navigationGeneration;
        lock (_sectionDetectGate)
        {
            if (!_sectionDetectInFlight.Add(cacheKey)) return;
        }
        try
        {
            // Read-only detection is shared work: finish it even when navigation leaves the section,
            // but never outlive the application window.
            await _engine.DetectAsync(tweaks, _windowLifetimeCts.Token);
            _tweakCacheTime[cacheKey] = DateTime.UtcNow;
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;
                RefreshCards();
                SyncAttachedClassicCacheRevisionStamp(sectionKey, cacheKey);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Background tweak detect failed: " + sectionKey, ex);
            if (!token.IsCancellationRequested &&
                generation == _navigationGeneration &&
                string.Equals(_section, sectionKey, StringComparison.OrdinalIgnoreCase))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested ||
                        generation != _navigationGeneration ||
                        !string.Equals(_section, sectionKey, StringComparison.OrdinalIgnoreCase))
                        return;

                    ShowActionFeedback(
                        sectionKey.Equals("privacy", StringComparison.OrdinalIgnoreCase) ? "Privacy & Security" : sectionKey,
                        ActionFeedbackKind.Failed,
                        "Nepodařilo se ověřit skutečný stav některých přepínačů: " + ex.Message);
                });
            }
        }
        finally
        {
            lock (_sectionDetectGate) _sectionDetectInFlight.Remove(cacheKey);
        }
    }
    private async Task DetectLazyTweaksAfterSettleAsync(string sectionKey, TweakDefinition[] tweaks, CancellationToken token, int generation)
    {
        try
        {
            await Task.Delay(250, token);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;
            await DetectLazyTweaksAsync(sectionKey, tweaks, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task DetectLazyTweaksAsync(string sectionKey, TweakDefinition[] tweaks, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            await _engine.DetectAsync(tweaks, token);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && generation == _navigationGeneration && _section == sectionKey)
                    RefreshCards();
            });
        }
        catch (OperationCanceledException) { }
    }

    private void RenderSectionError(string key, string id, Exception ex, int generation)
    {
        if (generation != _navigationGeneration || !string.Equals(_section, key, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info("navigation",
                $"STALE_ERROR_SURFACE_BLOCKED section={key} gen={generation} currentGen={_navigationGeneration} currentSection={_section}");
            return;
        }

        ContentStack.Children.Clear(); TopActions.Children.Clear(); ActionBar.Visibility = Visibility.Collapsed;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Sekci se nepodařilo načíst", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        panel.Children.Add(new TextBlock { Text = ex.Message + "  [ID " + id + "]", TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush() });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var retry = new Button { Content = "Zkusit znovu" };
        ApplyPillButtonAppearance(retry);
        retry.Click += (_, _) =>
        {
            if (generation != _navigationGeneration ||
                !string.Equals(_section, key, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_lastNavigationErrorId, id, StringComparison.Ordinal))
                return;

            SafeTaskService.Forget(ShowSectionAsync(key), "navigation", "Retry " + key);
        };
        var copy = new Button { Content = "Kopírovat chybu" };
        ApplyPillButtonAppearance(copy);
        copy.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText($"Spankyho Optimizer\nSekce: {key}\nID: {id}\nTyp: {ex.GetType().FullName}\nChyba: {ex.Message}\nStage: {StartupDiagnostics.LastStage}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        };
        actions.Children.Add(retry); actions.Children.Add(copy); panel.Children.Add(actions);
        ContentStack.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.2 : 1), CornerRadius = new CornerRadius(16), Padding = new Thickness(20), Child = panel });
    }

    public async Task RunSectionSmokeTestAsync()
    {
        foreach (string key in CatalogService.Sections.Select(x => x.Key))
        {
            if (key == "components")
            {
                LogService.Info("section-smoke", "SKIP components (replaced by ai-runtime)");
                continue;
            }
            LogService.Info("section-smoke", "ENTER " + key); await ShowSectionAsync(key); await AssertSectionLayoutHealthyAsync(key); EnsureNavigationHealthy(key); LogService.Info("section-smoke", "OK " + key); await Task.Delay(15);
        }
        var wmRead = Stopwatch.StartNew();
        WindowsModernizerSnapshot wm = WindowsModernizerService.ReadSnapshot();
        wmRead.Stop();
        LogService.Info("WINDOWS_MODERNIZER_READONLY_SMOKE_OK ms=" + wmRead.ElapsedMilliseconds +
            " ui=" + wm.UiBaselineConfigured + " search=" + wm.SearchCleanupConfigured +
            " mtu=" + (wm.ActiveMtu?.ToString() ?? "n/a") +
            " vendor=" + wm.VendorServicesTuned);
        if (wmRead.ElapsedMilliseconds > 100)
            throw new InvalidOperationException("WindowsModernizer snapshot hot-path exceeded 100 ms: " + wmRead.ElapsedMilliseconds + " ms");
        var adaptiveSw = Stopwatch.StartNew();
        AdaptiveScanReport adaptive = await WindowsModernizerService.ScanAdaptiveSystemAsync();
        adaptiveSw.Stop();
        if (adaptive.Total <= 0)
            throw new InvalidOperationException("Adaptive Windows scan returned no findings.");
        LogService.Info("WINDOWS_MODERNIZER_ADAPTIVE_SCAN_SMOKE_OK ms=" + adaptiveSw.ElapsedMilliseconds +
            " total=" + adaptive.Total + " needed=" + adaptive.Needed + " ok=" + adaptive.AlreadyOptimal +
            " manual=" + adaptive.ManualReview + " na=" + adaptive.NotApplicable);
        if (adaptiveSw.ElapsedMilliseconds > 5000)
            throw new InvalidOperationException("Adaptive Windows scan exceeded 5000 ms: " + adaptiveSw.ElapsedMilliseconds + " ms");
        LogService.Info("SECTION_SMOKE_OK");
    }


    private static List<UIElement> SnapshotChildren(StackPanel source)
    {
        int count = source.Children.Count;
        var snapshot = new List<UIElement>(count);
        for (int i = 0; i < count; i++)
            snapshot.Add(source.Children[i]);
        return snapshot;
    }

    private async Task CompleteDeferredServiceChromeAsync(int generation, CancellationToken token)
    {
        await Task.Delay(180, token).ConfigureAwait(false);
        if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "components" || _sub != "Služby")
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "components" || _sub != "Služby") return;
            BuildComponentsContextActions();
            BuildWindowsModernizerVendorPanel(token);
            CaptureComponentsPageCache("Služby");
        });
    }

    private async Task CompleteDeferredComponentsSelectorAsync(string[] options, int generation, CancellationToken token)
    {
        await Task.Delay(60, token).ConfigureAwait(false);
        if (token.IsCancellationRequested || generation != _navigationGeneration) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "components" || _sub != "Služby") return;
            BuildSubSelector("components", options);
        });
    }

    private void BuildSubSelector(string section, string[] options)
    {
        SubSelectorHost.Visibility = Visibility.Visible;
        if (!_subSelectorCache.TryGetValue(section, out var buttons))
        {
            buttons = new Button[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                string opt = options[i];
                var button = new Button { Content = opt, Tag = opt };
                ApplyPillButtonAppearance(button);
                button.Click += delegate
                {
                    SafeTaskService.Forget(NavigateSubSectionFromUiAsync(section, opt), "navigation", "Open sub-section " + opt);
                };
                buttons[i] = button;
            }
            _subSelectorCache[section] = buttons;
        }

        foreach (var button in buttons)
        {
            bool active = string.Equals(button.Tag as string, _sub, StringComparison.Ordinal);
            if (active)
            {
                button.Background = UiBrush("SpankyAccentBrush");
                button.Foreground = OnAccentTextBrush();
            }
            else
            {
                button.Background = ButtonVioletSoftBrush();
                button.Foreground = TextPrimaryBrush();
            }
            SubSelectorHost.Children.Add(button);
        }
    }



    private void BuildTweakCards()
    {
        if (UseVirtualTweakRows())
        {
            BuildVirtualTweakCards();
            return;
        }
        ClearVirtualTweakRows();
        BuildClassicTweakCards(ContentStack);
    }

    private void BuildClassicTweakCards(Panel host)
    {
        var groups = new Dictionary<string, List<TweakDefinition>>();
        var order = new List<string>();
        foreach (TweakDefinition tweak in _tweaks)
        {
            if (!groups.TryGetValue(tweak.Group, out List<TweakDefinition>? items))
            {
                items = new List<TweakDefinition>();
                groups[tweak.Group] = items;
                order.Add(tweak.Group);
            }
            items.Add(tweak);
        }

        foreach (string groupName in order)
        {
            List<TweakDefinition> items = groups[groupName];
            host.Children.Add(CreateClassicTweakGroupHeader(groupName, items.Count));
            foreach (TweakDefinition tweak in items)
                host.Children.Add(CreateClassicTweakCard(tweak));
        }
    }

    private Border CreateClassicTweakGroupHeader(string groupName, int count)
    {
        // Group name and count are separated for scanning; no legacy violet rail,
        // uppercase wall of text or extra bordered card around each group.
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = groupName,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var counter = new TextBlock
        {
            Text = $"{count} nastavení",
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(counter, 1);
        row.Children.Add(counter);
        return new Border
        {
            Tag = "tweak-group-heading",
            Margin = new Thickness(0, 9, 0, 4),
            Padding = new Thickness(5, 6, 5, 7),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SectionBorderBrush(),
            Background = null,
            Child = row
        };
    }

    private Border CreateClassicTweakCard(TweakDefinition tweak)
    {
        tweak.PropertyChanged -= Tweak_PropertyChanged;
        tweak.PropertyChanged += Tweak_PropertyChanged;
        return CreateTweakCard(tweak);
    }


    // Headings may also be grid-backed after the UI rework. Count only
    // actual tweak models, never arbitrary visual shapes or header containers.
    private static int CountClassicTweakCards(Panel? host) =>
        host?.Children.OfType<Border>().Count(x => x.Tag is TweakDefinition) ?? 0;

    private void Tweak_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Pending" or "Current" or "IsLoaded") QueueDirtyStateRefresh();
    }
    private void QueueDirtyStateRefresh()
    {
        if (Interlocked.Exchange(ref _dirtyRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _dirtyRefreshQueued, 0);
            UpdateDirtyState();
        }))
            Interlocked.Exchange(ref _dirtyRefreshQueued, 0);
    }


    private sealed record ClassicTweakCardRefs(
        ToggleSwitch Toggle,
        TextBlock ToggleState,
        TextBlock Meta,
        Border StateBadge,
        TextBlock StateText,
        Border VerdictBadge,
        TextBlock VerdictText,
        bool NeutralChrome = false)
    {
        // Counts this card's actual user-toggle writebacks; unlike the global
        // dirty-refresh flag, unrelated tweak detection cannot change it.
        public int UserWritebacks { get; set; }
    }
}
