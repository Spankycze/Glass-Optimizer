using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Expander? _backgroundActivityExpander;
    private Action? _backgroundActivityResume;
    private DateTime _backgroundActivityLoadedAt;
    private StackPanel? _backgroundActivityLoadedHost;
    private bool _backgroundActivityRunningOnly;
    private int _backgroundActivityLoadingGeneration = -1;
    private StackPanel? _backgroundActivityLoadingHost;

    // A cached scan belongs to one specific expander content host. A new
    // Startup view may be constructed before the global 20-second TTL ends,
    // and must not inherit the old host's recent-load marker.
    private static bool HasFreshBackgroundActivityScan(
        StackPanel host, StackPanel? loadedHost,
        DateTime loadedAtUtc, DateTime nowUtc) =>
        ReferenceEquals(host, loadedHost) &&
        loadedAtUtc != default && nowUtc >= loadedAtUtc &&
        nowUtc - loadedAtUtc < TimeSpan.FromSeconds(20);

    // Likewise, an in-flight read from a detached panel must not suppress
    // the new panel's first scan when navigation reused its generation.
    private static bool IsBackgroundActivityScanLoading(
        StackPanel host, StackPanel? loadingHost,
        int loadingGeneration, int navigationGeneration) =>
        ReferenceEquals(host, loadingHost) &&
        loadingGeneration == navigationGeneration;

    // Manual refresh and automatic expansion share this same per-host gate.
    // An old completion can only release its own host/generation slot.
    private void FinishBackgroundActivityScan(
        StackPanel host, int generation)
    {
        if (!IsBackgroundActivityScanLoading(
                host, _backgroundActivityLoadingHost,
                _backgroundActivityLoadingGeneration, generation))
            return;
        _backgroundActivityLoadingGeneration = -1;
        _backgroundActivityLoadingHost = null;
    }

    // A queued UI render must complete before its scan gate is released.
    // On navigation/close, however, canceled work must NOT await a dispatcher
    // that might not run again. Token registration unblocks its waiter while
    // the queued callback refuses to apply canceled/stale data.
    private async Task DispatchBackgroundActivityUiAsync(
        Action apply, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration cancellation = token.Register(
            () => completion.TrySetCanceled(token));
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
                    return;
                }
                try
                {
                    apply();
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
            completion.TrySetException(new InvalidOperationException(
                "Background Activity UI dispatcher is no longer available."));
        await completion.Task.ConfigureAwait(false);
    }

    internal async Task RunBackgroundActivityCacheSmokeAsync()
    {
        // Read-only UI path: no preference changes, process termination or
        // Startup registry operations are invoked by this smoke.
        await RunStartupRenderSmokeAsync();
        for (int i = 0; i < 100 && _backgroundActivityExpander == null; i++)
            await Task.Delay(25);
        var expander = _backgroundActivityExpander
            ?? throw new InvalidOperationException("Background Activity expander was not built.");
        // The returned task must complete AFTER the dispatcher action,
        // not after a successful TryEnqueue call. This is a read-only check.
        bool dispatcherApplied = false;
        await DispatchBackgroundActivityUiAsync(
            () => dispatcherApplied = true, _windowLifetimeCts.Token);
        if (!dispatcherApplied)
            throw new InvalidOperationException(
                "Background Activity dispatcher completion preceded UI action.");
        // Cancellation before enqueue and after enqueue must both finish
        // promptly without running the queued callback. No native or
        // persisted user state is affected by this in-memory fixture.
        using (var canceledBefore = new CancellationTokenSource())
        {
            canceledBefore.Cancel();
            bool invoked = false;
            try
            {
                await DispatchBackgroundActivityUiAsync(
                    () => invoked = true, canceledBefore.Token);
                throw new InvalidOperationException(
                    "Background Activity accepted an already canceled UI dispatch.");
            }
            catch (OperationCanceledException) { }
            if (invoked)
                throw new InvalidOperationException(
                    "Background Activity applied a pre-canceled UI callback.");
        }
        using (var canceledQueued = new CancellationTokenSource())
        {
            bool invoked = false;
            Task queued = DispatchBackgroundActivityUiAsync(
                () => invoked = true, canceledQueued.Token);
            canceledQueued.Cancel();
            try
            {
                await queued;
                throw new InvalidOperationException(
                    "Background Activity queued UI dispatch ignored cancellation.");
            }
            catch (OperationCanceledException) { }
            // Flush the canceled callback on the UI dispatcher. It must be
            // inert even if its queue entry executes after cancellation.
            await DispatchBackgroundActivityUiAsync(
                () => { }, _windowLifetimeCts.Token);
            if (invoked)
                throw new InvalidOperationException(
                    "Background Activity applied a canceled queued callback.");
        }
        // Exercise preference queue cancellation without touching the user's
        // persisted settings. A canceled waiter must never obtain a slot or
        // cause an unmatched Release(); valid waiters still acquire normally.
        using (var preferenceProbeGate = new SemaphoreSlim(1, 1))
        using (var preferenceProbeCancel = new CancellationTokenSource())
        {
            if (!await TryEnterBackgroundPreferenceGateAsync(
                    preferenceProbeGate, CancellationToken.None) ||
                preferenceProbeGate.CurrentCount != 0)
                throw new InvalidOperationException(
                    "Background preference gate valid acquisition failed.");
            Task<bool> canceledWaiter = TryEnterBackgroundPreferenceGateAsync(
                preferenceProbeGate, preferenceProbeCancel.Token);
            preferenceProbeCancel.Cancel();
            if (await canceledWaiter || preferenceProbeGate.CurrentCount != 0)
                throw new InvalidOperationException(
                    "Background preference canceled waiter acquired a slot.");
            preferenceProbeGate.Release();
            if (preferenceProbeGate.CurrentCount != 1)
                throw new InvalidOperationException(
                    "Background preference canceled waiter corrupted gate count.");
        }

        var sample = new BackgroundActivityState("spotify", "Spotify",
            Array.Empty<string>(), 1, 1, 0,
            BackgroundAppPreference.Unknown, 0, 0);
        var changedSample = sample with
        {
            Preference = BackgroundAppPreference.Keep
        };
        // Same scan number is insufficient once a preference click rerenders
        // cards. Prove removed buttons cannot mutate the replacement card.
        var probeHost = new StackPanel();
        var oldProbeCard = new StackPanel();
        var replacementProbeCard = new StackPanel();
        probeHost.Children.Add(new Border { Child = oldProbeCard });
        bool oldCardWasAttached =
            IsCurrentBackgroundActivityCard(probeHost, oldProbeCard);
        probeHost.Children.Clear();
        probeHost.Children.Add(new Border { Child = replacementProbeCard });
        if (!oldCardWasAttached ||
            IsCurrentBackgroundActivityCard(probeHost, oldProbeCard) ||
            !IsCurrentBackgroundActivityCard(probeHost, replacementProbeCard))
            throw new InvalidOperationException(
                "Background Activity stale detached card guard failed.");

        var nowUtc = new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
        var otherHost = new StackPanel();
        if (!HasFreshBackgroundActivityScan(probeHost, probeHost, nowUtc, nowUtc) ||
            HasFreshBackgroundActivityScan(otherHost, probeHost, nowUtc, nowUtc) ||
            HasFreshBackgroundActivityScan(probeHost, probeHost, default, nowUtc) ||
            HasFreshBackgroundActivityScan(probeHost, probeHost, nowUtc, nowUtc.AddSeconds(-1)) ||
            HasFreshBackgroundActivityScan(probeHost, probeHost, nowUtc, nowUtc.AddSeconds(20)) ||
            !IsBackgroundActivityScanLoading(probeHost, probeHost, 7, 7) ||
            IsBackgroundActivityScanLoading(otherHost, probeHost, 7, 7) ||
            IsBackgroundActivityScanLoading(probeHost, probeHost, 7, 8))
            throw new InvalidOperationException(
                "Background Activity cross-host scan TTL/in-flight parity failed.");

        // Simulate an old scan completion while a different host holds the
        // loading slot. It must not release the new host or a new generation.
        var savedLoadingHost = _backgroundActivityLoadingHost;
        int savedLoadingGeneration = _backgroundActivityLoadingGeneration;
        try
        {
            _backgroundActivityLoadingHost = probeHost;
            _backgroundActivityLoadingGeneration = 7;
            FinishBackgroundActivityScan(otherHost, 7);
            if (!IsBackgroundActivityScanLoading(
                    probeHost, _backgroundActivityLoadingHost,
                    _backgroundActivityLoadingGeneration, 7))
                throw new InvalidOperationException(
                    "Background Activity foreign-host completion cleared scan.");
            FinishBackgroundActivityScan(probeHost, 8);
            if (!IsBackgroundActivityScanLoading(
                    probeHost, _backgroundActivityLoadingHost,
                    _backgroundActivityLoadingGeneration, 7))
                throw new InvalidOperationException(
                    "Background Activity foreign-generation completion cleared scan.");
            FinishBackgroundActivityScan(probeHost, 7);
            if (_backgroundActivityLoadingHost != null ||
                _backgroundActivityLoadingGeneration != -1)
                throw new InvalidOperationException(
                    "Background Activity matched scan completion did not release gate.");
        }
        finally
        {
            _backgroundActivityLoadingHost = savedLoadingHost;
            _backgroundActivityLoadingGeneration = savedLoadingGeneration;
        }

        if (!CanStartBackgroundActivityRefresh(5, 5, viewActive: true,
                alreadyLoading: false) ||
            CanStartBackgroundActivityRefresh(5, 5, viewActive: true,
                alreadyLoading: true) ||
            CanStartBackgroundActivityRefresh(5, 6, viewActive: true,
                alreadyLoading: false) ||
            CanStartBackgroundActivityRefresh(5, null, viewActive: true,
                alreadyLoading: false) ||
            CanStartBackgroundActivityRefresh(5, 5, viewActive: false,
                alreadyLoading: false) ||
            !CanActOnBackgroundScan(5, 5, viewActive: true) ||
            CanActOnBackgroundScan(5, 6, viewActive: true) ||
            CanActOnBackgroundScan(5, null, viewActive: true) ||
            CanActOnBackgroundScan(-1, -1, viewActive: true) ||
            CanActOnBackgroundScan(5, 5, viewActive: false) ||
            ShouldRedrawBackgroundPreference(5, 5,
                new[] { sample }, new[] { sample }) ||
            !ShouldRedrawBackgroundPreference(5, 5,
                new[] { sample }, new[] { changedSample }) ||
            ShouldRedrawBackgroundPreference(5, 6,
                new[] { sample }, new[] { changedSample }) ||
            ShouldRedrawBackgroundPreference(5, null,
                new[] { sample }, new[] { changedSample }))
            throw new InvalidOperationException(
                "Background preference UI generation/no-op parity failed.");
        if (expander.IsExpanded || _backgroundActivityLoadedAt != default)
            throw new InvalidOperationException("Background Activity scanned while collapsed.");

        expander.IsExpanded = true;
        for (int i = 0; i < 300 && _backgroundActivityLoadedAt == default; i++)
            await Task.Delay(50);
        if (_backgroundActivityLoadedAt == default ||
            expander.Content is not StackPanel { Children.Count: > 2 } panel ||
            !ReferenceEquals(_backgroundActivityLoadedHost, panel))
            throw new InvalidOperationException("Background Activity did not render on its own host after expansion.");

        // Filtering must retain every existing card, including its live
        // preference buttons. No more whole-panel UI rebuild on each tap.
        var originalCards = panel.Children.OfType<Border>().ToArray();
        var filterButton = panel.Children.OfType<Button>()
            .FirstOrDefault(b => string.Equals(b.Content as string, "Jen běžící", StringComparison.Ordinal));
        if (filterButton == null)
            throw new InvalidOperationException("Background Activity running-only filter missing.");
        var refreshButton = panel.Children.OfType<Button>()
            .FirstOrDefault(b => string.Equals(b.Content as string,
                "Obnovit procesy", StringComparison.Ordinal));
        if (refreshButton == null)
            throw new InvalidOperationException("Background Activity refresh button missing.");
        // A manual refresh must be a no-op while the same panel already
        // has a scan in flight, preserving existing card objects and scan ID.
        var loadingHostBeforeClick = _backgroundActivityLoadingHost;
        int loadingGenerationBeforeClick = _backgroundActivityLoadingGeneration;
        object? scanBeforeClick = panel.Tag;
        try
        {
            _backgroundActivityLoadingHost = panel;
            _backgroundActivityLoadingGeneration = _navigationGeneration;
            new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(refreshButton).Invoke();
            if (!Equals(scanBeforeClick, panel.Tag) ||
                panel.Children.OfType<Border>().Where((b, i) =>
                    !ReferenceEquals(b, originalCards[i])).Any() ||
                panel.Children.OfType<Border>().Count() != originalCards.Length)
                throw new InvalidOperationException(
                    "Background Activity duplicate refresh modified live cards.");
        }
        finally
        {
            _backgroundActivityLoadingHost = loadingHostBeforeClick;
            _backgroundActivityLoadingGeneration = loadingGenerationBeforeClick;
        }
        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(filterButton).Invoke();
        if (panel.Children.OfType<Border>().Where((b, i) =>
            !ReferenceEquals(b, originalCards[i])).Any())
            throw new InvalidOperationException("Background Activity filter rebuilt existing cards.");
        foreach (Border card in originalCards)
        {
            if (card.Tag is BackgroundActivityState state &&
                card.Visibility != (state.ProcessCount == 0 ? Visibility.Collapsed : Visibility.Visible))
                throw new InvalidOperationException("Background Activity running-only visibility mismatch.");
        }
        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(filterButton).Invoke();
        if (originalCards.Any(card => card.Visibility != Visibility.Visible))
            throw new InvalidOperationException("Background Activity filter did not restore all cards.");

        await ShowSectionAsync("overview");
        bool cacheShouldReuse = _startupPageCache != null &&
            DateTime.UtcNow - _startupPageCacheAt < StartupPageCacheTtl;
        await ShowSectionAsync("startup");
        for (int i = 0; i < 100 && _backgroundActivityExpander == null; i++)
            await Task.Delay(25);
        if (_backgroundActivityExpander == null ||
            (cacheShouldReuse && !ReferenceEquals(_backgroundActivityExpander, expander)))
            throw new InvalidOperationException("Cached Background Activity expander was replaced or lost.");
        if (_backgroundActivityExpander.IsExpanded)
            _backgroundActivityResume?.Invoke();
        LogService.Info("startup",
            $"BACKGROUND_ACTIVITY_CACHE_SMOKE_OK initialCollapsed=true scanOnExpand=true warmCache={cacheShouldReuse}");
    }

    private void ScheduleBackgroundActivityPanel(
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token,
        StackPanel? attachHost = null)
    {
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "startup" ||
                (attachHost != null && attachHost.Parent == null))
                return;
            BuildBackgroundActivityPanel(startup, generation, token, attachHost);
        });
    }

    private void BuildBackgroundActivityPanel(
        IReadOnlyList<StartupEntry> startup,
        int generation,
        CancellationToken token,
        StackPanel? attachHost = null)
    {
        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(new TextBlock
        {
            Text = "Rozbalením načteš aktuální procesy a jejich spotřebu.",
            Foreground = TextSecondaryBrush()
        });

        var expander = BuildDashboardExpander(
            "Background Activity",
            "Reálné procesy • RAM • CPU time • autostart • tvoje Používám/Nepoužívám",
            host);
        if (attachHost != null)
            attachHost.Children.Add(expander);
        else
            ContentStack.Children.Add(expander);

        // Reading live processes is comparatively expensive. This expander starts
        // collapsed, so defer the scan until the user actually opens it.
        if (expander.Child is Expander activityExpander)
        {
            _backgroundActivityExpander = activityExpander;
            void ResumeOrRefresh()
            {
                if (_section != "startup" || !activityExpander.IsExpanded) return;
                // Startup pages are cached. Old navigation tokens are canceled
                // on leave, so resolve the CURRENT token on every expansion.
                CancellationToken currentToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
                int currentGeneration = _navigationGeneration;
                if (currentToken.IsCancellationRequested ||
                    IsBackgroundActivityScanLoading(host, _backgroundActivityLoadingHost,
                        _backgroundActivityLoadingGeneration, currentGeneration) ||
                    HasFreshBackgroundActivityScan(host, _backgroundActivityLoadedHost,
                        _backgroundActivityLoadedAt, DateTime.UtcNow))
                    return;
                _backgroundActivityLoadingGeneration = currentGeneration;
                _backgroundActivityLoadingHost = host;
                if (host.Children.Count > 0 && host.Children[0] is TextBlock status)
                    status.Text = "Měřím běžící background aplikace…";
                SafeTaskService.Forget(LoadAndFinishAsync(), "startup", "On-demand background activity");
                async Task LoadAndFinishAsync()
                {
                    try
                    {
                        await LoadBackgroundActivityPanelAsync(
                            host, startup, currentGeneration, currentToken,
                            _backgroundActivityRunningOnly);
                    }
                    finally
                    {
                        FinishBackgroundActivityScan(host, currentGeneration);
                    }
                }
            }
            _backgroundActivityResume = ResumeOrRefresh;
            // Expanding runs before IsExpanded is set; defer the check until
            // the routed event has completed.
            activityExpander.Expanding += (_, _) =>
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ResumeOrRefresh);
        }
    }

    private async Task LoadBackgroundActivityPanelAsync(
        StackPanel host,
        IReadOnlyList<StartupEntry> startup,
        int generation,
        CancellationToken token,
        bool runningOnly = false)
    {
        // Per-host generation prevents an older scan overwriting a newer refresh.
        int scanVersion = host.Tag is int priorVersion ? priorVersion + 1 : 1;
        host.Tag = scanVersion;
        var scanTimer = Stopwatch.StartNew();
        try
        {
            var states = await BackgroundActivityService.ReadAsync(startup, token).ConfigureAwait(false);
            scanTimer.Stop();
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "startup") return;

            await DispatchBackgroundActivityUiAsync(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "startup" ||
                    host.Parent == null || host.Tag is not int currentVersion || currentVersion != scanVersion) return;
                RenderBackgroundActivityPanel(host, startup, states, token,
                    scanTimer.ElapsedMilliseconds, generation,
                    _backgroundActivityRunningOnly);
                if (ReferenceEquals(_backgroundActivityExpander?.Content, host))
                {
                    _backgroundActivityLoadedHost = host;
                    _backgroundActivityLoadedAt = DateTime.UtcNow;
                }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("startup", "Background activity load failed", ex);
            if (token.IsCancellationRequested) return;
            try
            {
                await DispatchBackgroundActivityUiAsync(() =>
                {
                    if (token.IsCancellationRequested ||
                        generation != _navigationGeneration ||
                        _section != "startup" ||
                        host.Parent == null || host.Tag is not int currentVersion || currentVersion != scanVersion) return;
                    host.Children.Clear();
                    host.Children.Add(new TextBlock
                    {
                        Text = "Background Activity se nepodařilo načíst: " + ex.Message,
                        Foreground = WarningBrush(),
                        TextWrapping = TextWrapping.Wrap
                    });
                    host.Children.Add(QuickButton("Načíst znovu", () =>
                        RefreshBackgroundActivityFromUiAsync(host, startup, scanVersion)));
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception uiError)
            {
                LogService.Error("startup", "Background activity error UI dispatch failed", uiError);
            }
        }
    }

    // Both "Refresh processes" and "Retry" must use the same guarded
    // asynchronous path. An error-row button from an obsolete scan cannot
    // start a new scan or replace the currently displayed panel.
    private Task RefreshBackgroundActivityFromUiAsync(
        StackPanel host, IReadOnlyList<StartupEntry> startup, int expectedScan)
    {
        CancellationToken token = _loadCts?.Token ?? _windowLifetimeCts.Token;
        int generation = _navigationGeneration;
        if (!CanStartBackgroundActivityRefresh(expectedScan, host.Tag,
                !token.IsCancellationRequested &&
                generation == _navigationGeneration &&
                _section == "startup" && host.Parent != null &&
                ReferenceEquals(_backgroundActivityExpander?.Content, host),
                IsBackgroundActivityScanLoading(host, _backgroundActivityLoadingHost,
                    _backgroundActivityLoadingGeneration, generation)))
            return Task.CompletedTask;

        _backgroundActivityLoadingGeneration = generation;
        _backgroundActivityLoadingHost = host;
        _backgroundActivityLoadedAt = default;
        _backgroundActivityLoadedHost = null;
        host.Children.Clear();
        host.Children.Add(new TextBlock
        {
            Text = "Aktualizuji procesy…",
            Foreground = TextSecondaryBrush()
        });
        return RefreshAndFinishAsync();

        async Task RefreshAndFinishAsync()
        {
            try
            {
                await LoadBackgroundActivityPanelAsync(host, startup, generation,
                    token, _backgroundActivityRunningOnly);
            }
            finally
            {
                FinishBackgroundActivityScan(host, generation);
            }
        }
    }

    // A scan token is captured when the control is created, not on click.
    // The same check is shared by normal refresh and the failure retry.
    private static bool CanStartBackgroundActivityRefresh(
        int expectedScan, object? currentScan,
        bool viewActive, bool alreadyLoading) =>
        !alreadyLoading &&
        CanActOnBackgroundScan(expectedScan, currentScan, viewActive);

    // Only mutate WinUI cards if this is STILL the same scan and a new
    // preference actually changed a row. This prevents slow async saves
    // replacing a fresher process measurement or rebuilding all cards on
    // a click of the already selected preference.
    private static bool ShouldRedrawBackgroundPreference(
        int expectedScan, object? currentScan,
        IReadOnlyList<BackgroundActivityState> before,
        IReadOnlyList<BackgroundActivityState> after)
    {
        if (currentScan is not int actual || expectedScan < 0 ||
            actual != expectedScan)
            return false;
        if (before.Count != after.Count) return true;
        for (int i = 0; i < before.Count; i++)
            if (!ReferenceEquals(before[i], after[i]))
                return true;
        return false;
    }

    // An old click must not commit a preference after a newer background
    // process scan has superseded its card. Re-check AFTER the semaphore,
    // because a scan can finish while the click is waiting for an earlier
    // preference commit. The check itself never reads/writes Windows state.
    private static bool CanActOnBackgroundScan(
        int originalScan, object? currentScan, bool viewActive) =>
        viewActive && originalScan >= 0 &&
        currentScan is int activeScan && activeScan == originalScan;

    // A scan may remain unchanged after a preference redraw. A button from
    // a removed Border must never mutate a different, newly rendered card.
    // Check the current host collection, not a stale XAML Parent link.
    private static bool IsCurrentBackgroundActivityCard(
        StackPanel host, StackPanel card)
    {
        foreach (UIElement child in host.Children)
            if (child is Border border && ReferenceEquals(border.Child, card))
                return true;
        return false;
    }

    // Serialize preference button commits in click order without blocking the
    // WinUI dispatcher on JSON disk writes and read-back verification.
    private readonly SemaphoreSlim _backgroundPreferenceUiGate = new(1, 1);

    // Navigating away while a preference button waits in the queue is
    // expected user activity, not an actionable UI error. Never release
    // a semaphore slot that was NOT acquired due to cancellation.
    private static async Task<bool> TryEnterBackgroundPreferenceGateAsync(
        SemaphoreSlim gate, CancellationToken token)
    {
        try
        {
            await gate.WaitAsync(token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task SaveBackgroundPreferenceFromUiAsync(
        StackPanel host, StackPanel renderedCard,
        IReadOnlyList<StartupEntry> startup,
        IReadOnlyList<BackgroundActivityState> states,
        string key, string label, BackgroundAppPreference preference,
        CancellationToken token, long scanMs, int generation, bool runningOnly,
        int renderedScan)
    {
        // User choices must remain actionable after reattaching the cached
        // Startup page. The original scan token was canceled on navigation.
        token = _loadCts?.Token ?? _windowLifetimeCts.Token;
        generation = _navigationGeneration;
        if (!ReferenceEquals(_backgroundActivityExpander?.Content, host) ||
            token.IsCancellationRequested || _section != "startup") return;
        int originalScan = renderedScan;
        if (!CanActOnBackgroundScan(originalScan, host.Tag,
                viewActive: true) ||
            !IsCurrentBackgroundActivityCard(host, renderedCard))
            return;
        if (!await TryEnterBackgroundPreferenceGateAsync(
                _backgroundPreferenceUiGate, token))
            return;
        try
        {
            if (token.IsCancellationRequested ||
                !CanActOnBackgroundScan(originalScan, host.Tag,
                    generation == _navigationGeneration &&
                    _section == "startup" &&
                    host.Parent != null &&
                    ReferenceEquals(_backgroundActivityExpander?.Content, host)) ||
                !IsCurrentBackgroundActivityCard(host, renderedCard))
                return;
            await BackgroundActivityService.SetPreferenceAsync(key, preference, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Navigation cancelled an in-flight save. The underlying profile
            // service still owns atomic persistence and read-back checks.
            return;
        }
        catch (Exception ex)
        {
            LogService.Error("background-app", "Preference save failed", ex);
            // Never claim success after a failed or contended profile write.
            // The old preference is still active; another click can retry.
            if (!token.IsCancellationRequested && _section == "startup" &&
                generation == _navigationGeneration &&
                ReferenceEquals(_backgroundActivityExpander?.Content, host) &&
                IsCurrentBackgroundActivityCard(host, renderedCard))
                ShowActionFeedback(label, ActionFeedbackKind.Failed,
                    "Předvolba se neuložila. Zkus to znovu. " + ex.Message);
            return;
        }
        finally
        {
            _backgroundPreferenceUiGate.Release();
        }
        if (token.IsCancellationRequested || generation != _navigationGeneration ||
            _section != "startup" || host.Parent == null ||
            !ReferenceEquals(_backgroundActivityExpander?.Content, host) ||
            !CanActOnBackgroundScan(originalScan, host.Tag, viewActive: true) ||
            !IsCurrentBackgroundActivityCard(host, renderedCard))
            return;
        ShowActionFeedback(label, preference switch
        {
            BackgroundAppPreference.Keep => ActionFeedbackKind.Verified,
            BackgroundAppPreference.Unused => ActionFeedbackKind.Verified,
            _ => ActionFeedbackKind.Info
        }, preference switch
        {
            BackgroundAppPreference.Keep => "Preference uložena: Používám.",
            BackgroundAppPreference.Unused => "Preference uložena: Nepoužívám.",
            _ => "Preference byla resetována."
        });
        // The card, host and scan were revalidated BEFORE feedback. A newer
        // refresh must not show stale confirmation or overwrite its snapshot.
        // A second click may have changed another card while this queued.
        // Refresh effective values from the verified persisted profile, but
        // a no-op click should not destroy/recreate the same WinUI controls.
        var effectiveStates = BackgroundActivityService.ApplyCurrentPreferences(states);
        if (ShouldRedrawBackgroundPreference(
                originalScan, host.Tag, states, effectiveStates))
            RenderBackgroundActivityPanel(host, startup,
                effectiveStates, token, scanMs, generation,
                _backgroundActivityRunningOnly);
    }

    private void RenderBackgroundActivityPanel(
        StackPanel host,
        IReadOnlyList<StartupEntry> startup,
        IReadOnlyList<BackgroundActivityState> states,
        CancellationToken token,
        long scanMs,
        int generation,
        bool runningOnly)
    {
        host.Children.Clear();

        int refreshSourceScan = host.Tag is int currentScan ? currentScan : -1;
        host.Children.Add(QuickButton("Obnovit procesy", () =>
            RefreshBackgroundActivityFromUiAsync(host, startup, refreshSourceScan)));

        // Filtering never changes process measurements or preferences.
        // Preserve all WinUI cards/buttons: only toggle visibility in place.
        bool showRunningOnly = runningOnly;
        TextBlock? noRunningNotice = null;
        Button? filterButton = null;
        filterButton = QuickButton(showRunningOnly ? "Zobrazit všechny" : "Jen běžící", () =>
        {
            if (_section != "startup" ||
                !ReferenceEquals(_backgroundActivityExpander?.Content, host))
                return Task.CompletedTask;
            showRunningOnly = !showRunningOnly;
            _backgroundActivityRunningOnly = showRunningOnly;
            // QuickButton restores its content after the click task completes.
            // Queue the new label after that shared action wrapper unwinds.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (filterButton != null && filterButton.Parent != null)
                        filterButton.Content = showRunningOnly ? "Zobrazit všechny" : "Jen běžící";
                });
            foreach (UIElement child in host.Children)
            {
                if (child is Border { Tag: BackgroundActivityState entry } card)
                    card.Visibility = showRunningOnly && entry.ProcessCount == 0
                        ? Visibility.Collapsed : Visibility.Visible;
            }
            if (noRunningNotice != null)
                noRunningNotice.Visibility = showRunningOnly ? Visibility.Visible : Visibility.Collapsed;
            return Task.CompletedTask;
        });
        host.Children.Add(filterButton);

        int liveProcesses = 0;
        double trackedRamMb = 0;
        foreach (var state in states)
        {
            liveProcesses += state.ProcessCount;
            trackedRamMb += state.WorkingSetMb;
        }
        host.Children.Add(new TextBlock
        {
            Text = $"Sken {scanMs} ms • sledováno {states.Count} aplikací • {liveProcesses} procesů • RAM {trackedRamMb:0} MB (součet sledovaných)",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        if (states.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "Žádná sledovaná background aplikace neběží ani nemá aktivní startup položku.",
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        if (liveProcesses == 0)
        {
            noRunningNotice = new TextBlock
            {
                Text = "Momentálně neběží žádná sledovaná aplikace.",
                Foreground = TextSecondaryBrush(),
                Visibility = showRunningOnly ? Visibility.Visible : Visibility.Collapsed
            };
            host.Children.Add(noRunningNotice);
        }

        // Manual process/startup actions also require this exact snapshot
        // after the user confirmation dialog. A newer scan invalidates the
        // old card's process count and startup inventory.
        int renderedScan = host.Tag is int scan ? scan : -1;
        foreach (var state in states)
        {
            string current =
                $"{state.ProcessCount} proc. • {state.WorkingSetMb:0} MB • CPU {state.CpuSeconds:0.0}s • startup {state.EnabledStartupMatches}/{state.TotalStartupMatches}";

            string recommended = state.Preference switch
            {
                BackgroundAppPreference.Keep => "PONECHAT — označeno Používám",
                BackgroundAppPreference.Unused => state.EnabledStartupMatches > 0 || state.ProcessCount > 0
                    ? "VYPNOUT / ukončit podle potřeby"
                    : "OK — nepoužívané a neběží",
                _ => "Vyber Používám / Nepoužívám"
            };

            OptimizationAdvisorKind kind = state.Preference switch
            {
                BackgroundAppPreference.Keep => OptimizationAdvisorKind.Ok,
                BackgroundAppPreference.Unused when state.EnabledStartupMatches > 0 || state.ProcessCount > 0
                    => OptimizationAdvisorKind.Recommended,
                BackgroundAppPreference.Unused => OptimizationAdvisorKind.Ok,
                _ => OptimizationAdvisorKind.Info
            };

            var card = new StackPanel { Spacing = 7 };
            card.Children.Add(BuildOptimizationAdvisorRow(
                state.Label,
                current,
                recommended,
                "Doporučení vychází z toho, zda aplikaci skutečně používáš; Optimizer nevypíná background aplikace plošně.",
                kind));

            var preference = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7
            };
            preference.Children.Add(QuickButton(
                state.Preference == BackgroundAppPreference.Keep ? "Používám ✓" : "Používám",
                () => SaveBackgroundPreferenceFromUiAsync(host, card, startup, states,
                    state.Key, state.Label, BackgroundAppPreference.Keep,
                    token, scanMs, generation, runningOnly, renderedScan)));
            preference.Children.Add(QuickButton(
                state.Preference == BackgroundAppPreference.Unused ? "Nepoužívám ✓" : "Nepoužívám",
                () => SaveBackgroundPreferenceFromUiAsync(host, card, startup, states,
                    state.Key, state.Label, BackgroundAppPreference.Unused,
                    token, scanMs, generation, runningOnly, renderedScan)));
            preference.Children.Add(QuickButton("Reset volby", () =>
                SaveBackgroundPreferenceFromUiAsync(host, card, startup, states,
                    state.Key, state.Label, BackgroundAppPreference.Unknown,
                    token, scanMs, generation, runningOnly, renderedScan)));
            card.Children.Add(preference);

            if (state.Preference == BackgroundAppPreference.Unused)
            {
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7
                };

                if (state.EnabledStartupMatches > 0)
                {
                    actions.Children.Add(PrimaryQuickButton("Vypnout autostart", async () =>
                    {
                        CancellationToken actionToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
                        int actionGeneration = _navigationGeneration;
                        if (actionToken.IsCancellationRequested || _section != "startup" ||
                            !ReferenceEquals(_backgroundActivityExpander?.Content, host) ||
                            !CanActOnBackgroundScan(renderedScan, host.Tag, viewActive: true) ||
                            !IsCurrentBackgroundActivityCard(host, card)) return;
                        if (!await ShowStyledConfirmAsync(
                            "Vypnout automatické spouštění?",
                            $"Zakázat {state.EnabledStartupMatches} položek pro {state.Label} po startu Windows? Běžící procesy se tím neukončí.",
                            "Vypnout autostart", "Zrušit", "Po spuštění")) return;
                        if (actionToken.IsCancellationRequested ||
                            actionGeneration != _navigationGeneration ||
                            _section != "startup" ||
                            !CanActOnBackgroundScan(renderedScan, host.Tag,
                                ReferenceEquals(_backgroundActivityExpander?.Content, host) &&
                                host.Parent != null) ||
                            !IsCurrentBackgroundActivityCard(host, card))
                            return;
                        int changed = await BackgroundActivityService.DisableStartupMatchesAsync(
                            state.Key, startup, actionToken);
                        // Invalidate even after leaving the page: the service may
                        // have changed Startup entries before cancellation.
                        InvalidateStartupPageCache();
                        if (actionToken.IsCancellationRequested || _section != "startup") return;
                        ShowActionFeedback(
                            state.Label,
                            changed > 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                            changed > 0
                                ? $"Vypnuto startup položek: {changed}."
                                : "Nebyla nalezena žádná aktivní startup položka k vypnutí.");
                        await ShowSectionAsync("startup");
                    }));
                }

                if (state.ProcessCount > 0)
                {
                    actions.Children.Add(QuickButton("Ukončit teď", async () =>
                    {
                        CancellationToken actionToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
                        int actionGeneration = _navigationGeneration;
                        if (actionToken.IsCancellationRequested || _section != "startup" ||
                            !ReferenceEquals(_backgroundActivityExpander?.Content, host) ||
                            !CanActOnBackgroundScan(renderedScan, host.Tag, viewActive: true) ||
                            !IsCurrentBackgroundActivityCard(host, card)) return;
                        if (!await ShowStyledConfirmAsync(
                            "Ukončit běžící aplikaci?",
                            $"Ukončit až {state.ProcessCount} procesů aplikace {state.Label}? Neuložená práce může být ztracena.",
                            "Ukončit procesy", "Zrušit", "Background Activity")) return;
                        if (actionToken.IsCancellationRequested ||
                            actionGeneration != _navigationGeneration ||
                            _section != "startup" ||
                            !CanActOnBackgroundScan(renderedScan, host.Tag,
                                ReferenceEquals(_backgroundActivityExpander?.Content, host) &&
                                host.Parent != null) ||
                            !IsCurrentBackgroundActivityCard(host, card))
                            return;
                        int stopped = await BackgroundActivityService.StopProcessesAsync(
                            state.Key, actionToken);
                        if (actionToken.IsCancellationRequested ||
                            actionGeneration != _navigationGeneration || _section != "startup") return;
                        ShowActionFeedback(
                            state.Label,
                            stopped > 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                            stopped > 0
                                ? $"Ukončeno procesů: {stopped}."
                                : "Neběží žádný proces, který by bylo potřeba ukončit.");
                        _backgroundActivityLoadedAt = default;
                        await LoadBackgroundActivityPanelAsync(
                            host, startup, actionGeneration, actionToken,
                            _backgroundActivityRunningOnly);
                    }));
                }

                if (actions.Children.Count > 0)
                    card.Children.Add(actions);
            }

            host.Children.Add(new Border
            {
                Tag = state,
                Visibility = showRunningOnly && state.ProcessCount == 0
                    ? Visibility.Collapsed : Visibility.Visible,
                Background = SectionCardBackgroundBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Child = card
            });
        }
    }
}
