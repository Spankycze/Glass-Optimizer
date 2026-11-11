using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Learns, per local day-of-week and hour, how often the user is active or
/// gaming. Used only to DEFER new optional maintenance/A/B/A trials into
/// historically quiet hours. It never mutates Windows or the power plan.
/// </summary>
internal sealed class AiWorkloadForecastService
{
    private const int Buckets = 7 * 24;
    private const double Alpha = 0.05;          // ~20 samples of memory per bucket
    private const int MinSamples = 30;          // 30 one-minute samples before trusting a bucket
    private const double BusyThreshold = 0.70;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(10);

    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "ai-workload-forecast.json");

    private readonly string? _path;
    private double[] _active = new double[Buckets];
    private double[] _game = new double[Buckets];
    private int[] _count = new int[Buckets];
    private long _lastSampleStamp;
    private long _lastPersistStamp;
    private bool _dirty;

    public AiWorkloadForecastService() : this(DefaultPath) { }

    // path == null: in-memory only (smoke).
    internal AiWorkloadForecastService(string? path)
    {
        _path = path;
        Load();
    }

    private sealed record State(double[] Active, double[] Game, int[] Count);

    private static int Bucket(DateTime local) => (int)local.DayOfWeek * 24 + local.Hour;

    /// <summary>Records at most one sample per minute (monotonic throttle).</summary>
    public void Record(DateTime local, bool userActive, bool gameActive)
    {
        long stamp = Stopwatch.GetTimestamp();
        if (_lastSampleStamp != 0 &&
            Stopwatch.GetElapsedTime(_lastSampleStamp, stamp) < SampleInterval)
            return;
        _lastSampleStamp = stamp;
        Add(local, userActive || gameActive, gameActive);

        if (_lastPersistStamp == 0) _lastPersistStamp = stamp;
        else if (Stopwatch.GetElapsedTime(_lastPersistStamp, stamp) >= PersistInterval)
        {
            _lastPersistStamp = stamp;
            Persist();
        }
    }

    internal void Add(DateTime local, bool active, bool game)
    {
        int b = Bucket(local);
        if (_count[b] == 0)
        {
            _active[b] = active ? 1 : 0;
            _game[b] = game ? 1 : 0;
        }
        else
        {
            _active[b] += Alpha * ((active ? 1 : 0) - _active[b]);
            _game[b] += Alpha * ((game ? 1 : 0) - _game[b]);
        }
        if (_count[b] < int.MaxValue) _count[b]++;
        _dirty = true;
    }

    /// <summary>
    /// True when the current or next hour is learned (enough samples) and the
    /// user is usually active or gaming then. Unknown hours never defer work.
    /// </summary>
    public bool ExpectsActivitySoon(DateTime local) =>
        IsBusy(Bucket(local)) || IsBusy(Bucket(local.AddHours(1)));

    private bool IsBusy(int b)
    {
        if (_count[b] < MinSamples) return false;
        double p = Math.Max(_active[b], _game[b]);
        if (p < BusyThreshold) return false;
        // One-sided 90% normal lower bound. A bucket near the threshold must
        // collect more samples before it may defer AI maintenance; 30 noisy
        // samples are not enough.
        double standardError = Math.Sqrt(Math.Max(0, p * (1 - p)) / _count[b]);
        return p - 1.28 * standardError >= BusyThreshold - 0.10;
    }

    /// <summary>
    /// Get statistics about learned patterns for UI display.
    /// </summary>
    public (int LearnedHours, int BusyHours, int QuietHours, double AvgActivity) GetStatistics()
    {
        int learned = 0, busy = 0, quiet = 0;
        double totalActivity = 0;
        int activityCount = 0;

        for (int b = 0; b < Buckets; b++)
        {
            if (_count[b] >= MinSamples)
            {
                learned++;
                double activity = Math.Max(_active[b], _game[b]);
                totalActivity += activity;
                activityCount++;

                if (activity >= BusyThreshold)
                    busy++;
                else if (activity < 0.3)
                    quiet++;
            }
        }

        double avgActivity = activityCount > 0 ? totalActivity / activityCount : 0;
        return (learned, busy, quiet, avgActivity);
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            State? s = JsonSerializer.Deserialize<State>(File.ReadAllBytes(_path));
            if (s?.Active?.Length == Buckets && s.Game?.Length == Buckets && s.Count?.Length == Buckets)
            {
                for (int i = 0; i < Buckets; i++)
                {
                    // Reject corrupt values instead of trusting them.
                    if (!(s.Active[i] is >= 0 and <= 1) || !(s.Game[i] is >= 0 and <= 1) || s.Count[i] < 0)
                        return;
                }
                _active = s.Active; _game = s.Game; _count = s.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt/unreadable model: start fresh, the model is advisory only.
        }
    }

    public void Persist()
    {
        if (_path is null || !_dirty) return;
        try
        {
            AtomicFileService.WriteAllBytesAtomic(_path,
                JsonSerializer.SerializeToUtf8Bytes(new State(_active, _game, _count)));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Info("ai-system", "Workload forecast save failed: " + ex.Message);
        }
    }

    internal static string RunFunctionalSmoke()
    {
        var f = new AiWorkloadForecastService(null);
        var evening = new DateTime(2026, 10, 26, 20, 0, 0, DateTimeKind.Local); // Monday 20:00
        if (f.ExpectsActivitySoon(evening))
            throw new InvalidOperationException("Forecast deferred work without learned evidence.");
        for (int i = 0; i < MinSamples - 1; i++) f.Add(evening, true, true);
        if (f.ExpectsActivitySoon(evening))
            throw new InvalidOperationException("Forecast trusted a bucket below the sample minimum.");
        f.Add(evening, true, true);
        if (!f.ExpectsActivitySoon(evening) || !f.ExpectsActivitySoon(evening.AddHours(-1)))
            throw new InvalidOperationException("Forecast did not learn a busy hour.");
        var night = evening.AddHours(7); // Tuesday 03:00
        for (int i = 0; i < MinSamples; i++) f.Add(night, false, false);
        if (f.ExpectsActivitySoon(night))
            throw new InvalidOperationException("Forecast treated a quiet hour as busy.");
        for (int i = 0; i < 200; i++) f.Add(evening, false, false);
        if (f.ExpectsActivitySoon(evening))
            throw new InvalidOperationException("Forecast did not adapt to changed habits.");

        // Test statistics
        var stats = f.GetStatistics();
        if (stats.LearnedHours < 2 || stats.QuietHours < 1)
            throw new InvalidOperationException("Forecast statistics are incorrect.");

        return "AI_WORKLOAD_FORECAST_SMOKE_OK busyLearned=true quietLearned=true adapts=true unknownNeverDefers=true statistics=true";
    }
}
