using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Border VisualPanel(string glyph, string title, string subtitle, UIElement content)
    {
        return BuildSectionSurface(string.IsNullOrWhiteSpace(_themeSection) ? "overview" : _themeSection, title, subtitle, content);
    }




    private void BuildStorageSnapshot(CancellationToken token)
    {
        _storageSnapshotBody = null;
        _storageSnapshotReady = false;
        _storageSnapshotLoadGeneration = 0;
        _storageSnapshotShellHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(_storageSnapshotShellHost);
        ScheduleStorageSnapshotShell(token);
    }

    private void ScheduleStorageSnapshotShell(CancellationToken token)
    {
        int generation = _navigationGeneration;
        var host = _storageSnapshotShellHost;
        if (host != null && _storageSnapshotBody != null)
        {
            StartStorageSnapshotLoad(_storageSnapshotBody, token);
            return;
        }
        if (_storageSnapshotShellQueuedGeneration == generation || host == null || host.Children.Count > 0) return;
        _storageSnapshotShellQueuedGeneration = generation;
        void ReleaseQueueTicket()
        {
            if (_storageSnapshotShellQueuedGeneration == generation)
                _storageSnapshotShellQueuedGeneration = 0;
        }
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseQueueTicket();
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !ReferenceEquals(_storageSnapshotShellHost, host) ||
                host.Children.Count > 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock { Text = "Načítám svazky…", Foreground = TextSecondaryBrush() });
            host.Children.Add(BuildSectionSurface("storage", "Disky", "DriveInfo snapshot", stack));
            LogService.Info("performance", $"STORAGE_SNAPSHOT_SHELL_DEFERRED ms={sw.ElapsedMilliseconds}");
            _storageSnapshotBody = stack;
            StartStorageSnapshotLoad(stack, token);
            SchedulePageScrollPolicyRefresh();
        }))
            ReleaseQueueTicket();
    }

    private void StartStorageSnapshotLoad(StackPanel body, CancellationToken token)
    {
        int generation = _navigationGeneration;
        if (_storageSnapshotReady || _storageSnapshotLoadGeneration == generation) return;
        _storageSnapshotLoadGeneration = generation;
        SafeTaskService.Forget(LoadStorageSnapshotAsync(body, token, generation), "storage", "DriveInfo snapshot");
    }

    private Task? _storageSnapshotSmokeGate;
    private TaskCompletionSource<bool>? _storageSnapshotSmokeEntered;
    private TaskCompletionSource<bool>? _storageSnapshotSmokeFinished;
    private StackPanel? _storageSnapshotSmokeBody;

    private async Task LoadStorageSnapshotAsync(StackPanel host, CancellationToken token, int generation)
    {
        var smokeGate = _storageSnapshotSmokeGate;
        var smokeEntered = _storageSnapshotSmokeEntered;
        var smokeFinished = _storageSnapshotSmokeFinished;
        if (smokeGate != null) _storageSnapshotSmokeBody = host;
        try
        {
            var rows = await Task.Run(() => DriveInfo.GetDrives().Select(d =>
            {
                try
                {
                    if (!d.IsReady) return (Ready: false, Text: string.Empty);
                    return (Ready: true, Text: $"{d.Name}  •  {d.DriveFormat}  •  {d.VolumeLabel}\nVolno {d.AvailableFreeSpace / 1073741824} GB z {d.TotalSize / 1073741824} GB");
                }
                catch { return (Ready: false, Text: string.Empty); }
            }).Where(x => x.Ready).Select(x => x.Text).ToArray(), CancellationToken.None).ConfigureAwait(false);
            if (smokeGate != null)
            {
                smokeEntered!.TrySetResult(true);
                await smokeGate.ConfigureAwait(false);
            }
            if (token.IsCancellationRequested || _section != "storage") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration ||
                    _section != "storage" || !ReferenceEquals(_storageSnapshotBody, host)) return;
                var commit = System.Diagnostics.Stopwatch.StartNew();
                host.Children.Clear();
                if (rows.Length == 0)
                    host.Children.Add(new TextBlock { Text = "Nebyl nalezen žádný připravený svazek.", Foreground = TextSecondaryBrush() });
                else
                    foreach (string row in rows) host.Children.Add(new TextBlock { Text = row, FontSize = 12 });
                _storageSnapshotReady = true;
                SchedulePageScrollPolicyRefresh();
                LogService.Info("performance", $"STORAGE_SNAPSHOT_DATA_READY rows={rows.Length} commitMs={commit.ElapsedMilliseconds} generation={generation}");
            });
        }
        catch (Exception ex) { LogService.Error("storage", "DriveInfo snapshot failed", ex); }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_storageSnapshotLoadGeneration == generation && ReferenceEquals(_storageSnapshotBody, host))
                    _storageSnapshotLoadGeneration = 0;
            });
            smokeFinished?.TrySetResult(true);
        }
    }
    private void BuildActivityPage()
    {
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var perf = PerformanceTelemetryService.GetSnapshot();
        long snapshotMs = phase.ElapsedMilliseconds;

        var diagnostics = new StackPanel { Spacing = 6 };
        diagnostics.Children.Add(new TextBlock { Text = $"Working Set: {perf.WorkingSetMb} MB   •   Managed: {perf.ManagedMb} MB   •   Handles: {perf.Handles}", FontSize = 13, TextWrapping = TextWrapping.Wrap });
        diagnostics.Children.Add(new TextBlock { Text = $"GC Gen0/1/2: {perf.Gen0}/{perf.Gen1}/{perf.Gen2}   •   Startup stage: {StartupDiagnostics.LastStage}", FontSize = 12, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        long diagnosticsMs = phase.ElapsedMilliseconds - snapshotMs;

        ContentStack.Children.Add(BuildSectionHero("activity", "Live telemetry", "Performance Diagnostics", "Aktuální procesní telemetrie Optimizeru", QuickButton("Obnovit", async () => await ShowSectionAsync("activity"))));
        long heroMs = phase.ElapsedMilliseconds - snapshotMs - diagnosticsMs;
        LogService.Info("perf", $"MEM area=activity ws={perf.WorkingSetMb}MB private={perf.PrivateMb}MB managed={perf.ManagedMb}MB handles={perf.Handles} gc={perf.Gen0}/{perf.Gen1}/{perf.Gen2}");

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Načítám běžící procesy…", Foreground = TextSecondaryBrush() });
        ContentStack.Children.Add(BuildSectionSurface("activity", "Aktivita systému", "Největší běžící procesy podle využití paměti", stack));
        ContentStack.Children.Add(ActionCard("Monitor prostředků", "Otevře vestavěný Windows Resource Monitor.", "Otevřít", delegate { ProcessRunner.Open("resmon.exe"); return Task.CompletedTask; }));
        long shellMs = phase.ElapsedMilliseconds - snapshotMs - diagnosticsMs - heroMs;
        SafeTaskService.Forget(LoadActivityProcessesAsync(stack, _loadCts?.Token ?? CancellationToken.None), "activity", "Process inventory");
        LogService.Info("performance", $"ACTIVITY_UI_PHASE snapshotMs={snapshotMs} diagnosticsMs={diagnosticsMs} heroMs={heroMs} shellMs={shellMs} totalMs={phase.ElapsedMilliseconds}");
    }


    private async Task LoadActivityProcessesAsync(StackPanel host, CancellationToken token)
    {
        try
        {
            var rows = await Task.Run(() =>
            {
                var processes = System.Diagnostics.Process.GetProcesses();
                try
                {
                    return processes.Select(p =>
                    {
                        try { return (Name: p.ProcessName, Id: p.Id, Mb: p.WorkingSet64 / 1024 / 1024); }
                        catch { return (Name: string.Empty, Id: 0, Mb: 0L); }
                    }).Where(x => x.Id != 0).OrderByDescending(x => x.Mb).Take(30).ToArray();
                }
                finally { foreach (var proc in processes) proc.Dispose(); }
            }, CancellationToken.None).ConfigureAwait(false);
            if (token.IsCancellationRequested || _section != "activity") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "activity") return;
                host.Children.Clear();
                foreach (var row in rows) host.Children.Add(new TextBlock { Text = $"{row.Name}  •  PID {row.Id}  •  {row.Mb} MB", FontSize = 12 });
            });
        }
        catch (Exception ex) { LogService.Error("activity", "Process inventory failed", ex); }
    }

}
