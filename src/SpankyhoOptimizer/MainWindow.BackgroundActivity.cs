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
}
