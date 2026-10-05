using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void ScheduleSystemOptimizationAdvisors(StackPanel root)
    {
        CancellationToken token = _loadCts?.Token ?? CancellationToken.None;
        SafeTaskService.Forget(
            ScheduleSystemOptimizationAdvisorsAfterFirstPaintAsync(root, token),
            "system",
            "Deferred System optimization advisor shell");
    }

    private async Task ScheduleSystemOptimizationAdvisorsAfterFirstPaintAsync(
        StackPanel root,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "system" || root.Parent == null) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AttachSystemOptimizationAdvisors(root);
                LogService.Info("performance", $"SYSTEM_ADVISOR_SHELL_DEFERRED ms={sw.ElapsedMilliseconds}");
            });
        }
        catch (OperationCanceledException) { }
    }

    private void AttachSystemOptimizationAdvisors(StackPanel root)
    {
        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(new TextBlock
        {
            Text = "Načítám stav paměti, zařízení a plánování…",
            Foreground = TextSecondaryBrush()
        });

        root.Children.Add(BuildDashboardExpander(
            "Adaptivní optimalizace",
            "Paměť • přerušení zařízení • plánování Windows • pouze měřitelné hodnoty",
            host));

        CancellationToken token = _loadCts?.Token ?? CancellationToken.None;
        SafeTaskService.Forget(
            LoadSystemOptimizationAdvisorsAsync(host, token),
            "system",
            "System optimization advisors");
    }

    private async Task LoadSystemOptimizationAdvisorsAsync(StackPanel host, CancellationToken token)
    {
        try
        {
            var memoryTask = MemoryStateService.ReadAsync(token);
            var interruptTask = InterruptLatencyService.ReadAsync(token);
            var multimediaTask = LegacyMultimediaTuningService.ReadAsync(token);
            await Task.WhenAll(memoryTask, interruptTask, multimediaTask).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var memory = await memoryTask.ConfigureAwait(false);
            var interrupts = await interruptTask.ConfigureAwait(false);
            var multimedia = await multimediaTask.ConfigureAwait(false);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _section != "system") return;
                host.Children.Clear();
                RenderMemoryOptimizationAdvisor(host, memory);
                RenderInterruptOptimizationAdvisor(host, interrupts);
                RenderLegacyMultimediaAdvisor(host, multimedia);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("system", "System optimization advisors failed", ex);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "system") return;
                host.Children.Clear();
                host.Children.Add(new TextBlock
                {
                    Text = "Aktuální stav optimalizací se nepodařilo načíst: " + ex.Message,
                    Foreground = WarningBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
                host.Children.Add(QuickButton("Načíst znovu", () =>
                    LoadSystemOptimizationAdvisorsAsync(host, _loadCts?.Token ?? CancellationToken.None)));
                ShowActionFeedback("Systémové optimalizace", ActionFeedbackKind.Failed, ex.Message);
            });
        }
    }

    private void RenderInterruptOptimizationAdvisor(
        StackPanel host,
        System.Collections.Generic.IReadOnlyList<DeviceInterruptState> devices)
    {
        host.Children.Add(BuildFlatSectionHeader(
            "Interrupt / Device Latency",
            "GPU • NIC • NVMe • USB řadiče — MSI/MSI-X a přiřazení CPU"));

        if (devices.Count == 0)
        {
            host.Children.Add(BuildOptimizationAdvisorRow(
                "Interrupt inventory",
                "Žádná podporovaná zařízení",
                "INFO",
                "Optimizer nic nevynucuje, pokud Windows/driver interrupt metadata nevystavuje.",
                OptimizationAdvisorKind.Info));
            return;
        }

        foreach (var device in devices)
        {
            string msi = device.MsiSupported switch
            {
                true => "MSI/MSI-X enabled",
                false => "Legacy / MSI disabled",
                _ => "Not exposed"
            };
            var msiKind = device.MsiSupported == true
                ? OptimizationAdvisorKind.Ok
                : OptimizationAdvisorKind.Info;

            host.Children.Add(BuildOptimizationAdvisorRow(
                $"{device.Category} • {device.Name}",
                msi,
                "Driver-supported MSI/MSI-X",
                "MSI se nemá násilně zapínat u zařízení, které ho ovladač nepodporuje; důležitější je skutečný stav než přednastavená hodnota.",
                msiKind));

            string affinityCurrent = device.HasExplicitCpuMask
                ? $"{device.PolicyText} • mask {device.CpuMaskText}"
                : device.PolicyText;
            Func<Task>? affinityAction = null;
            string affinityActionText = "A/B test";
            bool hasAffinityBackup =
                device.Category.Equals("NIC", StringComparison.OrdinalIgnoreCase) &&
                InterruptLatencyService.HasAffinityBackup(device.InstanceId);
            if (device.Category.Equals("NIC", StringComparison.OrdinalIgnoreCase) && !hasAffinityBackup)
            {
                affinityAction = async () =>
                {
                    if (!await ShowStyledConfirmAsync(
                        "A/B test rozložení přerušení síťovky",
                        "Optimizer změří výchozí stav, rozloží přerušení síťovky mezi procesory, restartuje adaptér a znovu změří latenci, jitter a ztrátovost. Horší výsledek se automaticky vrátí.",
                        "Spustit test", "Zrušit", "Síť může na několik sekund vypadnout.")) return;
                    try
                    {
                        var result = await InterruptLatencyService.RunNicAffinityExperimentAsync(
                            device, _loadCts?.Token ?? CancellationToken.None);
                        ShowActionFeedback("A/B rozložení síťovky", ActionFeedbackKind.Verified, result.Message);
                        if (_section == "system")
                        {
                            host.Children.Clear();
                            host.Children.Add(new TextBlock { Text = "Obnovuji aktuální stav přerušení…", Foreground = TextSecondaryBrush() });
                            await LoadSystemOptimizationAdvisorsAsync(host, _loadCts?.Token ?? CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("interrupt", "NIC affinity A/B failed", ex);
                        ShowActionFeedback("A/B rozložení síťovky", ActionFeedbackKind.Failed, ex.Message);
                    }
                };
            }

            host.Children.Add(BuildOptimizationAdvisorRow(
                $"{device.Category} rozložení",
                affinityCurrent,
                device.Category.Equals("NIC", StringComparison.OrdinalIgnoreCase)
                    ? hasAffinityBackup ? "TEST aktivní • návrat uložen" : "TEST / A/B"
                    : device.HasExplicitCpuMask ? "TEST / ověřit benchmarkem" : "Výchozí Windows / ovladač",
                device.HasExplicitCpuMask
                    ? "Explicitní CPU maska může být výhodná i škodlivá; bez měření ji Optimizer automaticky nepřepisuje."
                    : device.Category.Equals("NIC", StringComparison.OrdinalIgnoreCase)
                        ? "Síťovku lze ručně otestovat rozložením přerušení mezi procesory; změna se ponechá jen při lepším měření."
                        : "Bez explicitní masky mohou Windows a ovladač rozdělovat zátěž přerušení podle topologie CPU.",
                device.Category.Equals("NIC", StringComparison.OrdinalIgnoreCase) || device.HasExplicitCpuMask
                    ? OptimizationAdvisorKind.Test
                    : OptimizationAdvisorKind.Info,
                affinityAction,
                affinityActionText));

            if (device.Category.Equals("NIC", StringComparison.OrdinalIgnoreCase) &&
                InterruptLatencyService.HasAffinityBackup(device.InstanceId))
            {
                host.Children.Add(QuickButton("Vrátit původní rozložení síťovky", async () =>
                {
                    try
                    {
                        await InterruptLatencyService.RestoreNicAffinityAsync(
                            device.InstanceId, _loadCts?.Token ?? CancellationToken.None);
                        ShowActionFeedback("Rozložení síťovky", ActionFeedbackKind.Verified,
                            "Původní nastavení rozložení bylo obnoveno a síťový adaptér restartován.");
                        if (_section == "system")
                        {
                            host.Children.Clear();
                            host.Children.Add(new TextBlock { Text = "Obnovuji aktuální stav přerušení…", Foreground = TextSecondaryBrush() });
                            await LoadSystemOptimizationAdvisorsAsync(host, _loadCts?.Token ?? CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("interrupt", "NIC affinity restore failed", ex);
                        ShowActionFeedback("NIC affinity restore", ActionFeedbackKind.Failed, ex.Message);
                    }
                }));
            }
        }
    }

    private void RenderLegacyMultimediaAdvisor(StackPanel host, LegacyMultimediaSnapshot state)
    {
        host.Children.Add(BuildFlatSectionHeader(
            "Scheduler / Multimedia — Legacy / Experimental",
            "Kontrola starších MMCSS hodnot; nejsou součástí Recommended"));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "SystemResponsiveness",
            state.SystemResponsiveness,
            "Windows default / TEST",
            "Hodnota ovlivňuje rezervaci MMCSS a není univerzální FPS/latency tweak.",
            OptimizationAdvisorKind.Test));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "NetworkThrottlingIndex",
            state.NetworkThrottlingIndex,
            "Windows default / TEST",
            "FFFFFFFF není automaticky rychlejší; síťová latence se má hodnotit reálným A/B měřením.",
            OptimizationAdvisorKind.Test));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "MMCSS Games task",
            $"GPU {state.GamesGpuPriority} • Priority {state.GamesPriority}",
            "Windows / driver profile",
            $"Scheduling={state.GamesSchedulingCategory} • SFIO={state.GamesSfioPriority}. Optimizer tyto hodnoty nebude slepě přepisovat.",
            OptimizationAdvisorKind.Info));
    }

    private void RenderMemoryOptimizationAdvisor(StackPanel host, MemoryStateSnapshot state)
    {
        host.Children.Add(BuildFlatSectionHeader(
            "Memory / Standby",
            "Recommended ponechává moderní správu paměti Windows; žádný periodický standby-list cleaner"));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "Memory Compression",
            state.CompressionEnabled ? "ON" : "OFF",
            "ON",
            "Snižuje tlak na pagefile bez agresivního ručního čištění RAM.",
            state.CompressionEnabled ? OptimizationAdvisorKind.Ok : OptimizationAdvisorKind.Recommended));

        string pagefileCurrent = state.AutomaticManagedPagefile switch
        {
            true => "System managed",
            false => "Manual / disabled",
            _ => "Nezjištěno"
        };
        host.Children.Add(BuildOptimizationAdvisorRow(
            "Pagefile",
            pagefileCurrent,
            "System managed",
            "Windows může dynamicky upravit commit limit; pevné/minimální pagefile profily nejsou součást Recommended.",
            state.AutomaticManagedPagefile == true
                ? OptimizationAdvisorKind.Ok
                : state.AutomaticManagedPagefile == false
                    ? OptimizationAdvisorKind.Recommended
                    : OptimizationAdvisorKind.Info));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "LargeSystemCache",
            state.LargeSystemCache.ToString(),
            "0 / Windows desktop default",
            "Desktop/gaming PC nemá být přepnutý do serverového file-cache režimu.",
            state.LargeSystemCache == 0 ? OptimizationAdvisorKind.Ok : OptimizationAdvisorKind.Recommended));

        host.Children.Add(BuildOptimizationAdvisorRow(
            "Foreground scheduler",
            $"Win32PrioritySeparation = {state.Win32PrioritySeparation}",
            "Windows default / neměnit",
            "Starší MMCSS a scheduler hodnoty nejsou univerzální latency tweak; Optimizer je nebude slepě přepisovat.",
            OptimizationAdvisorKind.Info));

        bool needsApply = !state.CompressionEnabled ||
                          state.AutomaticManagedPagefile == false ||
                          state.LargeSystemCache != 0;
        if (needsApply)
        {
            host.Children.Add(PrimaryQuickButton("Použít Memory Recommended", async () =>
            {
                try
                {
                    await MemoryStateService.ApplyRecommendedAsync(_loadCts?.Token ?? CancellationToken.None);
                    MemoryStateService.Invalidate();
                    var refreshed = await MemoryStateService.ReadAsync(_loadCts?.Token ?? CancellationToken.None);
                    if (_section != "system") return;
                    host.Children.Clear();
                    RenderMemoryOptimizationAdvisor(host, refreshed);
                    ShowActionFeedback("Memory Recommended", ActionFeedbackKind.Verified,
                        "Compression ON • Pagefile System managed • LargeSystemCache 0; stav ověřen.");
                }
                catch (Exception ex)
                {
                    LogService.Error("memory", "Memory Recommended failed", ex);
                    ShowActionFeedback("Memory Recommended", ActionFeedbackKind.Failed, ex.Message);
                    await ShowMessageAsync("Memory Recommended", ex.Message);
                }
            }));
        }
    }
}
