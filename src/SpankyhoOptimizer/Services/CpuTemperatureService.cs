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
    private static bool _includeMotherboardSensors;
    private static DateTime _lastMissingTemperatureLogUtc = DateTime.MinValue;
    private static bool _temperatureSensorMissing;

    public sealed record CpuTemperatureSample(double? Celsius, string Sensor, string CpuName);

    public sealed record CpuTelemetrySample(
        double? Celsius,
        string TemperatureSensor,
        string CpuName,
        double? LoadPercent,
        double? EffectiveClockMHz,
        double? PackagePowerW,
        double? MotherboardTempC = null,
        string? MotherboardSensor = null);

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

    // The privileged bridge can read board temperatures without probing
    // GPUs, storage or USB controllers on its two-second CPU telemetry loop.
    public static void ConfigureCpuAndMotherboardMode()
    {
        lock (Gate)
        {
            if (_computer == null)
            {
                _cpuOnlyMode = true;
                _includeMotherboardSensors = true;
            }
        }
    }

    public static CpuTelemetrySample ReadTelemetry()
    {
        lock (Gate)
        {
            try
            {
                EnsureComputer();

                // The privileged bridge runs this every two seconds.
                // Stream all four telemetry kinds into fixed-size winners in
                // provider order; no per-tick sensor Lists, LINQ arrays or
                // repeated provider value reads. The Gate still serializes
                // LibreHardwareMonitor and fan temperature safety.
                var selection = new CpuTelemetrySelectionReducer();
                foreach (IHardware hardware in _computer!.Hardware)
                {
                    UpdateTree(hardware);
                    CollectCpuSensorSelections(hardware, ref selection);
                }
                CpuTelemetrySample snapshot = selection.Build();

                if (!selection.HasCpuTemperature)
                {
                    _temperatureSensorMissing = true;
                    DateTime now = DateTime.UtcNow;
                    if (HardwareTelemetryLogPolicy.ShouldLogMissingSensor(now, _lastMissingTemperatureLogUtc))
                    {
                        // Preserve sensor-failure evidence, but allocate a
                        // full dump ONLY if the CPU temperature is missing.
                        var temperatures = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                        var loads = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                        var clocks = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                        var powers = new List<(IHardware Hardware, ISensor Sensor, float Value)>();
                        foreach (IHardware hardware in _computer.Hardware)
                            CollectSensors(hardware, temperatures, loads, clocks, powers);
                        string dump = string.Join(" | ", temperatures
                            .Take(24)
                            .Select(x => $"{x.Hardware.Name}/{x.Sensor.Name}={x.Value:0.0}"));
                        LogService.Info("hardware",
                            $"CPU_TEMP_NO_VALID_SENSOR count={temperatures.Count} sensors={dump}");
                        _lastMissingTemperatureLogUtc = now;
                    }
                }
                else if (_temperatureSensorMissing)
                {
                    _temperatureSensorMissing = false;
                    _lastMissingTemperatureLogUtc = DateTime.MinValue;
                    LogService.Info("hardware",
                        $"CPU_TEMP_SENSOR_RECOVERED sensor={snapshot.TemperatureSensor} value={snapshot.Celsius:0.0}");
                }

                return snapshot;
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
                var selection = new GpuTelemetrySelectionReducer();
                string? firstGpuName = null;
                foreach (IHardware hardware in _computer!.Hardware)
                {
                    if (!IsGpuHardware(hardware)) continue;
                    firstGpuName ??= hardware.Name;
                    UpdateTree(hardware);
                    CollectGpuSensorSelections(hardware, ref selection);
                }

                return selection.Build(firstGpuName);
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
            IsMotherboardEnabled = fullHardware || _includeMotherboardSensors,
            IsControllerEnabled = fullHardware,
            IsGpuEnabled = fullHardware,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false
        };
        try
        {
            _computer.Open();
        }
        catch when (_includeMotherboardSensors)
        {
            // Board chipset probing is optional. Never let it break the
            // previously working CPU/fan telemetry on this machine.
            try { _computer.Close(); } catch { }
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsGpuEnabled = false,
                IsMemoryEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false
            };
            _includeMotherboardSensors = false;
            _computer.Open();
        }
    }

    // GPU overview needs only temperature and load. Walk each native sensor
    // once in the original provider order, retaining only the priority
    // winners; the CPU/fan telemetry collector remains unchanged.
    private static void CollectGpuSensorSelections(
        IHardware hardware, ref GpuTelemetrySelectionReducer selection)
    {
        string hardwareName = hardware.Name;
        foreach (ISensor sensor in hardware.Sensors)
        {
            SensorType kind = sensor.SensorType;
            // Clocks, voltage and power are not displayed by Overview GPU
            // temperature/load meters. Skip their provider value getters.
            if (kind is not (SensorType.Temperature or SensorType.Load))
                continue;
            float? value = sensor.Value;
            if (value.HasValue)
                selection.Add(hardwareName, sensor.Name, kind, value.Value);
        }
        foreach (IHardware sub in hardware.SubHardware)
            CollectGpuSensorSelections(sub, ref selection);
    }

    private static void CollectCpuSensorSelections(
        IHardware hardware, ref CpuTelemetrySelectionReducer selection)
    {
        string hardwareName = hardware.Name;
        HardwareType type = hardware.HardwareType;
        bool isGpu = IsGpuHardware(hardware);
        bool isBoard = type == HardwareType.Motherboard ||
            string.Equals(type.ToString(), "SuperIO", StringComparison.OrdinalIgnoreCase);
        foreach (ISensor sensor in hardware.Sensors)
        {
            SensorType kind = sensor.SensorType;
            if (kind is not (SensorType.Temperature or SensorType.Load or
                SensorType.Clock or SensorType.Power))
                continue;
            float? value = sensor.Value;
            if (value.HasValue)
                selection.Add(hardwareName, sensor.Name, kind, value.Value,
                    isGpu, type == HardwareType.Cpu, isBoard,
                    _includeMotherboardSensors);
        }
        foreach (IHardware sub in hardware.SubHardware)
            CollectCpuSensorSelections(sub, ref selection);
    }

    // Failure-only snapshot collection for the existing missing-sensor report.
    internal static void CollectSensors(
        IHardware hardware,
        List<(IHardware Hardware, ISensor Sensor, float Value)> temperatures,
        List<(IHardware Hardware, ISensor Sensor, float Value)> loads,
        List<(IHardware Hardware, ISensor Sensor, float Value)> clocks,
        List<(IHardware Hardware, ISensor Sensor, float Value)> powers)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            SensorType kind = sensor.SensorType;
            // CPU/fan telemetry consumes only these four sensor kinds. Skip
            // unrelated provider getters and read each eligible value once.
            if (kind is not (SensorType.Temperature or SensorType.Load or
                SensorType.Clock or SensorType.Power))
                continue;
            float? value = sensor.Value;
            if (!value.HasValue) continue;

            switch (kind)
            {
                case SensorType.Temperature:
                    temperatures.Add((hardware, sensor, value.Value));
                    break;
                case SensorType.Load:
                    loads.Add((hardware, sensor, value.Value));
                    break;
                case SensorType.Clock:
                    clocks.Add((hardware, sensor, value.Value));
                    break;
                case SensorType.Power:
                    powers.Add((hardware, sensor, value.Value));
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

    // The CPU temperature bridge evaluates these fixed sensor-name priorities
    // every two seconds. Fixed arity avoids params-array, LINQ delegate and
    // enumerator allocations, retaining the exact ordinal-ignore-case order.
    internal static bool ContainsAny(string value, string a, string b) =>
        value.Contains(a, StringComparison.OrdinalIgnoreCase) ||
        value.Contains(b, StringComparison.OrdinalIgnoreCase);

    internal static bool ContainsAny(string value, string a, string b, string c) =>
        ContainsAny(value, a, b) ||
        value.Contains(c, StringComparison.OrdinalIgnoreCase);

    internal static bool ContainsAny(string value, string a, string b, string c, string d) =>
        ContainsAny(value, a, b, c) ||
        value.Contains(d, StringComparison.OrdinalIgnoreCase);

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
