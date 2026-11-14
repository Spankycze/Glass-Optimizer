using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void ClearDeferredVirtualGroups(string section)
    {
        if (!_deferredVirtualStates.TryGetValue(section, out var state)) return;
        state.Generation++;
        state.BatchQueued = false;
        state.Groups.Clear();
        state.RowTemplate = null;
    }

    private int GetDeferredVirtualGroupCount(string section) =>
        _deferredVirtualStates.TryGetValue(section, out var state) ? state.Groups.Count : 0;

    // Smoke/QA only: navigation returns after the page root, while virtual
    // groups are attached in bounded dispatcher batches. Wait for the real
    // queue to drain instead of relying on a fixed delay, so layout checks
    // never inspect a half-populated System page.
    internal async Task WaitForDeferredVirtualGroupsAsync(string section, int timeoutMs = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (!_deferredVirtualStates.TryGetValue(section, out var state) ||
                (!state.BatchQueued && state.Groups.Count == 0))
                return;
            await Task.Delay(15);
        }
    }

    // Navigation-only read-only performance regression: the GPU hardware
    // and card shell should not be discarded when tweak detection becomes stale.
    internal async Task RunGpuStaleCacheNavigationSmokeAsync()
    {
        await ShowSectionAsync("gpu");
        for (int i = 0; i < 120 && _gpuPageCache == null; i++)
            await Task.Delay(25);
        if (_gpuPageCache == null || _gpuTweakHost == null)
            throw new InvalidOperationException("GPU stale-cache smoke could not warm complete page.");
        var oldHost = _gpuTweakHost;
        DateTime oldDetectedAt = _tweakCacheTime.TryGetValue("gpu|", out var stamp) ? stamp : default;
        long elapsedMs;
        bool reused;
        try
        {
            _tweakCacheTime["gpu|"] = DateTime.UtcNow.AddSeconds(-95);
            await ShowSectionAsync("settings");
            var stopwatch = Stopwatch.StartNew();
            await ShowSectionAsync("gpu");
            stopwatch.Stop();
            elapsedMs = stopwatch.ElapsedMilliseconds;
            reused = ReferenceEquals(oldHost, _gpuTweakHost);
            if (!reused)
                throw new InvalidOperationException("GPU stale tweak refresh destroyed the cached UI and row viewmodels.");
            for (int i = 0; i < 100 && (!_tweakCacheTime.TryGetValue("gpu|", out var updated) ||
                                              DateTime.UtcNow - updated > TimeSpan.FromSeconds(10)); i++)
                await Task.Delay(25);
            if (!_tweakCacheTime.TryGetValue("gpu|", out var refreshedAt) ||
                DateTime.UtcNow - refreshedAt > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("GPU stale-cache refresh did not update tweak detection.");
            LogService.Info("performance",
                $"GPU_STALE_CACHE_NAV_SMOKE_OK wallMs={elapsedMs} reusedHost={reused} cards={CountClassicTweakCards(_gpuTweakHost)} backgroundRefresh=true");
        }
        finally
        {
            if (oldDetectedAt != default && _tweakCacheTime.TryGetValue("gpu|", out var now) &&
                now < oldDetectedAt) _tweakCacheTime["gpu|"] = oldDetectedAt;
        }
    }

    internal async Task RunGpuDeferredTweakSmokeAsync()
    {
        InvalidateGpuPageCache(includeStaticShell: true);
        var sw = Stopwatch.StartNew();
        await ShowSectionAsync("gpu");
        long renderMs = sw.ElapsedMilliseconds;
        if (renderMs > 55)
            throw new InvalidOperationException($"GPU foreground render too slow: {renderMs} ms.");

        int expected = _tweaks.Count;
        for (int i = 0; i < 40 && CountClassicTweakCards(_gpuTweakHost) < expected; i++)
            await Task.Delay(25);
        int actual = CountClassicTweakCards(_gpuTweakHost);
        if (expected != 4 || actual != expected)
            throw new InvalidOperationException($"GPU deferred tweak cards incomplete: expected={expected} actual={actual}.");

        for (int i = 0; i < 120 && _gpuPageCache == null; i++)
            await Task.Delay(25);
        if (_gpuPageCache == null)
            throw new InvalidOperationException("GPU page cache was not captured.");

        var host = _gpuTweakHost;
        await ShowSectionAsync("settings");
        await ShowSectionAsync("gpu");
        if (!ReferenceEquals(_gpuTweakHost, host))
            throw new InvalidOperationException("GPU deferred tweak host was rebuilt instead of reattached.");
        if (CountClassicTweakCards(_gpuTweakHost) != expected)
            throw new InvalidOperationException("GPU cached tweak cards were not preserved after reattach.");

        // Also exercise cancellation, navigation ownership and the retry
        // path of the deferred GPU driver status with in-memory fake Tasks.
        await RunGpuDeferredHydrationOwnershipSmokeAsync();
        LogService.Info("performance", $"GPU_DEFERRED_TWEAK_SMOKE_OK renderMs={renderMs} cards={actual} reattached=true hydrationOwnership=true");
    }

    internal async Task RunToggleStateConsistencySmokeAsync()
    {
        var source = CatalogService.GetTweaks("ui").First(x => x.Id == "ui.shortcutarrow");
        await _engine.DetectAsync(new[] { source });
        bool originalPending = source.Pending;

        var vm = new TweakVirtualRowViewModel(source, _tweakViewMode, _ => { }, null!);
        var card = CreateTweakCard(source);
        bool smokeCardAttached = false;
        try
        {
            source.Pending = !originalPending;
            vm.Refresh(_tweakViewMode, force: true);
            if (vm.Pending != source.Pending)
                throw new InvalidOperationException("Virtual toggle neodpovídá source.Pending po force-refreshi.");

            var grid = card.Child as Grid ?? throw new InvalidOperationException("Classic tweak card nemá Grid.");
            var cardRefs = grid.Tag as ClassicTweakCardRefs
                ?? throw new InvalidOperationException("Classic tweak card nemá přímé odkazy.");
            var toggle = cardRefs.Toggle;
            int writebacksBeforeSync = cardRefs.UserWritebacks;
            RefreshClassicTweakCard(card);
            // Explicitly simulate unrelated queued dirty-state work. This
            // previously caused a false failure when the smoke reset/read the
            // shared flag even though the tested card never wrote back.
            QueueDirtyStateRefresh();
            await Task.Yield();
            // The shared _dirtyRefreshQueued can be set by any concurrent
            // background tweak detection. Only this card's writeback count
            // proves whether the programmatic toggle refresh changed a model.
            if (cardRefs.UserWritebacks != writebacksBeforeSync)
                throw new InvalidOperationException("Programová synchronizace classic toggle nečekaně změnila Pending.");
            if (toggle.IsOn != source.Pending)
                throw new InvalidOperationException("Classic toggle neodpovídá source.Pending po force-refreshi.");

            card.Opacity = 0;
            ContentStack.Children.Add(card);
            smokeCardAttached = true;
            card.UpdateLayout();
            toggle.ApplyTemplate();
            await Task.Yield();

            // Test the actual ToggleSwitch -> Toggled -> tweak Pending path, not just visual states.
            bool start = toggle.IsOn;
            int writesBeforeUserToggle = cardRefs.UserWritebacks;
            toggle.IsOn = !start;
            await Task.Yield();
            if (source.Pending != toggle.IsOn ||
                cardRefs.UserWritebacks != writesBeforeUserToggle + 1)
                throw new InvalidOperationException("ToggleSwitch Toggled handler nepropagoval přesně jednu změnu Pending.");

            toggle.IsOn = start;
            await Task.Yield();
            if (source.Pending != start ||
                cardRefs.UserWritebacks != writesBeforeUserToggle + 2)
                throw new InvalidOperationException("ToggleSwitch návrat stavu nepropagoval přesně jednu změnu Pending.");

            if (!toggle.IsEnabled)
                throw new InvalidOperationException("Editovatelný tweak má neaktivní ToggleSwitch.");
        }
        finally
        {
            if (smokeCardAttached)
                ContentStack.Children.Remove(card);
            source.Pending = originalPending;
            vm.Refresh(_tweakViewMode, force: true);
            RefreshClassicTweakCard(card);
        }

        // Global Apply's final readback must preserve a newly staged choice,
        // yet reconcile Pending when Windows really reverted the confirmed
        // value. Exercise the same native-detection path in memory only.
        bool simulatedActual = true;
        var applyReadback = new TweakDefinition
        {
            Id = "smoke.ui.apply-final-readback",
            Section = "smoke",
            Group = "Apply readback",
            Name = "Apply readback",
            Description = "In-memory post-Apply state check",
            RequiresAdmin = false,
            DetectAsync = _ => Task.FromResult(simulatedActual)
        };
        applyReadback.IsLoaded = true;
        applyReadback.Current = true;
        applyReadback.Pending = false; // newer choice after confirmed Apply(true)
        await _engine.DetectAsync(new[] { applyReadback });
        if (!applyReadback.Current || applyReadback.Pending || !applyReadback.IsDirty)
            throw new InvalidOperationException(
                "Post-Apply readback lost the user's newer Pending selection.");

        // No newer edit: real external drift must update BOTH fields so the
        // confirmed target comparison can report failure instead of success.
        applyReadback.Pending = true;
        simulatedActual = false;
        await _engine.DetectAsync(new[] { applyReadback });
        if (applyReadback.Current || applyReadback.Pending || !applyReadback.IsLoaded)
            throw new InvalidOperationException(
                "Post-Apply drift readback did not reconcile the Windows state.");

        LogService.Info("ui", "TOGGLE_STATE_CONSISTENCY_SMOKE_OK virtual=true classic=true visual=true applyPendingPreserved=true driftReconciled=true");
    }

    private static T? FindNamedVisualDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal)) return typed;
            var nested = FindNamedVisualDescendant<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private void RefreshVirtualTweakRows(bool force = false)
    {
        if (_virtualTweakRows == null) return;
        foreach (var row in _virtualTweakRows) row.Refresh(_tweakViewMode, force);
    }
    private sealed class TweakVirtualRowViewModel : INotifyPropertyChanged
    {
        private readonly TweakDefinition? _source;
        private readonly Action<TweakDefinition>? _changed;
        private TweakViewMode _mode;
        private bool _pending;
        private readonly bool _glassFeatured;
        private readonly bool _invertDisplay;
        private bool _suppress;
        private string _verdict = string.Empty;
        private string _meta = string.Empty;
        private string _policyDetail = string.Empty;
        private bool _canEdit;
        // Native WinUI brush resource lookups should not run for every
        // deferred tweak row during the cold page-root build. Materialize
        // defaults only when a visible template actually binds them.
        private Brush? _badgeBrush;
        private string _statusText = string.Empty;
        private string _statusBrushResource = string.Empty;
        private Brush? _statusBrush;
        private int _lastSourceRevision = -1;
        private int _lastRevalidationRevision = -1;
        private TweakViewMode _lastRefreshMode;
        private bool _refreshInitialized;

        private TweakVirtualRowViewModel(string header)
        {
            HeaderText = header.ToUpperInvariant();
            HeaderVisibility = Visibility.Visible;
            CardVisibility = Visibility.Collapsed;
        }

        public TweakVirtualRowViewModel(TweakDefinition source, TweakViewMode mode, Action<TweakDefinition> changed, ImageSource iconSource, bool deferInitialRefresh = false)
        {
            _source = source; _changed = changed; _mode = mode;
            _glassFeatured = _themeSection == "system" && GlassSystemOrder(source) < 8;
            _invertDisplay = _glassFeatured && (source.Id is "ui.taskanim" or "ui.transparency" or "sys.gamedvr");
            HeaderVisibility = Visibility.Collapsed;
            CardVisibility = Visibility.Visible;
            Name = source.Name; Description = source.Description;
            RestartVisibility = source.RestartRequired ? Visibility.Visible : Visibility.Collapsed;
            IconSource = iconSource;
            if (!deferInitialRefresh) Refresh(mode);
        }

        public static TweakVirtualRowViewModel Header(string text) => new(text);
        public TweakDefinition? Source => _source;
        public string HeaderText { get; } = string.Empty;
        public Visibility HeaderVisibility { get; }
        public Visibility CardVisibility { get; }
        public string Name { get; } = string.Empty;
        public string Description { get; } = string.Empty;
        public string DisplayName => !_glassFeatured ? Name : _source?.Id switch
        {
            "ui.taskanim" => "Animace taskbaru", "ui.transparency" => "Průhlednost",
            "sys.gamemode" => "Herní režim", "sys.gamedvr" => "Záznam na pozadí",
            "privacy.telemetry" => "Omezit telemetrii", "privacy.adid" => "Vypnout reklamní ID",
            "privacy.tailored" => "Vypnout přizpůsobený obsah", "privacy.activity" => "Vypnout historii aktivit", _ => Name
        };
        public string DisplayDescription => !_glassFeatured ? Description : _source?.Id switch
        {
            "ui.taskanim" => "Zapne nebo vypne animace hlavního panelu Windows.",
            "ui.transparency" => "Povolí průhledné efekty v prostředí Windows.",
            "sys.gamemode" => "Optimalizuje systém pro lepší výkon ve hrách.",
            "sys.gamedvr" => "Povolí nebo zakáže záznam herních klipů na pozadí.",
            "privacy.telemetry" => "Omezí odesílání diagnostických dat společnosti Microsoft.",
            "privacy.adid" => "Zakáže používání reklamního identifikátoru pro personalizaci reklam.",
            "privacy.tailored" => "Vypne přizpůsobený obsah založený na diagnostických datech.",
            "privacy.activity" => "Zabrání odesílání historie vaší aktivity do cloudu.", _ => Description
        };
        public bool DisplayPending { get => _invertDisplay ? !Pending : Pending; set => Pending = _invertDisplay ? !value : value; }
        public Visibility RestartVisibility { get; }
        public ImageSource? IconSource { get; }
        public Brush CardBackground => _darkTheme && (_glassFeatured || _source?.Section is "system" or "privacy") ? ThemeColor(0,0,0,0) : SectionCardBackgroundBrush();
        public Brush CardBorder => SectionBorderBrush();
        public Brush TextPrimary => TextPrimaryBrush();
        public Brush TextSecondary => TextSecondaryBrush();
        public Brush Accent => AccentBrush();
        public Brush AccentSoft => AccentSoftBrush();
        public string Verdict => _verdict;
        public string Meta => _meta;
        public string PolicyDetail => _policyDetail;
        public Brush BadgeBrush => _badgeBrush ??= AccentBrush();
        public string StatusText => _statusText;
        public Brush StatusBrush => _statusBrush ??= AccentBrush();
        public bool CanEdit => _canEdit;
        public Visibility DetailVisibility => _darkTheme && (_glassFeatured || _source?.Section is "system" or "privacy") ? Visibility.Collapsed : Visibility.Visible;
        public string RowDetail => $"{StatusText}\n{Meta}\n{PolicyDetail}";
        public string ToggleStateText => DisplayPending ? "ON" : "OFF";

        public bool Pending
        {
            get => _pending;
            set
            {
                if (_pending == value) return;
                _pending = value; OnChanged(); OnChanged(nameof(ToggleStateText)); OnChanged(nameof(DisplayPending));
                if (_suppress || _source == null) return;
                var live = TweakPolicyService.Evaluate(_source, _mode);
                if (!live.CanEdit || !_source.IsLoaded || _source.IsBusy)
                {
                    // An async policy/working-state update can invalidate a
                    // click after WinUI toggles visually. Roll back every
                    // bound representation, including the ON/OFF label.
                    _pending = _source.Pending;
                    OnChanged();
                    OnChanged(nameof(ToggleStateText));
                    OnChanged(nameof(DisplayPending));
                    return;
                }
                _source.Pending = value;
                _changed?.Invoke(_source);
            }
        }
        public void Refresh(TweakViewMode mode, bool force = false)
        {
            if (_source == null) return;
            _mode = mode;
            int sourceRevision = _source.Revision;
            int revalidationRevision = WindowsBuildRevalidationService.Revision;
            if (!force && _refreshInitialized && _lastRefreshMode == mode && _lastSourceRevision == sourceRevision && _lastRevalidationRevision == revalidationRevision)
                return;

            _refreshInitialized = true;
            _lastRefreshMode = mode;
            _lastSourceRevision = sourceRevision;
            _lastRevalidationRevision = revalidationRevision;

            var policy = TweakPolicyService.Evaluate(_source, mode);
            string newVerdict = policy.Verdict;
            string newPolicyDetail = policy.Detail;
            bool newCanEdit = policy.CanEdit && !_source.IsBusy;
            string newMeta = mode == TweakViewMode.Diagnostics || !_source.IsLoaded
                ? policy.Detail
                : TweakVerificationMeta(_source);

            bool verdictChanged = !string.Equals(_verdict, newVerdict, StringComparison.Ordinal);
            bool detailChanged = !string.Equals(_policyDetail, newPolicyDetail, StringComparison.Ordinal);
            bool canEditChanged = _canEdit != newCanEdit;
            bool metaChanged = !string.Equals(_meta, newMeta, StringComparison.Ordinal);

            if (verdictChanged || force)
            {
                _verdict = newVerdict;
                // A forced row refresh may follow a theme change even when
                // the verdict text is identical. Rebind its current brush.
                _badgeBrush = UiBrush(newVerdict == "Recommended"
                    ? "SpankySuccessBrush"
                    : newVerdict is "Advanced" or "Needs attention" or "Requires elevation" or "Requires restart"
                        ? "SpankyWarningBrush" : "SpankyAccentBrush");
            }
            if (detailChanged) _policyDetail = newPolicyDetail;
            if (canEditChanged) _canEdit = newCanEdit;
            if (metaChanged) _meta = newMeta;

            var uiState = TweakUiState(_source);
            string newStatusText = uiState.Text;
            bool statusChanged = !string.Equals(_statusText, newStatusText, StringComparison.Ordinal);
            bool statusBrushChanged = !string.Equals(_statusBrushResource, uiState.Resource,
                StringComparison.Ordinal);
            if (statusChanged) _statusText = newStatusText;
            if (statusBrushChanged || force || _statusBrush == null)
            {
                // A policy can change semantic color WITHOUT changing the
                // Czech status string. Also refresh theme resources on a
                // forced row repaint without performing a brush lookup on
                // every otherwise unchanged row.
                _statusBrushResource = uiState.Resource;
                _statusBrush = UiBrush(uiState.Resource);
            }

            bool pendingChanged = _pending != _source.Pending;
            if (pendingChanged)
            {
                _suppress = true;
                _pending = _source.Pending;
                _suppress = false;
            }

            if (verdictChanged) OnChanged(nameof(Verdict));
            if (verdictChanged || force) OnChanged(nameof(BadgeBrush));
            if (detailChanged) OnChanged(nameof(PolicyDetail));
            if (detailChanged || metaChanged || statusChanged) OnChanged(nameof(RowDetail));
            if (canEditChanged) OnChanged(nameof(CanEdit));
            if (metaChanged) OnChanged(nameof(Meta));
            if (statusChanged) OnChanged(nameof(StatusText));
            if (statusChanged || statusBrushChanged || force) OnChanged(nameof(StatusBrush));
            if (pendingChanged) { OnChanged(nameof(Pending)); OnChanged(nameof(ToggleStateText)); OnChanged(nameof(DisplayPending)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null)
        {
            var handlers = PropertyChanged;
            if (handlers == null) return;

            var args = new PropertyChangedEventArgs(name);
            foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // WinUI virtualization can leave a native binding delegate attached
                    // briefly after its element has been recycled. Drop only that stale
                    // subscriber instead of aborting the whole row refresh.
                    PropertyChanged -= handler;
                }
                catch (ObjectDisposedException)
                {
                    PropertyChanged -= handler;
                }
            }
        }
    }
}
