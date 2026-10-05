using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record AiSelfOptimizationHealth(
    DateTime UpdatedUtc,
    bool Enabled,
    double ProcessCpuPercent,
    double WorkingSetMb,
    int Threads,
    int Handles,
    double ManagedMemoryMb,
    double PressureScore,
    int MaintenanceIntervalSeconds,
    string State);

public sealed class AiSelfOptimizationService
{
    private static readonly object HealthGate = new();
    private static AiSelfOptimizationHealth? CachedHealth;
    private static DateTime CachedHealthWriteUtc = DateTime.MinValue;
    private static readonly string HealthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer",
        "ai-self-optimization-health.json");

    private DateTime _lastSampleUtc = DateTime.MinValue;
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private int _maintenanceIntervalSeconds = 60;
    private double _cpuEwma;
    private double _ramEwma;
    private double _lastWorkingSetMb;
    private int _lastHandleCount;
    private double _ramGrowthEwmaPerMinute;
    private double _handleGrowthEwmaPerMinute;
    private int _pressureStreak;
    private int _calmStreak;
    private bool? _lastEnabledState;
    private string _lastHealthFingerprint = string.Empty;
    private DateTime _lastHealthWriteUtc = DateTime.MinValue;

    public int Sample(bool enabled) => Sample(enabled, AiWorkloadClass.Unknown);

    public int Sample(bool enabled, AiWorkloadClass workload)
    {
        DateTime now = DateTime.UtcNow;
        if (!enabled)
        {
            _maintenanceIntervalSeconds = 60;
            if (_lastEnabledState != false)
                WriteHealth(false, 0, "OFF");
            _lastEnabledState = false;
            return _maintenanceIntervalSeconds;
        }

        _lastEnabledState = true;
        TimeSpan sampleInterval = AiSelfOptimizationPolicy.SamplingInterval(workload);
        if (_lastSampleUtc != DateTime.MinValue &&
            now - _lastSampleUtc < sampleInterval)
            return _maintenanceIntervalSeconds;

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;

        if (_lastSampleUtc == DateTime.MinValue)
        {
            _lastSampleUtc = now;
            _lastCpu = cpu;
            _lastWorkingSetMb = process.WorkingSet64 / 1024d / 1024d;
            _lastHandleCount = process.HandleCount;
            WriteHealth(true, 0, "Inicializace");
            return _maintenanceIntervalSeconds;
        }

        double elapsedMs = Math.Max(1, (now - _lastSampleUtc).TotalMilliseconds);
        double cpuMs = Math.Max(0, (cpu - _lastCpu).TotalMilliseconds);
        double cpuPercent = Math.Clamp(
            cpuMs / elapsedMs / Math.Max(1, Environment.ProcessorCount) * 100d,
            0,
            100);

        double workingSetMb = process.WorkingSet64 / 1024d / 1024d;
        int threads = process.Threads.Count;
        int handles = process.HandleCount;
        double managedMemoryMb = GC.GetTotalMemory(false) / 1024d / 1024d;

        double elapsedMinutes = Math.Max(0.05, elapsedMs / 60000d);
        double ramGrowthPerMinute = Math.Clamp(
            Math.Max(0, workingSetMb - _lastWorkingSetMb) / elapsedMinutes,
            0,
            250);
        double handleGrowthPerMinute = Math.Clamp(
            Math.Max(0, handles - _lastHandleCount) / elapsedMinutes,
            0,
            3000);

        _lastSampleUtc = now;
        _lastCpu = cpu;
        _lastWorkingSetMb = workingSetMb;
        _lastHandleCount = handles;

        _cpuEwma = _cpuEwma <= 0 ? cpuPercent : (_cpuEwma * 0.72) + (cpuPercent * 0.28);
        _ramEwma = _ramEwma <= 0 ? workingSetMb : (_ramEwma * 0.82) + (workingSetMb * 0.18);
        _ramGrowthEwmaPerMinute = _ramGrowthEwmaPerMinute <= 0
            ? ramGrowthPerMinute
            : _ramGrowthEwmaPerMinute * 0.72 + ramGrowthPerMinute * 0.28;
        _handleGrowthEwmaPerMinute = _handleGrowthEwmaPerMinute <= 0
            ? handleGrowthPerMinute
            : _handleGrowthEwmaPerMinute * 0.72 + handleGrowthPerMinute * 0.28;

        double pressureScore = AiSelfOptimizationPolicy.ComputePressureScore(
            _cpuEwma,
            _ramEwma,
            threads,
            handles,
            _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);

        bool pressured = AiSelfOptimizationPolicy.IsPressured(
            pressureScore,
            managedMemoryMb,
            _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);
        bool calm = AiSelfOptimizationPolicy.IsCalm(
            pressureScore,
            managedMemoryMb,
            _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);
        _pressureStreak = pressured ? _pressureStreak + 1 : 0;
        _calmStreak = calm ? _calmStreak + 1 : 0;

        if (_pressureStreak >= 2)
        {
            _maintenanceIntervalSeconds = Math.Min(240, _maintenanceIntervalSeconds + 30);
            _pressureStreak = 0;
        }
        else if (_calmStreak >= 3)
        {
            _maintenanceIntervalSeconds = Math.Max(45, _maintenanceIntervalSeconds - 15);
            _calmStreak = 0;
        }

        _maintenanceIntervalSeconds = AiSelfOptimizationPolicy.ApplyWorkloadFloor(
            _maintenanceIntervalSeconds,
            workload);

        string state = AiSelfOptimizationPolicy.State(
            pressureScore,
            _maintenanceIntervalSeconds,
            _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);

        WriteHealth(true, cpuPercent, state, workingSetMb, threads, handles, managedMemoryMb, pressureScore);
        return _maintenanceIntervalSeconds;
    }

    public static AiSelfOptimizationHealth? ReadHealth()
    {
        lock (HealthGate)
        {
            try
            {
                DateTime writeUtc = File.Exists(HealthPath)
                    ? File.GetLastWriteTimeUtc(HealthPath)
                    : DateTime.MinValue;
                if (CachedHealth != null && writeUtc == CachedHealthWriteUtc)
                    return CachedHealth;

                CachedHealth = writeUtc != DateTime.MinValue
                    ? JsonSerializer.Deserialize<AiSelfOptimizationHealth>(File.ReadAllText(HealthPath))
                    : null;
                CachedHealthWriteUtc = writeUtc;
                return CachedHealth;
            }
            catch
            {
                return CachedHealth;
            }
        }
    }
    private void WriteHealth(
        bool enabled,
        double cpuPercent,
        string state,
        double? workingSetMb = null,
        int? threads = null,
        int? handles = null,
        double? managedMemoryMb = null,
        double pressureScore = 0)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            double roundedCpu = Math.Round(cpuPercent, 2);
            double roundedWorkingSet = Math.Round(workingSetMb ?? process.WorkingSet64 / 1024d / 1024d, 1);
            int threadCount = threads ?? process.Threads.Count;
            int handleCount = handles ?? process.HandleCount;
            double roundedManaged = Math.Round(managedMemoryMb ?? GC.GetTotalMemory(false) / 1024d / 1024d, 1);
            double roundedPressure = Math.Round(pressureScore, 2);
            string fingerprint = string.Join('|',
                enabled,
                roundedCpu,
                roundedWorkingSet,
                threadCount,
                handleCount,
                roundedManaged,
                roundedPressure,
                _maintenanceIntervalSeconds,
                state);
            DateTime nowUtc = DateTime.UtcNow;
            if (string.Equals(fingerprint, _lastHealthFingerprint, StringComparison.Ordinal) &&
                _lastHealthWriteUtc != DateTime.MinValue &&
                nowUtc - _lastHealthWriteUtc < TimeSpan.FromMinutes(2))
                return;

            var health = new AiSelfOptimizationHealth(
                nowUtc,
                enabled,
                roundedCpu,
                roundedWorkingSet,
                threadCount,
                handleCount,
                roundedManaged,
                roundedPressure,
                _maintenanceIntervalSeconds,
                state);

            Directory.CreateDirectory(Path.GetDirectoryName(HealthPath)!);
            string temp = HealthPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(health));
            File.Move(temp, HealthPath, true);
            _lastHealthFingerprint = fingerprint;
            _lastHealthWriteUtc = nowUtc;
            lock (HealthGate)
            {
                CachedHealth = health;
                CachedHealthWriteUtc = File.GetLastWriteTimeUtc(HealthPath);
            }
        }
        catch
        {
            // Self-telemetry must never affect the runtime.
        }
    }
}
