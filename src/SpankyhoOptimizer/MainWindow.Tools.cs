using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _settingsPageCache;
    private DateTime _settingsPageCacheAt;
    private StackPanel? _settingsDeferredBody;
    private bool _settingsDeferredQueued;
    private int _settingsDeferredStage;

    private Task BuildFixerPageAsync(CancellationToken token)
    {
        if (TryAttachFixerPageCache(token)) return Task.CompletedTask;
        var fixedUi = GetFixerStaticElements();
        ContentStack.Children.Add(fixedUi[0]);
        SafeTaskService.Forget(BuildAppHealthAsync(token), "fixer", "App health");
        SafeTaskService.Forget(BuildFixerDiagnosticsAsync(token), "fixer", "Fixer diagnostics");
        for (int i = 1; i < fixedUi.Count; i++) ContentStack.Children.Add(fixedUi[i]);
        CaptureFixerPageCache();
        ScheduleDeferredFixerStaticActions(token);
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
            AddGTweakRestoreActions(actions);
            heroHost.Children.Add(BuildSectionHero("restore", "Centrum obnovy", "Záloha / Obnova", "Historie změn a vratné dávky se načítají na pozadí", actions));
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
            await TweakFactory.Backups.RestoreAllAsync();
            InvalidateRestorePageCache();
            ShowActionFeedback("Obnova registru", ActionFeedbackKind.Verified, "Uložené hodnoty registru byly obnoveny.");
            await ShowMessageAsync("Obnova", "Uložené hodnoty registru byly obnoveny.");
        }));
        long actionMs = phase.ElapsedMilliseconds - heroMs - historyMs - undoMs;
        long tailStart = phase.ElapsedMilliseconds;
        SafeTaskService.Forget(AddSnapshotCenterAsync(), "restore", "Snapshot center");
        long snapshotKickoffMs = phase.ElapsedMilliseconds - tailStart;
        QueueRestoreSurfaceBatch(surfaceHost, history, batchHost, token);
        long surfaceKickoffMs = phase.ElapsedMilliseconds - tailStart - snapshotKickoffMs;
        CaptureRestorePageCache();
        long cacheMs = phase.ElapsedMilliseconds - tailStart - snapshotKickoffMs - surfaceKickoffMs;
        SafeTaskService.Forget(LoadRestoreHistoryAsync(history, batchHost, token), "restore", "Restore history");
        long historyKickoffMs = phase.ElapsedMilliseconds - tailStart - snapshotKickoffMs - surfaceKickoffMs - cacheMs;
        LogService.Info("performance", $"RESTORE_UI_PHASE heroMs={heroMs} historyMs={historyMs} undoMs={undoMs} actionMs={actionMs} snapshotKickoffMs={snapshotKickoffMs} surfaceKickoffMs={surfaceKickoffMs} cacheMs={cacheMs} historyKickoffMs={historyKickoffMs} renderMs={phase.ElapsedMilliseconds} deferredSurfaces=true");
        return Task.CompletedTask;
    }

    private void QueueRestoreSurfaceBatch(StackPanel surfaceHost, StackPanel history, StackPanel batchHost, CancellationToken token)
    {
        if (_restoreSurfaceBatchQueued || _restoreSurfaceStage >= 2) return;
        _restoreSurfaceBatchQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _restoreSurfaceBatchQueued = false;
            if (token.IsCancellationRequested || _section != "restore" || !ReferenceEquals(_restoreSurfaceHost, surfaceHost)) return;

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
                SafeTaskService.Forget(QueueNextRestoreSurfaceBatchAsync(surfaceHost, history, batchHost, token), "restore", "Restore surface pacing");
        });
    }

    private async Task QueueNextRestoreSurfaceBatchAsync(StackPanel surfaceHost, StackPanel history, StackPanel batchHost, CancellationToken token)
    {
        try
        {
            await Task.Delay(8, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            QueueRestoreSurfaceBatch(surfaceHost, history, batchHost, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadRestoreHistoryAsync(StackPanel history, StackPanel batchHost, CancellationToken token)
    {
        try
        {
            var journalTask = Task.Run(async () =>
                await OperationJournalReader.ReadRecentAsync(null, 80).ConfigureAwait(false), token);
            var batchesTask = Task.Run(async () =>
                await TweakBatchHistoryService.ReadRecentAsync(20).ConfigureAwait(false), token);
            await Task.WhenAll(journalTask, batchesTask).ConfigureAwait(false);
            var journal = await journalTask.ConfigureAwait(false);
            var batches = await batchesTask.ConfigureAwait(false);
            if (token.IsCancellationRequested || _section != "restore") return;
            DispatcherQueue.TryEnqueue(() =>
            {
            if (_section != "restore") return;
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
                if (_section != "restore") return;
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
        if (!await ShowStyledConfirmAsync("Aktualizace aplikace", $"Dostupná verze {check.Manifest.Version}.{note}", "Stáhnout", "Později", "SHA-256 bude ověřen")) return;
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
                AppUpdateService.LaunchInstallerForUpdate(path);
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
            if (_settingsDeferredBody != null && _settingsDeferredStage < 4) ScheduleDeferredSettingsBody();
            return;
        }

        var phase = System.Diagnostics.Stopwatch.StartNew();
        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroActions.Children.Add(QuickButton("Záloha / Obnova", async () => await ShowSectionAsync("restore")));
        ContentStack.Children.Add(BuildSectionHero("settings", "Application", "Nastavení", $"Spankyho Optimizer {AppUpdateService.CurrentVersion} • Windows {Environment.OSVersion.Version.Build}", heroActions));
        long heroMs = phase.ElapsedMilliseconds;

        var appState = new StackPanel { Spacing = 5 };
        appState.Children.Add(new TextBlock { Text = $"VERZE {AppUpdateService.CurrentVersion}   •   REŽIM {(CrashLoopGuardService.SafeMode ? "SAFE MODE" : "NORMAL")}   •   THEME {AppThemeEngine.DisplayName(_themePreference).ToUpperInvariant()}", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        appState.Children.Add(new TextBlock { Text = "Záloha a obnova jsou dostupné přímo z Nastavení.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        ContentStack.Children.Add(BuildSectionSurface("settings", "Stav aplikace", "Rychlý přehled konfigurace", appState));
        long stateMs = phase.ElapsedMilliseconds - heroMs;

        _settingsDeferredBody = new StackPanel { Spacing = 10 };
        ContentStack.Children.Add(_settingsDeferredBody);
        _settingsDeferredStage = 0;
        _settingsPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _settingsPageCacheAt = DateTime.UtcNow;
        ScheduleDeferredSettingsBody();
        LogService.Info("performance", $"SETTINGS_UI_PHASE heroMs={heroMs} stateMs={stateMs} deferredHostMs={phase.ElapsedMilliseconds - heroMs - stateMs} totalMs={phase.ElapsedMilliseconds} deferred=true");
    }

    private void ScheduleDeferredSettingsBody()
    {
        if (_settingsDeferredQueued || _settingsDeferredBody == null || _settingsDeferredStage >= 4) return;
        _settingsDeferredQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _settingsDeferredQueued = false;
            if (_section != "settings" || _settingsDeferredBody == null || _settingsDeferredStage >= 4) return;
            SafeTaskService.Forget(BuildDeferredSettingsBodyAsync(_settingsDeferredBody), "settings", "Deferred settings UI");
        });
    }

    private Button BuildThemePreviewCard(AppThemePreference preference)
    {
        bool dark = AppThemeService.ResolveDark(preference);
        var palette = AppThemeEngine.Resolve(preference, dark);
        bool active = preference == _themePreference;

        var title = new TextBlock
        {
            Text = AppThemeEngine.DisplayName(preference),
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeColor(palette.TextPrimary.R, palette.TextPrimary.G, palette.TextPrimary.B)
        };
        var status = new TextBlock
        {
            Text = active ? "AKTIVNÍ" : (dark ? "DARK" : "LIGHT"),
            FontSize = 8.5,
            CharacterSpacing = 80,
            Foreground = ThemeColor(palette.TextSecondary.R, palette.TextSecondary.G, palette.TextSecondary.B)
        };

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        swatches.Children.Add(new Border { Width = 26, Height = 5, CornerRadius = new CornerRadius(3), Background = ThemeColor(palette.Accent.R, palette.Accent.G, palette.Accent.B) });
        swatches.Children.Add(new Border { Width = 18, Height = 5, CornerRadius = new CornerRadius(3), Background = ThemeColor(palette.PrimaryButton.R, palette.PrimaryButton.G, palette.PrimaryButton.B) });
        swatches.Children.Add(new Border { Width = 18, Height = 5, CornerRadius = new CornerRadius(3), Background = ThemeColor(palette.Border.R, palette.Border.G, palette.Border.B) });

        var miniSurface = new Border
        {
            Height = 22,
            CornerRadius = new CornerRadius(5),
            Background = ThemeColor(palette.Surface.R, palette.Surface.G, palette.Surface.B),
            BorderBrush = ThemeColor(palette.Border.R, palette.Border.G, palette.Border.B),
            BorderThickness = new Thickness(1)
        };

        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(title);
        content.Children.Add(status);
        content.Children.Add(miniSurface);
        content.Children.Add(swatches);

        var preview = new Border
        {
            Width = 148,
            MinHeight = 88,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(10),
            Background = ThemeColor(palette.SurfaceElevated.R, palette.SurfaceElevated.G, palette.SurfaceElevated.B),
            BorderBrush = active ? AccentBrush() : ThemeColor(palette.Border.R, palette.Border.G, palette.Border.B),
            BorderThickness = new Thickness(active ? 1.6 : 1),
            Child = content
        };

        var button = new Button
        {
            Padding = new Thickness(0),
            Background = null,
            BorderThickness = new Thickness(0),
            Content = preview
        };
        string themeName = AppThemeEngine.DisplayName(preference);
        ToolTipService.SetToolTip(button, themeName);
        EnsureAccessibleButton(button, themeName);
        button.Click += (_, _) => SafeTaskService.Forget(ChangeThemeAsync(preference), "settings", "Theme preview selection");
        return button;
    }

    private async Task BuildDeferredSettingsBodyAsync(StackPanel root)
    {
        if (_settingsDeferredStage == 0)
        {
            if (!await RunLowPrioritySettingsStageAsync("theme-update", root, () =>
            {
                var motion = new ToggleSwitch { Header = "Animace UI", IsOn = UiMotionSettingsService.Enabled, OnContent = "Zapnuto", OffContent = "Vypnuto" };
                motion.Toggled += (_, _) => SafeTaskService.Forget(
                    ApplyVerifiedImmediateToggleAsync(motion, UiMotionSettingsService.SetEnabled,
                        () => UiMotionSettingsService.Enabled, "settings", "Animace UI"),
                    "settings", "Verified UI motion toggle");
                var theme = new StackPanel { Spacing = 10 };
                theme.Children.Add(new TextBlock { Text = $"Aktuální motiv: {AppThemeEngine.DisplayName(_themePreference)}", Foreground = TextSecondaryBrush() });

                var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row1.Children.Add(BuildThemePreviewCard(AppThemePreference.Dark));
                row1.Children.Add(BuildThemePreviewCard(AppThemePreference.MidnightBlue));
                row1.Children.Add(BuildThemePreviewCard(AppThemePreference.PurpleAccent));
                row1.Children.Add(BuildThemePreviewCard(AppThemePreference.CyanTech));

                var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row2.Children.Add(BuildThemePreviewCard(AppThemePreference.RedPerformance));
                row2.Children.Add(BuildThemePreviewCard(AppThemePreference.Light));
                row2.Children.Add(BuildThemePreviewCard(AppThemePreference.System));

                theme.Children.Add(row1);
                theme.Children.Add(row2);
                var motionRow = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 6, 0, 6) };
                motionRow.ColumnDefinitions.Add(new ColumnDefinition());
                motionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                motionRow.Children.Add(new TextBlock { Text = "Animace rozhran\u00ed", Foreground = TextPrimaryBrush(), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                motion.Header = null;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(motion, "Animace rozhran\u00ed");
                Grid.SetColumn(motion, 1);
                motionRow.Children.Add(motion);
                theme.Children.Add(motionRow);
                theme.Children.Add(QuickButton("Obnovit výchozí UI", async () =>
                {
                    UiMotionSettingsService.SetEnabled(true);
                    motion.IsOn = true;
                    await ChangeThemeAsync(AppThemePreference.Dark);
                }));
                root.Children.Add(BuildSectionSurface("settings", "Vzhled aplikace", "Motiv a animace rozhraní", theme));

            })) return;
            _settingsDeferredStage = 1;
        }

        if (_settingsDeferredStage == 1)
        {
            if (!await RunLowPrioritySettingsStageAsync("safety", root, () =>
            {
                var safety = new StackPanel { Spacing = 8 };
                safety.Children.Add(new TextBlock { Text = CrashLoopGuardService.SafeMode ? "Nouzový režim je aktivní pro tento běh." : "Normální režim. Rizikové subsystémy používají izolované pomocné procesy.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
                var safetyActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                safetyActions.Children.Add(QuickButton("Restart do nouzového režimu", () => { SafeModeLauncherService.RestartInSafeMode(); return Task.CompletedTask; }));
                safety.Children.Add(safetyActions);
                root.Children.Add(BuildSectionSurface("settings", "Stabilita a obnova", "Nouzový režim a ochrana proti problémům při startu", safety));
            })) return;
            _settingsDeferredStage = 2;
        }

        if (_settingsDeferredStage == 2)
        {
            if (!await RunLowPrioritySettingsStageAsync("maintenance", root, () =>
            {
                var maintenance = new StackPanel { Spacing = 8 };
                maintenance.Children.Add(ActionCard("Záloha / Obnova", "Body obnovení, historie změn a návrat optimalizací.", "Otevřít", async () => await ShowSectionAsync("restore")));
                root.Children.Add(BuildSectionSurface("settings", "Údržba", "Záloha a obnova nastavení Optimizeru", maintenance));
            })) return;
            _settingsDeferredStage = 3;
        }

        if (_settingsDeferredStage == 3)
        {
            if (!await RunLowPrioritySettingsStageAsync("tail", root, () =>
            {
                root.Children.Add(ActionCard("Nastavení motivů Windows", "Otevře motivy Windows.", "Otevřít", () => { ProcessRunner.Open("ms-settings:themes"); return Task.CompletedTask; }));
                root.Children.Add(ActionCard("Aplikace po spuštění", "Otevře systémovou správu aplikací po spuštění.", "Otevřít", () => { ProcessRunner.Open("ms-settings:startupapps"); return Task.CompletedTask; }));

            })) return;
            _settingsDeferredStage = 4;
        }
    }

    private Task<bool> RunLowPrioritySettingsStageAsync(string stage, StackPanel root, Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_section != "settings" || !ReferenceEquals(root, _settingsDeferredBody))
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
        _settingsDeferredStage = 0;
        _settingsPageCacheAt = default;
    }
}

