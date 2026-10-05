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
    private sealed class ContextMenuUiState
    {
        public List<UIElement> Elements { get; } = new();
        public ToggleSwitch Classic { get; set; } = null!;
        public ToggleSwitch Terminal { get; set; } = null!;
        public ToggleSwitch Cmd { get; set; } = null!;
        public ToggleSwitch PowerShell { get; set; } = null!;
        public ToggleSwitch AdminPowerShell { get; set; } = null!;
        public ToggleSwitch AdminTerminal { get; set; } = null!;
        public ToggleSwitch Notepad { get; set; } = null!;
        public ToggleSwitch CopyPath { get; set; } = null!;
        public ToggleSwitch Take { get; set; } = null!;
        public ToggleSwitch Sha256 { get; set; } = null!;
        public ToggleSwitch NewFile { get; set; } = null!;
        public ToggleSwitch VsCode { get; set; } = null!;
        public ToggleSwitch AdvancedStartup { get; set; } = null!;
        public ToggleSwitch RestartExplorer { get; set; } = null!;
        public bool Busy { get; set; }
    }

    private ContextMenuUiState? _contextMenuUiCache;
    private DateTime _contextMenuUiCacheAt;
    private readonly SemaphoreSlim _contextMenuToggleGate = new(1, 1);
    private static readonly TimeSpan ContextMenuStateFreshness = TimeSpan.FromSeconds(5);

    private sealed record ContextMenuStateSnapshot(
        bool Classic,
        bool Terminal,
        bool Cmd,
        bool PowerShell,
        bool AdminPowerShell,
        bool AdminTerminal,
        bool Notepad,
        bool CopyPath,
        bool Take,
        bool Sha256,
        bool NewFile,
        bool VsCode,
        bool AdvancedStartup,
        bool RestartExplorer);

    private static ContextMenuStateSnapshot ReadContextMenuStateSnapshot() => new(
        ContextMenuService.ClassicMenuEnabled(),
        ContextMenuService.TerminalHereEnabled(),
        ContextMenuService.CmdHereEnabled(),
        ContextMenuService.PowerShellHereEnabled(),
        ContextMenuService.AdminPowerShellHereEnabled(),
        ContextMenuService.AdminTerminalHereEnabled(),
        ContextMenuService.NotepadEnabled(),
        ContextMenuService.CopyPathEnabled(),
        ContextMenuService.TakeOwnershipEnabled(),
        ContextMenuService.Sha256Enabled(),
        ContextMenuService.NewEmptyFileEnabled(),
        ContextMenuService.VsCodeEnabled(),
        ContextMenuService.AdvancedStartupEnabled(),
        ContextMenuService.RestartExplorerEnabled());

    private void ApplyContextMenuStateSnapshot(ContextMenuUiState state, ContextMenuStateSnapshot snapshot)
    {
        state.Busy = true;
        try
        {
            state.Classic.IsOn = snapshot.Classic;
            state.Terminal.IsOn = snapshot.Terminal;
            state.Cmd.IsOn = snapshot.Cmd;
            state.PowerShell.IsOn = snapshot.PowerShell;
            state.AdminPowerShell.IsOn = snapshot.AdminPowerShell;
            state.AdminTerminal.IsOn = snapshot.AdminTerminal;
            state.Notepad.IsOn = snapshot.Notepad;
            state.CopyPath.IsOn = snapshot.CopyPath;
            state.Take.IsOn = snapshot.Take;
            state.Sha256.IsOn = snapshot.Sha256;
            state.NewFile.IsOn = snapshot.NewFile;
            state.VsCode.IsOn = snapshot.VsCode;
            state.AdvancedStartup.IsOn = snapshot.AdvancedStartup;
            state.RestartExplorer.IsOn = snapshot.RestartExplorer;
            _contextMenuUiCacheAt = DateTime.UtcNow;
        }
        finally
        {
            state.Busy = false;
        }
    }

    private async Task RefreshContextMenuUiStateAsync(ContextMenuUiState state, bool force = false)
    {
        if (!force && DateTime.UtcNow - _contextMenuUiCacheAt <= ContextMenuStateFreshness)
        {
            state.Busy = false;
            return;
        }

        try
        {
            ContextMenuStateSnapshot snapshot = await Task.Run(ReadContextMenuStateSnapshot).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section == "ui")
                    ApplyContextMenuStateSnapshot(state, snapshot);
                else
                    state.Busy = false;
            });
        }
        catch (Exception ex)
        {
            state.Busy = false;
            LogService.Error("ui", "Context Menu state refresh failed", ex);
        }
    }

    private void ScheduleContextMenuUiStateRefresh(ContextMenuUiState state, bool force = false)
    {
        state.Busy = true;
        SafeTaskService.Forget(
            RefreshContextMenuUiStateAsync(state, force),
            "ui",
            "Context Menu state refresh");
    }

    private bool RefreshContextMenuUiStateIfStale(ContextMenuUiState state, bool force = false)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now - _contextMenuUiCacheAt <= ContextMenuStateFreshness)
            return false;

        state.Busy = true;
        try
        {
            state.Classic.IsOn = ContextMenuService.ClassicMenuEnabled();
            state.Terminal.IsOn = ContextMenuService.TerminalHereEnabled();
            state.Cmd.IsOn = ContextMenuService.CmdHereEnabled();
            state.PowerShell.IsOn = ContextMenuService.PowerShellHereEnabled();
            state.AdminPowerShell.IsOn = ContextMenuService.AdminPowerShellHereEnabled();
            state.AdminTerminal.IsOn = ContextMenuService.AdminTerminalHereEnabled();
            state.Notepad.IsOn = ContextMenuService.NotepadEnabled();
            state.CopyPath.IsOn = ContextMenuService.CopyPathEnabled();
            state.Take.IsOn = ContextMenuService.TakeOwnershipEnabled();
            state.Sha256.IsOn = ContextMenuService.Sha256Enabled();
            state.NewFile.IsOn = ContextMenuService.NewEmptyFileEnabled();
            state.VsCode.IsOn = ContextMenuService.VsCodeEnabled();
            state.AdvancedStartup.IsOn = ContextMenuService.AdvancedStartupEnabled();
            state.RestartExplorer.IsOn = ContextMenuService.RestartExplorerEnabled();
            _contextMenuUiCacheAt = now;
            return true;
        }
        finally { state.Busy = false; }
    }

    private void BuildContextMenuTweaker()
    {
        if (_contextMenuUiCache != null)
        {
            var state = _contextMenuUiCache;
            foreach (var element in state.Elements) AttachCachedElement(element);
            ScheduleContextMenuUiStateRefresh(state);
            return;
        }

        var perf = System.Diagnostics.Stopwatch.StartNew();
        int cacheStart = ContentStack.Children.Count;
        var contextState = new ContextMenuUiState();
        var intro = new StackPanel { Spacing = 5 };
        intro.Children.Add(new TextBlock { Text = "Pravé tlačítko ve Windows 11", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        intro.Children.Add(new TextBlock { Text = "Vlastní položky se přidávají bezpečně jen pro aktuálního uživatele. Systémová rozšíření se pouze zapínají nebo vypínají, nemažou se.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        ContentStack.Children.Add(BuildSectionHero("ui", "Explorer", "Kontextové menu", "Klasické menu, vlastní příkazy a správa rozšíření", null));
        ContentStack.Children.Add(BuildSectionSurface("ui", "Kontextové menu", "Bez mazání systémových souborů", intro));
        long introMs = perf.ElapsedMilliseconds;

        var classic = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        classic.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, classic, ContextMenuService.SetClassicMenu,
                ContextMenuService.ClassicMenuEnabled, restartExplorer: true),
            "ui", "Context menu classic toggle");
        var terminal = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        terminal.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, terminal, ContextMenuService.SetTerminalHere,
                ContextMenuService.TerminalHereEnabled, restartExplorer: true),
            "ui", "Context menu terminal toggle");

        var cmd = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        cmd.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, cmd, ContextMenuService.SetCmdHere,
                ContextMenuService.CmdHereEnabled, restartExplorer: true),
            "ui", "Context menu CMD toggle");

        var powershell = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        powershell.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, powershell, ContextMenuService.SetPowerShellHere,
                ContextMenuService.PowerShellHereEnabled, restartExplorer: true),
            "ui", "Context menu PowerShell toggle");

        var adminPowerShell = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        adminPowerShell.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, adminPowerShell, ContextMenuService.SetAdminPowerShellHere,
                ContextMenuService.AdminPowerShellHereEnabled, restartExplorer: true),
            "ui", "Context menu admin PowerShell toggle");

        var adminTerminal = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        adminTerminal.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, adminTerminal, ContextMenuService.SetAdminTerminalHere,
                ContextMenuService.AdminTerminalHereEnabled, restartExplorer: true),
            "ui", "Context menu admin terminal toggle");

        var notepad = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        notepad.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, notepad, ContextMenuService.SetNotepad,
                ContextMenuService.NotepadEnabled, restartExplorer: true),
            "ui", "Context menu Notepad toggle");

        var copyPath = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        copyPath.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, copyPath, ContextMenuService.SetCopyPath,
                ContextMenuService.CopyPathEnabled, restartExplorer: true),
            "ui", "Context menu copy path toggle");

        var take = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        take.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyTakeOwnershipToggleAsync(contextState, take),
            "ui", "Context menu take ownership toggle");

        var sha256 = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        sha256.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, sha256, ContextMenuService.SetSha256, ContextMenuService.Sha256Enabled, restartExplorer: true),
            "ui", "Context menu SHA256 toggle");
        var newFile = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        newFile.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, newFile, ContextMenuService.SetNewEmptyFile, ContextMenuService.NewEmptyFileEnabled, restartExplorer: true),
            "ui", "Context menu new file toggle");
        var vsCode = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        vsCode.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, vsCode, ContextMenuService.SetVsCode, ContextMenuService.VsCodeEnabled, restartExplorer: true),
            "ui", "Context menu VS Code toggle");
        var advancedStartup = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        advancedStartup.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, advancedStartup, ContextMenuService.SetAdvancedStartup, ContextMenuService.AdvancedStartupEnabled, restartExplorer: true),
            "ui", "Context menu advanced startup toggle");
        var restartExplorer = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        restartExplorer.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, restartExplorer, ContextMenuService.SetRestartExplorer, ContextMenuService.RestartExplorerEnabled, restartExplorer: true),
            "ui", "Context menu restart Explorer toggle");
        contextState.Classic = classic;
        contextState.Terminal = terminal;
        contextState.Cmd = cmd;
        contextState.PowerShell = powershell;
        contextState.AdminPowerShell = adminPowerShell;
        contextState.AdminTerminal = adminTerminal;
        contextState.Notepad = notepad;
        contextState.CopyPath = copyPath;
        contextState.Take = take;
        contextState.Sha256 = sha256;
        contextState.NewFile = newFile;
        contextState.VsCode = vsCode;
        contextState.AdvancedStartup = advancedStartup;
        contextState.RestartExplorer = restartExplorer;
        long toggleMs = perf.ElapsedMilliseconds - introMs;

        var toggles = new StackPanel { Spacing = 8 };
        ContentStack.Children.Add(BuildSectionSurface("ui", "Rychlé položky", "Vlastní příkazy pro soubory a složky", toggles));
        long rowsMs = perf.ElapsedMilliseconds - introMs - toggleMs;

        var maintenance = new StackPanel { Spacing = 8 };
        ContentStack.Children.Add(BuildSectionSurface("ui", "Správa a údržba", "Handlery • synchronizace stavu • Explorer • obnova", maintenance));
        long maintenanceMs = perf.ElapsedMilliseconds - introMs - toggleMs - rowsMs;
        for (int i = cacheStart; i < ContentStack.Children.Count; i++) contextState.Elements.Add(ContentStack.Children[i]);
        _contextMenuUiCache = contextState;
        _contextMenuUiCacheAt = default;
        ScheduleContextMenuUiStateRefresh(contextState, force: true);

        Func<Border>[] contextMenuRowFactories =
        {
            () => ContextMenuToggleRow("Klasické celé kontextové menu", "Vrátí klasické plné menu místo zkráceného Windows 11 menu.", classic),
            () => ContextMenuToggleRow("Otevřít Terminal zde", "Přidá Windows Terminal do pravého tlačítka na pozadí složky.", terminal),
            () => ContextMenuToggleRow("Otevřít CMD zde", "Přidá klasický příkazový řádek otevřený v aktuální složce.", cmd),
            () => ContextMenuToggleRow("Otevřít PowerShell zde", "Spustí PowerShell přímo v aktuální složce.", powershell),
            () => ContextMenuToggleRow("PowerShell jako správce zde", "Spustí zvýšený PowerShell v aktuální složce přes UAC.", adminPowerShell, "ADMIN"),
            () => ContextMenuToggleRow("Terminal jako správce zde", "Spustí Windows Terminal v aktuální složce se zvýšenými právy.", adminTerminal, "ADMIN"),
            () => ContextMenuToggleRow("Otevřít v Poznámkovém bloku", "Přidá rychlé otevření libovolného souboru v Notepadu.", notepad),
            () => ContextMenuToggleRow("Kopírovat úplnou cestu", "Přidá rychlé zkopírování celé cesty k vybranému souboru nebo složce.", copyPath),
            () => ContextMenuToggleRow("SHA256 hash", "Vypočítá SHA-256 vybraného souboru přes vestavěný PowerShell příkaz.", sha256),
            () => ContextMenuToggleRow("Nový prázdný soubor", "Vytvoří v aktuální složce nový prázdný .txt soubor s bezpečným číslováním názvu.", newFile),
            () => ContextMenuToggleRow("Otevřít ve VS Code", "Přidá rychlé otevření aktuální složky ve Visual Studio Code.", vsCode),
            () => ContextMenuToggleRow("Pokročilé spuštění", "Přidá rychlou volbu pro restart do pokročilých možností spuštění Windows.", advancedStartup, "ADMIN"),
            () => ContextMenuToggleRow("Restart Explorer", "Přidá rychlý restart explorer.exe do kontextového menu.", restartExplorer, "ADMIN"),
            () => ContextMenuToggleRow("Převzít vlastnictví souboru", "Pokročilá změna vlastníka a oprávnění vybraného souboru.", take, "POKROČILÉ")
        };
        long maxDeferredRowMs = 0;

        void QueueContextMenuRow(int index)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (index < contextMenuRowFactories.Length)
                {
                    if (toggles.Children.Count == index)
                    {
                        var rowSw = System.Diagnostics.Stopwatch.StartNew();
                        toggles.Children.Add(contextMenuRowFactories[index]());
                        maxDeferredRowMs = Math.Max(maxDeferredRowMs, rowSw.ElapsedMilliseconds);
                        if (rowSw.ElapsedMilliseconds >= 8)
                            LogService.Info("performance", $"CONTEXT_MENU_ROW_DEFERRED index={index} ms={rowSw.ElapsedMilliseconds}");
                    }
                    QueueContextMenuRow(index + 1);
                    return;
                }

                if (maintenance.Children.Count == 0)
                {
                    var maintenanceSw = System.Diagnostics.Stopwatch.StartNew();
                    maintenance.Children.Add(ActionCard("Rozšíření kontextového menu", "Zobrazí nalezená rozšíření pro soubory, složky a pozadí složky. Jednotlivé položky lze bezpečně zapnout nebo vypnout.", "Spravovat", ShowContextHandlersAsync));
                    maintenance.Children.Add(ActionCard("Ověřit stav", "Načte skutečný stav všech položek a znovu synchronizuje přepínače.", "Ověřit", async () =>
                    {
                        try
                        {
                            RefreshContextMenuUiStateIfStale(contextState, force: true);
                            var states = ContextMenuService.ReadOurEntryStates();
                            int enabled = states.Count(x => x.Value);
                            await ShowMessageAsync("Kontextové menu", $"Ověřeno: {enabled}/{states.Count} vlastních položek je zapnutých.");
                        }
                        catch (Exception ex) { await ShowMessageAsync("Context Menu", ex.Message); }
                    }));
                    maintenance.Children.Add(ActionCard("Restartovat Explorer", "Restartuje explorer.exe, aby se změny kontextového menu projevily okamžitě.", "Restartovat", async () =>
                    {
                        try { await WindowsActions.RestartExplorerAsync(); RefreshContextMenuUiStateIfStale(contextState, force: true); await ShowMessageAsync("Context Menu", "Explorer byl restartován a stav znovu načten."); }
                        catch (Exception ex) { await ShowMessageAsync("Context Menu", ex.Message); }
                    }));
                    maintenance.Children.Add(ActionCard("Obnovit naše položky", "Odstraní jen položky vytvořené Spankyho Optimizerem a vrátí nové Windows 11 menu.", "Obnovit", async () =>
                    {
                        try { ContextMenuService.RestoreOurEntries(); RefreshContextMenuUiStateIfStale(contextState, force: true); await WindowsActions.RestartExplorerAsync(); await ShowMessageAsync("Context Menu", "Vlastní položky Optimizeru byly odstraněny."); }
                        catch (Exception ex) { await ShowMessageAsync("Context Menu", ex.Message); }
                    }));
                    LogService.Info("performance", $"CONTEXT_MENU_ROWS_DEFERRED rows={toggles.Children.Count} maintenance={maintenance.Children.Count} maxRowMs={maxDeferredRowMs} maintenanceMs={maintenanceSw.ElapsedMilliseconds}");
                }
            });
        }
        QueueContextMenuRow(0);

        LogService.Info("performance", $"CONTEXT_MENU_BUILD_PHASE introMs={introMs} togglesMs={toggleMs} rowsMs={rowsMs} maintenanceMs={maintenanceMs} totalMs={perf.ElapsedMilliseconds}");
    }

    private Border ContextMenuToggleRow(string title, string description, ToggleSwitch toggle, string? badge = null)
    {
        // Custom WinUI theme can make ToggleSwitch hit-testing unreliable.
        // The whole row owns the click; the switch is display-only and IsOn change still raises Toggled.
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
                FontSize = 10,
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
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Child = grid
        };
        row.Tapped += (_, _) =>
        {
            LogService.Info("ui", $"CONTEXT_MENU_ROW_TAP title={title} before={toggle.IsOn}");
            toggle.IsOn = !toggle.IsOn;
        };
        return row;
    }

    private readonly HashSet<ToggleSwitch> _verifiedImmediateToggleBusy = new();

    private async Task ApplyVerifiedImmediateToggleAsync(
        ToggleSwitch toggle, Action<bool> setter, Func<bool> getter, string area, string name)
    {
        if (!_verifiedImmediateToggleBusy.Add(toggle)) return;
        bool requested = toggle.IsOn;
        try
        {
            toggle.IsEnabled = false;
            setter(requested);
            bool actual = getter();
            if (actual != requested)
                throw new InvalidOperationException($"{name}: read-back nesouhlasí. Požadováno={requested}, skutečnost={actual}.");
            if (toggle.IsOn != actual) toggle.IsOn = actual;
            LogService.Info(area, $"VERIFIED_TOGGLE_OK name={name} requested={requested} actual={actual}");
        }
        catch (Exception ex)
        {
            bool actual = getter();
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
        // Busy is only a guard for programmatic IsOn refreshes. User changes are queued
        // instead of being silently dropped while another context-menu toggle restarts Explorer.
        if (state.Busy) return;
        bool requested = toggle.IsOn;
        await _contextMenuToggleGate.WaitAsync();
        try
        {
            setter(requested);
            bool actual = getter();
            LogService.Info("ui", $"CONTEXT_MENU_TOGGLE requested={requested} actual={actual}");

            if (actual != requested)
                throw new InvalidOperationException($"Context Menu read-back nesouhlasí. Požadováno: {requested}, skutečnost: {actual}.");

            state.Busy = true;
            try { if (toggle.IsOn != actual) toggle.IsOn = actual; }
            finally { state.Busy = false; }
            _contextMenuUiCacheAt = DateTime.UtcNow;

            if (restartExplorer)
            {
                await WindowsActions.RestartExplorerAsync();
                bool postRestart = getter();
                if (postRestart != requested)
                    throw new InvalidOperationException($"Context Menu stav se po restartu Exploreru změnil. Požadováno: {requested}, skutečnost: {postRestart}.");
                state.Busy = true;
                try { if (toggle.IsOn != postRestart) toggle.IsOn = postRestart; }
                finally { state.Busy = false; }
                _contextMenuUiCacheAt = DateTime.UtcNow;
                LogService.Info("ui", $"CONTEXT_MENU_POST_RESTART requested={requested} actual={postRestart}");
            }
        }
        catch (Exception ex)
        {
            bool actual = getter();
            state.Busy = true;
            try { if (toggle.IsOn != actual) toggle.IsOn = actual; }
            finally { state.Busy = false; }
            _contextMenuUiCacheAt = DateTime.UtcNow;
            LogService.Error("ui", $"Context Menu toggle failed requested={requested} actual={actual}", ex);
            await ShowMessageAsync("Context Menu", ex.Message);
        }
        finally
        {
            _contextMenuToggleGate.Release();
        }
    }

    private async Task ApplyTakeOwnershipToggleAsync(ContextMenuUiState state, ToggleSwitch take)
    {
        if (state.Busy) return;
        state.Busy = true;
        try
        {
            if (take.IsOn && !await ShowStyledConfirmAsync(
                "Převzít vlastnictví",
                "Přidá do kontextového menu pokročilou změnu vlastníka a oprávnění. Používej ji jen tam, kde je to opravdu potřeba.",
                "Přidat", "Zrušit", "Pokročilé kontextové menu"))
            {
                take.IsOn = false;
                return;
            }

            bool requested = take.IsOn;
            ContextMenuService.SetTakeOwnership(requested);
            bool actual = ContextMenuService.TakeOwnershipEnabled();
            if (actual != requested)
                throw new InvalidOperationException($"Ověření změny vlastnictví nesouhlasí. Požadováno: {requested}, skutečnost: {actual}.");
            take.IsOn = actual;
            _contextMenuUiCacheAt = DateTime.UtcNow;
            LogService.Info("ui", $"CONTEXT_MENU_TAKE_OWNERSHIP requested={requested} actual={actual}");
            await WindowsActions.RestartExplorerAsync();
            bool postRestart = ContextMenuService.TakeOwnershipEnabled();
            if (postRestart != requested)
                throw new InvalidOperationException($"Stav převzetí vlastnictví se po restartu Exploreru změnil. Požadováno: {requested}, skutečnost: {postRestart}.");
            take.IsOn = postRestart;
            _contextMenuUiCacheAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            take.IsOn = ContextMenuService.TakeOwnershipEnabled();
            _contextMenuUiCacheAt = DateTime.UtcNow;
            await ShowMessageAsync("Context Menu", ex.Message);
        }
        finally { state.Busy = false; }
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

        var backendChecks = new (string Name, Func<bool> Get, Action<bool> Set)[]
        {
            ("Classic", ContextMenuService.ClassicMenuEnabled, ContextMenuService.SetClassicMenu),
            ("Terminal", ContextMenuService.TerminalHereEnabled, ContextMenuService.SetTerminalHere),
            ("CMD", ContextMenuService.CmdHereEnabled, ContextMenuService.SetCmdHere),
            ("PowerShell", ContextMenuService.PowerShellHereEnabled, ContextMenuService.SetPowerShellHere),
            ("Admin PowerShell", ContextMenuService.AdminPowerShellHereEnabled, ContextMenuService.SetAdminPowerShellHere),
            ("Admin Terminal", ContextMenuService.AdminTerminalHereEnabled, ContextMenuService.SetAdminTerminalHere),
            ("Notepad", ContextMenuService.NotepadEnabled, ContextMenuService.SetNotepad),
            ("Copy Path", ContextMenuService.CopyPathEnabled, ContextMenuService.SetCopyPath),
            ("Take Ownership", ContextMenuService.TakeOwnershipEnabled, ContextMenuService.SetTakeOwnership)
        };
        foreach (var check in backendChecks)
        {
            bool before = check.Get();
            check.Set(before);
            if (check.Get() != before)
                throw new InvalidOperationException($"Context Menu backend read-back selhal: {check.Name}.");
        }

        // Top-level cards are intentionally compacted: hero + intro + quick toggles + maintenance.
        // Validate functional coverage above instead of depending on the old number of wrapper cards.
        if (state.Elements.Count < 4)
            throw new InvalidOperationException($"Context Menu UI je neúplné: elements={state.Elements.Count}.");

        LogService.Info("ui", $"CONTEXT_MENU_UI_SMOKE_OK toggles={toggles.Length} elements={state.Elements.Count}");
    }

    internal Task RunImmediateToggleBackendSmokeAsync()
    {
        bool motion = UiMotionSettingsService.Enabled;
        UiMotionSettingsService.SetEnabled(motion);
        if (UiMotionSettingsService.Enabled != motion)
            throw new InvalidOperationException("UI motion toggle backend read-back selhal.");

        bool upstream = UpstreamSourceUpdateService.AutoCheckEnabled;
        UpstreamSourceUpdateService.SetAutoCheckEnabled(upstream);
        if (UpstreamSourceUpdateService.AutoCheckEnabled != upstream)
            throw new InvalidOperationException("Upstream auto-check toggle backend read-back selhal.");

        LogService.Info("ui", "IMMEDIATE_TOGGLE_BACKEND_SMOKE_OK motion=true upstream=true");
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
