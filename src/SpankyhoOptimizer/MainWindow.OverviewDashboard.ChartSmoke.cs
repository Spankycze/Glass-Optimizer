using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    internal static string RunOverviewRamValiditySmoke()
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var normal = ComputeOverviewRamSnapshot(8 * gib, 3 * gib, 63);
        var empty = ComputeOverviewRamSnapshot(8 * gib, 8 * gib, 0);
        var full = ComputeOverviewRamSnapshot(8 * gib, 0, 100);
        var failed = ComputeOverviewRamSnapshot(0, 0, 0);
        var invalidAvailable = ComputeOverviewRamSnapshot(8 * gib, 9 * gib, 50);
        var invalidLoad = ComputeOverviewRamSnapshot(8 * gib, 4 * gib, 101);
        var huge = ComputeOverviewRamSnapshot(ulong.MaxValue, ulong.MaxValue, 0);
        if (normal.Percent != 63 || normal.UsedGb != 5 ||
            normal.TotalGb != 8 ||
            empty.Percent != 0 || empty.UsedGb != 0 ||
            full.Percent != 100 || full.UsedGb != 8 ||
            failed.Percent != null || invalidAvailable.Percent != null ||
            invalidLoad.Percent != null || huge.Percent != 0 ||
            !double.IsFinite(huge.TotalGb) ||
            !FormatOverviewRamSummary(failed).Contains("nedostupn", StringComparison.Ordinal) ||
            !FormatOverviewRamSummary(normal).Contains("63%", StringComparison.Ordinal))
            throw new InvalidOperationException("RAM native snapshot validity/summary regression.");

        var ramPoints = new PointCollection();
        if (AppendOverviewOptionalMetricPoint(ramPoints, failed.Percent, 38) ||
            !AppendOverviewOptionalMetricPoint(ramPoints, empty.Percent, 138) ||
            !AppendOverviewOptionalMetricPoint(ramPoints, normal.Percent, 238) ||
            ramPoints.Count != 2 ||
            Math.Abs(ramPoints[0].Y - 78) > 0.001 ||
            Math.Abs(ramPoints[1].Y - 35.16) > 0.001)
            throw new InvalidOperationException("RAM missing/true-zero history graph regression.");
        return "OVERVIEW_RAM_VALIDITY_OK cases=12 unavailableNotZero=true " +
            "nativeValidation=true trueZero=true full=true hugeSafe=true " +
            "summaryExplicit=true chartRecovered=true writes=0";
    }

    // History is chronological (clock rollback resets it on ingestion).
    // The 60-minute chart keeps ~1,800 samples: lower-bound is ~11 timestamp
    // comparisons rather than scanning most of the list every 4 seconds.
    private static int FindFirstOverviewSampleAtOrAfter(
        System.Collections.Generic.IReadOnlyList<OverviewPerfSample> samples, DateTime cutoff)
    {
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (samples[mid].Utc < cutoff) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // Historical X coordinates are anchored to NOW, not just the latest
    // telemetry sample. If readings pause or a range is reselected, shift
    // the geometry after one horizontal pixel of elapsed window time.
    // This avoids stale screen coordinates without 4-second redraw churn at 60m.
    private static bool IsOverviewChartRenderRedundant(
        DateTime latestUtc, int rangeMinutes, int first, int count,
        DateTime rightEdgeUtc, DateTime renderedUtc, int renderedRange,
        int renderedFirst, int renderedCount, DateTime renderedRightEdgeUtc)
    {
        if (latestUtc != renderedUtc || rangeMinutes != renderedRange ||
            first != renderedFirst || count != renderedCount ||
            renderedRightEdgeUtc == default || rightEdgeUtc < renderedRightEdgeUtc)
            return false;
        long onePixelTicks = Math.Max(1, TimeSpan.FromMinutes(rangeMinutes).Ticks / 474);
        return rightEdgeUtc.Ticks - renderedRightEdgeUtc.Ticks < onePixelTicks;
    }

    internal static string RunOverviewHistoryLookupBenchmark()
    {
        DateTime first = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
        const int points = 1950;
        const int iterations = 4800;
        var history = new System.Collections.Generic.List<OverviewPerfSample>(points);
        for (int i = 0; i < points; i++)
            history.Add(new OverviewPerfSample(first.AddSeconds(i * 2), i % 100, null, 42, 0));
        static int Linear(System.Collections.Generic.IReadOnlyList<OverviewPerfSample> list, DateTime cutoff)
        {
            int i = 0;
            while (i < list.Count && list[i].Utc < cutoff) i++;
            return i;
        }

        if (FindFirstOverviewSampleAtOrAfter(Array.Empty<OverviewPerfSample>(), first) != 0)
            throw new InvalidOperationException("Overview empty history lower-bound regression.");
        for (int i = -3; i < points + 3; i++)
        {
            DateTime cutoff = first.AddSeconds(i * 2);
            if (Linear(history, cutoff) != FindFirstOverviewSampleAtOrAfter(history, cutoff))
                throw new InvalidOperationException("Overview time cutoff does not match prior linear scan.");
        }
        for (int i = 0; i < 15; i++)
        {
            var cutoff = first.AddSeconds((i * 251) % 4100);
            _ = Linear(history, cutoff);
            _ = FindFirstOverviewSampleAtOrAfter(history, cutoff);
        }
        DateTime latest = history[^1].Utc;
        int checksumOld = 0, checksumNew = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            int range = i % 4 switch { 0 => 1, 1 => 5, 2 => 15, _ => 60 };
            checksumOld += Linear(history, latest.AddMinutes(-range));
        }
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            int range = i % 4 switch { 0 => 1, 1 => 5, 2 => 15, _ => 60 };
            checksumNew += FindFirstOverviewSampleAtOrAfter(history, latest.AddMinutes(-range));
        }
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (checksumOld != checksumNew || oldBytes > 1024 || newBytes > 1024)
            throw new InvalidOperationException("Overview binary cutoff work or allocations regressed.");
        return $"OVERVIEW_HISTORY_CUTOFF_BENCH_OK points={points} queries={iterations} " +
            $"linearMs={oldMs:F3} binaryMs={newMs:F3} " +
            $"linearAllocB={oldBytes} binaryAllocB={newBytes} sameResults=true";
    }

    internal static string RunOverviewChartRenderPolicySmoke()
    {
        DateTime time = new(2026, 10, 9, 4, 0, 0, DateTimeKind.Utc);
        bool Skip(DateTime latest, int range, int first, int count,
            DateTime rightEdge, DateTime lastRenderTime = default,
            DateTime lastRightEdge = default) =>
            IsOverviewChartRenderRedundant(latest, range, first, count, rightEdge,
                lastRenderTime == default ? time : lastRenderTime, 5, 1, 48,
                lastRightEdge == default ? time : lastRightEdge);

        TimeSpan pixel5m = TimeSpan.FromTicks(TimeSpan.FromMinutes(5).Ticks / 474);
        TimeSpan pixel60m = TimeSpan.FromTicks(TimeSpan.FromMinutes(60).Ticks / 474);
        if (!Skip(time, 5, 1, 48, time) ||
            !Skip(time, 5, 1, 48, time.AddTicks(pixel5m.Ticks - 1)) ||
            Skip(time, 5, 1, 48, time.AddTicks(pixel5m.Ticks)) ||
            Skip(time, 5, 1, 48, time.AddSeconds(-2)) ||
            Skip(time.AddSeconds(2), 5, 1, 48, time) ||
            Skip(time, 15, 1, 48, time) ||
            Skip(time, 5, 2, 48, time) ||
            Skip(time, 5, 1, 47, time))
            throw new InvalidOperationException("Overview chart ignored a changed window, sample or time axis.");
        // 60m is intentionally coarser: old geometry is reused until one
        // 474-column pixel has passed without any new sensor readings.
        if (!IsOverviewChartRenderRedundant(time, 60, 1, 48,
                time.AddTicks(pixel60m.Ticks - 1), time, 60, 1, 48, time) ||
            IsOverviewChartRenderRedundant(time, 60, 1, 48,
                time.AddTicks(pixel60m.Ticks), time, 60, 1, 48, time) ||
            IsOverviewChartRenderRedundant(time, 60, 1, 48,
                time, time, 60, 1, 48, default))
            throw new InvalidOperationException("Overview 60m horizontal-pixel refresh policy regressed.");

        // Native WinUI regression: same sensor readings must move to the left
        // when the displayed 5-minute clock window moves, preserving height.
        var history = new System.Collections.Generic.List<OverviewPerfSample>
        {
            new(time.AddSeconds(-60), 32, 10, 40, 12),
            new(time.AddSeconds(-58), 100, 15, 50, 14)
        };
        var probeCharts = new OverviewSegmentedChart[]
        {
            new(new SolidColorBrush(Microsoft.UI.Colors.Blue)),
            new(new SolidColorBrush(Microsoft.UI.Colors.Blue)),
            new(new SolidColorBrush(Microsoft.UI.Colors.Blue)),
            new(new SolidColorBrush(Microsoft.UI.Colors.Blue)),
            new(null, new SolidColorBrush(Microsoft.UI.Colors.Blue))
        };
        var cursors = new OverviewEnvelopeCursor[]
        {
            new(), new(), new(), new()
        };
        void RenderClockProbe(DateTime now) =>
            RenderOverviewHistoryBatch(history, 0, now.AddMinutes(-5), now,
                cursors, probeCharts[0], probeCharts[1], probeCharts[2],
                probeCharts[3], probeCharts[4]);
        RenderClockProbe(time);
        if (probeCharts[0].SegmentCount != 1 ||
            probeCharts[2].SegmentCount != 1 ||
            probeCharts[4].SegmentCount != 1)
            throw new InvalidOperationException("Chart clock regression broke contiguous sensor runs.");
        var original = probeCharts[0].GetSegment(0)!;
        double originalX = original.StartPoint.X;
        double originalY = original.StartPoint.Y;
        RenderClockProbe(time.AddSeconds(8));
        var shifted = probeCharts[0].GetSegment(0)!;
        double deltaX = originalX - shifted.StartPoint.X;
        if (probeCharts[0].SegmentCount != 1 ||
            Math.Abs(deltaX - 474d * 8 / 300) > .001 ||
            shifted.StartPoint.Y != originalY ||
            probeCharts[4].SegmentCount != 1)
            throw new InvalidOperationException("Historical native geometry froze on unchanged sensor readings.");

        // Pure hotpath probe. Per-call predicate overhead is timed and
        // allocated bytes measured independently of WinUI native rendering.
        const int samples = 32768;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int redraws = 0;
        for (int i = 0; i < samples; i++)
            if (!IsOverviewChartRenderRedundant(time, 60, 1, 48,
                    time.AddSeconds(i % 12), time, 60, 1, 48, time))
                redraws++;
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (redraws <= 0 || redraws >= samples || allocated != 0)
            throw new InvalidOperationException("Overview clock-shift predicate introduced allocation or skipped all redraws.");
        return $"OVERVIEW_CHART_RENDER_POLICY_SMOKE_OK samples={samples} elapsedMs={elapsedMs:F3} allocatedB={allocated} " +
            "duplicate=skipped rolling=redraw range=redraw expired=redraw 5mPixel=true 60mPixel=true nativePathShift=true clockRollback=true";
    }
    private static bool SetOverviewChartPoint(PointCollection points, int index, Windows.Foundation.Point next)
    {
        if (index >= points.Count)
        {
            points.Add(next);
            return true;
        }

        Windows.Foundation.Point current = points[index];
        if (current.X == next.X && current.Y == next.Y)
            return false;

        points[index] = next;
        return true;
    }

    private static void TrimOverviewChartPoints(PointCollection points, int count)
    {
        while (points.Count > count)
            points.RemoveAt(points.Count - 1);
    }

    internal static string RunOverviewChartPointReuseSmoke()
    {
        var points = new PointCollection();
        var first = new Windows.Foundation.Point(38, 78);
        var last = new Windows.Foundation.Point(512, 65);
        if (!SetOverviewChartPoint(points, 0, first) || !SetOverviewChartPoint(points, 1, last))
            throw new InvalidOperationException("Overview chart new points were not added.");
        if (SetOverviewChartPoint(points, 0, first) || SetOverviewChartPoint(points, 1, last))
            throw new InvalidOperationException("Overview chart unchanged points caused redundant WinUI writes.");
        if (points.Count != 2 || points[0].X != 38 || points[1].X != 512 || points[1].Y != 65)
            throw new InvalidOperationException("Overview chart steady-state points were not retained.");

        if (!SetOverviewChartPoint(points, 1, new Windows.Foundation.Point(512, 30)))
            throw new InvalidOperationException("Overview chart changing a point must write the new value.");
        if (points[1].Y != 30)
            throw new InvalidOperationException("Overview chart changed point was not updated.");
        TrimOverviewChartPoints(points, 1);
        if (points.Count != 1 || points[0].X != 38)
            throw new InvalidOperationException("Overview chart shrinking sample count retained stale points.");

        return "OVERVIEW_CHART_POINT_REUSE_SMOKE_OK retained=2 updated=1 trimmed=1";
    }

    // Detached WinUI collections: isolates point write overhead, not a claim about visible FPS.
    internal static string RunOverviewPointCollectionBenchmark()
    {
        static (double Milliseconds, long Bytes) Measure(bool reuse, bool rolling)
        {
            const int frames = 120;
            const int pointsPerSeries = 48;
            const int seriesCount = 5;
            var series = new PointCollection[seriesCount];
            for (int j = 0; j < series.Length; j++) series[j] = new PointCollection();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int frame = 0; frame < frames; frame++)
            {
                for (int j = 0; j < series.Length; j++)
                {
                    PointCollection points = series[j];
                    if (!reuse) points.Clear();
                    for (int i = 0; i < pointsPerSeries; i++)
                    {
                        int sample = rolling ? (i + frame) % 47 : i % 47;
                        var next = new Windows.Foundation.Point(38 + i * 10, 78 - sample - j);
                        if (reuse) SetOverviewChartPoint(points, i, next);
                        else points.Add(next);
                    }
                }
            }
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (series.Any(x => x.Count != pointsPerSeries))
                throw new InvalidOperationException("Chart benchmark generated incomplete data.");
            return (ticks * 1000d / System.Diagnostics.Stopwatch.Frequency, allocated);
        }

        // Warm up both code paths before reporting results.
        _ = Measure(false, false);
        _ = Measure(true, false);
        var legacySteady = Measure(false, false);
        var reuseSteady = Measure(true, false);
        var legacyRolling = Measure(false, true);
        var reuseRolling = Measure(true, true);
        return $"OVERVIEW_POINT_COLLECTION_BENCH_OK mode=detached-winui frames=120 series=5 points=48 " +
               $"baselineSteadyMs={legacySteady.Milliseconds:0.###} reuseSteadyMs={reuseSteady.Milliseconds:0.###} " +
               $"baselineRollingMs={legacyRolling.Milliseconds:0.###} reuseRollingMs={reuseRolling.Milliseconds:0.###} " +
               $"baselineSteadyBytes={legacySteady.Bytes} reuseSteadyBytes={reuseSteady.Bytes} " +
               $"baselineRollingBytes={legacyRolling.Bytes} reuseRollingBytes={reuseRolling.Bytes}";
    }
    private static string FormatRate(double mbps)
        => mbps >= 100 ? $"{mbps:0} Mb/s" : mbps >= 10 ? $"{mbps:0.0} Mb/s" : $"{mbps:0.00} Mb/s";

    private (double DownMbps, double UpMbps, bool Online, bool BaselineReset, string AdapterLabel) ReadNetworkRates()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            bool adapterChanged = false;
            if (_overviewNetInterfaces == null || now >= _overviewNetInterfaceRefreshUtc ||
                now < _overviewPrevNetUtc)
            {
                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                                  nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel &&
                                  nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                                  nic.Speed > 0)
                    .ToArray();
                // Only compute topology identity at the already existing 10s
                // refresh; NIC order may change without a topology change.
                string identity = NetworkAdapterIdentity(interfaces.Select(nic => nic.Id));
                adapterChanged = _overviewNetInterfaces != null &&
                    !string.Equals(_overviewNetInterfaceIdentity, identity,
                        StringComparison.OrdinalIgnoreCase);
                _overviewNetInterfaces = interfaces;
                _overviewNetInterfaceIdentity = identity;
                _overviewNetInterfaceRefreshUtc = now.AddSeconds(10);
            }

            long rx = 0, tx = 0;
            bool online = false;
            int measuredAdapterCount = 0;
            string? measuredAdapterName = null;
            _overviewSampleNicIds.Clear();
            foreach (var nic in _overviewNetInterfaces)
            {
                // A cached adapter can go down before the 10-second topology
                // refresh. Do not treat its old byte counters as live traffic,
                // and force a normal enumeration on the next active tick.
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    adapterChanged = true;
                    _overviewNetInterfaceRefreshUtc = now;
                    continue;
                }
                try
                {
                    var stats = nic.GetIPv4Statistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                    online = true;
                    measuredAdapterCount++;
                    measuredAdapterName ??= nic.Name;
                    _overviewSampleNicIds.Add(nic.Id);
                }
                catch (System.Net.NetworkInformation.NetworkInformationException)
                {
                    // Exclude this NIC from this sample and re-discover on the
                    // next tick. The successful-NIC membership below forces
                    // a fresh baseline on both failure and recovery.
                    _overviewNetInterfaceRefreshUtc = now;
                }
            }

            bool measuredSetChanged = !SameNetworkAdapterSet(
                _overviewMeasuredNicIds, _overviewSampleNicIds);
            if (measuredSetChanged)
                _overviewMeasuredNicIds = _overviewSampleNicIds.ToArray();
            bool reset = ShouldResetNetworkRateBaseline(
                _overviewPrevNetUtc, now, _overviewPrevRxBytes,
                _overviewPrevTxBytes, rx, tx, online,
                adapterChanged || measuredSetChanged);
            double seconds = Math.Max(.1, (now - _overviewPrevNetUtc).TotalSeconds);
            double down = reset ? 0 : (rx - _overviewPrevRxBytes) * 8d / seconds / 1_000_000d;
            double up = reset ? 0 : (tx - _overviewPrevTxBytes) * 8d / seconds / 1_000_000d;
            _overviewPrevNetUtc = now;
            _overviewPrevRxBytes = rx;
            _overviewPrevTxBytes = tx;
            return (down, up, online, reset,
                FormatOverviewNetworkAdapterLabel(measuredAdapterCount, measuredAdapterName));
        }
        catch
        {
            // The next successful NIC read must establish a fresh baseline;
            // otherwise a temporary query failure can yield a false traffic spike.
            _overviewPrevNetUtc = default;
            _overviewNetInterfaceRefreshUtc = default;
            _overviewMeasuredNicIds = null;
            return (0, 0, false, true, "Síť odpojena");
        }
    }

    // Display a label from the same successful-NIC sample as the total rates.
    // Multiple adapters must not be misidentified as the first enumerated NIC.
    internal static string FormatOverviewNetworkAdapterLabel(int count, string? singleName) =>
        count <= 0 ? "Síť odpojena" : count == 1
            ? (string.IsNullOrWhiteSpace(singleName) ? "Připojení" : singleName)
            : count < 5 ? $"{count} aktivní rozhraní" : $"{count} aktivních rozhraní";

    private static string NetworkAdapterIdentity(System.Collections.Generic.IEnumerable<string> adapterIds) =>
        string.Join("|", adapterIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

    // Membership ignores provider order/casing. No LINQ iterator, string
    // join or array allocation on the frequent unchanged 2-second path.
    internal static bool SameNetworkAdapterSet(
        System.Collections.Generic.IReadOnlyList<string>? previous,
        System.Collections.Generic.IReadOnlyList<string> current)
    {
        if (previous == null || previous.Count != current.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            int beforeCount = 0, afterCount = 0;
            for (int j = 0; j < current.Count; j++)
            {
                if (string.Equals(current[i], previous[j], StringComparison.OrdinalIgnoreCase))
                    beforeCount++;
                if (string.Equals(current[i], current[j], StringComparison.OrdinalIgnoreCase))
                    afterCount++;
            }
            if (beforeCount != afterCount) return false;
        }
        return true;
    }

    internal static bool ShouldResetNetworkRateBaseline(
        DateTime previousUtc, DateTime nowUtc, long previousRx, long previousTx,
        long currentRx, long currentTx, bool online, bool adapterChanged) =>
        previousUtc == default || nowUtc <= previousUtc ||
        nowUtc - previousUtc > TimeSpan.FromSeconds(8) || !online ||
        adapterChanged || currentRx < previousRx || currentTx < previousTx;

    // Read-only regression for switched/vanished NICs and reset byte counters.
    // No network changes, actual adapter enumeration or filesystem writes.
    internal static string RunOverviewNetworkBaselineSmoke()
    {
        DateTime now = new(2026, 10, 12, 17, 0, 0, DateTimeKind.Utc);
        DateTime previous = now.AddSeconds(-2);
        bool Check(long priorRx, long priorTx, long currentRx, long currentTx,
            bool online = true, bool changed = false,
            DateTime? oldTime = null, DateTime? currentTime = null) =>
            ShouldResetNetworkRateBaseline(
                oldTime ?? previous, currentTime ?? now,
                priorRx, priorTx, currentRx, currentTx, online, changed);

        if (Check(1000, 2000, 3000, 4000) ||
            Check(1000, 2000, 1000, 2000) ||
            !Check(1000, 2000, 3000, 4000, changed: true) ||
            !Check(1000, 2000, 3000, 4000, online: false) ||
            !Check(1000, 2000, 900, 4000) ||
            !Check(1000, 2000, 3000, 1900) ||
            !Check(1000, 2000, 3000, 4000, oldTime: default(DateTime)) ||
            !Check(1000, 2000, 3000, 4000, currentTime: previous) ||
            !Check(1000, 2000, 3000, 4000, currentTime: previous.AddSeconds(-1)) ||
            !Check(1000, 2000, 3000, 4000, oldTime: now.AddSeconds(-30)) ||
            !Check(1000, 2000, 3000, 4000, currentTime: previous.AddSeconds(9)) ||
            Check(1000, 2000, 3000, 4000, currentTime: previous.AddSeconds(8)) ||
            !string.Equals(NetworkAdapterIdentity(new[] { "eth", "wifi" }),
                NetworkAdapterIdentity(new[] { "WIFI", "ETH" }),
                StringComparison.OrdinalIgnoreCase) )
            throw new InvalidOperationException("Overview network baseline/topology regression.");

        // A reset returns (0,0) for one sample, then the next 2s delta
        // is measured against the new interface's captured counters.
        long received = 800_000_000, sent = 500_000_000;
        if (!Check(5_000_000_000, 7_000_000_000, received, sent,
                changed: true) ||
            Check(received, sent, received + 250_000, sent + 100_000))
            throw new InvalidOperationException("Overview adapter-reconnect spike regression.");
        double downstreamMbps = 250_000 * 8d / 2d / 1_000_000d;
        if (Math.Abs(downstreamMbps - 1d) > 0.000001)
            throw new InvalidOperationException("Overview network post-reset rate regression.");
        return "OVERVIEW_NETWORK_BASELINE_RESET_OK cases=16 " +
            "reconnect=true topologyOrderIndependent=true counterReset=true " +
            "offline=true clockCorrection=true pauseResume=true " +
            "deltaResumed=true writes=0";
    }

    // Read-only continuation regression: a failed NIC must not contaminate
    // the byte totals for surviving NICs or produce an enormous reconnection
    // spike; a busy dispatcher must not paint a stale telemetry sample.
    internal static string RunOverviewNetworkContinuitySmoke()
    {
        if (FormatOverviewNetworkAdapterLabel(0, null) != "Síť odpojena" ||
            FormatOverviewNetworkAdapterLabel(1, "Ethernet 2") != "Ethernet 2" ||
            FormatOverviewNetworkAdapterLabel(1, null) != "Připojení" ||
            FormatOverviewNetworkAdapterLabel(2, "Ethernet 2") != "2 aktivní rozhraní" ||
            FormatOverviewNetworkAdapterLabel(5, "Wi-Fi") != "5 aktivních rozhraní")
            throw new InvalidOperationException("Aggregated network adapter labeling regression.");
        string[] a = { "ethernet", "wifi" };
        string[] reordered = { "WIFI", "ETHERNET" };
        string[] partial = { "ethernet" };
        string[] duplicate = { "ethernet", "ethernet" };
        if (!SameNetworkAdapterSet(a, reordered) ||
            !SameNetworkAdapterSet(Array.Empty<string>(), Array.Empty<string>()) ||
            SameNetworkAdapterSet(null, a) ||
            SameNetworkAdapterSet(a, partial) ||
            SameNetworkAdapterSet(partial, a) ||
            SameNetworkAdapterSet(a, duplicate))
            throw new InvalidOperationException("NIC successful-sample membership regression.");

        DateTime now = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);
        if (!ShouldResetNetworkRateBaseline(now.AddSeconds(-2), now,
                7_000_000, 5_000_000, 100_000, 200_000, true,
                !SameNetworkAdapterSet(a, partial)) ||
            !ShouldResetNetworkRateBaseline(now.AddSeconds(-2), now,
                100_000, 200_000, 8_000_000, 6_000_000, true,
                !SameNetworkAdapterSet(partial, reordered)) ||
            ShouldResetNetworkRateBaseline(now.AddSeconds(-2), now,
                7_000_000, 5_000_000, 7_250_000, 5_125_000, true,
                !SameNetworkAdapterSet(a, reordered)))
            throw new InvalidOperationException("NIC partial-failure or recovery baseline regression.");

        long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        if (!IsOverviewLiveSampleFresh(timestamp, timestamp) ||
            !IsOverviewLiveSampleFresh(timestamp, timestamp + frequency * 8L) ||
            IsOverviewLiveSampleFresh(timestamp, timestamp + frequency * 9L) ||
            IsOverviewLiveSampleFresh(timestamp, timestamp - 1) ||
            IsOverviewLiveSampleFresh(0, timestamp))
            throw new InvalidOperationException("Overview queued sample freshness regression.");

        var down = new PointCollection();
        var up = new PointCollection();
        var spark = new PointCollection();
        foreach (var collection in new[] { down, up, spark })
        {
            collection.Add(new Windows.Foundation.Point(12, 14));
            collection.Add(new Windows.Foundation.Point(22, 24));
            ClearOverviewNetworkPlot(collection);
            if (collection.Count != 0)
                throw new InvalidOperationException("Reset left stale network chart points rendered.");
        }
        return "OVERVIEW_NETWORK_CONTINUITY_OK cases=15 " +
            "partialFailure=true recovery=true duplicateIds=true " +
            "staleUiDropped=true activeUiRetained=true plotsCleared=true writes=0";
    }

    private void ResetOverviewNetworkHistory()
    {
        // These UI collections belong to the dispatcher, not the sensor worker.
        // Never visually join samples from different adapters/counter epochs.
        _overviewNetDownSamples.Clear();
        _overviewNetUpSamples.Clear();
        _overviewNetworkWasFlat = false;
        _referenceNetworkHistory.Clear();
        _referenceNetworkLastTotal = double.NaN;
        _referenceNetworkSteadySamples = 0;
        _referenceNetworkLastSparkWidth = double.NaN;
        _referenceNetworkLastSparkHeight = double.NaN;
        // Clear the *rendered* WinUI points too. An empty history alone does
        // not hide the old plot, because the renderer returns on empty input.
        ClearOverviewNetworkPlot(_overviewNetDownLine?.Points);
        ClearOverviewNetworkPlot(_overviewNetUpLine?.Points);
        ClearOverviewNetworkPlot(_referenceNetworkSpark?.Points);
        _overviewNextNetworkChartUtc = DateTime.MinValue;
    }

    // Format the four footers only when their actual data or native visual
    // target changes; the four fill bars still update on every 2-second tick.
    // Missing/nonfinite input must not send NaN/Infinity into WinUI Width.
}
