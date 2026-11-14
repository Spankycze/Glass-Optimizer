using System;
using System.Collections.Generic;
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
    private Border ContextMenuToggleRow(string title, string description, ToggleSwitch toggle, string? badge = null)
    {
        // Custom WinUI theme can make ToggleSwitch hit-testing unreliable.
        // The whole row owns the click; the switch is display-only and IsOn change still raises Toggled.
        NormalizeToggleAppearance(toggle);
        toggle.IsHitTestVisible = false;

        var text = new StackPanel { Spacing = 2 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(badge))
            titleRow.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 10.5,
                Foreground = TextSecondaryBrush(),
                VerticalAlignment = VerticalAlignment.Center
            });
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        var row = new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.2 : 1),
            CornerRadius = new CornerRadius(UseGlassChrome ? 18 : 10),
            Padding = new Thickness(UseGlassChrome ? 14 : 12, UseGlassChrome ? 11 : 9, UseGlassChrome ? 14 : 12, UseGlassChrome ? 11 : 9),
            Child = grid
        };
        row.Tapped += (_, _) =>
        {
            // The row itself remains hit-testable while an async registry
            // change disables its switch. Never queue conflicting toggles.
            if (toggle.IsEnabled) toggle.IsOn = !toggle.IsOn;
        };
        return row;
    }

    private readonly HashSet<ToggleSwitch> _verifiedImmediateToggleBusy = new();

    private async Task ApplyVerifiedImmediateToggleAsync(
        ToggleSwitch toggle, Action<bool> setter, Func<bool> getter, string area, string name,
        bool offloadPersistence = false)
    {
        if (!_verifiedImmediateToggleBusy.Add(toggle)) return;
        bool requested = toggle.IsOn;
        try
        {
            toggle.IsEnabled = false;
            // Only the Guard's file-backed consent uses this branch. An
            // exclusive cross-process lease may take up to ~1.3 s; never
            // hold the WinUI dispatcher while waiting for another process.
            bool actual;
            if (offloadPersistence)
            {
                actual = await Task.Run(() =>
                {
                    setter(requested);
                    return getter();
                });
            }
            else
            {
                setter(requested);
                actual = getter();
            }
            if (actual != requested)
                throw new InvalidOperationException($"{name}: read-back nesouhlasí. Požadováno={requested}, skutečnost={actual}.");
            if (toggle.IsOn != actual) toggle.IsOn = actual;
            LogService.Info(area, $"VERIFIED_TOGGLE_OK name={name} requested={requested} actual={actual}");
        }
        catch (Exception ex)
        {
            bool actual = offloadPersistence ? await Task.Run(getter) : getter();
            if (toggle.IsOn != actual) toggle.IsOn = actual;
            LogService.Error(area, $"VERIFIED_TOGGLE_FAIL name={name} requested={requested} actual={actual}", ex);
            await ShowMessageAsync(name, ex.Message);
        }
        finally
        {
            toggle.IsEnabled = true;
            _verifiedImmediateToggleBusy.Remove(toggle);
        }
    }

    private async Task ApplyContextToggleAsync(ContextMenuUiState state, ToggleSwitch toggle,
        Action<bool> setter, Func<bool> getter, bool restartExplorer)
    {
        // Programmatic refreshes may raise Toggled. Never leave the switch showing a state
        // that was not actually applied; snap it back to the verified backing state.
        if (state.Busy)
        {
            bool actualBusyState = getter();
            if (toggle.IsOn != actualBusyState) toggle.IsOn = actualBusyState;
            return;
        }
        bool requested = toggle.IsOn;
        await _contextMenuToggleGate.WaitAsync();
        state.Busy = true;
        SetContextMenuTogglesEnabled(state, false);
        try
        {
            setter(requested);
            bool actual = getter();
            LogService.Info("ui", $"CONTEXT_MENU_TOGGLE requested={requested} actual={actual}");

            if (actual != requested)
                throw new InvalidOperationException($"Context Menu read-back nesouhlasí. Požadováno: {requested}, skutečnost: {actual}.");

            if (toggle.IsOn != actual) toggle.IsOn = actual;
            _contextMenuUiCacheAt = DateTime.UtcNow;

            if (restartExplorer)
            {
                await WindowsActions.RestartExplorerAsync();
                bool postRestart = getter();
                if (postRestart != requested)
                    throw new InvalidOperationException($"Context Menu stav se po restartu Exploreru změnil. Požadováno: {requested}, skutečnost: {postRestart}.");
                if (toggle.IsOn != postRestart) toggle.IsOn = postRestart;
                _contextMenuUiCacheAt = DateTime.UtcNow;
                LogService.Info("ui", $"CONTEXT_MENU_POST_RESTART requested={requested} actual={postRestart}");
            }
        }
        catch (Exception ex)
        {
            bool actual = getter();
            if (toggle.IsOn != actual) toggle.IsOn = actual;
            _contextMenuUiCacheAt = DateTime.UtcNow;
            LogService.Error("ui", $"Context Menu toggle failed requested={requested} actual={actual}", ex);
            await ShowMessageAsync("Context Menu", ex.Message);
        }
        finally
        {
            SetContextMenuTogglesEnabled(state, true);
            state.Busy = false;
            _contextMenuToggleGate.Release();
        }
    }

    private async Task ApplyTakeOwnershipToggleAsync(ContextMenuUiState state, ToggleSwitch take)
    {
        if (state.Busy) return;
        await _contextMenuToggleGate.WaitAsync();
        state.Busy = true;
        SetContextMenuTogglesEnabled(state, false);
        try
        {
            if (take.IsOn && !await ShowStyledConfirmAsync(
                "Převzít vlastnictví",
                "Přidá do kontextového menu pokročilou změnu vlastníka a oprávnění. Používej ji jen tam, kde je to opravdu potřeba.",
                "Přidat", "Zrušit", "Pokročilé kontextové menu"))
            {
                take.IsOn = ContextMenuService.TakeOwnershipEnabled();
                return;
            }

            bool requested = take.IsOn;
            ContextMenuService.SetTakeOwnership(requested);
            bool actual = ContextMenuService.TakeOwnershipEnabled();
            if (actual != requested)
                throw new InvalidOperationException($"Ověření změny vlastnictví nesouhlasí. Požadováno: {requested}, skutečnost: {actual}.");
            if (take.IsOn != actual) take.IsOn = actual;
            _contextMenuUiCacheAt = DateTime.UtcNow;
            LogService.Info("ui", $"CONTEXT_MENU_TAKE_OWNERSHIP requested={requested} actual={actual}");
            await WindowsActions.RestartExplorerAsync();
            bool postRestart = ContextMenuService.TakeOwnershipEnabled();
            if (postRestart != requested)
                throw new InvalidOperationException($"Stav převzetí vlastnictví se po restartu Exploreru změnil. Požadováno: {requested}, skutečnost: {postRestart}.");
            if (take.IsOn != postRestart) take.IsOn = postRestart;
            _contextMenuUiCacheAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            bool actual = ContextMenuService.TakeOwnershipEnabled();
            if (take.IsOn != actual) take.IsOn = actual;
            _contextMenuUiCacheAt = DateTime.UtcNow;
            await ShowMessageAsync("Context Menu", ex.Message);
        }
        finally
        {
            SetContextMenuTogglesEnabled(state, true);
            state.Busy = false;
            _contextMenuToggleGate.Release();
        }
    }

    internal async Task RunContextMenuLazyRenderSmokeAsync()
    {
        await ShowSectionAsync("ui");
        ContextMenuUiState state = _contextMenuUiCache
            ?? throw new InvalidOperationException("Context Menu UI was not constructed.");
        Expander[] groups = state.Elements.OfType<Border>()
            .Select(card => card.Child).OfType<Expander>().ToArray();
        if (groups.Length != 2 ||
            groups[0].Content is not StackPanel advanced ||
            groups[1].Content is not StackPanel maintenance)
            throw new InvalidOperationException("Expected two lazy Context Menu groups.");

        // This test does not flip toggles, mutate registry entries or restart Explorer.
        if (advanced.Children.Count != 0 || maintenance.Children.Count != 0)
            throw new InvalidOperationException("Collapsed Context Menu groups allocated their hidden rows.");

        groups[0].IsExpanded = true;
        for (int i = 0; advanced.Children.Count < 15 && i < 40; i++)
            await Task.Delay(50);
        if (advanced.Children.Count != 15)
            throw new InvalidOperationException($"Advanced Context Menu group rows={advanced.Children.Count}, expected=15.");

        groups[1].IsExpanded = true;
        for (int i = 0; maintenance.Children.Count < 5 && i < 40; i++)
            await Task.Delay(50);
        if (maintenance.Children.Count != 5)
            throw new InvalidOperationException($"Context Menu maintenance cards={maintenance.Children.Count}, expected=5.");

        groups[0].IsExpanded = false;
        groups[0].IsExpanded = true;
        await Task.Delay(75);
        if (advanced.Children.Count != 15 || maintenance.Children.Count != 5)
            throw new InvalidOperationException("Context Menu group re-expansion created duplicate controls.");

        LogService.Info("ui", "CONTEXT_MENU_LAZY_RENDER_SMOKE_OK initialHidden=0 advanced=15 maintenance=5 reexpand=no-duplicates");
    }

    internal async Task RunContextMenuUiSmokeAsync()
    {
        await NavigateSubSectionFromUiAsync("ui", "Context Menu");
        var state = _contextMenuUiCache
            ?? throw new InvalidOperationException("Context Menu UI cache nebyla vytvořena.");

        ToggleSwitch[] toggles =
        {
            state.Classic, state.Terminal, state.Cmd, state.PowerShell,
            state.AdminPowerShell, state.AdminTerminal, state.Notepad, state.CopyPath, state.Take
        };
        if (toggles.Length != 9 || toggles.Any(x => x == null))
            throw new InvalidOperationException("Context Menu toggle coverage mismatch.");

        foreach (var toggle in toggles)
        {
            if (toggle.Header != null ||
                !string.IsNullOrWhiteSpace(toggle.OnContent?.ToString()) ||
                !string.IsNullOrWhiteSpace(toggle.OffContent?.ToString()))
                throw new InvalidOperationException("Context Menu toggle znovu používá Header/OnContent/OffContent text.");
        }

        var backendChecks = new (string Name, Func<bool> Get)[]
        {
            ("Classic", ContextMenuService.ClassicMenuEnabled),
            ("Terminal", ContextMenuService.TerminalHereEnabled),
            ("CMD", ContextMenuService.CmdHereEnabled),
            ("PowerShell", ContextMenuService.PowerShellHereEnabled),
            ("Admin PowerShell", ContextMenuService.AdminPowerShellHereEnabled),
            ("Admin Terminal", ContextMenuService.AdminTerminalHereEnabled),
            ("Notepad", ContextMenuService.NotepadEnabled),
            ("Copy Path", ContextMenuService.CopyPathEnabled),
            ("Take Ownership", ContextMenuService.TakeOwnershipEnabled)
        };
        // Read-only stability checks on the real menu; the real enable/disable
        // and read-back logic is exercised in a disposable app-owned test key.
        foreach (var check in backendChecks)
        {
            bool first = check.Get();
            bool second = check.Get();
            if (first != second)
                throw new InvalidOperationException($"Context Menu backend read není stabilní: {check.Name}.");
        }
        LogService.Info("ui", ContextMenuService.RunIsolatedPersistenceSmoke());

        // Top-level cards are intentionally compacted: hero + intro + quick toggles + maintenance.
        // Validate functional coverage above instead of depending on the old number of wrapper cards.
        if (state.Elements.Count < 4)
            throw new InvalidOperationException($"Context Menu UI je neúplné: elements={state.Elements.Count}.");

        LogService.Info("ui", $"CONTEXT_MENU_UI_SMOKE_OK toggles={toggles.Length} elements={state.Elements.Count} actualUserSettingsTouched=false");
    }

    internal Task RunImmediateToggleBackendSmokeAsync()
    {
        // Exercise both persistence backends on disposable paths. The old
        // "set the current value" calls rewrote the real user motion.txt and
        // UpstreamSources\auto-check.txt during every release smoke.
        bool motion = UiMotionSettingsService.Enabled;
        LogService.Info("ui", UiMotionSettingsService.RunIsolatedPersistenceSmoke());
        if (UiMotionSettingsService.Enabled != motion)
            throw new InvalidOperationException("UI motion isolated persistence smoke changed the live preference.");

        bool upstream = UpstreamSourceUpdateService.AutoCheckEnabled;
        LogService.Info("ui", UpstreamSourceUpdateService.RunIsolatedPersistenceSmoke());
        if (UpstreamSourceUpdateService.AutoCheckEnabled != upstream)
            throw new InvalidOperationException("Upstream auto-check isolated persistence smoke changed the live preference.");

        LogService.Info("ui", "IMMEDIATE_TOGGLE_BACKEND_SMOKE_OK motion=true upstream=true actualUserSettingsTouched=false");
        return Task.CompletedTask;
    }

    private void InvalidateContextMenuUiCache()
    {
        _contextMenuUiCache = null;
        _contextMenuUiCacheAt = default;
    }

    private async Task ShowContextHandlersAsync()
    {
        var handlers = await ContextMenuService.GetHandlersAsync();
        var checks = new Dictionary<ContextHandlerInfo, CheckBox>();
        var rows = new StackPanel { Spacing = 8 };
        foreach (var h in handlers)
        {
            var cb = new CheckBox { IsChecked = h.Enabled, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
            checks[h] = cb;
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = h.Name, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
            text.Children.Add(new TextBlock { Text = h.Location + "  •  " + h.Clsid, FontSize = 11, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(cb); Grid.SetColumn(text, 1); grid.Children.Add(text);
            rows.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(11), Padding = new Thickness(11), Child = grid });
        }
        var root = new StackPanel { Spacing = 10 };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(QuickButton("Povolit vše", () => { foreach (var c in checks.Values) c.IsChecked = true; return Task.CompletedTask; }));
        bar.Children.Add(QuickButton("Zakázat vše", () => { foreach (var c in checks.Values) c.IsChecked = false; return Task.CompletedTask; }));
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 540, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        if (!await ShowStyledContentAsync("Rozšíření kontextového menu", $"Nalezeno položek: {handlers.Count}. Změny jsou vratné a nic se nemaže.", root, "Použít změny")) return;
        try
        {
            foreach (var pair in checks) ContextMenuService.SetHandlerEnabled(pair.Key.Clsid, pair.Value.IsChecked == true);
            await WindowsActions.RestartExplorerAsync();
            foreach (var pair in checks)
            {
                bool requested = pair.Value.IsChecked == true;
                bool actual = ContextMenuService.IsHandlerEnabled(pair.Key.Clsid);
                if (actual != requested)
                    throw new InvalidOperationException($"Shell handler {pair.Key.Name} se po restartu Exploreru vrátil do jiného stavu.");
            }
            await ShowMessageAsync("Context Menu", "Změny handlerů byly aplikovány a znovu ověřeny po restartu Exploreru.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Context Menu", ex.Message);
        }
    }
}
