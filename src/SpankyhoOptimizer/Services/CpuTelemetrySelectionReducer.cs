using System;
using LibreHardwareMonitor.Hardware;

namespace SpankyhoOptimizer.Services;

// Streaming CPU / board telemetry reducer. Keeps only priority winners, not
// arrays of every sensor. The caller already serializes LHM access under Gate.
// First matching sensor wins each named priority, highest reading only for
// the final fallback priority (matching legacy Enumerable.MaxBy ties).
internal struct CpuTelemetrySelectionReducer
{
    private readonly record struct Reading(string Hardware, string Sensor, float Value);
    private Reading _temp, _board, _load, _power;
    private int _tempRank, _boardRank, _loadRank, _powerRank;
    private bool _hasTemp, _hasBoard, _hasLoad, _hasPower;
    private string? _firstRawLoadHardware, _firstRawClockHardware;
    private double _clockSum;
    private int _clockCount;

    internal readonly bool HasCpuTemperature => _hasTemp;

    internal void Add(string hardware, string sensor, SensorType kind, float value,
        bool isGpu, bool isCpu, bool isBoard, bool cpuTempStrict)
    {
        if (kind == SensorType.Load && _firstRawLoadHardware == null)
            _firstRawLoadHardware = hardware;
        if (kind == SensorType.Clock && _firstRawClockHardware == null)
            _firstRawClockHardware = hardware;

        if (kind == SensorType.Temperature)
        {
            if (isBoard && value > 5 && value < 125)
            {
                int rank = CpuTemperatureService.ContainsAny(sensor,
                    "System", "Motherboard", "Ambient", "T_Sensor") ? 0 :
                    CpuTemperatureService.ContainsAny(sensor,
                    "Chipset", "PCH", "VRM") ? 1 : 2;
                if (rank < 2 && (!_hasBoard || rank < _boardRank))
                {
                    _board = new Reading(hardware, sensor, value);
                    _boardRank = rank;
                    _hasBoard = true;
                }
            }

            // Preserve the strict positive-range filter: NaN must be rejected,
            // not treated as a valid temperature by reversed <=/>= checks.
            if (isGpu || !(value > 5 && value < 150) || (cpuTempStrict && !isCpu))
                return;

            int priority = CpuTemperatureService.ContainsAny(sensor,
                "Tctl/Tdie", "Tdie", "CPU Package", "Package") ? 0 :
                CpuTemperatureService.ContainsAny(sensor,
                    "CPU", "Core", "CCD") ? 1 :
                CpuTemperatureService.ContainsAny(hardware, "CPU", "Ryzen") ? 2 : 3;
            if (!_hasTemp || priority < _tempRank ||
                (priority == 3 && _tempRank == 3 && value > _temp.Value))
            {
                _temp = new Reading(hardware, sensor, value);
                _tempRank = priority;
                _hasTemp = true;
            }
            return;
        }

        if (isGpu) return;
        switch (kind)
        {
            case SensorType.Load when value >= 0 && value <= 100:
            {
                int priority = CpuTemperatureService.ContainsAny(sensor, "CPU Total", "Total") ? 0 :
                    CpuTemperatureService.ContainsAny(sensor, "CPU", "Package") ? 1 : 2;
                if (!_hasLoad || priority < _loadRank)
                {
                    _load = new Reading(hardware, sensor, value);
                    _loadRank = priority;
                    _hasLoad = true;
                }
                break;
            }
            case SensorType.Clock when value > 100 && value < 10000:
                if (CpuTemperatureService.ContainsAny(sensor, "Effective", "Core", "CPU"))
                {
                    _clockSum += value;
                    _clockCount++;
                }
                break;
            case SensorType.Power when value > 0 && value < 1000:
            {
                int priority = CpuTemperatureService.ContainsAny(sensor,
                    "Package", "CPU Package") ? 0 :
                    CpuTemperatureService.ContainsAny(sensor, "PPT", "CPU", "Cores") ? 1 : 2;
                if (!_hasPower || priority < _powerRank ||
                    (priority == 2 && _powerRank == 2 && value > _power.Value))
                {
                    _power = new Reading(hardware, sensor, value);
                    _powerRank = priority;
                    _hasPower = true;
                }
                break;
            }
        }
    }

    internal CpuTemperatureService.CpuTelemetrySample Build()
    {
        return new CpuTemperatureService.CpuTelemetrySample(
            _hasTemp ? Math.Round(_temp.Value, 1) : null,
            _hasTemp ? _temp.Sensor : "CPU senzor nedostupný",
            (_hasTemp ? _temp.Hardware : null) ??
                _firstRawLoadHardware ?? _firstRawClockHardware ?? "CPU",
            _hasLoad ? Math.Round(_load.Value, 1) : null,
            _clockCount > 0 ? Math.Round(_clockSum / _clockCount, 0) : null,
            _hasPower ? Math.Round(_power.Value, 1) : null,
            _hasBoard ? Math.Round(_board.Value, 1) : null,
            _hasBoard ? _board.Sensor : null);
    }
}
