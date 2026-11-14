using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static partial class FanProfileService
{
    private static async Task HandleTrayActionAsync(FanTrayAction action, CancellationToken token)
    {
        FanProfile current = Load();

        if (action == FanTrayAction.ToggleAuto65)
        {
            bool enabled = !current.CpuTempTargetEnabled;
            Save(current with
            {
                CpuTempTargetEnabled = enabled,
                CpuTempTargetC = 65,
                MotherboardPwm = false,
                AutoCurveEnabled = false,
                ChannelCurveEnabled = new[] { false, false, false, false }
            });
            if (enabled)
                LianLiSlInfinityService.SetAllFanRpm(1620);
            return;
        }

        int rpm = action switch
        {
            FanTrayAction.Set700 => 700,
            FanTrayAction.Set1100 => 1100,
            FanTrayAction.Set1620 => 1620,
            FanTrayAction.Set2100 => 2100,
            _ => 0
        };

        if (rpm > 0)
        {
            Save(current with
            {
                TargetRpm = rpm,
                CpuTempTargetEnabled = false,
                AutoCurveEnabled = false,
                ChannelCurveEnabled = new[] { false, false, false, false },
                MotherboardPwm = false
            });
            LianLiSlInfinityService.SetAllFanRpm(rpm);
            return;
        }

        if (action == FanTrayAction.OpenFans)
        {
            string exe = ResolveMainAppPath();
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                _ = ProcessRunner.StartDetached(
                    exe,
                    useShellExecute: true);
            }
        }

        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
    }

    public static async Task ApplySavedProfileAsync(CancellationToken token = default)
    {
        FanProfile profile = Load();

        for (int attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var controller = LianLiSlInfinityService.Detect();
                if (!controller.Present)
                {
                    await Task.Delay(2500, token).ConfigureAwait(false);
                    continue;
                }

                if (profile.MotherboardPwm)
                {
                    LianLiSlInfinityService.SetAllMotherboardPwm();
                }
                else if (profile.CpuTempTargetEnabled)
                {
                    var temp = HardwareTelemetryBridgeService.ReadBestTemperature();
                    int rpm = temp.Celsius is double c
                        ? CalculateTargetTemperatureRpm(Math.Clamp(profile.TargetRpm, 700, LianLiSlInfinityService.MaximumRpm), c, profile.CpuTempTargetC)
                        : Math.Clamp(profile.TargetRpm, 700, LianLiSlInfinityService.MaximumRpm);
                    LianLiSlInfinityService.SetAllFanRpm(rpm);
                }
                else
                {
                    var temp = HardwareTelemetryBridgeService.ReadBestTemperature();
                    bool anyPerChannelCurve = Array.IndexOf(profile.ChannelCurveEnabled, true) >= 0;

                    if (!profile.AutoCurveEnabled && !anyPerChannelCurve)
                    {
                        LianLiSlInfinityService.SetAllFanRpm(profile.TargetRpm);
                    }
                    else
                    {
                        for (int channel = 0; channel < 4; channel++)
                        {
                            int rpm = profile.ChannelCurveEnabled[channel] && temp.Celsius is double c1
                                ? CalculateChannelCurveRpm(profile, channel, c1)
                                : profile.AutoCurveEnabled && temp.Celsius is double c2
                                    ? CalculateCurveRpm(profile, c2)
                                    : profile.TargetRpm;

                            LianLiSlInfinityService.SetFanRpm(channel, rpm, profile.ChannelMaxRpms[channel]);
                        }
                    }
                }

                if (profile.ArgbSync)
                {
                    LianLiSlInfinityService.SetArgbSync(true);
                }
                else if (TryParseHex(profile.HexColor, out byte r, out byte g, out byte b))
                {
                    LianLiSlInfinityService.SetAllEffect(
                        profile.EffectMode,
                        profile.EffectSpeed,
                        profile.EffectDirection,
                        r, g, b,
                        profile.BrightnessPercent);
                }
                return;
            }
            catch (Exception ex)
            {
                LogService.Error("lianli", $"Fan agent apply attempt {attempt + 1} failed", ex);
                if (attempt >= 11) return;
                await Task.Delay(2500, token).ConfigureAwait(false);
            }
        }
    }

    private static string BuildTrayTooltip(
        CpuTemperatureService.CpuTelemetrySample telemetry,
        int[]? rpms,
        string powerMode,
        bool auto65,
        bool gameActive,
        AiSmartRuntimeDecision ai)
    {
        string cpu = telemetry.Celsius is double c ? $"{c:0}°C" : "—°C";
        string fanText;

        fanText = FanRpmTooltipPolicy.FormatFirstFour(rpms);

        string mode = auto65 ? "AUTO65" : powerMode;
        if (gameActive)
            mode += " • DIABLO PERF";
        if (ai.Enabled)
            mode += ai.AnomalyScore >= 55 ? $" • AI {ai.AnomalyScore:0}" : " • AI AUTO";
        return $"CPU {cpu} • {fanText} • {mode}";
    }

    // Same-host measured allocation cost of building tooltip strings on each
    // one-second agent tick vs only on due/safety-transition ticks.
    internal static string RunTrayCompositionBenchmark()
    {
        var telemetry = new CpuTemperatureService.CpuTelemetrySample(
            56, "fixture", "CPU", 15, 3800, 65);
        var ai = new AiSmartRuntimeDecision(
            false, "OFF", "None", 0, 0, 0,
            "smoke", "n=0", "none", 0, false, false);
        int[] rpms = { 1620, 1620, 1620, 1620 };
        const int iterations = 120;
        string latest = string.Empty;
        for (int i = 0; i < 20; i++)
            latest = BuildTrayTooltip(telemetry, rpms, "Balanced", true, false, ai);
        long baselineStart = GC.GetAllocatedBytesForCurrentThread();
        long clock = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            rpms[0] = 1600 + i;
            latest = BuildTrayTooltip(telemetry, rpms, "Balanced", true, false, ai);
        }
        double baselineMs = System.Diagnostics.Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long baselineAllocated = GC.GetAllocatedBytesForCurrentThread() - baselineStart;
        var start = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        DateTime lastUpdated = DateTime.MinValue;
        bool hasPublished = false;
        int rendered = 0;
        long throttledStart = GC.GetAllocatedBytesForCurrentThread();
        clock = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            DateTime now = start.AddSeconds(i);
            if (!FanTrayIconService.NeedsTooltipCompose(
                    hasPublished, FanTrayState.Ok, FanTrayState.Ok, now - lastUpdated))
                continue;
            rpms[0] = 1600 + i;
            latest = BuildTrayTooltip(telemetry, rpms, "Balanced", true, false, ai);
            lastUpdated = now;
            hasPublished = true;
            rendered++;
        }
        double throttledMs = System.Diagnostics.Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long throttledAllocated = GC.GetAllocatedBytesForCurrentThread() - throttledStart;
        if (rendered != 24 || baselineAllocated <= throttledAllocated ||
            string.IsNullOrWhiteSpace(latest))
            throw new InvalidOperationException("Fan tooltip composition allocation budget failed.");
        return $"FAN_TRAY_FORMAT_BENCH_OK ticks={iterations} originalFormats={iterations} " +
            $"throttledFormats={rendered} originalMs={baselineMs:F3} throttledMs={throttledMs:F3} " +
            $"originalAllocatedB={baselineAllocated} throttledAllocatedB={throttledAllocated}";
    }

    public static int CalculateTargetTemperatureRpm(int currentRpm, double temperature, int targetC) =>
        CalculateTargetTemperatureRpm(currentRpm, temperature, targetC, 0);

    public static int CalculateTargetTemperatureRpm(int currentRpm, double temperature, int targetC, double trendPerTick)
    {
        const int Auto65MinimumRpm = 1620;
        targetC = Math.Clamp(targetC, 50, 85);
        currentRpm = Math.Clamp(currentRpm, Auto65MinimumRpm, LianLiSlInfinityService.MaximumRpm);

        double delta = temperature - targetC;
        int step = delta switch
        {
            >= 6 => 350,
            >= 3 => 250,
            >= 1 => 150,
            >= 0 => 100,
            <= -10 => -120,
            <= -7 => -100,
            <= -4 => -75,
            _ => 0
        };

        if (trendPerTick >= 0.8)
            step += 150;
        else if (trendPerTick >= 0.4)
            step += 75;
        else if (trendPerTick <= -0.8 && step < 0)
            step = (int)Math.Ceiling(step * 0.5);

        return Math.Clamp(currentRpm + step, Auto65MinimumRpm, LianLiSlInfinityService.MaximumRpm);
    }

    public static int CalculateCurveRpm(FanProfile profile, double temperature)
    {
        return InterpolateCurve(
            temperature,
            profile.CurveTemp1, profile.CurveRpm1,
            profile.CurveTemp2, profile.CurveRpm2,
            profile.CurveTemp3, profile.CurveRpm3,
            profile.CurveTemp4, profile.CurveRpm4,
            profile.TargetRpm);
    }

    public static int CalculateChannelCurveRpm(FanProfile profile, int channel, double temperature)
    {
        channel = Math.Clamp(channel, 0, 3);
        return InterpolateCurve(
            temperature,
            profile.CurveTemp1, profile.ChannelCurveRpm1[channel],
            profile.CurveTemp2, profile.ChannelCurveRpm2[channel],
            profile.CurveTemp3, profile.ChannelCurveRpm3[channel],
            profile.CurveTemp4, profile.ChannelCurveRpm4[channel],
            profile.ChannelTargetRpms[channel]);
    }

    // Fan safety hot path: preserve the exact old four-point interpolation
    // without allocating a new (temperature,RPM)[] on each channel/tick.
    private static int InterpolateCurve(double temperature,
        int t1, int r1, int t2, int r2, int t3, int r3, int t4, int r4, int fallback)
    {
        if (temperature <= t1) return r1;
        if (temperature >= t4) return r4;
        // Negated comparisons also preserve the legacy NaN fallback to the
        // first segment (rather than silently using the profile's target RPM).
        if (!(temperature > t2)) return InterpolateCurveSegment(temperature, t1, r1, t2, r2);
        if (!(temperature > t3)) return InterpolateCurveSegment(temperature, t2, r2, t3, r3);
        if (!(temperature > t4)) return InterpolateCurveSegment(temperature, t3, r3, t4, r4);
        return fallback;
    }

    private static int InterpolateCurveSegment(double temperature, int fromT, int fromR, int toT, int toR)
    {
        double span = Math.Max(1, toT - fromT);
        double t = (temperature - fromT) / span;
        return Math.Clamp(
            (int)Math.Round(fromR + (toR - fromR) * t),
            LianLiSlInfinityService.MinimumSafeRpm,
            LianLiSlInfinityService.MaximumRpm);
    }

    // Regression of the allocation-free fan controller against the former
    // exact algorithm, including boundaries and non-finite sensor values.
    // No HID writes: these are pure arithmetic checks.
    internal static string RunFanCurveAllocationSmoke()
    {
        static int LegacyCurve(double temp, int t1, int r1, int t2, int r2,
            int t3, int r3, int t4, int r4, int fallback)
        {
            var points = new[] { (T: t1, R: r1), (T: t2, R: r2),
                                 (T: t3, R: r3), (T: t4, R: r4) };
            if (temp <= points[0].T) return points[0].R;
            if (temp >= points[3].T) return points[3].R;
            for (int i = 0; i < points.Length - 1; i++)
            {
                if (temp > points[i + 1].T) continue;
                double span = Math.Max(1, points[i + 1].T - points[i].T);
                double ratio = (temp - points[i].T) / span;
                return Math.Clamp((int)Math.Round(
                    points[i].R + (points[i + 1].R - points[i].R) * ratio),
                    LianLiSlInfinityService.MinimumSafeRpm,
                    LianLiSlInfinityService.MaximumRpm);
            }
            return fallback;
        }

        const int cases = 4096;
        for (int i = 0; i < cases; i++)
        {
            double temp = i % 127 - 16 + (i % 5) / 5d;
            if (i == 0) temp = double.NaN;
            if (i == 1) temp = double.PositiveInfinity;
            if (i == 2) temp = double.NegativeInfinity;
            int r2 = 1050 + i % 320;
            int oldRpm = LegacyCurve(temp, 30, 700, 47, r2, 67, 1790, 89, 2220, 1450);
            int newRpm = InterpolateCurve(temp, 30, 700, 47, r2, 67, 1790, 89, 2220, 1450);
            if (oldRpm != newRpm)
                throw new InvalidOperationException(
                    $"Fan RPM curve mismatch case={i} temp={temp} old={oldRpm} new={newRpm}");
        }

        const int rounds = 16384;
        int baselineChecksum = 0, optimizedChecksum = 0;
        long startAllocated = GC.GetAllocatedBytesForCurrentThread();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
            baselineChecksum += LegacyCurve(32 + i % 64, 30, 700, 47, 1100, 67, 1790, 89, 2220, 1450);
        double beforeMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long beforeB = GC.GetAllocatedBytesForCurrentThread() - startAllocated;

        startAllocated = GC.GetAllocatedBytesForCurrentThread();
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
            optimizedChecksum += InterpolateCurve(32 + i % 64, 30, 700, 47, 1100, 67, 1790, 89, 2220, 1450);
        double afterMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long afterB = GC.GetAllocatedBytesForCurrentThread() - startAllocated;
        if (baselineChecksum != optimizedChecksum || beforeB <= afterB)
            throw new InvalidOperationException("Fan curve arithmetic/allocation regression.");
        return $"FAN_CURVE_NO_ALLOC_SMOKE_OK cases={cases} rounds={rounds} " +
               $"baselineB={beforeB} optimizedB={afterB} " +
               $"baselineMs={beforeMs:F3} optimizedMs={afterMs:F3} " +
               "rpmIdentical=true noHidWrites=true";
    }

    private static FanProfile Normalize(FanProfile p)
    {
        int t1 = Math.Clamp(p.CurveTemp1, 25, 70);
        int t2 = Math.Clamp(Math.Max(p.CurveTemp2, t1 + 5), 30, 80);
        int t3 = Math.Clamp(Math.Max(p.CurveTemp3, t2 + 5), 40, 90);
        int t4 = Math.Clamp(Math.Max(p.CurveTemp4, t3 + 5), 50, 100);

        return p with
        {
            TargetRpm = Math.Clamp(p.TargetRpm, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            CurveTemp1 = t1,
            CurveTemp2 = t2,
            CurveTemp3 = t3,
            CurveTemp4 = t4,
            CurveRpm1 = Math.Clamp(p.CurveRpm1, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            CurveRpm2 = Math.Clamp(p.CurveRpm2, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            CurveRpm3 = Math.Clamp(p.CurveRpm3, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            CurveRpm4 = Math.Clamp(p.CurveRpm4, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            BrightnessPercent = Math.Clamp(p.BrightnessPercent, 25, 100),
            ChannelTargetRpms = NormalizeRpmArray(p.ChannelTargetRpms, p.TargetRpm),
            ChannelCurveEnabled = NormalizeBoolArray(p.ChannelCurveEnabled),
            ChannelCurveRpm1 = NormalizeRpmArray(p.ChannelCurveRpm1, p.CurveRpm1),
            ChannelCurveRpm2 = NormalizeRpmArray(p.ChannelCurveRpm2, p.CurveRpm2),
            ChannelCurveRpm3 = NormalizeRpmArray(p.ChannelCurveRpm3, p.CurveRpm3),
            ChannelCurveRpm4 = NormalizeRpmArray(p.ChannelCurveRpm4, p.CurveRpm4),
            ChannelRgbHex = NormalizeColorArray(p.ChannelRgbHex, p.HexColor),
            ChannelMaxRpms = NormalizeMaxRpmArray(p.ChannelMaxRpms),
            ChannelFanCounts = NormalizeFanCountArray(p.ChannelFanCounts),
            CpuTempTargetC = Math.Clamp(p.CpuTempTargetC, 50, 85)
        };
    }

    private static int[] NormalizeRpmArray(int[]? source, int fallback)
    {
        var result = new int[4];
        for (int i = 0; i < result.Length; i++)
        {
            int value = source != null && i < source.Length ? source[i] : fallback;
            result[i] = Math.Clamp(value, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm);
        }
        return result;
    }

    private static bool[] NormalizeBoolArray(bool[]? source)
    {
        var result = new bool[4];
        for (int i = 0; i < result.Length; i++)
            result[i] = source != null && i < source.Length && source[i];
        return result;
    }

    private static int[] NormalizeMaxRpmArray(int[]? source)
    {
        var result = new int[4];
        for (int i = 0; i < result.Length; i++)
        {
            int value = source != null && i < source.Length ? source[i] : LianLiSlInfinityService.MaximumRpm;
            result[i] = Math.Clamp(value, 1200, 2600);
        }
        return result;
    }

    private static int[] NormalizeFanCountArray(int[]? source)
    {
        var result = new int[4];
        for (int i = 0; i < result.Length; i++)
        {
            int value = source != null && i < source.Length ? source[i] : 0;
            result[i] = Math.Clamp(value, 0, 4);
        }
        return result;
    }

    private static string[] NormalizeColorArray(string[]? source, string fallback)
    {
        var result = new string[4];
        for (int i = 0; i < result.Length; i++)
        {
            string value = source != null && i < source.Length ? source[i] : fallback;
            result[i] = TryParseHex(value, out _, out _, out _) ? value.ToUpperInvariant() : fallback;
        }
        return result;
    }

    private static bool TryParseHex(string? value, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string text = (value ?? string.Empty).Trim().TrimStart('#');
        return text.Length == 6
            && byte.TryParse(text.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(text.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(text.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
