using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private volatile bool _usbHydrationCompleted;
    private int _usbHydratedGhostCount = -1;

    private Task BuildUsbToolsAsync(CancellationToken token = default)
    {
        int generation = _navigationGeneration;
        token.ThrowIfCancellationRequested();
        var shellPerf = System.Diagnostics.Stopwatch.StartNew();
        _usbHydrationCompleted = false;
        _usbHydratedGhostCount = -1;

        var shellHost = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(shellHost);

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "usb" || !ContentStack.Children.Contains(shellHost) || shellHost.Children.Count > 0)
                return;

            var deferred = System.Diagnostics.Stopwatch.StartNew();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(PrimaryQuickButton("Vypnout úsporné režimy", DisableUsbPowerSavingEverywhereAsync));
            actions.Children.Add(QuickButton("Obnovit", RefreshUsbSectionAsync));

            shellHost.Children.Add(BuildModernSectionHero(
                "usb",
                "USB / HID",
                "Správa napájení a bezpečné čištění odpojených zařízení.",
                "USB CLEANER",
                "Selective Suspend • úsporné režimy • odpojená zařízení",
                actions));

            var loading = new StackPanel { Spacing = 8 };
            loading.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left });
            loading.Children.Add(new TextBlock { Text = "Načítám stav napájení a odpojená zařízení…", Foreground = TextSecondaryBrush() });
            var loadingSurface = BuildSectionSurface("usb", "USB / HID stav", "Úspora napájení • čištění odpojených zařízení", loading);
            shellHost.Children.Add(loadingSurface);

            LogService.Info("performance", $"USB_SHELL_DEFERRED ms={deferred.ElapsedMilliseconds}");

            SafeTaskService.Forget(
                HydrateUsbToolsAsync(shellHost, loadingSurface, loading, token, generation),
                "usb",
                "Hydrate USB/HID dashboard");
        });

        LogService.Info("performance", $"USB_SHELL_PHASE totalMs={shellPerf.ElapsedMilliseconds} deferredKickoff=true");
        return Task.CompletedTask;
    }

    private async Task HydrateUsbToolsAsync(StackPanel shellHost, Border loadingSurface, StackPanel loading, CancellationToken token, int generation)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            static async Task<(T Value, long Ms)> TimedAsync<T>(Task<T> task)
            {
                var phase = System.Diagnostics.Stopwatch.StartNew();
                T value = await task.ConfigureAwait(false);
                return (value, phase.ElapsedMilliseconds);
            }

            var ghostTask = TimedAsync(GhostUsbService.GetAsync());
            var powerTask = TimedAsync(UsbHidInventoryService.GetPowerSnapshotAsync(token));
            await Task.WhenAll(ghostTask, powerTask).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            var ghostResult = await ghostTask.ConfigureAwait(false);
            var powerResult = await powerTask.ConfigureAwait(false);
            IReadOnlyList<string> ghosts = ghostResult.Value;
            UsbHidPowerSnapshot power = powerResult.Value;
            long dataMs = sw.ElapsedMilliseconds;
            LogService.Info("performance",
                $"USB_HYDRATE_DATA_PHASE ghostMs={ghostResult.Ms} powerMs={powerResult.Ms} totalMs={dataMs}");

            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "usb") return;
                if (!ContentStack.Children.Contains(shellHost) || !shellHost.Children.Contains(loadingSurface)) return;

                var uiSw = System.Diagnostics.Stopwatch.StartNew();
                shellHost.Children.Remove(loadingSurface);

                shellHost.Children.Add(BuildSectionSurface(
                    "usb",
                    "Úspora napájení",
                    "Selective Suspend • úsporné režimy zařízení • ověření stavu",
                    BuildUsbPowerPanel(power)));

                shellHost.Children.Add(BuildSectionSurface(
                    "usb",
                    "Cleaner odpojených zařízení",
                    "Pouze odpojená USB/HID zařízení • čerstvé ověření před každým odebráním",
                    BuildGhostUsbPanel(ghosts)));

                _usbHydratedGhostCount = ghosts.Count;
                _usbHydrationCompleted = true;

                LogService.Info("performance",
                    $"USB_HYDRATE_UI_PHASE totalUiMs={uiSw.ElapsedMilliseconds}");
                LogService.Info("usb",
                    $"USB_PAGE_RENDERED ghosts={ghosts.Count} powerRegistry={power.RegistryDeviceParameters} dataMs={dataMs}");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("usb", "USB/HID page load failed", ex);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "usb") return;
                loading.Children.Clear();
                loading.Children.Add(new TextBlock
                {
                    Text = "USB/HID inventory se nepodařilo načíst: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
                loading.Children.Add(QuickButton("Zkusit znovu", RefreshUsbSectionAsync));
                ShowActionFeedback("USB / HID", ActionFeedbackKind.Failed, ex.Message);
            });
        }
    }

    internal async Task RunUsbDeferredRenderSmokeAsync()
    {
        UsbHidInventoryService.Invalidate();
        GhostUsbService.InvalidateCache();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("usb");
        sw.Stop();
        long renderMs = sw.ElapsedMilliseconds;

        if (renderMs > 100)
            throw new InvalidOperationException($"USB deferred shell render too slow: {renderMs} ms.");

        DateTime deadline = DateTime.UtcNow.AddSeconds(4);
        while (!_usbHydrationCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        if (!_usbHydrationCompleted)
            throw new InvalidOperationException("USB deferred smoke did not finish hydration.");
        if (_usbHydratedGhostCount < 0)
            throw new InvalidOperationException($"USB deferred smoke returned invalid ghost count: {_usbHydratedGhostCount}.");

        LogService.Info("performance", $"USB_DEFERRED_RENDER_SMOKE_OK renderMs={renderMs} ghosts={_usbHydratedGhostCount} powerAndCleanerOnly=true");
    }
    private StackPanel BuildUsbPowerPanel(UsbHidPowerSnapshot power)
    {
        var stack = new StackPanel { Spacing = 10 };
        string selective = power.SelectiveSuspendDisabled switch
        {
            true => "Selective Suspend: vypnutý",
            false => "Selective Suspend: zapnutý",
            _ => "Selective Suspend: stav nelze přečíst"
        };
        string registry = power.RegistryDeviceParameters == 0
            ? "Úsporné režimy zařízení: žádné dostupné hodnoty"
            : $"Úsporné režimy zařízení: {power.RegistryPowerSavingDisabled}/{power.RegistryDeviceParameters} bez úsporného režimu";

        stack.Children.Add(new TextBlock
        {
            Text = selective,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = power.SelectiveSuspendDisabled == true ? SuccessBrush() : WarningBrush()
        });
        stack.Children.Add(new TextBlock { Text = registry, FontSize = 11, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(PrimaryQuickButton("Vypnout všechny úsporné režimy", DisableUsbPowerSavingEverywhereAsync));
        actions.Children.Add(QuickButton("Zapnout Selective Suspend v plánu", RestoreUsbSelectiveSuspendAsync));
        stack.Children.Add(actions);
        return stack;
    }

    private StackPanel BuildGhostUsbPanel(IReadOnlyList<string> ids)
    {
        var stack = new StackPanel { Spacing = 7 };

        if (ids.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Žádná odpojená USB/HID zařízení nebyla nalezena.",
                Foreground = SuccessBrush()
            });
            return stack;
        }

        foreach (string id in ids)
        {
            stack.Children.Add(new Border
            {
                Background = SectionCardBackgroundBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(_darkTheme ? 1.1 : 1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 7, 10, 7),
                Child = new TextBlock
                {
                    Text = id,
                    FontSize = 11.5,
                    Foreground = TextPrimaryBrush(),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        stack.Children.Add(PrimaryQuickButton("Clean", async () =>
        {
            if (!await ShowStyledConfirmAsync(
                    "Vyčistit odpojená USB/HID zařízení?",
                    $"Bude bezpečně znovu ověřeno a odebráno až {ids.Count} odpojených položek. Připojené zařízení se neodebere.",
                    "Clean", "Zrušit", "USB / HID"))
                return;

            int removed = 0;
            int skipped = 0;
            foreach (string id in ids)
            {
                try
                {
                    await GhostUsbService.RemoveAsync(id);
                    removed++;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("už není odpojené", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                }
            }

            ShowActionFeedback("USB Cleaner", ActionFeedbackKind.Verified,
                $"Odebráno {removed} odpojených položek" + (skipped > 0 ? $" • {skipped} mezitím připojeno a přeskočeno" : string.Empty) + ".");
            await RefreshUsbSectionAsync();
        }));

        return stack;
    }
    private async Task RefreshUsbSectionAsync()
    {
        UsbHidInventoryService.Invalidate();
        GhostUsbService.InvalidateCache();
        DriverInventoryService.Invalidate();
        await ShowSectionAsync("usb");
    }

    private async Task RestoreUsbSelectiveSuspendAsync()
    {
        var progressResult = await RunCancellableWithProgressAsync(
            "USB / HID — Selective Suspend",
            "Obnovuji Selective Suspend v aktivním plánu napájení…",
            async token =>
            {
                await WindowsActions.SetUsbSelectiveSuspendAsync(false, token);
                return true;
            });
        if (!progressResult.Completed)
        {
            LogService.Info("usb", "Restore USB Selective Suspend cancelled by user.");
            return;
        }

        UsbHidInventoryService.Invalidate();
        UsbHidPowerSnapshot verified = await UsbHidInventoryService.GetPowerSnapshotAsync();
        if (verified.SelectiveSuspendDisabled == false)
            ShowActionFeedback("USB/HID napájení", ActionFeedbackKind.Verified, "Selective Suspend je v aktivním plánu napájení zapnutý a ověření to potvrdilo.");
        else
            ShowActionFeedback("USB/HID napájení", ActionFeedbackKind.Failed, "Windows po zápisu nepotvrdil zapnutý Selective Suspend.");
        await ShowSectionAsync("usb");
    }

    private async Task DisableUsbPowerSavingEverywhereAsync()
    {
        var progressResult = await RunCancellableWithProgressAsync(
            "USB / HID — Max Performance",
            "Vypínám Selective Suspend a dostupné úsporné režimy jednotlivých zařízení…",
            async token =>
            {
                await WindowsActions.SetUsbSelectiveSuspendAsync(true, token);
                return await UsbPowerMutationService.DisableDevicePowerSavingAsync(token);
            });
        if (!progressResult.Completed)
        {
            LogService.Info("usb", "USB Max Performance cancelled by user.");
            return;
        }
        int changed = progressResult.Result;

        UsbHidInventoryService.Invalidate();
        UsbHidPowerSnapshot verified = await UsbHidInventoryService.GetPowerSnapshotAsync();
        bool planVerified = verified.SelectiveSuspendDisabled == true;
        bool deviceFlagsVerified = verified.RegistryPowerSavingEnabled == 0;
        if (planVerified && deviceFlagsVerified)
        {
            ShowActionFeedback("USB/HID napájení", ActionFeedbackKind.Verified,
                $"Selective Suspend vypnutý; {verified.RegistryPowerSavingDisabled}/{verified.RegistryDeviceParameters} dostupných device flagů potvrzeno jako bez úspory.");
            await ShowStyledInfoAsync("USB/HID napájení",
                $"Ověření je v pořádku. Selective Suspend je vypnutý a {verified.RegistryPowerSavingDisabled}/{verified.RegistryDeviceParameters} dostupných USB/HID instancí má vypnuté úsporné režimy. Změna se plně projeví po znovupřipojení zařízení nebo restartu.");
        }
        else
        {
            string detail = $"Selective Suspend: {(planVerified ? "vypnutý" : "neověřeno")} • zařízení: {verified.RegistryPowerSavingDisabled}/{verified.RegistryDeviceParameters} bez úspory";
            ShowActionFeedback("USB/HID napájení", ActionFeedbackKind.Failed, detail);
            await ShowStyledInfoAsync("USB/HID napájení — ověření",
                "Zápis proběhl, ale Windows nepotvrdil všechny změny. " + detail + ". Zkus restart nebo znovupřipojení zařízení a potom akci spusť znovu.");
        }
        await ShowSectionAsync("usb");
    }
    private async Task ShowLauncherStartupDialogAsync()
    {
        var checks = new List<CheckBox>(); var stack = new StackPanel { Spacing = 5 };
        foreach (var name in LauncherCacheService.Catalog.Keys) { var c = new CheckBox { Content = name, IsChecked = true, Tag = name }; checks.Add(c); stack.Children.Add(c); }
        var result = await ShowStyledChoiceAsync("Autostart launcherů", "Vybrané launchery lze vypnout nebo obnovit.", "Vypnout vybrané", "Obnovit vybrané"); if (result == 0) return;
        var selected = checks.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToArray();
        await ShowMessageAsync("Autostart launcherů", await LauncherGamingModeService.SetStartupAsync(selected, result == 2));
    }

    private StackPanel? _networkToolsRow;
    private Border? _networkToolsCard;
    private bool _networkToolsQueued;
    private int _networkToolsQueuedGeneration = -1;

    private void ScheduleNetworkTools(CancellationToken token)
    {
        if (_networkToolsCard != null)
        {
            if (!ContentStack.Children.Contains(_networkToolsCard))
                AttachCachedElement(_networkToolsCard);
            return;
        }
        int generation = _navigationGeneration;
        if (_networkToolsQueued && _networkToolsQueuedGeneration == generation) return;
        _networkToolsQueued = true;
        _networkToolsQueuedGeneration = generation;
        bool accepted = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration ||
                _networkToolsQueuedGeneration != generation) return;
            _networkToolsQueued = false;
            _networkToolsQueuedGeneration = -1;
            if (token.IsCancellationRequested || _section != "network" ||
                _networkToolsCard != null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildNetworkTools();
            LogService.Info("performance", $"NETWORK_TOOLS_DEFERRED ms={sw.ElapsedMilliseconds}");
        });
        if (!accepted && _networkToolsQueuedGeneration == generation)
        {
            _networkToolsQueued = false;
            _networkToolsQueuedGeneration = -1;
        }
    }

    private void BuildNetworkTools()
    {
        if (_networkToolsCard != null)
        {
            AttachCachedElement(_networkToolsCard);
            return;
        }

        var row = _networkToolsRow ?? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(QuickButton("Vyprázdnit DNS cache", async () =>
        {
            await WindowsActions.FlushDnsAsync();
            await ShowMessageAsync("Síť", "DNS cache byla vyprázdněna.");
        }));
        row.Children.Add(QuickButton("Reset Winsock", async () =>
        {
            if (!await ShowStyledConfirmAsync("Resetovat Winsock?", "Opraví poškozené síťové nastavení. Pro dokončení bude potřeba restart Windows.", "Resetovat", "Zrušit", "Internet / Síť")) return;
            await WindowsActions.ResetWinsockAsync();
            await ShowMessageAsync("Síť", "Winsock byl resetován. Restartuj Windows.");
        }));
        row.Children.Add(QuickButton("Síťové adaptéry", () => { ProcessRunner.Open("ncpa.cpl"); return Task.CompletedTask; }));
        _networkToolsRow = row;
        _networkToolsCard = BuildSectionSurface(
            "network",
            "Rychlé opravy",
            "DNS cache • Winsock • systémové adaptéry",
            row);
        ContentStack.Children.Add(_networkToolsCard);
    }

    private StackPanel? _storageToolsRow;
    private Border? _storageMaintenanceCard;
    private int _storageToolsQueuedGeneration;

    private void ScheduleStorageTools(CancellationToken token)
    {
        if (_storageMaintenanceCard != null)
        {
            if (!ContentStack.Children.Contains(_storageMaintenanceCard))
                AttachCachedElement(_storageMaintenanceCard);
            return;
        }
        int generation = _navigationGeneration;
        if (_storageToolsQueuedGeneration == generation) return;
        _storageToolsQueuedGeneration = generation;
        void ReleaseQueueTicket()
        {
            if (_storageToolsQueuedGeneration == generation)
                _storageToolsQueuedGeneration = 0;
        }
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseQueueTicket();
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || _storageMaintenanceCard != null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildStorageTools();
            if (_storagePageCache != null && _storageMaintenanceCard != null &&
                !_storagePageCache.Contains(_storageMaintenanceCard))
                _storagePageCache.Add(_storageMaintenanceCard);
            SchedulePageScrollPolicyRefresh();
            LogService.Info("performance", $"STORAGE_TOOLS_DEFERRED ms={sw.ElapsedMilliseconds}");
        }))
            ReleaseQueueTicket();
    }
    private void BuildStorageTools()
    {
        if (_storageMaintenanceCard != null)
        {
            AttachCachedElement(_storageMaintenanceCard);
            return;
        }
        var row = _storageToolsRow ?? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(QuickButton("Spustit TRIM všech SSD", async () =>
        {
            await WindowsActions.RetrimAllAsync();
            await ShowMessageAsync("Úložiště", "Optimalizace a TRIM disků byly dokončeny.");
        }));
        row.Children.Add(QuickButton("Zdraví disků", async () => await ShowMessageAsync("Zdraví disků", await StorageMaintenanceService.GetHealthAsync())));
        row.Children.Add(QuickButton("Analyzovat a vyčistit cache", async () =>
        {
            var scanProgress = await RunCancellableWithProgressAsync(
                "Cleaner — analýza",
                "Počítám velikost bezpečných cache…",
                token => StorageMaintenanceService.ScanSafeCleanupAsync(token));
            if (!scanProgress.Completed)
            {
                LogService.Info("storage", "Cleaner scan cancelled by user.");
                return;
            }
            var scan = scanProgress.Result;
            if (scan.TotalBytes <= 0 || scan.Categories.All(x => x.Bytes <= 0))
            {
                ShowActionFeedback("Cleaner", ActionFeedbackKind.Info, "Bezpečné cache jsou už čisté.");
                await ShowMessageAsync("Cleaner", "Nebyly nalezeny žádné bezpečné cache k vyčištění.");
                return;
            }

            var chooser = new StackPanel { Spacing = 8 };
            chooser.Children.Add(new TextBlock
            {
                Text = $"Celkem nalezeno: {scan.TotalBytes / 1048576d:0.0} MB",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = TextPrimaryBrush()
            });

            var checks = new List<CheckBox>(scan.Categories.Count);
            foreach (var category in scan.Categories)
            {
                var check = new CheckBox
                {
                    Content = $"{category.Name} — {category.Bytes / 1048576d:0.0} MB",
                    IsChecked = category.Bytes > 0,
                    IsEnabled = category.Bytes > 0,
                    Tag = category.Id
                };
                checks.Add(check);
                chooser.Children.Add(check);
            }

            var selectionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            selectionActions.Children.Add(QuickButton("Vybrat vše", () =>
            {
                foreach (CheckBox check in checks)
                    if (check.IsEnabled) check.IsChecked = true;
                return Task.CompletedTask;
            }));
            selectionActions.Children.Add(QuickButton("Zrušit vše", () =>
            {
                foreach (CheckBox check in checks)
                    if (check.IsEnabled) check.IsChecked = false;
                return Task.CompletedTask;
            }));
            chooser.Children.Add(selectionActions);

            if (!await ShowStyledContentAsync(
                    "Cleaner — vyber kategorie",
                    "Nejdřív se velikost skutečně změří. Mažou se pouze bezpečné dočasné a shader cache.",
                    chooser,
                    "Vyčistit vybrané"))
                return;

            var selectedIds = new List<string>(checks.Count);
            foreach (CheckBox check in checks)
                if (check.IsChecked == true && check.Tag is string id)
                    selectedIds.Add(id);
            string[] selected = selectedIds.ToArray();
            if (selected.Length == 0)
            {
                ShowActionFeedback("Cleaner", ActionFeedbackKind.Info, "Není vybraná žádná kategorie.");
                return;
            }

            var clearProgress = await RunCancellableWithProgressAsync(
                "Cleaner",
                "Mažu vybrané cache a ověřuji skutečně uvolněné místo…",
                token => StorageMaintenanceService.ClearSafeCachesAsync(selected, token));
            if (!clearProgress.Completed)
            {
                LogService.Info("storage", "Cleaner delete cancelled by user.");
                return;
            }
            var result = clearProgress.Result;

            var after = await StorageMaintenanceService.ScanSafeCleanupAsync();
            string detail = $"Uvolněno {result.FreedBytes / 1048576d:0.0} MB • zbývá {after.TotalBytes / 1048576d:0.0} MB";
            if (result.FailedFiles > 0) detail += $" • zamčené/nepřístupné soubory: {result.FailedFiles}";
            ShowActionFeedback(
                "Cleaner",
                result.FailedFiles > 0 ? ActionFeedbackKind.Info : ActionFeedbackKind.Verified,
                detail);
            await ShowMessageAsync("Cleaner", detail);
        }));
        _storageToolsRow = row;
        _storageMaintenanceCard = BuildDashboardExpander(
            "Údržba disků",
            "TRIM • zdraví disků • bezpečné cache",
            row);
        ContentStack.Children.Add(_storageMaintenanceCard);
    }

}
