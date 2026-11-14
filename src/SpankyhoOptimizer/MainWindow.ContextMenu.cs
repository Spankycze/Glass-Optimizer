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
        public ToggleSwitch TaskManager { get; set; } = null!;
        public ToggleSwitch DeviceManager { get; set; } = null!;
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
        bool RestartExplorer,
        bool TaskManager,
        bool DeviceManager);

    private static IEnumerable<ToggleSwitch> ContextMenuToggles(ContextMenuUiState state)
    {
        yield return state.Classic;
        yield return state.Terminal;
        yield return state.Cmd;
        yield return state.PowerShell;
        yield return state.AdminPowerShell;
        yield return state.AdminTerminal;
        yield return state.Notepad;
        yield return state.CopyPath;
        yield return state.Take;
        yield return state.Sha256;
        yield return state.NewFile;
        yield return state.VsCode;
        yield return state.AdvancedStartup;
        yield return state.RestartExplorer;
        yield return state.TaskManager;
        yield return state.DeviceManager;
    }

    private static void SetContextMenuTogglesEnabled(ContextMenuUiState state, bool enabled)
    {
        foreach (ToggleSwitch item in ContextMenuToggles(state))
            item.IsEnabled = enabled;
    }

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
        ContextMenuService.RestartExplorerEnabled(),
        ContextMenuService.TaskManagerEnabled(),
        ContextMenuService.DeviceManagerEnabled());

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
            state.TaskManager.IsOn = snapshot.TaskManager;
            state.DeviceManager.IsOn = snapshot.DeviceManager;
            _contextMenuUiCacheAt = DateTime.UtcNow;
        }
        finally
        {
            state.Busy = false;
        }
    }

    private int _contextMenuRefreshVersion;

    private async Task RefreshContextMenuUiStateAsync(ContextMenuUiState state, bool force = false)
    {
        int generation = _navigationGeneration;
        int refreshVersion = System.Threading.Interlocked.Increment(ref _contextMenuRefreshVersion);
        if (!force && DateTime.UtcNow - _contextMenuUiCacheAt <= ContextMenuStateFreshness)
        {
            if (refreshVersion == System.Threading.Volatile.Read(ref _contextMenuRefreshVersion))
                state.Busy = false;
            return;
        }

        try
        {
            ContextMenuStateSnapshot snapshot = await Task.Run(ReadContextMenuStateSnapshot).ConfigureAwait(false);
            bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (refreshVersion != System.Threading.Volatile.Read(ref _contextMenuRefreshVersion)) return;
                if (generation == _navigationGeneration && _section == "ui" && ReferenceEquals(_contextMenuUiCache, state))
                    ApplyContextMenuStateSnapshot(state, snapshot);
                else
                    state.Busy = false;
            });
            if (!queued && refreshVersion == System.Threading.Volatile.Read(ref _contextMenuRefreshVersion))
                state.Busy = false;
        }
        catch (Exception ex)
        {
            if (refreshVersion == System.Threading.Volatile.Read(ref _contextMenuRefreshVersion))
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
            state.TaskManager.IsOn = ContextMenuService.TaskManagerEnabled();
            state.DeviceManager.IsOn = ContextMenuService.DeviceManagerEnabled();
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
        intro.Children.Add(new TextBlock { Text = "Doporučený režim: plné menu hned napoprvé, transparency ON a Power / systémové nástroje v hover submenu bez duplicit.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
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
        var taskManager = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        taskManager.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, taskManager, ContextMenuService.SetTaskManager, ContextMenuService.TaskManagerEnabled, restartExplorer: true),
            "ui", "Context menu Task Manager toggle");
        var deviceManager = new ToggleSwitch { IsOn = false, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        deviceManager.Toggled += (_, _) => SafeTaskService.Forget(
            ApplyContextToggleAsync(contextState, deviceManager, ContextMenuService.SetDeviceManager, ContextMenuService.DeviceManagerEnabled, restartExplorer: true),
            "ui", "Context menu Device Manager toggle");
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
        contextState.TaskManager = taskManager;
        contextState.DeviceManager = deviceManager;
        long toggleMs = perf.ElapsedMilliseconds - introMs;

        // Keep the two everyday settings immediately visible. Other commands
        // remain accessible but do not create a long scrolling wall of rows.
        var essentialToggles = new StackPanel { Spacing = 8 };
        ContentStack.Children.Add(BuildSectionSurface("ui", "Hlavní nastavení", "Klasické menu", essentialToggles));
        var toggles = new StackPanel { Spacing = 8 };
        var advancedCard = BuildDashboardExpander(
            "Další příkazy", "Terminál, PowerShell, soubory a systémové nástroje", toggles);
        ContentStack.Children.Add(advancedCard);
        long rowsMs = perf.ElapsedMilliseconds - introMs - toggleMs;

        var maintenance = new StackPanel { Spacing = 8 };
        var maintenanceCard = BuildDashboardExpander(
            "Správa a údržba", "Ověření • rozšíření • Explorer • obnova", maintenance);
        ContentStack.Children.Add(maintenanceCard);
        long maintenanceMs = perf.ElapsedMilliseconds - introMs - toggleMs - rowsMs;
        for (int i = cacheStart; i < ContentStack.Children.Count; i++) contextState.Elements.Add(ContentStack.Children[i]);
        _contextMenuUiCache = contextState;
        _contextMenuUiCacheAt = default;
        ScheduleContextMenuUiStateRefresh(contextState, force: true);

        Func<Border>[] contextMenuRowFactories =
        {
            () => ContextMenuToggleRow("Klasické celé kontextové menu", "Vrátí klasické plné menu místo zkráceného Windows 11 menu.", classic),
            () => ContextMenuToggleRow("Otevřít Terminal zde", "V hover submenu Terminál a PowerShell.", terminal, "HOVER"),
            () => ContextMenuToggleRow("Otevřít CMD zde", "V hover submenu Terminál a PowerShell.", cmd, "HOVER"),
            () => ContextMenuToggleRow("Otevřít PowerShell 7 zde", "V hover submenu Terminál a PowerShell.", powershell, "HOVER"),
            () => ContextMenuToggleRow("PowerShell 7 jako správce", "V hover submenu Terminál a PowerShell, se zvýšenými právy.", adminPowerShell, "HOVER"),
            () => ContextMenuToggleRow("Terminal jako správce", "V hover submenu Terminál a PowerShell, se zvýšenými právy.", adminTerminal, "HOVER"),
            () => ContextMenuToggleRow("Otevřít v Poznámkovém bloku", "Přidá rychlé otevření libovolného souboru v Notepadu.", notepad),
            () => ContextMenuToggleRow("Kopírovat úplnou cestu", "Přidá rychlé zkopírování celé cesty k vybranému souboru nebo složce.", copyPath),
            () => ContextMenuToggleRow("SHA256 hash", "Vypočítá SHA-256 vybraného souboru přes vestavěný PowerShell příkaz.", sha256),
            () => ContextMenuToggleRow("Nový prázdný soubor", "Vytvoří v aktuální složce nový prázdný .txt soubor s bezpečným číslováním názvu.", newFile),
            () => ContextMenuToggleRow("Otevřít ve VS Code", "Přidá rychlé otevření aktuální složky ve Visual Studio Code.", vsCode),
            () => ContextMenuToggleRow("Pokročilé spuštění", "V hover submenu Systémové nástroje.", advancedStartup, "HOVER"),
            () => ContextMenuToggleRow("Restart Explorer", "V hover submenu Systémové nástroje.", restartExplorer, "HOVER"),
            () => ContextMenuToggleRow("Správce úloh", "V hover submenu Systémové nástroje.", taskManager, "HOVER"),
            () => ContextMenuToggleRow("Správce zařízení", "V hover submenu Systémové nástroje.", deviceManager, "HOVER"),
            () => ContextMenuToggleRow("Převzít vlastnictví souboru", "Pokročilá změna vlastníka a oprávnění vybraného souboru.", take, "POKROČILÉ")
        };
        long maxDeferredRowMs = 0;

        void QueueContextMenuRow(int index, bool advancedRequested = false, bool maintenanceRequested = false, Action? rejected = null)
        {
            // A rejected low-priority enqueue must release the caller's pending
            // group flag, otherwise re-expanding the card can never retry.
            bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // Do not allocate invisible advanced command rows on first
                // navigation. Materialize each group only when first expanded.
                if (index < contextMenuRowFactories.Length)
                {
                    if (index >= 1 && !advancedRequested) return;
                    var target = index < 1 ? essentialToggles : toggles;
                    int targetIndex = index < 1 ? index : index - 1;
                    if (target.Children.Count == targetIndex)
                    {
                        var rowSw = System.Diagnostics.Stopwatch.StartNew();
                        target.Children.Add(contextMenuRowFactories[index]());
                        maxDeferredRowMs = Math.Max(maxDeferredRowMs, rowSw.ElapsedMilliseconds);
                        if (rowSw.ElapsedMilliseconds >= 8)
                            LogService.Info("performance", $"CONTEXT_MENU_ROW_DEFERRED index={index} ms={rowSw.ElapsedMilliseconds}");
                    }
                    QueueContextMenuRow(index + 1, advancedRequested, rejected: rejected);
                    return;
                }

                if (maintenanceRequested && maintenance.Children.Count == 0)
                {
                    var maintenanceSw = System.Diagnostics.Stopwatch.StartNew();
                    maintenance.Children.Add(ActionCard("Vyčistit a optimalizovat", "Zapne klasické plné menu hned na první klik, průhlednost a dvě hover submenu bez duplicit.", "Optimalizovat", async () =>
                    {
                        try
                        {
                            ContextMenuService.OptimizeContextMenuExperience();
                            RefreshContextMenuUiStateIfStale(contextState, force: true);
                            await WindowsActions.RestartExplorerAsync();
                            await ShowMessageAsync("Kontextové menu", "Hotovo: plné menu • transparency ON • hover Power nástroje • hover Systémové nástroje.");
                        }
                        catch (Exception ex) { await ShowMessageAsync("Kontextové menu", ex.Message); }
                    }));
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
                    LogService.Info("performance", $"CONTEXT_MENU_ROWS_DEFERRED rows={essentialToggles.Children.Count + toggles.Children.Count} maintenance={maintenance.Children.Count} maxRowMs={maxDeferredRowMs} maintenanceMs={maintenanceSw.ElapsedMilliseconds}");
                }
            });
            if (!queued) rejected?.Invoke();
        }
        QueueContextMenuRow(0);
        // The two collapsed groups stay lightweight until explicitly opened.
        // WinUI keeps the existing cached elements after first expansion.
        if (advancedCard.Child is Expander advancedExpander)
        {
            bool advancedQueued = false;
            advancedExpander.Expanding += (_, _) =>
            {
                if (advancedQueued) return;
                advancedQueued = true;
                QueueContextMenuRow(1, advancedRequested: true, rejected: () => advancedQueued = false);
            };
        }
        if (maintenanceCard.Child is Expander maintenanceExpander)
        {
            bool maintenanceQueued = false;
            maintenanceExpander.Expanding += (_, _) =>
            {
                if (maintenanceQueued) return;
                maintenanceQueued = true;
                QueueContextMenuRow(contextMenuRowFactories.Length, maintenanceRequested: true, rejected: () => maintenanceQueued = false);
            };
        }

        LogService.Info("performance", $"CONTEXT_MENU_BUILD_PHASE introMs={introMs} togglesMs={toggleMs} rowsMs={rowsMs} maintenanceMs={maintenanceMs} totalMs={perf.ElapsedMilliseconds}");
    }
}
