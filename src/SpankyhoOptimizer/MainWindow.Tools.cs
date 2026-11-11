using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _settingsPageCache;
    private DateTime _settingsPageCacheAt;
    private StackPanel? _settingsDeferredBody;
    private bool _settingsDeferredQueued;
    private bool _settingsDeferredRunning;
    private int _settingsDeferredStage;

    private Task BuildFixerPageAsync(CancellationToken token)
    {
        if (TryAttachFixerPageCache(token)) return Task.CompletedTask;
        var fixedUi = GetFixerStaticElements();
        ContentStack.Children.Add(fixedUi[0]);
        // Builders only construct UI and return completed tasks.
        _ = BuildAppHealthAsync(token);
        for (int i = 1; i < fixedUi.Count; i++) ContentStack.Children.Add(fixedUi[i]);
        CaptureFixerPageCache();
        ScheduleDeferredFixerHero(token);
        ScheduleDeferredFixerStaticActions(token);
        // The builder creates a placeholder; start its loader on first navigation.
        if (_fixerAppHealthHost is { } healthHost)
            SafeTaskService.Forget(LoadAppHealthAsync(healthHost, token), "fixer", "Initial App Health");
        return Task.CompletedTask;
    }

    private void BuildLogsPageAsync(CancellationToken token)
    {
        int generation = _navigationGeneration;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Obnovit", BuildLogsPageRefreshAsync));
        actions.Children.Add(QuickButton("Otevřít složku", () => { Directory.CreateDirectory(LogService.Root); ProcessRunner.Open(LogService.Root); return Task.CompletedTask; }));
        ContentStack.Children.Add(BuildSectionHero("logs", "Diagnostics", "Monitor / Log", "Rychlý přehled posledních událostí Optimizeru bez skrytých filtrů.", actions));

        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(new TextBlock { Text = "Načítám log…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("logs", "Live log", "Zobrazeno maximálně posledních 2500 řádků", host));

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "logs") return;
            var box = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MinHeight = 500,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                FontSize = 11.5,
                Text = "Načítám log…"
            };
            host.Children.Clear();
            host.Children.Add(box);
            SafeTaskService.Forget(LoadLogTailAsync(box, token, generation), "logs", "Load log tail");
        });
    }

    private async Task LoadLogTailAsync(TextBox box, CancellationToken token, int generation)
    {
        var allLines = await LogTailService.ReadTailLinesAsync(LogService.PathName, maxLines: 2500, token: token).ConfigureAwait(false);
        if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "logs") return;
        if (allLines.Length == 0) allLines = new[] { "Log je zatím prázdný." };
        int start = Math.Max(0, allLines.Length - 2500);
        var sb = new StringBuilder(Math.Min(512 * 1024, Math.Max(256, (allLines.Length - start) * 96)));
        for (int i = start; i < allLines.Length; i++)
        {
            if (sb.Length != 0) sb.AppendLine();
            sb.Append(allLines[i]);
        }
        string text = sb.ToString();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!token.IsCancellationRequested && generation == _navigationGeneration && _section == "logs")
                    box.Text = text;
            });
    }

    private Task BuildLogsPageRefreshAsync() => ShowSectionAsync("logs");
    private Task BuildRestorePageAsync(CancellationToken token)
    {
        if (TryAttachRestorePageCache(token)) return Task.CompletedTask;
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var heroHost = new StackPanel();
        ContentStack.Children.Add(heroHost);
        long heroMs = phase.ElapsedMilliseconds;
        int restoreGeneration = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || restoreGeneration != _navigationGeneration ||
                _section != "restore" || heroHost.Children.Count != 0)
                return;

            var heroSw = System.Diagnostics.Stopwatch.StartNew();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(PrimaryQuickButton("Vytvořit bod obnovení", async () =>
            {
                bool ok = await WindowsActions.CreateRestorePointAsync();
                ShowActionFeedback(
                    "Bod obnovení",
                    ok ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
                    ok ? "Bod obnovení byl vytvořen." : "Windows bod obnovení nevytvořil.");
                await ShowMessageAsync("Bod obnovení", ok ? "Bod obnovení byl vytvořen." : "Windows bod obnovení nevytvořil.");
            }));
            actions.Children.Add(QuickButton("Obnovení systému", () => { ProcessRunner.Open("rstrui.exe"); return Task.CompletedTask; }));
            heroHost.Children.Add(BuildSectionHero("restore", "Centrum obnovy", "Obnova systému", "Historie změn a vratné dávky se načítají na pozadí", actions));
            LogService.Info("performance", $"RESTORE_HERO_DEFERRED ms={heroSw.ElapsedMilliseconds}");
        });
        long historyStart = phase.ElapsedMilliseconds;
        var surfaceHost = new StackPanel();
        _restoreSurfaceHost = surfaceHost;
        _restoreSurfaceStage = 0;
        ContentStack.Children.Add(surfaceHost);

        var history = new StackPanel { Spacing = 6 };
        _restoreHistoryHost = history;
        history.Children.Add(new TextBlock { Text = "Načítám historii změn…", Foreground = TextSecondaryBrush() });
        long historyMs = phase.ElapsedMilliseconds - historyStart;

        long undoStart = phase.ElapsedMilliseconds;
        var batchHost = new StackPanel { Spacing = 8 };
        _restoreBatchHost = batchHost;
        batchHost.Children.Add(new TextBlock { Text = "Načítám historii vrácení změn…", Foreground = TextSecondaryBrush() });
        long undoMs = phase.ElapsedMilliseconds - undoStart;
        ContentStack.Children.Add(ActionCard("Vrátit úpravy registru", "Obnoví původní hodnoty registru uložené Optimizerem.", "Obnovit registr", async () =>
        {
            try
            {
                await TweakFactory.Backups.RestoreAllAsync();
                InvalidateRestorePageCache();
                ShowActionFeedback("Obnova registru", ActionFeedbackKind.Verified, "Uložené hodnoty registru byly obnoveny.");
                await ShowMessageAsync("Obnova", "Uložené hodnoty registru byly obnoveny.");
            }
            catch (AggregateException ex)
            {
                // RestoreAll attempts independent items and reports partial
                // failures. Never emit the success toast on this path.
                InvalidateRestorePageCache();
                string details = RegistryRestoreFeedbackService.FormatPartialRestore(ex);
                LogService.Error("restore", "Partial registry RestoreAll failure", ex);
                ShowActionFeedback("Obnova registru", ActionFeedbackKind.Failed, details);
                await ShowMessageAsync("Obnova registru není úplná", details);
            }
        }));
        long actionMs = phase.ElapsedMilliseconds - heroMs - historyMs - undoMs;
        long tailStart = phase.ElapsedMilliseconds;
        SafeTaskService.Forget(
            AddSnapshotCenterAsync(_navigationGeneration, token),
            "restore",
            "Snapshot center");
        long snapshotKickoffMs = phase.ElapsedMilliseconds - tailStart;
        QueueRestoreSurfaceBatch(surfaceHost, history, batchHost, _navigationGeneration, token);
        long surfaceKickoffMs = phase.ElapsedMilliseconds - tailStart - snapshotKickoffMs;
        CaptureRestorePageCache();
        long cacheMs = phase.ElapsedMilliseconds - tailStart - snapshotKickoffMs - surfaceKickoffMs;
        SafeTaskService.Forget(LoadRestoreHistoryAsync(history, batchHost, token), "restore", "Restore history");
        long historyKickoffMs = phase.ElapsedMilliseconds - tailStart - snapshotKickoffMs - surfaceKickoffMs - cacheMs;
        LogService.Info("performance", $"RESTORE_UI_PHASE heroMs={heroMs} historyMs={historyMs} undoMs={undoMs} actionMs={actionMs} snapshotKickoffMs={snapshotKickoffMs} surfaceKickoffMs={surfaceKickoffMs} cacheMs={cacheMs} historyKickoffMs={historyKickoffMs} renderMs={phase.ElapsedMilliseconds} deferredSurfaces=true");
        return Task.CompletedTask;
    }

    private void QueueRestoreSurfaceBatch(
        StackPanel surfaceHost,
        StackPanel history,
        StackPanel batchHost,
        int generation,
        CancellationToken token)
    {
        if (_restoreSurfaceBatchQueued || _restoreSurfaceStage >= 2) return;
        _restoreSurfaceBatchQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _restoreSurfaceBatchQueued = false;
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "restore" ||
                !ReferenceEquals(_restoreSurfaceHost, surfaceHost)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int stage = _restoreSurfaceStage;
            if (stage == 0)
            {
                surfaceHost.Children.Add(BuildSectionSurface("restore", "Historie změn", "Posledních 30 operací Optimizeru", history));
                _restoreSurfaceStage = 1;
            }
            else if (stage == 1)
            {
                surfaceHost.Children.Add(BuildSectionSurface("restore", "Undo historie", "Každou dávku tweaků lze vrátit samostatně", batchHost));
                _restoreSurfaceStage = 2;
            }

            LogService.Info("performance", $"RESTORE_SURFACE_BATCH stage={stage} ms={sw.ElapsedMilliseconds}");
            if (_restoreSurfaceStage < 2)
                SafeTaskService.Forget(
                    QueueNextRestoreSurfaceBatchAsync(surfaceHost, history, batchHost, generation, token),
                    "restore",
                    "Restore surface pacing");
        })) _restoreSurfaceBatchQueued = false;
    }

    private async Task QueueNextRestoreSurfaceBatchAsync(
        StackPanel surfaceHost,
        StackPanel history,
        StackPanel batchHost,
        int generation,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(8, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _navigationGeneration) return;
            QueueRestoreSurfaceBatch(surfaceHost, history, batchHost, generation, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadRestoreHistoryAsync(StackPanel history, StackPanel batchHost, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            var journalTask = Task.Run(async () =>
                await OperationJournalReader.ReadRecentAsync(null, 80).ConfigureAwait(false), token);
            var batchesTask = Task.Run(async () =>
                await TweakBatchHistoryService.ReadRecentAsync(20).ConfigureAwait(false), token);
            await Task.WhenAll(journalTask, batchesTask).ConfigureAwait(false);
            var journal = await journalTask.ConfigureAwait(false);
            var batches = await batchesTask.ConfigureAwait(false);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "restore" ||
                !ReferenceEquals(_restoreHistoryHost, history) ||
                !ReferenceEquals(_restoreBatchHost, batchHost)) return;
            DispatcherQueue.TryEnqueue(() =>
            {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "restore" ||
                !ReferenceEquals(_restoreHistoryHost, history) ||
                !ReferenceEquals(_restoreBatchHost, batchHost)) return;
            history.Children.Clear();
            foreach (var e in journal.Take(30))
                history.Children.Add(new TextBlock { Text = $"{e.Time:dd.MM HH:mm} • {e.Area} • {e.Target} • {e.State}", FontSize = 11.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
            if (journal.Length == 0) history.Children.Add(new TextBlock { Text = "Zatím nejsou zaznamenané žádné změny.", Foreground = TextSecondaryBrush() });

            batchHost.Children.Clear();
            foreach (var batch in batches)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var txt = new StackPanel { Spacing = 2 };
                txt.Children.Add(new TextBlock { Text = $"{batch.Time:dd.MM.yyyy HH:mm} • {batch.Area} • {batch.Items.Count} změn", FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
                txt.Children.Add(new TextBlock { Text = batch.Restored ? "Vráceno" : string.Join(" • ", batch.Items.Take(4).Select(x => x.Name)), FontSize = 11, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
                row.Children.Add(txt);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                buttons.Children.Add(QuickButton("Položky", async () => await ShowBatchItemsAsync(batch)));
                var undo = QuickButton(batch.Restored ? "Vráceno" : "Vrátit dávku", async () => await RestoreTweakBatchAsync(batch)); undo.IsEnabled = !batch.Restored;
                buttons.Children.Add(undo);
                Grid.SetColumn(buttons, 1); row.Children.Add(buttons);
                batchHost.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = row });
            }
            if (batches.Length == 0) batchHost.Children.Add(new TextBlock { Text = "Zatím nejsou žádné vratné dávky tweaků.", Foreground = TextSecondaryBrush() });
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("restore", "Restore history load failed", ex);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "restore" ||
                    !ReferenceEquals(_restoreHistoryHost, history) ||
                    !ReferenceEquals(_restoreBatchHost, batchHost)) return;
                history.Children.Clear();
                batchHost.Children.Clear();

                history.Children.Add(new TextBlock
                {
                    Text = "Historii změn se nepodařilo načíst: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
                batchHost.Children.Add(new TextBlock
                {
                    Text = "Undo historii se nepodařilo načíst.",
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });

                var retry = QuickButton("Načíst znovu", () =>
                    LoadRestoreHistoryAsync(history, batchHost, token));
                history.Children.Add(retry);
            });
        }
    }
    private async Task RestoreTweakBatchAsync(TweakBatchRecord batch)
    {
        if (!await ShowStyledConfirmAsync("Vrátit dávku tweaků?", $"{batch.Items.Count} změn z {batch.Time:dd.MM.yyyy HH:mm}", "Vrátit", "Zrušit", batch.Area)) return;
        var errors = await TweakBatchHistoryService.RestoreAsync(batch.BatchId);
        InvalidateRestorePageCache();
        string detail = errors.Length == 0
            ? "Dávka byla vrácena a ověřena read-backem."
            : string.Join(Environment.NewLine, errors.Take(8));
        ShowActionFeedback(
            "Undo — " + batch.Area,
            errors.Length == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
            detail);
        await ShowMessageAsync("Undo", detail);
        await ShowSectionAsync("restore");
    }

    private async Task CheckAppUpdateAsync(TextBlock status)
    {
        var check = await AppUpdateService.CheckAsync();
        status.Text = check.Message;
        if (!check.Available || check.Manifest == null) return;
        string note = string.IsNullOrWhiteSpace(check.Manifest.Changelog) ? "" : "\n\n" + check.Manifest.Changelog;
        if (!await ShowStyledConfirmAsync("Aktualizace aplikace", $"Dostupná verze {RuntimeVersionInfo.PublicVersion(check.Manifest.Version)}.{note}", "Stáhnout", "Později", "SHA-256 bude ověřen")) return;
        try
        {
            var progressResult = await RunCancellableWithProgressAsync(
                "Stahuji aktualizaci",
                "Stahuji a ověřuji installer…",
                token => AppUpdateService.DownloadAsync(check.Manifest, token));
            if (!progressResult.Completed)
            {
                LogService.Info("update", "Application update download cancelled by user.");
                return;
            }
            string path = progressResult.Result;
            if (await ShowStyledConfirmAsync("Aktualizace stažena", "Installer je ověřený. Spustit transakční update a ukončit Optimizer?", "Spustit", "Později", path))
            {
                AppUpdateService.LaunchInstallerForUpdate(path, check.Manifest.Sha256);
                Close();
            }
        }
        catch (Exception ex) { await ShowStyledInfoAsync("Aktualizace", ex.Message); }
    }

    private async Task ShowSelfDiagnosticsAsync()
    {
        var report = await SelfDiagnosticsService.RunAsync();
        var body = new StackPanel { Spacing = 7 };
        foreach (var item in report.Items)
            body.Children.Add(new TextBlock { Text = $"{(item.Ok ? "✓" : "✗")} {item.Name} — {item.Detail}", Foreground = item.Ok ? TextPrimaryBrush() : UiBrush("SpankyWarningBrush"), TextWrapping = TextWrapping.Wrap });
        await ShowStyledContentAsync("Kontrola aplikace", report.Healthy ? "Všechny základní subsystémy jsou dostupné." : "Některý subsystém potřebuje pozornost.", new ScrollViewer { Content = body, MaxHeight = 520 }, "Zavřít");
    }

    private async Task ExportManualCrashReportAsync()
    {
        string path = await CrashReportService.CreateAsync(_section, _lastNavigationErrorId);
        await ShowMessageAsync("Crash report", "Lokální diagnostický ZIP byl vytvořen:\n" + path);
    }

    private void BuildSettingsPage()
    {
        if (_settingsPageCache != null)
        {
            _settingsPageCacheAt = DateTime.UtcNow;
            foreach (var element in _settingsPageCache) AttachCachedElement(element);
            if (_settingsDeferredBody != null && _settingsDeferredStage < 3) ScheduleDeferredSettingsBody();
            return;
        }

        var phase = System.Diagnostics.Stopwatch.StartNew();
        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroActions.Children.Add(QuickButton("Záloha / Obnova", async () => await ShowSectionAsync("restore")));
        ContentStack.Children.Add(BuildSectionHero("settings", "Application", "Nastavení", $"Windows {Environment.OSVersion.Version.Build}", heroActions));
        long heroMs = phase.ElapsedMilliseconds;

        var appState = new StackPanel { Spacing = 5 };
        appState.Children.Add(new TextBlock { Text = $"VERZE {RuntimeVersionInfo.PublicVersion(typeof(MainWindow).Assembly)}   •   REŽIM {(CrashLoopGuardService.SafeMode ? "SAFE MODE" : "NORMAL")}   •   VZHLED DARK GLASS", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        appState.Children.Add(new TextBlock { Text = "Záloha a obnova jsou dostupné přímo z Nastavení.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        ContentStack.Children.Add(BuildSectionSurface("settings", "Stav aplikace", "Rychlý přehled konfigurace", appState));
        long stateMs = phase.ElapsedMilliseconds - heroMs;

        _settingsDeferredBody = new StackPanel { Spacing = 10 };
        ContentStack.Children.Add(_settingsDeferredBody);
        _settingsDeferredStage = 0;
        _settingsPageCache = SnapshotChildren(ContentStack);
        _settingsPageCacheAt = DateTime.UtcNow;
        ScheduleDeferredSettingsBody();
        LogService.Info("performance", $"SETTINGS_UI_PHASE heroMs={heroMs} stateMs={stateMs} deferredHostMs={phase.ElapsedMilliseconds - heroMs - stateMs} totalMs={phase.ElapsedMilliseconds} deferred=true");
    }

    private void ScheduleDeferredSettingsBody()
    {
        StackPanel? host = _settingsDeferredBody;
        if (_settingsDeferredQueued || _settingsDeferredRunning || host == null || _settingsDeferredStage >= 3) return;
        int generation = _navigationGeneration;
        _settingsDeferredQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _settingsDeferredQueued = false;
            if (generation != _navigationGeneration ||
                _section != "settings" ||
                !ReferenceEquals(_settingsDeferredBody, host) ||
                _settingsDeferredStage >= 3)
                return;
            _settingsDeferredRunning = true;
            SafeTaskService.Forget(
                RunDeferredSettingsBodyOnceAsync(host, generation),
                "settings",
                "Deferred settings UI");
        }))
        {
            // Dispatcher rejection is possible during window disposal. Do not
            // leave settings permanently marked queued after enqueue failure.
            _settingsDeferredQueued = false;
        }
    }

    private async Task RunDeferredSettingsBodyOnceAsync(StackPanel host, int generation)
    {
        try
        {
            await BuildDeferredSettingsBodyAsync(host, generation);
        }
        finally
        {
            // An older navigation task must never clear the state of a newly
            // created settings host. Reattach may require resuming the remaining
            // stages when navigation changed while the old job was in flight.
            if (ReferenceEquals(_settingsDeferredBody, host))
            {
                _settingsDeferredRunning = false;
                if (generation != _navigationGeneration && _section == "settings" &&
                    _settingsDeferredStage < 3)
                    ScheduleDeferredSettingsBody();
            }
        }
    }

    // User selecting an already active fixed palette must not rebuild the whole
    // shell, invalidate twenty page caches or write the same registry value.
    // System theme is deliberately excluded: reselecting it can re-read a new
    // Windows light/dark preference. Explicit reset/QA still call ChangeThemeAsync.
    private Task SelectThemePreviewAsync(AppThemePreference preference)
    {
        if (preference == _themePreference && preference != AppThemePreference.System)
            return Task.CompletedTask;
        return ChangeThemeAsync(preference);
    }

    // Shared by the real Settings button and a strictly isolated UI smoke.
    // The caller provides the verified persistence backend and a guarded
    // visual update: tests never read/write the user's motion.txt setting.
    private Button BuildVerifiedMotionResetButton(
        ToggleSwitch motion,
        Func<bool> read,
        Action<bool> write,
        Action<bool> showVerifiedState,
        Func<Exception, Task> showError)
    {
        Button button = QuickButton("Obnovit výchozí animace", async () =>
        {
            if (!motion.IsEnabled) return;
            try
            {
                if (!read()) write(true);
                bool verified = read();
                if (!verified)
                    throw new InvalidOperationException("Obnovu animací se nepodařilo ověřit.");
                showVerifiedState(verified);
            }
            catch (Exception ex)
            {
                await showError(ex);
            }
        });
        // Keep a stable assistive-technology action name even while QuickButton
        // temporarily displays "Pracuji…" during an asynchronous operation.
        AutomationProperties.SetName(button, "Obnovit výchozí animace");
        AutomationProperties.SetHelpText(button, "Zapne výchozí animace rozhraní. Barevný Glass vzhled se nemění.");
        return button;
    }

    private async Task BuildDeferredSettingsBodyAsync(StackPanel root, int generation)
    {
        if (_settingsDeferredStage == 0)
        {
            if (!await RunLowPrioritySettingsStageAsync("glass-motion", root, generation, () =>
            {
                UiMotionSettingsService.RefreshSystemPreference();
                 var motion = new ToggleSwitch { Header = LanguageService.Text("iOS 27 animace", "iOS 27 motion"), IsOn = UiMotionSettingsService.Enabled, OnContent = LanguageService.Text("Zapnuto", "On"), OffContent = LanguageService.Text("Vypnuto", "Off") };
                NormalizeToggleAppearance(motion);
                bool suppressProgrammaticMotionToggle = false;
                motion.Toggled += (_, _) =>
                {
                    if (suppressProgrammaticMotionToggle) return;
                    SafeTaskService.Forget(
                        ApplyVerifiedImmediateToggleAsync(motion, UiMotionSettingsService.SetEnabled,
                             () => UiMotionSettingsService.Enabled, "settings", LanguageService.Text("iOS 27 animace", "iOS 27 motion")),
                        "settings", "Verified UI motion toggle");
                };
                var theme = new StackPanel { Spacing = 10 };
                theme.Children.Add(new TextBlock
                {
                    Text = UiMotionSettingsService.SystemAnimationsEnabled
                        ? "Motion Engine: Micro 110 ms • Normal 170 ms • Popup 220 ms • GPU Composition"
                        : "Motion Engine je omezený nastavením Windows Reduce Motion.",
                    Foreground = TextSecondaryBrush(),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap
                });
                 theme.Children.Add(new TextBlock
                 {
                    Text = "Glass vzhled s volitelným plným tmavým tématem • modrobílé ovládací prvky",
                    Foreground = TextSecondaryBrush(),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap
                 });
                var themeOptions = new[]
                {
                    AppThemePreference.Dark,
                    AppThemePreference.SolidDark
                };
                var themePicker = new ComboBox
                {
                     Header = LanguageService.Text("Téma aplikace", "Application theme"),
                 ItemsSource = new[] { LanguageService.Text("Glass (průhledné)", "Glass (transparent)"), LanguageService.Text("Solid Dark (lehce průhledné)", "Solid Dark (lightly translucent)") },
                    SelectedIndex = System.Array.IndexOf(themeOptions, _themePreference),
                    MinWidth = 220,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                if (themePicker.SelectedIndex < 0) themePicker.SelectedIndex = 0;
                 AutomationProperties.SetName(themePicker, LanguageService.Text("Téma aplikace", "Application theme"));
                 AutomationProperties.SetHelpText(themePicker, LanguageService.Text("Vybere výrazně průhledný Glass vzhled nebo méně průhledný Solid Dark.", "Choose transparent Glass or lightly translucent Solid Dark."));
                themePicker.SelectionChanged += (_, _) =>
                {
                    int selectedIndex = themePicker.SelectedIndex;
                    if ((uint)selectedIndex >= (uint)themeOptions.Length) return;
                    SafeTaskService.Forget(
                        SelectThemePreviewAsync(themeOptions[selectedIndex]),
                        "settings",
                        "Application theme selection");
                };
                theme.Children.Add(themePicker);
                var motionRow = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 6, 0, 6) };
                motionRow.ColumnDefinitions.Add(new ColumnDefinition());
                motionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                 motionRow.Children.Add(new TextBlock { Text = LanguageService.Text("iOS 27 animace", "iOS 27 motion"), Foreground = TextPrimaryBrush(), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                motion.Header = null;
                 Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(motion, LanguageService.Text("iOS 27 animace", "iOS 27 motion"));
                Grid.SetColumn(motion, 1);
                motionRow.Children.Add(motion);
                theme.Children.Add(motionRow);
                theme.Children.Add(BuildVerifiedMotionResetButton(
                    motion,
                    () => UiMotionSettingsService.Enabled,
                    UiMotionSettingsService.SetEnabled,
                    verified =>
                    {
                        suppressProgrammaticMotionToggle = true;
                        try { if (motion.IsOn != verified) motion.IsOn = verified; }
                        finally { suppressProgrammaticMotionToggle = false; }
                    },
                    async ex =>
                    {
                        LogService.Error("settings", "Glass UI animation reset failed", ex);
                        await ShowMessageAsync("Glass UI animace", ex.Message);
                    }));
                 var language = new ComboBox
                 {
                     Header = LanguageService.Text("Jazyk aplikace", "Application language"),
                     ItemsSource = new[] { "Čeština", "English" },
                     SelectedIndex = LanguageService.IsEnglish ? 1 : 0,
                     MinWidth = 220,
                     HorizontalAlignment = HorizontalAlignment.Left
                 };
                 language.SelectionChanged += (_, _) =>
                 {
                     if (language.SelectedIndex is < 0 or > 1) return;
                     LanguageService.Set(language.SelectedIndex == 1 ? UiLanguage.English : UiLanguage.Czech);
                     _ = ShowMessageAsync(LanguageService.Text("Jazyk uložen", "Language saved"), LanguageService.Text("Nový jazyk se použije při dalším otevření nastavení.", "The new language will apply when Settings is opened again."));
                 };
                 theme.Children.Add(language);
                 root.Children.Add(LanguageService.IsEnglish
                     ? BuildSectionSurface("settings", "Glass UI", "Interface motion", theme)
                     : BuildSectionSurface("settings", "Glass UI", "Animace rozhraní", theme));

            })) return;
            _settingsDeferredStage = 1;
        }

        if (_settingsDeferredStage == 1)
        {
            if (!await RunLowPrioritySettingsStageAsync("safety", root, generation, () =>
            {
                var safety = new StackPanel { Spacing = 8 };
                safety.Children.Add(new TextBlock { Text = CrashLoopGuardService.SafeMode ? "Nouzový režim je aktivní pro tento běh." : "Normální režim. Rizikové subsystémy používají izolované pomocné procesy.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
                var safetyActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                safetyActions.Children.Add(QuickButton("Restart do nouzového režimu", () => { SafeModeLauncherService.RestartInSafeMode(); return Task.CompletedTask; }));
                safety.Children.Add(safetyActions);

                // Registry are managed by ONE switch in AI Runtime. Keeping
                // seven more switches here meant two competing places to edit
                // the same consent. NIC settings stay in the Network section.
                safety.Children.Add(new TextBlock
                {
                    Text = "Automatické hlídání výchozích registrů spravuje jeden přepínač v AI Runtime. Případnou ruční opravu (UAC) najdeš tamtéž.",
                    Foreground = TextSecondaryBrush(),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap
                });
                safety.Children.Add(QuickButton("Otevřít AI Runtime", () => ShowSectionAsync("ai-runtime")));
                safety.Children.Add(QuickButton("Síť a Green Ethernet", () => ShowSectionAsync("network")));
                root.Children.Add(BuildSectionSurface("settings", "Stabilita a obnova", "Nouzový režim a ochrana proti problémům při startu", safety));
            })) return;
            _settingsDeferredStage = 2;
        }

        // Backup / Restore already has an always-visible hero shortcut and its
        // own navigation page. Avoid a duplicate settings card and dispatcher
        // round-trip after retiring the old theme picker.
        if (_settingsDeferredStage == 2)
        {
            if (!await RunLowPrioritySettingsStageAsync("tail", root, generation, () =>
            {
                root.Children.Add(ActionCard("Aplikace po spuštění", "Otevře systémovou správu aplikací po spuštění.", "Otevřít", () => { ProcessRunner.Open("ms-settings:startupapps"); return Task.CompletedTask; }));

            })) return;
            _settingsDeferredStage = 3;
        }
    }

    private Task<bool> RunLowPrioritySettingsStageAsync(string stage, StackPanel root, int generation, Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration ||
                _section != "settings" ||
                !ReferenceEquals(root, _settingsDeferredBody))
            {
                tcs.TrySetResult(false);
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                action();
                LogService.Info("performance", $"SETTINGS_DEFERRED_STAGE stage={stage} ms={sw.ElapsedMilliseconds}");
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                LogService.Error("settings", $"Deferred settings stage failed: {stage}", ex);
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);
        return tcs.Task;
    }

    private void InvalidateSettingsPageCache()
    {
        _settingsPageCache = null;
        _settingsDeferredBody = null;
        _settingsDeferredQueued = false;
        _settingsDeferredRunning = false;
        _settingsDeferredStage = 0;
        _settingsPageCacheAt = default;
    }
}
