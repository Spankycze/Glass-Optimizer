using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace SpankyhoOptimizer.Services;

public static class CpuTemperatureService
{
    private static readonly object Gate = new();
    private static Computer? _computer;
    private static bool _cpuOnlyMode;
    private static DateTime _lastMissingTemperatureLogUtc = DateTime.MinValue;
    private static bool _temperatureSensorMissing;

    public sealed record CpuTemperatureSample(double? Celsius, string Sensor, string CpuName);

    public sealed record CpuTelemetrySample(
        double? Celsius,
        string TemperatureSensor,
        string CpuName,
        double? LoadPercent,
        double? EffectiveClockMHz,
        double? PackagePowerW);

    public sealed record GpuTelemetrySample(
        double? Celsius,
        string TemperatureSensor,
        string GpuName,
        double? LoadPercent);

    public static CpuTemperatureSample Read()
    {
        CpuTelemetrySample sample = ReadTelemetry();
        return new CpuTemperatureSample(sample.Celsius, sample.TemperatureSensor, sample.CpuName);
    }

    public static void ConfigureCpuOnlyMode()
    {
        lock (Gate)
        {
            if (_computer == null)
                _cpuOnlyMode = true;
        }
    }

    public static CpuTelemetrySample ReadTelemetry()
    {
        lock (Gate)
        {
            try
            {
                EnsureComputer();

                var temperatures = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                var loads = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                var clocks = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                var powers = new List<(IHardware Hardware, ISensor Sensor, float Value)>();

                foreach (IHardware hardware in _computer!.Hardware)
                {
                    UpdateTree(hardware);
                    CollectSensors(hardware, temperatures, loads, clocks, powers);
                }

                var validTemps = temperatures.Where(x => !IsGpuHardware(x.Hardware) && x.Value > 5 && x.Value < 150).ToArray();
                var preferredTemp =
                    validTemps.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "Tctl/Tdie", "Tdie", "CPU Package", "Package"))
                    is var p1 && p1.Sensor != null ? p1 :
                    validTemps.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "CPU", "Core", "CCD"))
                    is var p2 && p2.Sensor != null ? p2 :
                    validTemps.FirstOrDefault(x => ContainsAny(x.Hardware.Name, "CPU", "Ryzen"))
                    is var p3 && p3.Sensor != null ? p3 :
                    validTemps.OrderByDescending(x => x.Value).FirstOrDefault();

                string cpuName = preferredTemp.Hardware?.Name
                    ?? loads.FirstOrDefault().Hardware?.Name
                    ?? clocks.FirstOrDefault().Hardware?.Name
                    ?? "CPU";

                double? celsius = preferredTemp.Sensor != null
                    ? Math.Round(preferredTemp.Value, 1)
                    : null;

                string tempSensor = preferredTemp.Sensor?.Name ?? "CPU senzor nedostupný";

                var validLoads = loads.Where(x => !IsGpuHardware(x.Hardware) && x.Value >= 0 && x.Value <= 100).ToArray();
                var preferredLoad =
                    validLoads.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "CPU Total", "Total"))
                    is var l1 && l1.Sensor != null ? l1 :
                    validLoads.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "CPU", "Package"))
                    is var l2 && l2.Sensor != null ? l2 :
                    validLoads.FirstOrDefault();

                double? loadPercent = preferredLoad.Sensor != null
                    ? Math.Round(preferredLoad.Value, 1)
                    : null;

                var validClocks = clocks
                    .Where(x => !IsGpuHardware(x.Hardware))
                    .Where(x => x.Value > 100 && x.Value < 10000)
                    .Where(x => ContainsAny(x.Sensor.Name, "Effective", "Core", "CPU"))
                    .ToArray();

                double? effectiveClock = validClocks.Length > 0
                    ? Math.Round(validClocks.Average(x => (double)x.Value), 0)
                    : null;

                var validPowers = powers.Where(x => !IsGpuHardware(x.Hardware) && x.Value > 0 && x.Value < 1000).ToArray();
                var preferredPower =
                    validPowers.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "Package", "CPU Package"))
                    is var w1 && w1.Sensor != null ? w1 :
                    validPowers.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "PPT", "CPU", "Cores"))
                    is var w2 && w2.Sensor != null ? w2 :
                    validPowers.OrderByDescending(x => x.Value).FirstOrDefault();

                double? packagePower = preferredPower.Sensor != null
                    ? Math.Round(preferredPower.Value, 1)
                    : null;

                if (celsius == null)
                {
                    _temperatureSensorMissing = true;
                    DateTime now = DateTime.UtcNow;
                    if (HardwareTelemetryLogPolicy.ShouldLogMissingSensor(now, _lastMissingTemperatureLogUtc))
                    {
                        string dump = string.Join(" | ", temperatures
                            .Take(24)
                            .Select(x => $"{x.Hardware.Name}/{x.Sensor.Name}={x.Value:0.0}"));
                        LogService.Info("hardware", $"CPU_TEMP_NO_VALID_SENSOR count={temperatures.Count} sensors={dump}");
                        _lastMissingTemperatureLogUtc = now;
                    }
                }
                else if (_temperatureSensorMissing)
                {
                    _temperatureSensorMissing = false;
                    _lastMissingTemperatureLogUtc = DateTime.MinValue;
                    LogService.Info("hardware", $"CPU_TEMP_SENSOR_RECOVERED sensor={tempSensor} value={celsius:0.0}");
                }

                return new CpuTelemetrySample(
                    celsius,
                    tempSensor,
                    cpuName,
                    loadPercent,
                    effectiveClock,
                    packagePower);
            }
            catch (Exception ex)
            {
                LogService.Error("hardware", "CPU telemetry read failed", ex);
                return new CpuTelemetrySample(
                    null,
                    "CPU senzor nedostupný",
                    "CPU",
                    null,
                    null,
                    null);
            }
        }
    }

    public static GpuTelemetrySample ReadGpuTelemetry()
    {
        lock (Gate)
        {
            try
            {
                EnsureComputer();
                var temperatures = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                var loads = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                var clocks = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                var powers = new List<(IHardware Hardware, ISensor Sensor, float Value)>();

                foreach (IHardware hardware in _computer!.Hardware.Where(IsGpuHardware))
                {
                    UpdateTree(hardware);
                    CollectSensors(hardware, temperatures, loads, clocks, powers);
                }

                var validTemps = temperatures.Where(x => x.Value > 5 && x.Value < 150).ToArray();
                var preferredTemp =
                    validTemps.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "GPU Core", "Core"))
                    is var t1 && t1.Sensor != null ? t1 :
                    validTemps.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "Hot Spot", "Hotspot"))
                    is var t2 && t2.Sensor != null ? t2 :
                    validTemps.OrderByDescending(x => x.Value).FirstOrDefault();

                var validLoads = loads.Where(x => x.Value >= 0 && x.Value <= 100).ToArray();
                var preferredLoad =
                    validLoads.FirstOrDefault(x => ContainsAny(x.Sensor.Name, "GPU Core", "Core", "D3D 3D"))
                    is var l1 && l1.Sensor != null ? l1 :
                    validLoads.OrderByDescending(x => x.Value).FirstOrDefault();

                string gpuName = preferredTemp.Hardware?.Name
                    ?? preferredLoad.Hardware?.Name
                    ?? _computer.Hardware.FirstOrDefault(IsGpuHardware)?.Name
                    ?? "GPU";

                return new GpuTelemetrySample(
                    preferredTemp.Sensor != null ? Math.Round(preferredTemp.Value, 1) : null,
                    preferredTemp.Sensor?.Name ?? "GPU senzor nedostupný",
                    gpuName,
                    preferredLoad.Sensor != null ? Math.Round(preferredLoad.Value, 1) : null);
            }
            catch (Exception ex)
            {
                LogService.Error("hardware", "GPU telemetry read failed", ex);
                return new GpuTelemetrySample(null, "GPU senzor nedostupný", "GPU", null);
            }
        }
    }

    private static void EnsureComputer()
    {
        if (_computer != null) return;

        bool fullHardware = !_cpuOnlyMode;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = fullHardware,
            IsControllerEnabled = fullHardware,
            IsGpuEnabled = fullHardware,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false
        };
        _computer.Open();
    }

    private static void CollectSensors(
        IHardware hardware,
        List<(IHardware Hardware, ISensor Sensor, float Value)> temperatures,
        List<(IHardware Hardware, ISensor Sensor, float Value)> loads,
        List<(IHardware Hardware, ISensor Sensor, float Value)> clocks,
        List<(IHardware Hardware, ISensor Sensor, float Value)> powers)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (!sensor.Value.HasValue) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    temperatures.Add((hardware, sensor, sensor.Value.Value));
                    break;
                case SensorType.Load:
                    loads.Add((hardware, sensor, sensor.Value.Value));
                    break;
                case SensorType.Clock:
                    clocks.Add((hardware, sensor, sensor.Value.Value));
                    break;
                case SensorType.Power:
                    powers.Add((hardware, sensor, sensor.Value.Value));
                    break;
            }
        }

        foreach (IHardware sub in hardware.SubHardware)
            CollectSensors(sub, temperatures, loads, clocks, powers);
    }

    private static bool IsGpuHardware(IHardware hardware) =>
        hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel ||
        hardware.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
        hardware.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
        hardware.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
        hardware.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static void UpdateTree(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware sub in hardware.SubHardware)
            UpdateTree(sub);
    }

    public static void Dispose()
    {
        lock (Gate)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}
