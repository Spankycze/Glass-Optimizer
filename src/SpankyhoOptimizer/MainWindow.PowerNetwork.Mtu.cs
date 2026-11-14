using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void BuildMtuOptimizerPanelCore(StackPanel host, int generation)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        var body = new StackPanel { Spacing = 11 };
        var status = new TextBlock
        {
            Text = "Načítám aktuální MTU…",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        _networkMtuStatus = status;
        body.Children.Add(status);
        var packetHost = new Grid { MinHeight = 62 };
        body.Children.Add(packetHost);
        body.Children.Add(new TextBlock
        {
            Text = "MTU mění pouze velikost packetu na aktivním adaptéru. DNS se touto operací nikdy nemění.",
            FontSize = 10.8,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        var controlsHost = new StackPanel { Spacing = 8 };
        body.Children.Add(controlsHost);

        body.Children.Add(new TextBlock
        {
            Text = "Automatika otestuje více internetových cílů pomocí DF ping, najde nejvyšší stabilní MTU a před změnou ukáže doporučení. VPN, tunely a virtuální adaptéry se ignorují.",
            Foreground = TextSecondaryBrush(),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        });

        host.Children.Add(BuildSectionSurface("network", "MTU Optimizer", "Aktuální stav • automatická detekce • ověření • obnova", body));
        long shellMs = perf.ElapsedMilliseconds;
        LogService.Info("performance", $"MTU_UI_DEFERRED staged=true shellMs={shellMs}");

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration || _section != "network" || !ReferenceEquals(_networkMtuStatus, status) || host.Parent == null || controlsHost.Children.Count > 0)
                return;

            var controlsSw = System.Diagnostics.Stopwatch.StartNew();
            var primary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            primary.Children.Add(PrimaryQuickButton("Najít nejlepší MTU", () => AutoOptimizeMtuAsync(status)));
            primary.Children.Add(QuickButton("Obnovit stav", () => RefreshMtuStatusAsync(status)));
            if (MtuOptimizerService.HasRollbackSnapshot)
                primary.Children.Add(QuickButton("Vrátit původní", () => RestoreOriginalMtuAsync(status)));
            controlsHost.Children.Add(primary);

            var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            presets.Children.Add(new TextBlock { Text = "Rychlé hodnoty:", Foreground = TextSecondaryBrush(), VerticalAlignment = VerticalAlignment.Center });
            foreach (int mtu in new[] { 1500, 1492, 1400 })
            {
                int captured = mtu;
                presets.Children.Add(QuickButton(captured.ToString(), () => ApplyMtuPresetAsync(captured, false, status)));
            }
            controlsHost.Children.Add(presets);

            var custom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var input = new TextBox { Width = 100, PlaceholderText = "Vlastní MTU", Text = "1500" };
            var ipv6 = new CheckBox { Content = "Také IPv6", VerticalAlignment = VerticalAlignment.Center };
            custom.Children.Add(input);
            custom.Children.Add(ipv6);
            custom.Children.Add(QuickButton("Použít", async () =>
            {
                if (!int.TryParse(input.Text, out int value))
                {
                    await ShowMessageAsync("MTU Optimizer", "Zadej platnou číselnou hodnotu MTU.");
                    return;
                }
                await ApplyMtuPresetAsync(value, ipv6.IsChecked == true, status);
            }));
            controlsHost.Children.Add(custom);
            LogService.Info("performance", $"MTU_CONTROLS_DEFERRED ms={controlsSw.ElapsedMilliseconds}");
        });

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration || _section != "network" || !ReferenceEquals(_networkMtuStatus, status) || host.Parent == null || packetHost.Children.Count > 0)
                return;
            var graphicSw = System.Diagnostics.Stopwatch.StartNew();
            packetHost.Children.Add(BuildMtuPacketGraphic());
            LogService.Info("performance", $"MTU_PACKET_GRAPHIC_DEFERRED ms={graphicSw.ElapsedMilliseconds}");
        });

        SafeTaskService.Forget(RefreshMtuStatusAsync(status, generation), "network", "MTU status");
    }

    private async Task RefreshMtuStatusAsync(TextBlock status, int generation = -1)
    {
        if (generation < 0) generation = _navigationGeneration;
        try
        {
            var adapter = await MtuOptimizerService.GetActiveAdapterAsync(_loadCts?.Token ?? _windowLifetimeCts.Token);
            if (_section != "network") return;
            string ipv6Mtu = adapter.Ipv6Mtu > 0 ? adapter.Ipv6Mtu.ToString() : "N/A";
            status.Text = $"{adapter.Name} • {adapter.Description} • IPv4 MTU {adapter.Ipv4Mtu} • IPv6 MTU {ipv6Mtu}" +
                          (string.IsNullOrWhiteSpace(adapter.Gateway) ? "" : $" • Gateway {adapter.Gateway}") +
                          $" • Původní stav: {(MtuOptimizerService.HasRollbackSnapshot ? "uložen" : "zatím neuložen")}";
            status.Foreground = TextSecondaryBrush();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "MTU status read failed", ex);
            status.Text = "MTU nelze načíst: " + ex.Message;
            status.Foreground = WarningBrush();
            ShowActionFeedback("MTU Optimizer", ActionFeedbackKind.Failed, ex.Message);
        }
    }

    private async Task RestoreOriginalMtuAsync(TextBlock status)
    {
        try
        {
            status.Text = "Obnovuji původní MTU a ověřuji výsledek…";
            var after = await MtuOptimizerService.RestoreOriginalAsync(_loadCts?.Token ?? _windowLifetimeCts.Token);
            string ipv6Mtu = after.Ipv6Mtu > 0 ? after.Ipv6Mtu.ToString() : "N/A";
            string detail = $"Původní MTU obnoveno a ověřeno: {after.Name} • IPv4 {after.Ipv4Mtu} • IPv6 {ipv6Mtu}";
            status.Text = detail;
            InvalidateUiDataCache("network-live");
            RefreshNetworkLiveAfterMutation(_loadCts?.Token ?? _windowLifetimeCts.Token);
            ShowActionFeedback("MTU Restore", ActionFeedbackKind.Verified, detail);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "MTU restore failed", ex);
            ShowActionFeedback("MTU Restore", ActionFeedbackKind.Failed, ex.Message);
            await ShowMessageAsync("MTU Optimizer", ex.Message);
            await RefreshMtuStatusAsync(status);
        }
    }

    private async Task AutoOptimizeMtuAsync(TextBlock status)
    {
        try
        {
            status.Text = "Probíhá vícecestný DF path-MTU test (Cloudflare / Google / Quad9)…";
            var result = await MtuOptimizerService.DetectRecommendedAsync(_loadCts?.Token ?? _windowLifetimeCts.Token);
            if (!result.Conclusive)
            {
                status.Text = $"{result.Adapter.Name} • IPv4 MTU {result.CurrentMtu} • {result.Detail}";
                ShowActionFeedback("MTU Auto", ActionFeedbackKind.Unsupported, "DF test nebyl průkazný; MTU zůstalo beze změny.");
                return;
            }

            status.Text = $"{result.Adapter.Name} • Current {result.CurrentMtu} → Recommended {result.RecommendedMtu} • {result.Detail}";
            if (result.RecommendedMtu == result.CurrentMtu)
            {
                ShowActionFeedback("MTU Auto", ActionFeedbackKind.Verified, $"MTU {result.CurrentMtu} je už optimální pro testovanou cestu.");
                return;
            }

            if (!await ShowStyledConfirmAsync(
                "MTU Auto Optimizer",
                $"Adaptér: {result.Adapter.Name}\nAktuální IPv4 MTU: {result.CurrentMtu}\nDoporučené MTU: {result.RecommendedMtu}\nCíl testu: {result.Target}\n\n{result.Detail}",
                "Použít", "Zrušit", "Změna je persistentní a bude ověřena read-backem.")) return;

            await ApplyMtuPresetAsync(result.RecommendedMtu, false, status, skipConfirm: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "MTU auto optimize failed", ex);
            ShowActionFeedback("MTU Auto", ActionFeedbackKind.Failed, ex.Message);
            await ShowMessageAsync("MTU Optimizer", ex.Message);
            await RefreshMtuStatusAsync(status);
        }
    }

    private async Task ApplyMtuPresetAsync(int mtu, bool applyIpv6, TextBlock status, bool skipConfirm = false)
    {
        if (mtu < 576 || mtu > 9000)
        {
            await ShowMessageAsync("MTU Optimizer", "Povolený rozsah je 576–9000. IPv6 vyžaduje minimálně 1280.");
            return;
        }
        if (applyIpv6 && mtu < 1280)
        {
            await ShowMessageAsync("MTU Optimizer", "IPv6 MTU nesmí být nižší než 1280.");
            return;
        }

        if (!skipConfirm && !await ShowStyledConfirmAsync(
            "Změnit MTU?",
            $"Nastavit IPv4 MTU na {mtu}" + (applyIpv6 ? " a stejnou hodnotu také pro IPv6." : "."),
            "Použít", "Zrušit", "Aktivní fyzický adaptér • trvalá změna • ověření")) return;

        try
        {
            status.Text = $"Aplikuji MTU {mtu}…";
            var after = await MtuOptimizerService.ApplyAsync(mtu, applyIpv6, _loadCts?.Token ?? _windowLifetimeCts.Token);
            string ipv6Mtu = after.Ipv6Mtu > 0 ? after.Ipv6Mtu.ToString() : "N/A";
            string detail = after.Ipv4Mtu == mtu
                ? $"Ověřeno: {after.Name} • IPv4 MTU {after.Ipv4Mtu} • IPv6 MTU {ipv6Mtu}"
                : $"Ověřeno: {after.Name} • požadováno IPv4 MTU {mtu} → Windows/driver přijal {after.Ipv4Mtu} • IPv6 MTU {ipv6Mtu}";
            status.Text = detail;
            InvalidateUiDataCache("network-live");
            RefreshNetworkLiveAfterMutation(_loadCts?.Token ?? _windowLifetimeCts.Token);
            ShowActionFeedback("MTU", ActionFeedbackKind.Verified, detail);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "MTU apply failed", ex);
            string rollbackDetail = "Změna selhala; původní MTU bylo automaticky obnoveno. " + ex.Message;
            status.Text = rollbackDetail;
            ShowActionFeedback("MTU", ActionFeedbackKind.Failed, rollbackDetail);
            await ShowMessageAsync("MTU Optimizer", rollbackDetail);
            await RefreshMtuStatusAsync(status);
        }
    }

    internal async Task RunMtuReadbackSmokeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result = await MtuOptimizerService.RunReadbackSmokeAsync();
        sw.Stop();
        LogService.Info($"MTU_READBACK_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} detail={result}");
    }

    internal async Task RunMtuProbeSmokeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await MtuOptimizerService.DetectRecommendedAsync();
        sw.Stop();
        if (result.CurrentMtu <= 0 || result.RecommendedMtu < 576 || result.RecommendedMtu > 1500)
            throw new InvalidOperationException($"MTU probe vrátil neplatná data: current={result.CurrentMtu} recommended={result.RecommendedMtu}.");
        LogService.Info("network", $"MTU_PROBE_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} adapter={result.Adapter.Name} current={result.CurrentMtu} recommended={result.RecommendedMtu} target={result.Target} conclusive={result.Conclusive}");
    }

    private void ScheduleNetworkAdvancedControls(StackPanel tcp)
    {
        if (tcp.Children.Count >= 3) return;
        if (_networkAdvancedLoadQueued)
            return;

        int generation = _networkAdvancedLoadGeneration;
        _networkAdvancedLoadQueued = true;
        tcp.Children.Clear();
        tcp.Children.Add(new TextBlock { Text = "Načítám capability aktivního NIC driveru…", Foreground = TextSecondaryBrush() });

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_section != "network" || generation != _networkAdvancedLoadGeneration)
            {
                if (generation == _networkAdvancedLoadGeneration)
                    _networkAdvancedLoadQueued = false;
                return;
            }

            SafeTaskService.Forget(
                LoadNetworkAdvancedControlsTrackedAsync(tcp, _loadCts?.Token ?? _windowLifetimeCts.Token, generation),
                "network",
                "NIC capability controls");
        })) _networkAdvancedLoadQueued = false;
    }

    private async Task LoadNetworkAdvancedControlsTrackedAsync(StackPanel tcp, CancellationToken token, int generation)
    {
        try
        {
            await LoadNetworkAdvancedControlsAsync(tcp, token, generation);
        }
        finally
        {
            if (generation == _networkAdvancedLoadGeneration)
                _networkAdvancedLoadQueued = false;
        }
    }

    private async Task LoadNetworkAdvancedControlsAsync(StackPanel tcp, CancellationToken token, int generation)
    {
        var phase = Stopwatch.StartNew();
        try
        {
            // The nominally async capability service performs a synchronous
            // NIC route/adapter identity lookup and persisted-cache file read
            // before its first await. Keep those cold operations off the
            // WinUI dispatcher; their shared cache is already lock-protected.
            var capsTask = Task.Run(
                () => PerformanceProfilesService.GetNicCapabilitiesAsync(token), token);
            // This is read-only UI presentation, not an optimization decision.
            // Reuse the verified TCP snapshot and refresh stale values off-thread
            // rather than starting netsh.exe on every Network navigation after
            // its 45-second live TTL. Mutations explicitly invalidate the cache.
            // A first UI snapshot may synchronously read the persistent
            // netsh result from disk, so keep this pre-await work off UI too.
            var autoTuningTask = Task.Run(
                () => TcpGlobalStateService.GetValueForUiAsync("autotuninglevel", token), token);
            await Task.WhenAll(capsTask, autoTuningTask);
            var caps = await capsTask;
            string autoTuning = await autoTuningTask ?? "Unknown";
            if (token.IsCancellationRequested || generation != _networkAdvancedLoadGeneration || _section != "network" || !ReferenceEquals(_networkAdvancedBody, tcp)) return;
            QueueNetworkAdvancedCapabilityUiBatches(tcp, caps, autoTuning, phase);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("network", "NIC capability controls failed", ex);
            if (_section == "network" && generation == _networkAdvancedLoadGeneration)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_section != "network" || generation != _networkAdvancedLoadGeneration || !ReferenceEquals(_networkAdvancedBody, tcp)) return;
                    tcp.Children.Clear();
                    tcp.Children.Add(new TextBlock
                    {
                        Text = "NIC capability se nepodařilo načíst: " + ex.Message,
                        Foreground = UiBrush("SpankyWarningBrush"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    tcp.Children.Add(QuickButton("Načíst znovu", () =>
                    {
                        _networkAdvancedLoadGeneration++;
                        _networkAdvancedLoadQueued = false;
                        tcp.Children.Clear();
                        ScheduleNetworkAdvancedControls(tcp);
                        return Task.CompletedTask;
                    }));
                    ShowActionFeedback("Síťová karta", ActionFeedbackKind.Failed, ex.Message);
                });
        }
    }

    private void QueueNetworkAdvancedCapabilityUiBatches(
        StackPanel tcp,
        IReadOnlyDictionary<string, string> caps,
        string autoTuning,
        Stopwatch phase)
    {
        int generation = _networkAdvancedLoadGeneration;
        tcp.Children.Clear();

        void QueueBatch1()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section != "network" || generation != _networkAdvancedLoadGeneration) return;
                var sw = Stopwatch.StartNew();

                AddNicAdvancedCapabilityCard(tcp, caps, "Energy Efficient Ethernet",
                    "Úsporné uspávání linky může přidat wake latency na desktopu.",
                    new[] { "Energy-Efficient Ethernet", "Energy Efficient Ethernet", "EEE", "Advanced EEE" },
                    recommendedOn: false);
                AddNicAdvancedCapabilityCard(tcp, caps, "Green Ethernet",
                    "Driverové šetření linky není potřeba pro desktopový low-latency profil.",
                    new[] { "Green Ethernet", "Green Ethernet Lite", "EnableGreenEthernet" },
                    recommendedOn: false);
                AddNicAdvancedCapabilityCard(tcp, caps, "Power Saving Mode",
                    "NIC power saving může přepínat adaptér do úsporných stavů při nečinnosti.",
                    new[] { "Power Saving Mode", "System Idle Power Saver", "Ultra Low Power Mode", "PowerSavingMode" },
                    recommendedOn: false);
                AddNicAdvancedCapabilityCard(tcp, caps, "Interrupt Moderation",
                    "Nižší batching může pomoci latenci, ale zvýšit CPU load; výsledek závisí na driveru a síti.",
                    new[] { "Interrupt Moderation", "InterruptModeration" },
                    allowAbTest: true);

                LogService.Info("performance", $"NETWORK_ADVANCED_UI_BATCH batch=1 ms={sw.ElapsedMilliseconds}");
                QueueBatch2();
            });
        }

        void QueueBatch2()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section != "network" || generation != _networkAdvancedLoadGeneration) return;
                var sw = Stopwatch.StartNew();

                AddNicAdvancedCapabilityCard(tcp, caps, "Flow Control",
                    "Při zahlcení může být výhodné ON i OFF; Optimizer proto rozhodne jen měřením.",
                    new[] { "Flow Control", "FlowControl" },
                    allowAbTest: true);
                AddNicAdvancedCapabilityCard(tcp, caps, "Packet Coalescing",
                    "Coalescing snižuje počet interruptů, ale může měnit latency/CPU trade-off.",
                    new[] { "Packet Coalescing", "DMA Coalescing", "PacketCoalescing", "DMACoalescing" },
                    allowAbTest: true);
                AddNicAdvancedCapabilityCard(tcp, caps, "LSO IPv4",
                    "Large Send Offload může zlepšit throughput nebo naopak latency podle NIC/driveru.",
                    new[] { "Large Send Offload v2 (IPv4)", "Large Send Offload V2 (IPv4)", "*LsoV2IPv4" },
                    allowAbTest: true);
                AddNicAdvancedCapabilityCard(tcp, caps, "LSO IPv6",
                    "IPv6 LSO se netuní naslepo; změna se má nechat pouze pokud A/B měření vyjde lépe.",
                    new[] { "Large Send Offload v2 (IPv6)", "Large Send Offload V2 (IPv6)", "*LsoV2IPv6" },
                    allowAbTest: true);

                LogService.Info("performance", $"NETWORK_ADVANCED_UI_BATCH batch=2 ms={sw.ElapsedMilliseconds}");
                QueueBatch3();
            });
        }

        void QueueBatch3()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section != "network" || generation != _networkAdvancedLoadGeneration) return;
                var sw = Stopwatch.StartNew();

                string selective = CapabilityValue(caps, "Selective Suspend");
                AddNicValueAdvisor(tcp, "Selective Suspend", selective, "Disabled (desktop)",
                    "Síťový adaptér nebude uspáván během nečinnosti.",
                    selective.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? OptimizationAdvisorKind.Ok : OptimizationAdvisorKind.Recommended);

                LogService.Info("performance", $"NETWORK_ADVANCED_UI_BATCH batch=3 ms={sw.ElapsedMilliseconds}");
                QueueBatch4();
            });
        }

        void QueueBatch4()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_section != "network" || generation != _networkAdvancedLoadGeneration) return;
                var sw = Stopwatch.StartNew();

                AddNicValueAdvisor(tcp, "TCP Auto-Tuning", autoTuning, "Normal",
                    "Windows receive-window autotuning má zůstat v Normal; Disabled může omezit throughput.",
                    autoTuning.Equals("normal", StringComparison.OrdinalIgnoreCase) ? OptimizationAdvisorKind.Ok : OptimizationAdvisorKind.Recommended);

                string selective = CapabilityValue(caps, "Selective Suspend");
                LogService.Info("performance", $"NETWORK_ADVANCED_UI_BATCH batch=4 ms={sw.ElapsedMilliseconds}");
                LogService.Info("performance",
                    $"NETWORK_ADVANCED_CAPABILITY totalMs={phase.ElapsedMilliseconds} uiBatched=true interrupt={CapabilityValue(caps, "Interrupt Moderation")} flow={CapabilityValue(caps, "Flow Control")} packet={CapabilityValue(caps, "Packet Coalescing")} selective={selective} autotuning={autoTuning}");
            });
        }

        QueueBatch1();
    }

    private static string CapabilityValue(IReadOnlyDictionary<string, string> caps, string key) =>
        caps.TryGetValue(key, out var value) ? value : "Unsupported";
}
