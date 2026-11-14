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
    private Border CreateTweakCard(TweakDefinition t)
    {
        bool neutralChrome = UseNeutralSectionChrome;
        var policy = TweakPolicyService.Evaluate(t, _tweakViewMode);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconBox = new Border { Width = UseCompactSectionChrome ? 34 : 42, Height = UseCompactSectionChrome ? 34 : 42, CornerRadius = new CornerRadius(8), Background = _darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = SectionIcon(t.Section, UseCompactSectionChrome ? 25 : 31), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(iconBox);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        // Keep status, risk and restart markers aligned without pushing the
        // whole tweak name off-screen on a narrower section viewport.
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = t.Name, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap,
            MaxLines = 2, VerticalAlignment = VerticalAlignment.Center
        });
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        var uiState = TweakUiState(t);
        var stateText = uiState.Text;
        var stateBrush = uiState.Resource;
        var stateBadge = Badge(stateText, stateBrush);
        badges.Children.Add(stateBadge);
        var verdict = Badge(policy.Verdict, policy.Verdict == "Recommended" ? "SpankySuccessBrush" : policy.Verdict == "Advanced" || policy.Verdict == "Needs attention" ? "SpankyWarningBrush" : "SpankyInfoBrush");
        verdict.Tag = "policyVerdict"; badges.Children.Add(verdict);
        if (t.RestartRequired) badges.Children.Add(Badge("Restart", "SpankyInfoBrush"));
        Grid.SetColumn(badges, 1); titleRow.Children.Add(badges);
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock { Text = t.Description, FontSize = UseCompactSectionChrome ? 12.5 : 12, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxLines = UseCompactSectionChrome ? 0 : 2, TextTrimming = UseCompactSectionChrome ? TextTrimming.None : TextTrimming.CharacterEllipsis, MaxWidth = 1100 });
        var meta = new TextBlock { Text = _tweakViewMode == TweakViewMode.Diagnostics || !t.IsLoaded ? policy.Detail : TweakVerificationMeta(t), FontSize = 10.8, Foreground = TextSecondaryBrush(), Tag = "meta", TextWrapping = TextWrapping.Wrap };
        text.Children.Add(meta);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var toggleState = new TextBlock { Text = t.Pending ? "ON" : "OFF", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ClassicToggleStateBrush(t.Pending, neutralChrome), VerticalAlignment = VerticalAlignment.Center, Tag = "toggleState", Margin = new Thickness(0, 0, 3, 0) };
        Grid.SetColumn(toggleState, 2); grid.Children.Add(toggleState);
        var toggle = new ToggleSwitch { IsOn = t.Pending, Tag = t, IsEnabled = policy.CanEdit && !t.IsBusy, VerticalAlignment = VerticalAlignment.Center, OffContent = "", OnContent = "", MinWidth = 50 };
        NormalizeToggleAppearance(toggle);
        AutomationProperties.SetName(toggle, t.Name);
        toggle.Toggled += delegate
        {
            var live = TweakPolicyService.Evaluate(t, _tweakViewMode);
            bool editable = live.CanEdit && t.IsLoaded && !t.IsBusy;
            if (!editable && toggle.IsOn != t.Pending)
            {
                // Do not let a denied or in-flight edit leave a false visual
                // ON/OFF state. The second Toggled callback sees equality
                // and does not write to the model.
                toggle.IsOn = t.Pending;
            }
            else if (editable && t.Pending != toggle.IsOn)
            {
                t.Pending = toggle.IsOn;
                if (grid.Tag is ClassicTweakCardRefs refs)
                    refs.UserWritebacks++;
                QueueDirtyStateRefresh();
            }

            string actual = t.Pending ? "ON" : "OFF";
            if (!string.Equals(toggleState.Text, actual, StringComparison.Ordinal))
                toggleState.Text = actual;
            toggleState.Foreground = ClassicToggleStateBrush(t.Pending, neutralChrome);
        };
        Grid.SetColumn(toggle, 3); grid.Children.Add(toggle);
        grid.Tag = new ClassicTweakCardRefs(
            toggle,
            toggleState,
            meta,
            stateBadge,
            (TextBlock)stateBadge.Child,
            verdict,
            (TextBlock)verdict.Child,
            neutralChrome);
        var row = new Border { Tag = t, Background = UseCompactSectionChrome ? (_darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush()) : SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = UseCompactSectionChrome ? new Thickness(0,0,0,1) : new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 0, UseCompactSectionChrome ? 0 : 7), Padding = UseCompactSectionChrome ? new Thickness(12,16,12,16) : new Thickness(14, 10, 14, 10), Child = grid };
        if (!UseNeutralSectionChrome) AttachHoverMotion(row, 1.002f); ToolTipService.SetToolTip(row, policy.Detail);
        return row;
    }


    private static Brush ClassicToggleStateBrush(bool on, bool neutral) =>
        neutral ? ThemeColor(on ? (byte)224 : (byte)152, on ? (byte)224 : (byte)152, on ? (byte)224 : (byte)152)
                : on ? SuccessBrush() : TextSecondaryBrush();

    private static void ApplyBadgeAppearance(Border badge, string resource, bool neutral)
    {
        Brush semantic = neutral ? PaletteBrush(resource switch
        {
            "SpankySuccessBrush" => NeutralBlackPalette.Success,
            "SpankyWarningBrush" => NeutralBlackPalette.Warning,
            "SpankyInfoBrush" => NeutralBlackPalette.Info,
            _ => NeutralBlackPalette.Accent
        }) : UiBrush(resource);
        badge.Background = neutral ? (_darkTheme ? GlassSurfaceLowBrush() : ThemeColor(245,245,245)) : semantic;
        if (badge.Child is TextBlock label)
            label.Foreground = neutral ? semantic : OnAccentTextBrush();
    }

    private static Border Badge(string text, string resource)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(7.0), Opacity = 0.88,
            Padding = new Thickness(7.0, 3.0, 7.0, 3.0),
            Child = new TextBlock { Text = text, FontSize = 10.5 }
        };
        ApplyBadgeAppearance(badge, resource, UseNeutralSectionChrome);
        return badge;
    }
    private long CurrentTweakRevisionStamp()
    {
        long stamp = _tweaks.Count;
        foreach (var tweak in _tweaks)
            stamp += tweak.Revision;
        return stamp;
    }

    private bool RefreshCardsIfTweakRevisionChanged(ref long cachedStamp)
    {
        long currentStamp = CurrentTweakRevisionStamp();
        if (currentStamp == cachedStamp)
            return false;

        RefreshCards();
        cachedStamp = currentStamp;
        return true;
    }

    private void SyncAttachedClassicCacheRevisionStamp(string sectionKey, string cacheKey)
    {
        long stamp = CurrentTweakRevisionStamp();
        if (sectionKey == "network" && _networkPageCache != null)
        {
            _networkPageRevisionStamp = stamp;
            return;
        }

    }

    private void RefreshClassicTweakCard(Border item)
    {
        if (item.Tag is not TweakDefinition tweakDefinition || item.Child is not Grid grid) return;
        var policy = TweakPolicyService.Evaluate(tweakDefinition, _tweakViewMode);

        if (grid.Tag is not ClassicTweakCardRefs refs)
        {
            // Compatibility fallback for any cached card created by an older layout path.
            ToggleSwitch? toggle = grid.Children.OfType<ToggleSwitch>().FirstOrDefault();
            StackPanel? stack = grid.Children.OfType<StackPanel>().FirstOrDefault();
            TextBlock? meta = stack?.Children.OfType<TextBlock>().FirstOrDefault(x => Equals(x.Tag, "meta"));
            StackPanel? title = stack?.Children.OfType<StackPanel>().FirstOrDefault(x => x.Orientation == Orientation.Horizontal);
            Border? verdict = title?.Children.OfType<Border>().FirstOrDefault(x => Equals(x.Tag, "policyVerdict"));
            Border? state = title?.Children.OfType<Border>().FirstOrDefault(x => !Equals(x.Tag, "policyVerdict"));
            TextBlock? toggleState = grid.Children.OfType<TextBlock>().FirstOrDefault(x => Equals(x.Tag, "toggleState"));
            if (toggle == null || meta == null || verdict?.Child is not TextBlock verdictText || state?.Child is not TextBlock stateTextBlock)
                return;
            toggleState ??= new TextBlock { Text = toggle.IsOn ? "ON" : "OFF", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = toggle.IsOn ? ThemeColor(224,224,224) : ThemeColor(152,152,152), VerticalAlignment = VerticalAlignment.Center, Tag = "toggleState" };
            refs = new ClassicTweakCardRefs(toggle, toggleState, meta, state, stateTextBlock, verdict, verdictText, UseNeutralSectionChrome);
            grid.Tag = refs;
        }

        bool enabled = policy.CanEdit && !tweakDefinition.IsBusy;
        if (refs.Toggle.IsEnabled != enabled) refs.Toggle.IsEnabled = enabled;
        if (refs.Toggle.IsOn != tweakDefinition.Pending) refs.Toggle.IsOn = tweakDefinition.Pending;
        string toggleText = tweakDefinition.Pending ? "ON" : "OFF";
        if (!string.Equals(refs.ToggleState.Text, toggleText, StringComparison.Ordinal)) refs.ToggleState.Text = toggleText;
        refs.ToggleState.Foreground = ClassicToggleStateBrush(tweakDefinition.Pending, refs.NeutralChrome);

        string metaText = _tweakViewMode == TweakViewMode.Diagnostics || !tweakDefinition.IsLoaded
            ? policy.Detail
            : TweakVerificationMeta(tweakDefinition);
        if (!string.Equals(refs.Meta.Text, metaText, StringComparison.Ordinal)) refs.Meta.Text = metaText;

        var uiState = TweakUiState(tweakDefinition);
        string stateText = uiState.Text;
        if (!string.Equals(refs.StateText.Text, stateText, StringComparison.Ordinal))
        {
            refs.StateText.Text = stateText;
            ApplyBadgeAppearance(refs.StateBadge, uiState.Resource, refs.NeutralChrome);
        }

        if (!string.Equals(refs.VerdictText.Text, policy.Verdict, StringComparison.Ordinal))
        {
            refs.VerdictText.Text = policy.Verdict;
            ApplyBadgeAppearance(refs.VerdictBadge, policy.Verdict == "Recommended"
                ? "SpankySuccessBrush"
                : policy.Verdict == "Advanced" || policy.Verdict == "Needs attention"
                    ? "SpankyWarningBrush"
                    : "SpankyInfoBrush", refs.NeutralChrome);
        }
        // Tooltip is initialized when the card is created. Rebuilding it during every
        // state refresh causes expensive WinUI tooltip materialization on the UI thread.
    }

    private void RefreshCards(bool force = false)
    {
        if (_virtualTweakRows != null)
            RefreshVirtualTweakRows(force);
        RefreshVirtualSectionDashboardMetrics(_section);

        foreach (Border item in ContentStack.Children.OfType<Border>())
            RefreshClassicTweakCard(item);
        // Detached cached pages are not visible, and touching their WinUI
        // controls makes every System/Privacy toggle also refresh unrelated
        // NIC, GPU and storage cards. Those sections refresh on cache attach
        // via RefreshCardsIfTweakRevisionChanged.
        if (_section == "network" && _networkTweakHost is { } networkHost &&
            ContentStack.Children.Contains(networkHost))
        {
            foreach (Border item in networkHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
        if (_section == "gpu" && _gpuTweakHost is { } gpuHost &&
            ContentStack.Children.Contains(gpuHost))
        {
            foreach (Border item in gpuHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
        if (_section == "storage" && _storageTweakHost is { } storageHost &&
            ContentStack.Children.Contains(storageHost))
        {
            foreach (Border item in storageHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
    }

    private void UpdateDirtyState()
    {
        int num = _tweaks.Count((TweakDefinition x) => x.IsDirty);
        if (num <= 0)
        {
            if (_actionBarInitialized && ActionBar.Visibility != Visibility.Collapsed)
            {
                ActionBar.Visibility = Visibility.Collapsed;
                UpdateRowlessShellGeometry();
            }
            return;
        }

        EnsureActionBarInitialized();
        // Most toggle edits leave the action bar visible. Recalculating the
        // entire WinUI shell geometry on every row change causes unnecessary
        // layout work; only a true visibility transition needs that update.
        if (ActionBar.Visibility != Visibility.Visible)
        {
            ActionBar.Visibility = Visibility.Visible;
            UpdateRowlessShellGeometry();
        }
        string countText = num == 1
            ? "1 nevyřízená změna" : $"{num} nevyřízených změn";
        if (!string.Equals(DirtyText.Text, countText, StringComparison.Ordinal))
            DirtyText.Text = countText;
    }

    // Guard the confirmation AND the actual Windows apply as one operation.
    // The progress-dialog gate alone begins too late: two quick clicks
    // could otherwise open duplicate confirmations for the same batch.
    private int _tweakApplyInProgress;

    private void Apply_Click(object sender, RoutedEventArgs e) =>
        SafeTaskService.Forget(ApplyClickCoreAsync(), "tweaks", "Apply selected tweaks");

    private async Task ApplyClickCoreAsync()
    {
        if (Interlocked.Exchange(ref _tweakApplyInProgress, 1) != 0)
            return;
        try
        {
        List<TweakDefinition> selectedCatalog = _tweaks;
        int confirmationGeneration = _navigationGeneration;
        var dirty = selectedCatalog.Where(x => x.IsDirty).ToList();
        if (dirty.Count == 0) return;
        var originalSelection = dirty
            .Select(x => (Tweak: x, x.Pending, x.Current)).ToArray();

        // A confirmation may be open while a background Windows refresh or
        // navigation changes the models. Never apply a DIFFERENT batch from
        // the one the user reviewed.
        bool SelectionStillCurrent() =>
            confirmationGeneration == _navigationGeneration &&
            ReferenceEquals(_tweaks, selectedCatalog) &&
            selectedCatalog.Count(x => x.IsDirty) == originalSelection.Length &&
            originalSelection.All(x =>
                x.Tweak.IsLoaded && !x.Tweak.IsBusy && x.Tweak.IsDirty &&
                x.Tweak.Pending == x.Pending && x.Tweak.Current == x.Current);

        if (!await ShowTweakApplyConfirmationAsync(dirty)) return;
        if (!SelectionStillCurrent())
        {
            ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed,
                "Výběr změn se mezitím změnil. Zkontroluj jej a potvrď znovu.");
            return;
        }
        if (dirty.Any(x => x.RestartRequired || x.Risk == TweakRisk.Advanced))
        {
            await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - tweak batch");
            if (!SelectionStillCurrent())
            {
                ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed,
                    "Stav změn se mezitím změnil. Potvrď aktuální výběr znovu.");
                return;
            }
        }
        var progressResult = await RunCancellableWithProgressAsync(
            "Aplikuji tweaky",
            $"Provádím a ověřuji {dirty.Count} vybraných změn…",
            progressToken => _engine.ApplyAsync(dirty, progressToken),
            result => result.Count == 0,
            result => result.Count == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("tweaks", $"Global apply cancelled by user. requested={dirty.Count}");
            bool stateRechecked = await ReconcileCancelledApplyAsync(dirty, "global");
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            if (!stateRechecked && confirmationGeneration == _navigationGeneration &&
                ReferenceEquals(_tweaks, selectedCatalog))
                ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed,
                    "Dávka byla přerušena, ale skutečný stav některých položek se nepodařilo ověřit.");
            return;
        }
        IReadOnlyList<string> readOnlyList = progressResult.Result;
        Exception? finalReadBackError = null;
        try
        {
            // Read the real Windows state after Apply, without discarding a
            // NEW Pending choice staged while the native batch was in flight.
            // DetectAsync reconciles an unchanged Pending with actual state;
            // a newer dirty choice stays staged for the user's next Apply.
            var immediateReadBack = dirty.Where(x => !x.RestartRequired).ToArray();
            if (immediateReadBack.Length > 0)
            {
                await _engine.DetectAsync(immediateReadBack);
                if (immediateReadBack.Any(x => !x.IsLoaded))
                    finalReadBackError = new InvalidOperationException(
                        "U některých změn nelze načíst skutečný stav Windows.");
                else
                {
                    // Re-detection can succeed but still find that Windows
                    // reverted a value after the engine's own verification.
                    // Compare with the USER-CONFIRMED expected values, not
                    // Pending (which a detect pass intentionally refreshes).
                    var mismatches = originalSelection
                        .Where(x => !x.Tweak.RestartRequired &&
                                    x.Tweak.Current != x.Pending)
                        .Select(x => x.Tweak.Name).Take(3).ToArray();
                    if (mismatches.Length != 0)
                        finalReadBackError = new InvalidOperationException(
                            "Windows nepotvrdil požadované hodnoty: " +
                            string.Join(", ", mismatches));
                }
            }
        }
        catch (Exception ex)
        {
            finalReadBackError = ex;
            LogService.Error("tweaks", "Post-apply final detect failed", ex);
        }

        if (dirty.Any(x => x.Section == "storage")) InvalidateStoragePageCache();
        if (dirty.Any(x => x.Section == "memory")) InvalidateMemoryRecommendedPanel();
        if (dirty.Any(x => x.Section == "defender")) InvalidateDefenderPageCache();
        if (dirty.Any(x => x.Section == "components")) InvalidateComponentsPageCache();
        if (dirty.Any(x => x.Section is "memory" or "system")) MemoryStateService.Invalidate();
        RefreshCards(force: true);
        QueueDirtyStateRefresh();
        if (readOnlyList.Count > 0)
        {
            ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed, "Některé změny Windows po aplikaci nepotvrdil.");
            await ShowTweakApplyFailureAsync(readOnlyList);
            return;
        }
        if (finalReadBackError != null)
        {
            // A backend returning no errors is NOT proof of success when the
            // independent native readback could not confirm actual state.
            ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed,
                "Změny mohly proběhnout, ale jejich konečný stav nebyl ověřen.");
            await ShowMessageAsync("Nelze potvrdit stav Windows",
                "Kontrola po použití změn se nepodařila. " +
                "Neopakuj aplikaci naslepo; nejprve znovu načti skutečný stav. " +
                finalReadBackError.GetBaseException().Message);
            return;
        }
        bool restartRequired = dirty.Any((TweakDefinition x) => x.RestartRequired);
        ShowActionFeedback("Tweaky", restartRequired ? ActionFeedbackKind.RestartRequired : ActionFeedbackKind.Verified, $"{dirty.Count} změn bylo ověřeno.");
        await ShowMessageAsync("Hotovo", restartRequired ? "Změny byly aplikovány a ověřeny. Některé vyžadují restart Windows." : "Změny byly aplikovány a ověřeny.");
        }
        finally
        {
            Interlocked.Exchange(ref _tweakApplyInProgress, 0);
        }
    }

    private async Task<bool> ReconcileCancelledApplyAsync(IEnumerable<TweakDefinition> requested, string area)
    {
        var immediateReadBack = requested.Where(x => !x.RestartRequired).Distinct().ToArray();
        if (immediateReadBack.Length == 0) return true;

        using var reconcileCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            // A cancelled batch needs factual Windows readback, not an
            // implicit "Discard". Preserve any user-selected target still
            // pending after the engine's best-effort rollback.
            await _engine.DetectAsync(immediateReadBack, reconcileCts.Token);
            bool verified = immediateReadBack.All(x => x.IsLoaded);
            if (verified)
                LogService.Info("tweaks",
                    $"CANCEL_RECONCILE_OK area={area} count={immediateReadBack.Length}");
            else
                LogService.Info("tweaks",
                    $"CANCEL_RECONCILE_INCOMPLETE area={area} count={immediateReadBack.Length}");
            return verified;
        }
        catch (OperationCanceledException)
        {
            LogService.Info("tweaks", $"CANCEL_RECONCILE_TIMEOUT area={area} count={immediateReadBack.Length}");
            return false;
        }
        catch (Exception ex)
        {
            LogService.Error("tweaks", $"Cancelled apply reconcile failed area={area}", ex);
            return false;
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        // Do not reset Pending while a confirmed batch is being applied or
        // post-apply native readback is reconciling those same definitions.
        if (Volatile.Read(ref _tweakApplyInProgress) != 0) return;
        foreach (TweakDefinition tweak in _tweaks)
        {
            tweak.Pending = tweak.Current;
        }
        RefreshCards(force: true);
        QueueDirtyStateRefresh();
    }
}
