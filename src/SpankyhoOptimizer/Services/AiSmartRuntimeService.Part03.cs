using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed partial class AiSmartRuntimeService
{
    private void UpdateModel(
        double temp,
        double power,
        double load,
        double rpm,
        double clock,
        AiWorkloadClass workload,
        DateTime now)
    {
        const double alphaWarmup = 0.10;
        const double alphaStable = 0.025;
        double alpha = _model.Samples < 50 ? alphaWarmup : alphaStable;

        if (_model.Samples == 0)
        {
            _model = new AiSmartRuntimeModel(
                1,
                temp,
                power,
                load,
                rpm > 100 ? rpm : 1620,
                clock > 100 ? clock : 2800,
                now,
                _model.Workloads);
        }
        else
        {
            _model = _model with
            {
                Samples = Math.Min(500000, _model.Samples + 1),
                AvgTempC = Ewma(_model.AvgTempC, temp, alpha),
                AvgPowerW = Ewma(_model.AvgPowerW, power, alpha),
                AvgLoadPercent = Ewma(_model.AvgLoadPercent, load, alpha),
                AvgRpm = rpm > 100 ? Ewma(_model.AvgRpm, rpm, alpha) : _model.AvgRpm,
                AvgClockMHz = clock > 100 ? Ewma(_model.AvgClockMHz, clock, alpha) : _model.AvgClockMHz,
                UpdatedUtc = now
            };
        }

        _modelDirty = true;
        if (workload == AiWorkloadClass.Unknown)
            return;

        // FanAgent owns this model on its serial tick. The saved JSON is a
        // separate serialized copy, so recreating the whole workload dictionary
        // on every 5–10 second learning sample only adds GC pressure.
        // Preserve its case-insensitive key semantics and retain all baselines.
        var workloads = _model.Workloads;
        if (workloads == null)
        {
            workloads = new Dictionary<string, AiSmartWorkloadBaseline>(
                StringComparer.OrdinalIgnoreCase);
            _model = _model with { Workloads = workloads };
        }

        string key = WorkloadKey(workload);
        workloads.TryGetValue(key, out AiSmartWorkloadBaseline? current);
        workloads[key] = LearnWorkloadBaseline(
            current,
            temp,
            power,
            load,
            rpm > 100 ? rpm : _model.AvgRpm,
            clock > 100 ? clock : _model.AvgClockMHz,
            now);
    }

    // Read-only training microbenchmark: same EWMA, same dictionary/JSON after
    // all samples. Avoid rebuilding the complete map for one changed key.
    internal static string RunWorkloadDictionaryReuseSmoke()
    {
        const int rounds = 8192;
        static Dictionary<string, AiSmartWorkloadBaseline> Seed()
        {
            var now = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
            var result = new Dictionary<string, AiSmartWorkloadBaseline>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "Game", "Interactive", "Background", "Idle" })
                result[key] = new AiSmartWorkloadBaseline(
                    30, 60, 57, 25, 1700, 3600, now);
            return result;
        }

        var beforeMap = Seed();
        var afterMap = Seed();
        var identity = afterMap;
        var startUtc = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        // Warm the same numeric/JIT code without changing either measured map.
        var warm = Seed();
        for (int i = 0; i < 100; i++)
            warm["Game"] = LearnWorkloadBaseline(
                warm["Game"], 65, 72, 48, 1850, 3900, startUtc.AddSeconds(i));

        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            var next = new Dictionary<string, AiSmartWorkloadBaseline>(
                beforeMap, StringComparer.OrdinalIgnoreCase);
            next["Game"] = LearnWorkloadBaseline(
                next["Game"], 65, 72, 48, 1850, 3900, startUtc.AddSeconds(i));
            beforeMap = next;
        }
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        startBytes = GC.GetAllocatedBytesForCurrentThread();
        startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            afterMap["Game"] = LearnWorkloadBaseline(
                afterMap["Game"], 65, 72, 48, 1850, 3900, startUtc.AddSeconds(i));
        }
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        if (!ReferenceEquals(identity, afterMap) ||
            beforeMap.Count != afterMap.Count ||
            !beforeMap["Game"].Equals(afterMap["Game"]) ||
            JsonSerializer.Serialize(beforeMap) != JsonSerializer.Serialize(afterMap) ||
            oldBytes <= newBytes ||
            afterMap.Comparer != StringComparer.OrdinalIgnoreCase)
            throw new InvalidOperationException("AI workload memory or persisted baselines changed.");

        return $"AI_SMART_WORKLOAD_MAP_REUSE_OK samples={rounds} " +
            $"oldAllocB={oldBytes} newAllocB={newBytes} " +
            $"oldMs={oldMs:F3} newMs={newMs:F3} " +
            "learningIdentical=true jsonIdentical=true caseInsensitive=true writes=0";
    }

    private void PersistModelIfDue(AiWorkloadClass workload, DateTime now, bool forceDue = false)
    {
        if (!_modelDirty) return;
        if (_lastPersistFailureUtc != DateTime.MinValue)
        {
            TimeSpan elapsed = now - _lastPersistFailureUtc;
            if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(30))
                return;
        }
        else if (!forceDue && !AiSmartLearningPolicy.IsPersistenceDue(
                     _modelDirty,
                     _lastPersistUtc,
                     workload,
                     now))
            return;

        try
        {
            string modelPath = AiSmartModelPathPolicy.WritablePath;
            _model = SanitizeModel(_model, now);
            AtomicFileService.WriteText(
                modelPath,
                JsonSerializer.Serialize(_model));
            _lastPersistUtc = now;
            _lastPersistFailureUtc = DateTime.MinValue;
            _modelDirty = false;
        }
        catch (Exception ex)
        {
            // A failed write did not persist any learning. Keep the model dirty
            // and retry after a bounded 30-second delay on the normal FanAgent
            // tick; the usual 1-5 minute cadence applies only after success.
            // Do not spin, create a timer or repeatedly write on every tick.
            _lastPersistFailureUtc = now;
            LogService.Error("hardware", "AI Smart model persist failed", ex);
        }
    }

    internal static AiSmartRuntimeModel SanitizeModel(
        AiSmartRuntimeModel model,
        DateTime? nowOverride = null)
    {
        DateTime now = nowOverride ?? DateTime.UtcNow;

        static double Safe(double value, double minimum, double maximum, double fallback) =>
            double.IsFinite(value) && value >= minimum && value <= maximum
                ? value
                : fallback;

        DateTime updated = model.UpdatedUtc;
        if (updated != DateTime.MinValue && updated > now.AddMinutes(5))
            updated = DateTime.MinValue;

        double temp = Safe(model.AvgTempC, 5, 120, 55);
        double power = Safe(model.AvgPowerW, 0, 300, 55);
        double load = Safe(model.AvgLoadPercent, 0, 100, 20);
        double rpm = Safe(model.AvgRpm, 0, 5000, 1620);
        double clock = Safe(model.AvgClockMHz, 100, 8000, 2800);

        Dictionary<string, AiSmartWorkloadBaseline>? workloads = null;
        if (model.Workloads != null)
        {
            workloads = new Dictionary<string, AiSmartWorkloadBaseline>(
                StringComparer.OrdinalIgnoreCase);

            foreach ((string key, AiSmartWorkloadBaseline baseline) in model.Workloads)
            {
                if (!Enum.TryParse(key, true, out AiWorkloadClass workload) ||
                    workload == AiWorkloadClass.Unknown ||
                    // Enum.TryParse also accepts numeric or unknown enum
                    // values: only canonical named workload keys belong in
                    // the persisted learning model.
                    !string.Equals(key, WorkloadKey(workload), StringComparison.OrdinalIgnoreCase) ||
                    // Invalid JSON can contain a null workload value even
                    // though the record type declares baselines non-null.
                    // Discard that one entry, not every other valid profile.
                    baseline is null ||
                    baseline.Samples <= 0 ||
                    baseline.UpdatedUtc == DateTime.MinValue ||
                    baseline.UpdatedUtc > now.AddMinutes(5) ||
                    !double.IsFinite(baseline.AvgTempC) ||
                    baseline.AvgTempC is < 5 or > 120 ||
                    !double.IsFinite(baseline.AvgPowerW) ||
                    baseline.AvgPowerW is < 0 or > 300 ||
                    !double.IsFinite(baseline.AvgLoadPercent) ||
                    baseline.AvgLoadPercent is < 0 or > 100 ||
                    !double.IsFinite(baseline.AvgRpm) ||
                    baseline.AvgRpm is < 0 or > 5000 ||
                    !double.IsFinite(baseline.AvgClockMHz) ||
                    baseline.AvgClockMHz is < 100 or > 8000)
                {
                    continue;
                }

                workloads[workload.ToString()] = baseline with
                {
                    Samples = Math.Clamp(baseline.Samples, 1, 500000)
                };
            }

            if (workloads.Count == 0)
                workloads = null;
        }

        return model with
        {
            Samples = Math.Clamp(model.Samples, 0, 500000),
            AvgTempC = temp,
            AvgPowerW = power,
            AvgLoadPercent = load,
            AvgRpm = rpm,
            AvgClockMHz = clock,
            UpdatedUtc = updated,
            Workloads = workloads
        };
    }

    private static AiSmartRuntimeModel LoadModel()
    {
        try
        {
            string loadPath = AiSmartModelPathPolicy.ResolveLoadPath();
            if (!File.Exists(loadPath))
                return EmptyModel();

            // The model contains one global baseline and at most four named
            // workloads. Reject an implausibly large/corrupt JSON file before
            // deserializing; do not read, truncate or modify its contents.
            using var modelStream = new FileStream(loadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (modelStream.Length > 1024 * 1024)
                return EmptyModel();
            var model = JsonSerializer.Deserialize<AiSmartRuntimeModel>(modelStream);
            return model == null ? EmptyModel() : SanitizeModel(model);
        }
        catch
        {
            return EmptyModel();
        }
    }

    private static AiSmartRuntimeModel EmptyModel() =>
        new(0, 55, 55, 20, 1620, 2800, DateTime.MinValue);

    private string BaselineText(AiWorkloadClass workload, DateTime nowUtc)
    {
        AiSmartWorkloadBaseline baseline = ResolveBaseline(workload, nowUtc, out bool specialized);

        string source = specialized ? WorkloadKey(workload) : "global";
        if (_lastBaselineLabelText != null &&
            source == _lastBaselineLabelSource &&
            baseline == _lastBaselineLabelValue)
            return _lastBaselineLabelText;

        string label = $"{source} n={baseline.Samples} • {baseline.AvgTempC:0.0}°C • {baseline.AvgPowerW:0}W • {baseline.AvgLoadPercent:0}% • {baseline.AvgRpm:0}RPM";
        _lastBaselineLabelValue = baseline;
        _lastBaselineLabelSource = source;
        _lastBaselineLabelText = label;
        return label;
    }

    // Called each FanAgent RPM cycle: calculating the mean of at most four
    // channels should not materialize a Where iterator and a new int[].
    // Use a wide integral accumulator, matching LINQ Average(int)'s arithmetic.
    private static double AverageActiveRpm(int[]? rpms)
    {
        if (rpms == null) return 0;
        long sum = 0;
        int count = 0;
        foreach (int rpm in rpms)
        {
            if (rpm <= 100) continue;
            sum += rpm;
            count++;
        }
        return count == 0 ? 0 : (double)sum / count;
    }

    internal static string RunAverageRpmNoAllocationSmoke()
    {
        static double Legacy(int[]? rpms)
        {
            if (rpms == null) return 0;
            int[] active = rpms.Where(x => x > 100).ToArray();
            return active.Length == 0 ? 0 : active.Average();
        }
        if (AverageActiveRpm(null) != Legacy(null))
            throw new InvalidOperationException("Null RPM parity.");
        for (int count = 0; count <= 8; count++)
        {
            for (int variation = 0; variation < 64; variation++)
            {
                var values = new int[count];
                for (int i = 0; i < count; i++)
                    values[i] = (variation + i) % 7 == 0 ? 0 :
                                (variation + i) % 7 == 1 ? 100 :
                                520 + (i * 371 + variation * 41) % 5000;
                if (AverageActiveRpm(values) != Legacy(values))
                    throw new InvalidOperationException("Average RPM parity regression.");
            }
        }
        int[] live = { 1620, 0, 1790, 2140 };
        const int rounds = 16384;
        double oldCheck = 0, newCheck = 0;
        long bytesStart = GC.GetAllocatedBytesForCurrentThread();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++) oldCheck += Legacy(live);
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - bytesStart;
        bytesStart = GC.GetAllocatedBytesForCurrentThread();
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++) newCheck += AverageActiveRpm(live);
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - bytesStart;
        if (oldCheck != newCheck || newBytes >= oldBytes)
            throw new InvalidOperationException("Average RPM allocation regression.");
        return $"AI_SMART_AVG_RPM_NO_ALLOC_OK samples={rounds} " +
            $"oldAllocB={oldBytes} newAllocB={newBytes} " +
            $"oldMs={oldMs:F3} newMs={newMs:F3} " +
            "parityCases=577 identical=true writes=0";
    }

    private static double Ewma(double current, double next, double alpha) =>
        current + (next - current) * alpha;

    internal static bool TryParseBlockerPercent(string blocker, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(blocker))
            return false;

        int pct = blocker.LastIndexOf('%');
        if (pct <= 0) return false;
        int start = pct - 1;
        while (start >= 0 && (char.IsDigit(blocker[start]) || blocker[start] == '.' || blocker[start] == ','))
            start--;
        ReadOnlySpan<char> digits = blocker.AsSpan(start + 1, pct - start - 1);
        if (digits.IndexOf(',') < 0)
            return double.TryParse(digits,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out percent);

        // Preserve decimal-comma normalization, but avoid Substring and
        // Replace allocations on the recurring fan-pressure assessment.
        Span<char> normalized = digits.Length <= 64
            ? stackalloc char[digits.Length] : new char[digits.Length];
        digits.CopyTo(normalized);
        normalized.Replace(',', '.');
        return double.TryParse(normalized,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out percent);
    }
}
