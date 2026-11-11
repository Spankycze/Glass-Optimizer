using System;
using LibreHardwareMonitor.Hardware;

namespace SpankyhoOptimizer.Services;

// Streaming selection of the same first-priority GPU temperature/load sensors
// used by the original list/LINQ path, without retaining every sensor reading.
internal struct GpuTelemetrySelectionReducer
{
    internal readonly record struct Reading(string HardwareName, string SensorName, float Value);
    private Reading _coreTemp, _hotTemp, _hottestTemp, _coreLoad, _highestLoad;
    private bool _hasCoreTemp, _hasHotTemp, _hasHottestTemp, _hasCoreLoad, _hasHighestLoad;

    internal void Add(string hardware, string sensor, SensorType kind, float value)
    {
        if (kind == SensorType.Temperature && value > 5 && value < 150)
        {
            var reading = new Reading(hardware, sensor, value);
            if (!_hasHottestTemp || value > _hottestTemp.Value)
            {
                _hottestTemp = reading;
                _hasHottestTemp = true;
            }
            if (!_hasCoreTemp && (sensor.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                                  sensor.Contains("Core", StringComparison.OrdinalIgnoreCase)))
            {
                _coreTemp = reading;
                _hasCoreTemp = true;
            }
            if (!_hasHotTemp && (sensor.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                                 sensor.Contains("Hotspot", StringComparison.OrdinalIgnoreCase)))
            {
                _hotTemp = reading;
                _hasHotTemp = true;
            }
        }
        else if (kind == SensorType.Load && value >= 0 && value <= 100)
        {
            var reading = new Reading(hardware, sensor, value);
            if (!_hasHighestLoad || value > _highestLoad.Value)
            {
                _highestLoad = reading;
                _hasHighestLoad = true;
            }
            if (!_hasCoreLoad && (sensor.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                                  sensor.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                                  sensor.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase)))
            {
                _coreLoad = reading;
                _hasCoreLoad = true;
            }
        }
    }

    internal CpuTemperatureService.GpuTelemetrySample Build(string? firstGpuName)
    {
        Reading temp = _hasCoreTemp ? _coreTemp : _hasHotTemp ? _hotTemp : _hottestTemp;
        Reading load = _hasCoreLoad ? _coreLoad : _highestLoad;
        bool hasTemp = _hasCoreTemp || _hasHotTemp || _hasHottestTemp;
        bool hasLoad = _hasCoreLoad || _hasHighestLoad;
        return new CpuTemperatureService.GpuTelemetrySample(
            hasTemp ? Math.Round(temp.Value, 1) : null,
            hasTemp ? temp.SensorName : "GPU senzor nedostupný",
            (hasTemp ? temp.HardwareName : null) ??
                (hasLoad ? load.HardwareName : null) ?? firstGpuName ?? "GPU",
            hasLoad ? Math.Round(load.Value, 1) : null);
    }
}
