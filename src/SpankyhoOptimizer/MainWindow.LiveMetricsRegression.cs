using System;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;
namespace SpankyhoOptimizer;
public sealed partial class MainWindow
{
    internal async Task RunLiveMetricsLifecycleSmokeAsync()
    {
        for (int i = 0; i < 80 && !_initialSectionNavigationCompleted; i++) await Task.Delay(25);
        if (!_initialSectionNavigationCompleted) throw new InvalidOperationException("Initial navigation did not settle.");
        string section = _section;
        bool active = _windowTelemetryActive;
        bool diagnosticSuppressUi = _liveMetricsDiagnosticSuppressUi;
        _liveMetricsDiagnosticSuppressUi = true; // Prevent the live sensor worker overriding test callbacks.
        using var source = new CancellationTokenSource();
        try
        {
            foreach (string scenario in new[] { "navigation", "deactivation", "cancellation", "active" })
            {
                _section = "overview";
                _windowTelemetryActive = true;
                int applied = 0;
                QueueLiveMetricsUiUpdate(() => applied++, source.Token,
                    System.Diagnostics.Stopwatch.GetTimestamp());
                // Change lifecycle state after scheduling, before the UI dispatcher can run it.
                if (scenario == "navigation") _section = "settings";
                if (scenario == "deactivation") _windowTelemetryActive = false;
                if (scenario == "cancellation") source.Cancel();
                await Task.Delay(40);
                if (scenario == "active")
                {
                    // Use a fresh token after cancellation, and verify live updates still work.
                    applied = 0;
                    QueueLiveMetricsUiUpdate(() => applied++, CancellationToken.None,
                        System.Diagnostics.Stopwatch.GetTimestamp());
                    await Task.Delay(40);
                }
                int expected = scenario == "active" ? 1 : 0;
                if (applied != expected)
                    throw new InvalidOperationException($"Late telemetry callback applied after {scenario}: count={applied}, expected={expected}.");
            }
            // An update queued before a long UI stall must not repaint the
            // reopened dashboard with stale CPU/network data.
            _section = "overview";
            _windowTelemetryActive = true;
            int staleUpdates = 0;
            QueueLiveMetricsUiUpdate(() => staleUpdates++, CancellationToken.None,
                System.Diagnostics.Stopwatch.GetTimestamp() -
                System.Diagnostics.Stopwatch.Frequency * 9L);
            await Task.Delay(40);
            if (staleUpdates != 0)
                throw new InvalidOperationException("Stale telemetry was painted after dispatcher stall.");
            // The dispatcher is on this thread until the first await, so
            // 256 incoming sensor samples must post ONE callback and apply
            // only the latest value when the UI gets its next turn.
            int coalescedUpdates = 0;
            int latestIndex = -1;
            for (int i = 0; i < 256; i++)
            {
                int captured = i;
                QueueLiveMetricsUiUpdate(() => { coalescedUpdates++; latestIndex = captured; },
                    CancellationToken.None, System.Diagnostics.Stopwatch.GetTimestamp());
            }
            lock (_liveMetricsUiGate)
            {
                if (!_liveMetricsUiEnqueued ||
                    _latestLiveMetricsUiSample is not { })
                    throw new InvalidOperationException("Overview worker did not coalesce pending updates.");
            }
            await Task.Delay(70);
            if (coalescedUpdates != 1 || latestIndex != 255)
                throw new InvalidOperationException(
                    $"Overview telemetry backlog replayed {coalescedUpdates} UI samples (last={latestIndex}).");

            // Simulate losing an odd 4-second GPU/disk sample when the
            // dispatcher retains only the following CPU/RAM-only tick.
            // The new fast tick must still carry the unpublished slow data.
            long acknowledgedSlowGeneration = 31;
            int paintedSlow = 0;
            for (int i = 0; i < 2; i++)
            {
                long observedSlowGeneration = 32; // same newest slow sensor reading
                QueueLiveMetricsUiUpdate(() =>
                {
                    if (ShouldPaintSlowSensorSample(
                            observedSlowGeneration, acknowledgedSlowGeneration, false))
                    {
                        acknowledgedSlowGeneration = observedSlowGeneration;
                        paintedSlow++;
                    }
                }, CancellationToken.None, System.Diagnostics.Stopwatch.GetTimestamp());
            }
            await Task.Delay(60);
            if (paintedSlow != 1 || acknowledgedSlowGeneration != 32 ||
                ShouldPaintSlowSensorSample(32, acknowledgedSlowGeneration, false) ||
                !ShouldPaintSlowSensorSample(33, acknowledgedSlowGeneration, false) ||
                !ShouldPaintSlowSensorSample(32, acknowledgedSlowGeneration, true))
                throw new InvalidOperationException(
                    "Coalesced fast telemetry tick lost a pending GPU/disk sensor sample.");
            LogService.Info("LIVE_METRICS_SLOW_SAMPLE_COALESCING_SMOKE_OK queued=2 painted=1 generation=32");

            // A window can deactivate and reactivate before the pending
            // callback executes; timestamps alone cannot detect that cycle.
            int epochCallbacks = 0;
            QueueLiveMetricsUiUpdate(() => epochCallbacks++,
                CancellationToken.None, System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _liveMetricsActivationEpoch);
            await Task.Delay(50);
            if (epochCallbacks != 0)
                throw new InvalidOperationException("Pre-deactivation sample painted after reactivation.");

            // Repeated activation/navigation signals must coalesce rather than
            // allocating a backlog of pending telemetry work.
            for (int i = 0; i < 256; i++)
                SignalLiveMetricsWake();
            if (_liveMetricsWake.CurrentCount > 1)
                throw new InvalidOperationException("Live telemetry wake signals accumulated.");
            LogService.Info("LIVE_METRICS_WAKE_COALESCING_SMOKE_OK events=256 pendingMax=1");
            LogService.Info("LIVE_METRICS_UI_BACKPRESSURE_SMOKE_OK queued=256 applied=1 latest=255 activationEpoch=true staleRejected=true");
            LogService.Info("LIVE_METRICS_LIFECYCLE_SMOKE_OK navigation=true deactivation=true cancellation=true active=true staleRejected=true coalesced=true");
        }
        finally
        {
            _section = section;
            _windowTelemetryActive = active;
            _liveMetricsDiagnosticSuppressUi = diagnosticSuppressUi;
        }
    }
}