using System;

namespace SpankyhoOptimizer.Services;

public enum TaskSafetyLevel { Safe, Conditional, Critical }
public sealed record TaskSafetyInfo(TaskSafetyLevel Level, string Reason);

public static class TaskSafetyService
{
    public static TaskSafetyInfo Classify(string path)
    {
        string p = path ?? string.Empty;
        if (p.StartsWith("\\Microsoft\\Windows\\TaskScheduler\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\Servicing\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\WindowsUpdate\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\UpdateOrchestrator\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\WaaSMedic\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\Registry\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\RecoveryEnvironment\\", StringComparison.OrdinalIgnoreCase))
            return new(TaskSafetyLevel.Critical, "Systémová úloha důležitá pro Task Scheduler, servicing nebo Windows Update.");

        if (p.StartsWith("\\Microsoft\\Windows\\Defrag\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\DiskCleanup\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\Maintenance\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\Diagnosis\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\DiskDiagnostic\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows Defender\\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("\\Microsoft\\Windows\\Windows Error Reporting\\", StringComparison.OrdinalIgnoreCase))
            return new(TaskSafetyLevel.Conditional, "Systémová údržba; vypínej jen pokud víš, že ji nahrazuješ jiným mechanismem.");

        return new(TaskSafetyLevel.Safe, "Úloha není na interním seznamu kritických Windows tasků.");
    }

    public static bool CanDisable(string path) => Classify(path).Level != TaskSafetyLevel.Critical;
}
