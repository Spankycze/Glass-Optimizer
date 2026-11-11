using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Explicit regression only. Isolated snapshot values, no registry/service/tweak mutations.
    internal async Task RunOverviewStatusSmokeAsync()
    {
        await ShowSectionAsync("settings"); // Cancel real Overview callbacks before isolating the state.
        var oldRefs = _overviewTextRefs;
        var oldSystem = _overviewSystemStatusText;
        var oldDriver = _overviewDriverStatusText;
        var oldGpu = _gpuInfo;
        var oldGpuGeneration = _gpuDetectionGeneration;
        var oldSection = _section;
        var oldGauges = (_cpuGauge, _gpuGauge, _ramGauge, _diskGauge);
        var oldHardwareLabels = (_overviewHardwareCpuText, _overviewHardwareGpuText, _overviewHardwareRamText, _overviewHardwareDiskText);
        var oldActiveSignature = _overviewActiveTweaksLogSignature;
        var oldActiveLogUtc = _overviewActiveTweaksLastLogUtc;
        _cpuGauge = _gpuGauge = _ramGauge = _diskGauge = null;
        _overviewHardwareCpuText = _overviewHardwareGpuText = _overviewHardwareRamText = _overviewHardwareDiskText = null;
        var results = new List<object>();
        int failed = 0;
        var snapshot = new OverviewSnapshot("Windows fixture", "CPU fixture", "GPU fixture", "16 GB", "RAM fixture", "Board fixture", "Disk fixture", "Network fixture", "Power fixture");
        OverviewTextRefs Reset()
        {
            _section = "overview";
            _overviewSystemStatusText = null; _overviewDriverStatusText = null;
            _gpuInfo = new GpuDriverInfo("Test GPU", "Test", "1.0", "1.0", null);
            _gpuDetectionGeneration = GpuDriverService.DetectionCacheGeneration;
            return _overviewTextRefs = new OverviewTextRefs
            {
                Cpu = new(), Gpu = new(), Ram = new(), Disk = new(), Os = new(), Board = new(),
                Power = new(), Network = new(), Driver = new(), History = new(), Windows = new()
            };
        }
        void BuildCard(OverviewTextRefs refs) => _ = BuildOverviewOptimizerStatusCard(new TextBlock { Text = "fixture" }, new TextBlock { Text = "fixture" });
        Task Complete(OverviewTextRefs refs, Task<OverviewSnapshot> task, CancellationToken token,
            int? gen = null, Task<TweakBatchOverviewSnapshot>? historyTask = null) =>
            CompleteOverviewDashboardAsync(task, refs.Cpu, refs.Gpu, refs.Ram, refs.Disk, refs.Os, refs.Board,
                refs.Power, refs.Network, refs.Driver, refs.History, refs.Windows,
                token, gen ?? _navigationGeneration, historyTask);
        async Task Check(string name, Func<OverviewTextRefs, CancellationToken, Task> action)
        {
            using var cts = new CancellationTokenSource();
            var refs = Reset();
            try { await action(refs, cts.Token); results.Add(new { name, outcome = "PASS" }); }
            catch (Exception ex) { failed++; results.Add(new { name, outcome = "FAIL", error = ex.Message }); }
            finally { cts.Cancel(); await FlushOverviewUiForSmokeAsync(); }
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        bool Loaded() => _overviewSystemStatusText?.Text == "●  Systém načten";
        bool Loading() => _overviewSystemStatusText?.Text == "●  Načítám systém…";
        bool Failed() => _overviewSystemStatusText?.Text == "●  Systém se nepodařilo načíst";
        try
        {
            await Check("snapshot-before-deferred-card", async (refs, token) => {
                await Complete(refs, Task.FromResult(snapshot), token); await FlushOverviewUiForSmokeAsync();
                Require(refs.Os.Text == snapshot.Windows, "Snapshot did not complete before fixture card.");
                BuildCard(refs); Require(Loaded(), "Completed snapshot lost before status card construction.");
            });
            await Check("driver-before-deferred-card", async (refs, token) => {
                await CompleteOverviewDriverAsync(refs.Driver, token, _navigationGeneration); await FlushOverviewUiForSmokeAsync();
                BuildCard(refs); Require(_overviewDriverStatusText?.Text == "●  Ovladač zjištěn", "Cached driver completion lost before card construction.");
            });
            await Check("worker-gpu-cache-completion-returns-to-ui", async (refs, token) => {
                // Force mocked persistent-cache I/O to complete on a worker.
                // Driver status and _gpuInfo must be committed on the WinUI dispatcher.
                _gpuInfo = null;
                var pending = new TaskCompletionSource<GpuDriverInfo?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var completed = CompleteOverviewDriverAsync(refs.Driver, token,
                    _navigationGeneration, pending.Task);
                await Task.Run(() => pending.SetResult(
                    new GpuDriverInfo("Test GPU worker", "Test", "4.8", "4.8", null)), CancellationToken.None);
                await completed;
                await FlushOverviewUiForSmokeAsync();
                BuildCard(refs);
                Require(_gpuInfo?.DisplayVersion == "4.8" &&
                    _overviewDriverStatusText?.Text == "●  Ovladač zjištěn" &&
                    refs.Driver.Text == "4.8",
                    "Worker cache completion mutated GPU state off UI or did not publish the driver.");
            });
            await Check("snapshot-after-visible-card", async (refs, token) => {
                BuildCard(refs); Require(Loading(), "Fresh card must begin in loading state.");
                await Complete(refs, Task.FromResult(snapshot), token); await FlushOverviewUiForSmokeAsync(); Require(Loaded(), "Visible status did not complete.");
            });
            await Check("worker-snapshot-completion-returns-to-ui", async (refs, token) => {
                BuildCard(refs);
                var pending = new TaskCompletionSource<OverviewSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var completion = Complete(refs, pending.Task, token);
                // Force the hardware snapshot to finish on a ThreadPool worker.
                await Task.Run(() => pending.SetResult(snapshot), CancellationToken.None);
                await completion;
                await FlushOverviewUiForSmokeAsync();
                Require(Loaded() && !string.IsNullOrWhiteSpace(refs.History.Text) &&
                    !refs.History.Text.Contains("Načítám", StringComparison.Ordinal),
                    "Async hardware completion failed to resume safe UI tweak status loading.");
            });
            await Check("worker-direct-tweak-cache-read-blocked", async (refs, token) => {
                bool rejected = await Task.Run(() =>
                {
                    try { GetDetectedTweakStates(); return false; }
                    catch (InvalidOperationException) { return true; }
                }, CancellationToken.None);
                Require(rejected, "Mutable cached tweak state was read outside WinUI dispatcher.");
            });
            await Check("history-failure-after-hardware-does-not-spin", async (refs, token) => {
                BuildCard(refs);
                await Complete(refs, Task.FromResult(snapshot), token, historyTask:
                    Task.FromException<TweakBatchOverviewSnapshot>(new IOException("fixture: history unavailable")));
                await FlushOverviewUiForSmokeAsync();
                Require(Loaded() && refs.Os.Text == snapshot.Windows &&
                    refs.History.Text == "Historie tweaků je dočasně nedostupná",
                    "History exception left permanent loading text or invalidated loaded hardware.");
            });
            await Check("history-failure-before-card-still-visible", async (refs, token) => {
                await Complete(refs, Task.FromResult(snapshot), token, historyTask:
                    Task.FromException<TweakBatchOverviewSnapshot>(new IOException("fixture: history offline")));
                await FlushOverviewUiForSmokeAsync();
                BuildCard(refs);
                Require(Loaded() && refs.History.Text == "Historie tweaků je dočasně nedostupná",
                    "Deferred History failure was lost before status card construction.");
            });
            await Check("history-retry-restores-text", async (refs, token) => {
                BuildCard(refs);
                await Complete(refs, Task.FromResult(snapshot), token, historyTask:
                    Task.FromException<TweakBatchOverviewSnapshot>(new IOException("fixture: history offline")));
                await FlushOverviewUiForSmokeAsync();
                Require(refs.History.Text == "Historie tweaků je dočasně nedostupná", "Failure fixture did not apply.");
                var recovered = new TweakBatchOverviewSnapshot(Array.Empty<TweakBatchRecord>(),
                    new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
                await Complete(refs, Task.FromResult(snapshot), token, historyTask: Task.FromResult(recovered));
                await FlushOverviewUiForSmokeAsync();
                Require(Loaded() && !string.IsNullOrWhiteSpace(refs.History.Text) &&
                    !refs.History.Text.Contains("Načítám", StringComparison.Ordinal) &&
                    refs.History.Text != "Historie tweaků je dočasně nedostupná",
                    "History retry did not replace the previous error state.");
            });
            await Check("stale-history-failure-cannot-repaint-new-tree", async (refs, token) => {
                var pending = new TaskCompletionSource<TweakBatchOverviewSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var completion = Complete(refs, Task.FromResult(snapshot), token, historyTask: pending.Task);
                await FlushOverviewUiForSmokeAsync();
                var replacement = Reset();
                BuildCard(replacement);
                await Task.Run(() => pending.SetException(new IOException("fixture stale history failure")),
                    CancellationToken.None);
                await completion;
                await FlushOverviewUiForSmokeAsync();
                Require(Loading() && string.IsNullOrEmpty(replacement.History.Text),
                    "Stale History failure repainted an unrelated Overview tree.");
            });
            await Check("failure-before-deferred-card", async (refs, token) => {
                await Complete(refs, Task.FromException<OverviewSnapshot>(new IOException("fixture: snapshot unavailable")), token);
                await FlushOverviewUiForSmokeAsync(); BuildCard(refs); Require(Failed(), "Snapshot failure remained a permanent loading state.");
            });
            await Check("failure-after-visible-card", async (refs, token) => {
                BuildCard(refs); await Complete(refs, Task.FromException<OverviewSnapshot>(new IOException("fixture failure")), token);
                await FlushOverviewUiForSmokeAsync(); Require(Failed(), "Visible failure has no terminal state.");
            });
            await Check("retry-replaces-error-with-loaded", async (refs, token) => {
                BuildCard(refs); await Complete(refs, Task.FromException<OverviewSnapshot>(new IOException("fixture failure")), token);
                await FlushOverviewUiForSmokeAsync(); Require(Failed(), "Failure state was not reached.");
                await Complete(refs, Task.FromResult(snapshot), token); await FlushOverviewUiForSmokeAsync(); Require(Loaded(), "Retry did not recover status.");
            });
            await Check("cancelled-request-does-not-publish", async (refs, _) => {
                BuildCard(refs); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Complete(refs, Task.FromResult(snapshot), cancelled.Token); await FlushOverviewUiForSmokeAsync(); Require(Loading(), "Cancelled work changed current state.");
            });
            await Check("old-generation-does-not-publish", async (refs, token) => {
                BuildCard(refs); await Complete(refs, Task.FromResult(snapshot), token, _navigationGeneration - 1);
                await FlushOverviewUiForSmokeAsync(); Require(Loading(), "Old generation changed the current status.");
            });
            await Check("old-tree-success-does-not-mark-replacement-loaded", async (refs, token) => {
                var pending = new TaskCompletionSource<OverviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                var work = Complete(refs, pending.Task, token); await FlushOverviewUiForSmokeAsync();
                var next = Reset(); BuildCard(next); pending.SetResult(snapshot); await work; await FlushOverviewUiForSmokeAsync();
                Require(Loading() && next.Os.Text == "", "Detached tree completion leaked into replacement status.");
            });
            await Check("old-tree-failure-does-not-mark-replacement-failed", async (refs, token) => {
                var pending = new TaskCompletionSource<OverviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                var work = Complete(refs, pending.Task, token); await FlushOverviewUiForSmokeAsync();
                var next = Reset(); BuildCard(next); pending.SetException(new IOException("fixture stale failure")); await work; await FlushOverviewUiForSmokeAsync();
                Require(Loading(), "Detached tree failure changed replacement state.");
            });
            await Check("card-rebuild-retains-completed-state", async (refs, token) => {
                BuildCard(refs); await Complete(refs, Task.FromResult(snapshot), token); await FlushOverviewUiForSmokeAsync(); Require(Loaded(), "Control did not load.");
                BuildCard(refs); Require(Loaded(), "Recreated status label forgot a completed snapshot.");
            });
            await Check("snapshot-failure-does-not-block-driver-completion", async (refs, token) => {
                await Complete(refs, Task.FromException<OverviewSnapshot>(new IOException("fixture hardware failure")), token);
                await FlushOverviewUiForSmokeAsync(); BuildCard(refs);
                Require(Failed() && _overviewDriverStatusText?.Text == "●  Ovladač zjištěn", "Unrelated snapshot failure blocked cached driver status.");
            });
            await Check("empty-cached-driver-is-not-reported-as-detected", async (refs, token) => {
                _gpuInfo = new GpuDriverInfo("Test", "Test", "", "", null);
                await CompleteOverviewDriverAsync(refs.Driver, token, _navigationGeneration); await FlushOverviewUiForSmokeAsync();
                BuildCard(refs); Require(_overviewDriverStatusText?.Text == "●  Ovladač nezjištěn", "Empty driver version was reported as detected.");
            });
            await Check("fresh-tree-does-not-inherit-previous-success", async (refs, token) => {
                await Complete(refs, Task.FromResult(snapshot), token); await FlushOverviewUiForSmokeAsync();
                var next = Reset(); BuildCard(next); Require(Loading(), "Fresh data owner inherited a previous success.");
            });
        }
        finally
        {
            _overviewTextRefs = oldRefs; _overviewSystemStatusText = oldSystem; _overviewDriverStatusText = oldDriver;
            _gpuInfo = oldGpu; _gpuDetectionGeneration = oldGpuGeneration; _section = oldSection;
            (_cpuGauge, _gpuGauge, _ramGauge, _diskGauge) = oldGauges;
            (_overviewHardwareCpuText, _overviewHardwareGpuText, _overviewHardwareRamText, _overviewHardwareDiskText) = oldHardwareLabels;
            _overviewActiveTweaksLogSignature = oldActiveSignature;
            _overviewActiveTweaksLastLogUtc = oldActiveLogUtc;
        }
        await File.WriteAllTextAsync(Path.Combine(LogService.Root, "overview-status-smoke.json"), JsonSerializer.Serialize(new {
            version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), utc = DateTime.UtcNow,
            passed = failed == 0, count = results.Count, failures = failed, results
        }));
        if (failed != 0) throw new InvalidOperationException("Overview status regression failures: " + failed);
        LogService.Info("visual-qa", "OVERVIEW_STATUS_SMOKE_OK tests=" + results.Count);
    }
}
