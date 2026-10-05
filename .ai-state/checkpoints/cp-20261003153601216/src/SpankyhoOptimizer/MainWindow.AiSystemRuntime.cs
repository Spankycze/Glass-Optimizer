using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Task BuildAiSystemRuntimePageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var dataPerf = System.Diagnostics.Stopwatch.StartNew();

        FanProfile profile = FanProfileService.Load();
        long fanProfileMs = dataPerf.ElapsedMilliseconds;

        FanAgentHealth? health = FanProfileService.ReadHealthStatus();
        long fanHealthMs = dataPerf.ElapsedMilliseconds - fanProfileMs;
        bool fresh = health != null &&
            DateTime.UtcNow - health.UpdatedUtc <= TimeSpan.FromSeconds(10);

        AiSystemRuntimeConfig systemConfig = AiSystemResourceManagerService.LoadConfig();
        long configMs =
            dataPerf.ElapsedMilliseconds - fanProfileMs - fanHealthMs;

        AiSystemRuntimeHealth? systemHealth = AiSystemResourceManagerService.ReadHealth();
        long systemHealthMs =
            dataPerf.ElapsedMilliseconds - fanProfileMs - fanHealthMs - configMs;
        bool systemFresh = systemHealth != null &&
            DateTime.UtcNow - systemHealth.UpdatedUtc <= TimeSpan.FromMinutes(5);

        long beforeCloud = dataPerf.ElapsedMilliseconds;
        AiCloudProfileStatus cloudStatus = AiCloudProfileService.ReadStatus();
        long cloudStatusMs = dataPerf.ElapsedMilliseconds - beforeCloud;

        long beforeGoogle = dataPerf.ElapsedMilliseconds;
        GoogleDriveAuthStatus googleAuth = GoogleDriveOAuthService.ReadStatus();
        long googleAuthMs = dataPerf.ElapsedMilliseconds - beforeGoogle;

        long beforeSelf = dataPerf.ElapsedMilliseconds;
        AiSelfOptimizationHealth? selfHealth = AiSelfOptimizationService.ReadHealth();
        long selfHealthMs = dataPerf.ElapsedMilliseconds - beforeSelf;

        LogService.Info(
            "performance",
            $"AI_RUNTIME_SYNC_DATA fanProfileMs={fanProfileMs} fanHealthMs={fanHealthMs} configMs={configMs} systemHealthMs={systemHealthMs} cloudStatusMs={cloudStatusMs} googleAuthMs={googleAuthMs} selfHealthMs={selfHealthMs} totalMs={dataPerf.ElapsedMilliseconds}");

        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroActions.Children.Add(PrimaryQuickButton("Ventilátory", () => ShowSectionAsync("lianli")));
        heroActions.Children.Add(QuickButton("Obnovit vše", async () =>
        {
            await AiSystemResourceManagerService.RestoreAllNowAsync(token);
            await RefreshAiRuntimePageAsync();
        }));

        ContentStack.Children.Add(BuildModernSectionHero(
            "ai-runtime",
            "AI System Runtime",
            "Lokální AI runtime pro výkon, ventilátory a dynamickou správu systémových prostředků.",
            "AI RUNTIME",
            "CPU • ventilátory • služby • tasky • volitelné ovladače",
            heroActions));

        var voiceCloudHost = new Grid();
        voiceCloudHost.Children.Add(new TextBlock
        {
            Text = "Načítám Voice AI / Cloud Brain…",
            FontSize = 12,
            Foreground = TextSecondaryBrush(),
            Margin = new Thickness(2, 6, 2, 6)
        });
        ContentStack.Children.Add(voiceCloudHost);
        SafeTaskService.Forget(
            LoadAiVoiceCloudDeferredAsync(voiceCloudHost, token),
            "ai-runtime",
            "Deferred Voice AI panel");

        string agentState = fresh ? "ONLINE" : "STALE";
        string confidence = fresh && health!.AiEnabled ? $"{health.AiConfidence:0} %" : "—";
        string anomaly = fresh && health!.AiEnabled ? $"{health.AiAnomalyScore:0}" : "—";
        string prediction = fresh && health!.AiEnabled ? $"{health.AiPredictedTemp10s:0.0} °C" : "—";

        ContentStack.Children.Add(BuildDashboardMetricGrid(
            BuildDashboardMetric("Fan Agent", agentState, fresh ? $"PID {health!.ProcessId}" : "Čekám na health data", fresh ? "SpankySuccessBrush" : "SpankyWarningBrush"),
            BuildDashboardMetric("Uspané", systemFresh ? systemHealth!.Sleeping.ToString() : "—", "Služby + tasky bez práce", "SpankySuccessBrush"),
            BuildDashboardMetric("Blokované", systemFresh ? systemHealth!.Blocked.ToString() : "—", "Bezpečnostní guard", systemFresh && systemHealth!.Blocked > 0 ? "SpankyWarningBrush" : "SpankySuccessBrush"),
            BuildDashboardMetric("AI confidence", confidence, $"CPU za 10 s {prediction}", "SpankyInfoBrush")));

        var runtimePanel = new StackPanel { Spacing = 10 };
        var master = new ToggleSwitch
        {
            Header = "AI System Runtime",
            OnContent = "Zapnuto",
            OffContent = "Vypnuto",
            IsOn = profile.AiSmartRuntimeEnabled && systemConfig.Enabled
        };
        master.Toggled += (_, _) =>
        {
            FanProfile current = FanProfileService.Load();
            FanProfileService.Save(current with { AiSmartRuntimeEnabled = master.IsOn });
            AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
            AiSystemResourceManagerService.SaveConfig(cfg with { Enabled = master.IsOn });
            FanProfileService.StartAgentIfNeeded();
        };
        runtimePanel.Children.Add(master);

        runtimePanel.Children.Add(new TextBlock
        {
            Text = fresh && health!.AiEnabled
                ? $"Režim: {health.AiMode} • load {health.LoadPercent:0}% • {health.PackagePowerW:0} W • {health.AiPrediction}" +
                  (systemFresh
                      ? $" • System: {systemHealth!.LastAction} • maintenance {(systemHealth.LastRealIdle || systemHealth.LastLoadPercent <= 5 ? "READY" : "WAIT")} ({systemHealth.LastLoadPercent:0}% load) • cycle {systemHealth.LastCycleMs:0.0} ms • S/T/D {systemHealth.LastServiceScanMs:0.0}/{systemHealth.LastTaskScanMs:0.0}/{systemHealth.LastDriverScanMs:0.0} ms"
                      : string.Empty)
                : "AI runtime nemá čerstvá health data. Fan Agent se obnoví přes watchdog.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });

        ContentStack.Children.Add(BuildDashboardExpander(
            "AI System Runtime",
            "Jeden lokální engine místo oddělených Services / Task Scheduler / Driver tweakerů",
            runtimePanel,
            expanded: true));

        var scope = new StackPanel { Spacing = 8 };
        scope.Children.Add(BuildRuntimeToggleRow(
            "Služby",
            "Jen Windows systémové služby z bezpečného allowlistu. AI kontroluje závislosti, Windows-owned ImagePath, readback a při opětovném použití službu obnoví.",
            systemConfig.ServicesEnabled,
            enabled =>
            {
                AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
                AiSystemResourceManagerService.SaveConfig(cfg with { ServicesEnabled = enabled });
                FanProfileService.StartAgentIfNeeded();
            }));
        scope.Children.Add(BuildRuntimeToggleRow(
            "Scheduled Tasks",
            "Jen systémové tasky pod Microsoft\\Windows. AI hlídá running state, blízký trigger, observation window a rollback.",
            systemConfig.ScheduledTasksEnabled,
            enabled =>
            {
                AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
                AiSystemResourceManagerService.SaveConfig(cfg with { ScheduledTasksEnabled = enabled });
                FanProfileService.StartAgentIfNeeded();
            }));
        scope.Children.Add(BuildRuntimeToggleRow(
            "Ovladače / zařízení",
            "Jen volitelné Windows legacy drivery bez připojeného zařízení. Storage/network/USB/HID a ne-Windows drivery jsou natvrdo blokované.",
            systemConfig.OptionalDevicesEnabled,
            enabled =>
            {
                AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
                AiSystemResourceManagerService.SaveConfig(cfg with { OptionalDevicesEnabled = enabled });
                FanProfileService.StartAgentIfNeeded();
            }));

        var maintenanceCard = BuildDashboardExpander(
            "Správa Windows",
            "Windows-only • adaptivní scan 2/3/5 min • zásahy jen při idle / velmi nízkém loadu",
            scope,
            expanded: true);

        var actionsHost = new Grid();
        actionsHost.Children.Add(new TextBlock
        {
            Text = "Načítám AI Actions…",
            FontSize = 12,
            Foreground = TextSecondaryBrush(),
            Margin = new Thickness(2, 4, 2, 4)
        });
        SafeTaskService.Forget(
            LoadAiRuntimeActionsDeferredAsync(actionsHost, token),
            "ai-runtime",
            "Deferred AI actions render");

        var cloud = new StackPanel { Spacing = 8 };
        cloud.Children.Add(BuildRuntimeToggleRow(
            "Google Drive cloud profil",
            googleAuth.SignedIn
                ? $"Přihlášeno • {cloudStatus.ItemCount} položek • Drive / Spankyho Optimizer / AI"
                : googleAuth.Configured
                    ? "OAuth připraven • přihlas se přes Google"
                    : "Google přihlášení zatím není v buildu nakonfigurované.",
            systemConfig.CloudSyncEnabled,
            enabled =>
            {
                AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
                AiSystemResourceManagerService.SaveConfig(cfg with { CloudSyncEnabled = enabled });
            }));
        var cloudButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        cloudButtons.Children.Add(PrimaryQuickButton(
            googleAuth.SignedIn ? "Google přihlášen" : "Přihlásit přes Google",
            async () =>
            {
                if (!googleAuth.SignedIn)
                {
                    bool signedIn = await GoogleDriveOAuthService.SignInAsync(token);
                    if (signedIn && AiSystemResourceManagerService.ReadItems().Count == 0)
                        await AiCloudProfileService.RestoreNowAsync(token);
                }
                await RefreshAiRuntimePageAsync();
            }));
        cloudButtons.Children.Add(QuickButton("Odhlásit", () =>
        {
            GoogleDriveOAuthService.SignOut();
            return RefreshAiRuntimePageAsync();
        }));
        cloud.Children.Add(cloudButtons);

        var syncButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        syncButtons.Children.Add(QuickButton("Synchronizovat teď", async () =>
        {
            AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
            await AiCloudProfileService.SyncNowAsync(cfg, AiSystemResourceManagerService.ReadItems(), token);
            await RefreshAiRuntimePageAsync();
        }));
        syncButtons.Children.Add(QuickButton("Načíst cloud paměť", async () =>
        {
            await AiCloudProfileService.RestoreNowAsync(token);
            await RefreshAiRuntimePageAsync();
        }));
        cloud.Children.Add(syncButtons);
        var cloudCard = BuildDashboardExpander(
            "Google Drive",
            cloudStatus.LastSyncUtc is DateTime sync ? $"Poslední sync {sync.ToLocalTime():g}" : "Cloud profil ještě nebyl synchronizovaný",
            cloud,
            expanded: true);

        var self = new StackPanel { Spacing = 8 };
        self.Children.Add(BuildRuntimeToggleRow(
            "Self-optimalizace Optimizeru",
            selfHealth == null
                ? "Měří jen vlastní RuntimeHost/Optimizer a upravuje pouze vlastní polling."
                : $"{selfHealth.State} • CPU {selfHealth.ProcessCpuPercent:0.00}% • RAM {selfHealth.WorkingSetMb:0.0} MB • GC {selfHealth.ManagedMemoryMb:0.0} MB • {selfHealth.Threads} vláken • {selfHealth.Handles} handles • tlak {selfHealth.PressureScore:0.00} • interval {selfHealth.MaintenanceIntervalSeconds}s",
            systemConfig.SelfOptimizationEnabled,
            enabled =>
            {
                AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
                AiSystemResourceManagerService.SaveConfig(cfg with { SelfOptimizationEnabled = enabled });
            }));
        var selfCard = BuildDashboardExpander(
            "Self-optimalizace",
            "Optimalizuje jen vlastní AI runtime a Spankyho Optimizer",
            self,
            expanded: true);

        var safety = new StackPanel { Spacing = 8 };
        safety.Children.Add(new TextBlock
        {
            Text = "Bezpečnostní pravidla",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        safety.Children.Add(new TextBlock
        {
            Text = "Žádné náhodné vypínání. Kritické Windows služby a drivery jsou blokované. Každý automatický zásah bude mít cooldown, readback a rollback. Při chybě se stav vrátí a položka se označí jako nevhodná pro automatiku.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        safety.Children.Add(QuickButton("Reset learned protection", async () =>
        {
            AiSystemResourceManagerService.ResetLearnedProtection();
            await RefreshAiRuntimePageAsync();
        }));
        var safetyCard = BuildDashboardExpander(
            "Safety / rollback",
            "AI nesmí rozbít síť, USB, storage ani boot",
            safety,
            expanded: true);

        var dashboard = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        dashboard.ColumnDefinitions.Add(new ColumnDefinition());
        dashboard.ColumnDefinitions.Add(new ColumnDefinition());
        dashboard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dashboard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumn(maintenanceCard, 0);
        Grid.SetRow(maintenanceCard, 0);
        dashboard.Children.Add(maintenanceCard);

        Grid.SetColumn(cloudCard, 1);
        Grid.SetRow(cloudCard, 0);
        dashboard.Children.Add(cloudCard);

        Grid.SetColumn(selfCard, 0);
        Grid.SetRow(selfCard, 1);
        dashboard.Children.Add(selfCard);

        Grid.SetColumn(safetyCard, 1);
        Grid.SetRow(safetyCard, 1);
        dashboard.Children.Add(safetyCard);

        ContentStack.Children.Add(dashboard);
        ContentStack.Children.Add(actionsHost);

        return Task.CompletedTask;
    }

    private async Task LoadAiVoiceCloudDeferredAsync(
        Grid host,
        CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (_section != "ai-runtime")
            return;

        var perf = System.Diagnostics.Stopwatch.StartNew();
        StackPanel panel = BuildAiVoiceCloudPanel(token);
        long uiMs = perf.ElapsedMilliseconds;

        token.ThrowIfCancellationRequested();
        if (_section != "ai-runtime")
            return;

        host.Children.Clear();
        host.Children.Add(panel);
        LogService.Info(
            "performance",
            $"AI_RUNTIME_VOICE_DEFERRED uiMs={uiMs}");
    }

    private StackPanel BuildAiVoiceCloudPanel(CancellationToken token)
    {
        bool chatConnected = ChatGptPlanAuthService.IsConnected;
        bool hasApiKey = OpenAiVoiceSettingsService.HasApiKey;

        var voiceCloudPanel = new StackPanel { Spacing = 10 };
        voiceCloudPanel.Children.Add(new TextBlock
        {
            Text = "OpenAI GPT-6 Sol",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        voiceCloudPanel.Children.Add(new TextBlock
        {
            Text = "Hlavní mozek Voice AI. OpenAI GPT-6 Sol + cloud STT + lokální systémové nástroje.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });

        var providerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        providerRow.Children.Add(new TextBlock
        {
            Text = "Provider: OpenAI",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        });
        providerRow.Children.Add(new TextBlock
        {
            Text = chatConnected
                ? "Model: podle ChatGPT účtu"
                : "Model: gpt-6-sol (API fallback)",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        });
        voiceCloudPanel.Children.Add(providerRow);

        var apiKeyBox = new PasswordBox
        {
            PlaceholderText = hasApiKey
                ? "API klíč je uložený — vlož nový jen pokud ho chceš změnit"
                : "Vlož OpenAI API klíč",
            PasswordRevealMode = PasswordRevealMode.Peek,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = true,
            IsEnabled = true
        };
        voiceCloudPanel.Children.Add(apiKeyBox);

        var voiceCloudStatus = new TextBlock
        {
            Text = chatConnected
                ? "ChatGPT plán: připojený — používá se před API klíčem"
                : hasApiKey
                    ? "ChatGPT plán: nepřipojený • API klíč: uložený fallback"
                    : "ChatGPT plán: nepřipojený • API klíč: nenastavený",
            FontSize = 12,
            Foreground = chatConnected || hasApiKey
                ? SuccessBrush()
                : WarningBrush()
        };
        voiceCloudPanel.Children.Add(voiceCloudStatus);

        var voiceButtons = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var signInChatGpt = new Button
        {
            Content = chatConnected
                ? "Obnovit ChatGPT přihlášení"
                : "Pokračovat přes ChatGPT",
            MinWidth = 190,
            Height = 34
        };
        signInChatGpt.Click += (_, _) =>
        {
            _ = SignInChatGptAsync(signInChatGpt, voiceCloudStatus, token);
        };
        voiceButtons.Children.Add(signInChatGpt);

        var disconnectChatGpt = new Button
        {
            Content = "Odpojit ChatGPT",
            MinWidth = 135,
            Height = 34,
            IsEnabled = chatConnected
        };
        disconnectChatGpt.Click += (_, _) =>
        {
            ChatGptPlanAuthService.SignOut();
            ChatGptPlanConversationService.Reset();
            bool apiKeyAvailable = OpenAiVoiceSettingsService.HasApiKey;
            voiceCloudStatus.Text = apiKeyAvailable
                ? "ChatGPT plán odpojený • používá se API key fallback."
                : "ChatGPT plán odpojený.";
            voiceCloudStatus.Foreground = apiKeyAvailable
                ? SuccessBrush()
                : WarningBrush();
            disconnectChatGpt.IsEnabled = false;
            signInChatGpt.Content = "Pokračovat přes ChatGPT";
        };
        voiceButtons.Children.Add(disconnectChatGpt);

        var pasteVoiceKey = new Button
        {
            Content = "Vložit ze schránky",
            MinWidth = 150,
            Height = 34,
            IsHitTestVisible = true,
            IsEnabled = true
        };
        pasteVoiceKey.Click += (_, _) =>
        {
            if (!NativeClipboardService.TryGetText(out string clipboardText))
            {
                voiceCloudStatus.Text = "CHYBA: schránku se nepodařilo přečíst.";
                voiceCloudStatus.Foreground = WarningBrush();
                return;
            }

            apiKeyBox.Password = clipboardText;
            voiceCloudStatus.Text = $"KLÍČ NAČTEN ({clipboardText.Length} znaků)";
            voiceCloudStatus.Foreground = SuccessBrush();
        };
        voiceButtons.Children.Add(pasteVoiceKey);

        var saveVoiceKey = new Button
        {
            Content = "Uložit API klíč",
            MinWidth = 145,
            Height = 34,
            IsHitTestVisible = true,
            IsEnabled = true
        };
        saveVoiceKey.Click += (_, _) =>
        {
            string entered = apiKeyBox.Password?.Trim() ?? string.Empty;

            if (entered.Length == 0 &&
                NativeClipboardService.TryGetText(out string clipboardText))
            {
                entered = clipboardText;
                apiKeyBox.Password = clipboardText;
                voiceCloudStatus.Text =
                    $"KLÍČ NAČTEN ZE SCHRÁNKY ({clipboardText.Length} znaků)";
                voiceCloudStatus.Foreground = SuccessBrush();
            }

            if (entered.Length == 0)
            {
                voiceCloudStatus.Text = "CHYBA: API klíč je prázdný.";
                voiceCloudStatus.Foreground = WarningBrush();
                return;
            }

            OpenAiVoiceSettingsService.SaveApiKey(entered);
            apiKeyBox.Password = string.Empty;
            voiceCloudStatus.Text =
                "ULOŽENO: API klíč je šifrovaně uložený.";
            voiceCloudStatus.Foreground = SuccessBrush();
            OpenAiConversationService.Reset();
        };
        voiceButtons.Children.Add(saveVoiceKey);

        var testVoiceKey = new Button
        {
            Content = "Test připojení",
            MinWidth = 135,
            Height = 34,
            IsHitTestVisible = true,
            IsEnabled = true
        };
        testVoiceKey.Click += (_, _) =>
        {
            _ = TestVoiceCloudConnectionAsync(
                apiKeyBox,
                voiceCloudStatus,
                token);
        };
        voiceButtons.Children.Add(testVoiceKey);
        voiceCloudPanel.Children.Add(voiceButtons);

        var voiceCloudDirectHost = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = true
        };
        voiceCloudDirectHost.Children.Add(new TextBlock
        {
            Text = "Voice AI / Cloud Brain",
            FontSize = 14.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = TextPrimaryBrush()
        });
        voiceCloudDirectHost.Children.Add(new TextBlock
        {
            Text = "ChatGPT plán přes bezpečné OAuth přihlášení • OpenAI/Gemini cloud STT • bez lokálního Whisperu",
            FontSize = 12,
            Foreground = _darkTheme
                ? ThemeColor(184, 192, 205)
                : TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        voiceCloudDirectHost.Children.Add(voiceCloudPanel);
        voiceCloudPanel.IsHitTestVisible = true;

        return voiceCloudDirectHost;
    }

    private async Task LoadAiRuntimeActionsDeferredAsync(
        Grid host,
        CancellationToken token)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            AiSystemRuntimeItemState[] allItems =
                AiSystemResourceManagerService.ReadItems().ToArray();
            AiSystemRuntimeItemState[] visibleItems = allItems
                .OrderByDescending(x => x.Managed || x.Sleeping)
                .ThenBy(x => x.Kind)
                .ThenBy(x => x.Id)
                .Take(24)
                .ToArray();

            return (
                AllItems: allItems,
                VisibleItems: visibleItems,
                History: AiSystemResourceManagerService.ReadActionHistory().ToArray());
        }, token);

        token.ThrowIfCancellationRequested();
        if (_section != "ai-runtime")
            return;

        long dataMs = perf.ElapsedMilliseconds;
        var actions = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());

        Border shell = BuildDashboardExpander(
            "AI Actions",
            $"Spravované {snapshot.AllItems.Count(x => x.Managed)} • zastavené {snapshot.AllItems.Count(x => x.Sleeping)} • blokované {snapshot.AllItems.Count(x => !string.IsNullOrWhiteSpace(x.BlockedReason))} • historie {snapshot.History.Length} • zobrazeno max 24",
            actions,
            expanded: true);

        host.Children.Clear();
        host.Children.Add(shell);
        long shellMs = perf.ElapsedMilliseconds - dataMs;

        if (snapshot.VisibleItems.Length == 0)
        {
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            actions.Children.Add(new TextBlock
            {
                Text = "AI zatím neprovedla ani nezablokovala žádný zásah.",
                FontSize = 12,
                Foreground = TextSecondaryBrush()
            });
        }

        const int batchSize = 4;
        int actionIndex = 0;
        long maxBatchUiMs = 0;
        long totalCardUiMs = 0;
        var batchPerf = System.Diagnostics.Stopwatch.StartNew();

        foreach (AiSystemRuntimeItemState item in snapshot.VisibleItems)
        {
            token.ThrowIfCancellationRequested();
            if (_section != "ai-runtime")
                return;

            string status = item.Sleeping
                ? "ZASTAVENO"
                : !string.IsNullOrWhiteSpace(item.BlockedReason)
                    ? "BLOKOVÁNO"
                    : item.Managed ? "SPRAVOVÁNO" : "SLEDOVÁNO";
            string detail = !string.IsNullOrWhiteSpace(item.BlockedReason)
                ? item.BlockedReason
                : item.LastAction;
            if (item.LastChangedUtc is DateTime changed)
                detail += $" • {changed.ToLocalTime():g}";
            if (actionIndex % 2 == 0)
                actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border actionCard = BuildRuntimeScopeRow(
                $"{item.Kind.ToUpperInvariant()} • {item.Id}",
                string.IsNullOrWhiteSpace(detail) ? "Bez zásahu." : detail,
                status);
            Grid.SetColumn(actionCard, actionIndex % 2);
            Grid.SetRow(actionCard, actionIndex / 2);
            actions.Children.Add(actionCard);
            actionIndex++;

            if (actionIndex % batchSize == 0)
            {
                long batchMs = batchPerf.ElapsedMilliseconds;
                totalCardUiMs += batchMs;
                maxBatchUiMs = Math.Max(maxBatchUiMs, batchMs);
                batchPerf.Restart();
                await Task.Yield();
            }
        }

        long tailBatchMs = batchPerf.ElapsedMilliseconds;
        totalCardUiMs += tailBatchMs;
        maxBatchUiMs = Math.Max(maxBatchUiMs, tailBatchMs);

        if (snapshot.History.Length > 0)
        {
            AiSystemActionHistoryEntry latest = snapshot.History[^1];
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var latestText = new TextBlock
            {
                Text = $"Poslední zásah: {latest.Kind.ToUpperInvariant()} • {latest.Id} • {latest.Action} • {latest.TimestampUtc.ToLocalTime():g}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            };
            Grid.SetColumnSpan(latestText, 2);
            Grid.SetRow(latestText, actions.RowDefinitions.Count - 1);
            actions.Children.Add(latestText);
        }

        long totalMs = perf.ElapsedMilliseconds;
        LogService.Info(
            "performance",
            $"AI_RUNTIME_ACTIONS_BATCHED items={snapshot.AllItems.Length} visible={snapshot.VisibleItems.Length} history={snapshot.History.Length} dataMs={dataMs} shellMs={shellMs} cardUiMs={totalCardUiMs} maxBatchUiMs={maxBatchUiMs} totalMs={totalMs}");
    }

    private async Task SignInChatGptAsync(Button signInChatGpt, TextBlock voiceCloudStatus, CancellationToken token)
    {
        signInChatGpt.IsEnabled = false;
        voiceCloudStatus.Text = "Otevírám přihlášení ChatGPT v prohlížeči…";
        voiceCloudStatus.Foreground = TextSecondaryBrush();
        try
        {
            (bool ok, string message) = await ChatGptPlanAuthService.SignInAsync(token);
            voiceCloudStatus.Text = ok ? message + " • AI voice používá ChatGPT plán." : "CHYBA: " + message;
            voiceCloudStatus.Foreground = ok ? SuccessBrush() : WarningBrush();
            if (ok)
            {
                signInChatGpt.Content = "Obnovit ChatGPT přihlášení";
                ChatGptPlanConversationService.Reset();
                FanProfileService.RequestAgentRestart();
                try
                {
                    await VoiceRuntimeBootstrapService.EnsureAsync(null, token);
                    await PiperTtsService.SpeakAsync("ChatGPT je připojený. Hlasový asistent je připravený.", token);
                }
                catch (Exception ex)
                {
                    LogService.Error("voice", "ChatGPT login voice confirmation failed", ex);
                }
            }
        }
        catch (Exception ex)
        {
            voiceCloudStatus.Text = "CHYBA CHATGPT LOGIN: " + ex.Message;
            voiceCloudStatus.Foreground = WarningBrush();
        }
        finally
        {
            signInChatGpt.IsEnabled = true;
        }
    }

    private async Task TestVoiceCloudConnectionAsync(PasswordBox apiKeyBox, TextBlock voiceCloudStatus, CancellationToken token)
    {
        string entered = apiKeyBox.Password?.Trim() ?? string.Empty;
        string keyToTest = entered.Length > 0 ? entered : OpenAiVoiceSettingsService.GetApiKey();

        if (string.IsNullOrWhiteSpace(keyToTest))
        {
            voiceCloudStatus.Text = "CHYBA: není uložený API klíč.";
            voiceCloudStatus.Foreground = WarningBrush();
            return;
        }

        voiceCloudStatus.Text = "TESTUJU PŘIPOJENÍ…";
        var result = await OpenAiConversationService.TestConnectionAsync(keyToTest, token);
        if (result.Ok)
        {
            if (entered.Length > 0)
            {
                OpenAiVoiceSettingsService.SaveApiKey(entered);
                apiKeyBox.Password = string.Empty;
                OpenAiConversationService.Reset();
            }

            voiceCloudStatus.Text = "PŘIPOJENO A ULOŽENO: " + result.Message;
            voiceCloudStatus.Foreground = SuccessBrush();
        }
        else
        {
            voiceCloudStatus.Text = "CHYBA: " + result.Message;
            voiceCloudStatus.Foreground = WarningBrush();
        }
    }

    private Border BuildRuntimeToggleRow(
        string title,
        string description,
        bool initialValue,
        Action<bool> onChanged)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        grid.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsOn = initialValue,
            OnContent = "AUTO",
            OffContent = "OFF",
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (_, _) => onChanged(toggle.IsOn);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid
        };
    }

    private Border BuildRuntimeScopeRow(string title, string description, string badgeText)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        grid.Children.Add(text);

        var badge = Badge(badgeText, badgeText == "GUARDED" ? "SpankyWarningBrush" : "SpankySuccessBrush");
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid
        };
    }
}
