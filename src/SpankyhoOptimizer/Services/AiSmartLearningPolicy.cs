using System;

namespace SpankyhoOptimizer.Services;

public static class AiSmartLearningPolicy
{
    public static TimeSpan MinimumSampleInterval(AiWorkloadClass workload) =>
        workload switch
        {
            AiWorkloadClass.Game => TimeSpan.FromSeconds(5),
            AiWorkloadClass.Interactive => TimeSpan.FromSeconds(5),
            AiWorkloadClass.Background => TimeSpan.FromSeconds(8),
            AiWorkloadClass.Idle => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(10)
        };

    public static bool IsSampleDue(
        DateTime lastSampleUtc,
        AiWorkloadClass lastWorkload,
        AiWorkloadClass workload,
        DateTime nowUtc)
    {
        if (lastSampleUtc == DateTime.MinValue || lastWorkload != workload)
            return true;
        if (nowUtc < lastSampleUtc)
            return false;
        return nowUtc - lastSampleUtc >= MinimumSampleInterval(workload);
    }

    public static TimeSpan PersistenceInterval(AiWorkloadClass workload) =>
        workload switch
        {
            AiWorkloadClass.Game => TimeSpan.FromMinutes(5),
            AiWorkloadClass.Interactive => TimeSpan.FromMinutes(2),
            AiWorkloadClass.Background => TimeSpan.FromMinutes(1),
            AiWorkloadClass.Idle => TimeSpan.FromMinutes(1),
            _ => TimeSpan.FromMinutes(2)
        };

    public static bool IsPersistenceDue(
        bool dirty,
        DateTime lastPersistUtc,
        AiWorkloadClass workload,
        DateTime nowUtc)
    {
        if (!dirty)
            return false;
        if (lastPersistUtc == DateTime.MinValue)
            return true;
        if (nowUtc < lastPersistUtc)
            return false;
        return nowUtc - lastPersistUtc >= PersistenceInterval(workload);
    }
}
