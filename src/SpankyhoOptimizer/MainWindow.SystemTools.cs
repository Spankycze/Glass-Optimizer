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
            }).Where(x => x.Ready).Select(x => x.Text).ToArray(), token).ConfigureAwait(false);
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
        // Navigating away cancels this snapshot; that is expected control flow,
        // not a storage failure, and must not be logged as an error.
        catch (OperationCanceledException) when (token.IsCancellationRequested || _windowLifetimeCts.IsCancellationRequested) { }
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
    private StackPanel? _activityProcessRowsHost;
    private Border? _activityProcessSurface;

    private void BuildActivityPage()
    {
        ContentStack.Children.Add(BuildSectionHero("activity", "Aktivita systému", "Běžící procesy", "Přehled procesů podle využití paměti", QuickButton("Obnovit", async () => { ActivityProcessSnapshotService.Invalidate(); await ShowSectionAsync("activity"); })));

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Načítám běžící procesy…", Foreground = TextSecondaryBrush() });
        _activityProcessRowsHost = stack;
        _activityProcessSurface = BuildSectionSurface("activity", "Aktivita systému", "Největší běžící procesy podle využití paměti", stack);
        ContentStack.Children.Add(_activityProcessSurface);
        ContentStack.Children.Add(ActionCard("Monitor prostředků", "Otevře vestavěný Windows Resource Monitor.", "Otevřít", delegate { ProcessRunner.Open("resmon.exe"); return Task.CompletedTask; }));
        SafeTaskService.Forget(LoadActivityProcessesAsync(stack, _loadCts?.Token ?? _windowLifetimeCts.Token), "activity", "Process inventory");
    }


    internal async Task RunActivityUiPopulationSmokeAsync()
    {
        await ShowSectionAsync("activity");
        var firstHost = _activityProcessRowsHost;
        var firstSurface = _activityProcessSurface;
        if (firstHost == null || firstSurface == null ||
            !ContentStack.Children.Contains(firstSurface))
            throw new InvalidOperationException("Activity process card is not attached.");

        static bool HasProcessRows(StackPanel? host) =>
            host != null && host.Children.Count > 0 &&
            host.Children.OfType<TextBlock>().Any(row =>
                row.Text.Contains("PID ", StringComparison.Ordinal));

        for (int i = 0; i < 60 && !HasProcessRows(firstHost); i++)
            await Task.Delay(25);
        if (!HasProcessRows(firstHost))
            throw new InvalidOperationException(
                "Activity process snapshot loaded but visible process rows were never committed.");

        await ShowSectionAsync("settings");
        await ShowSectionAsync("activity");
        var secondHost = _activityProcessRowsHost;
        if (secondHost == null || ReferenceEquals(firstHost, secondHost))
            throw new InvalidOperationException("Activity reused an obsolete process-list host.");

        for (int i = 0; i < 60 && !HasProcessRows(secondHost); i++)
            await Task.Delay(25);
        if (!HasProcessRows(secondHost) || !ReferenceEquals(_activityProcessRowsHost, secondHost) ||
            _activityProcessSurface == null ||
            !ContentStack.Children.Contains(_activityProcessSurface))
            throw new InvalidOperationException(
                "Activity process-list refresh did not populate the current card.");

        LogService.Info("performance",
            $"ACTIVITY_UI_POPULATION_SMOKE_OK firstRows={firstHost.Children.Count} " +
            $"secondRows={secondHost.Children.Count} staleHostPreserved=true");
    }

    private async Task LoadActivityProcessesAsync(StackPanel host, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            var rows = await ActivityProcessSnapshotService.GetAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "activity") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != _navigationGeneration ||
                    _section != "activity" ||
                    !ReferenceEquals(_activityProcessRowsHost, host) ||
                    _activityProcessSurface == null ||
                    !ContentStack.Children.Contains(_activityProcessSurface))
                    return;
                host.Children.Clear();
                foreach (var row in rows) host.Children.Add(new TextBlock { Text = $"{row.Name}  •  PID {row.Id}  •  {row.Mb} MB", FontSize = 12 });
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Error("activity", "Process inventory failed", ex); }
    }

}
