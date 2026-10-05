namespace SpankyhoOptimizer.Services;

public sealed record PlatformCompatibilityAuditResult(int Build, bool StandardCimv2, bool MmAgent, bool FutureBuildSafe, string Summary);

public static class PlatformCompatibilityAuditService
{
    public static PlatformCompatibilityAuditResult Run()
    {
        var c = WindowsCapabilityService.Current;
        bool safe = c.Build >= 22000 && c.TaskSchedulerCom && c.ModernPowerApi;
        string summary = $"build={c.Build}; band={c.ReleaseBand}; StandardCimv2={c.StandardCimv2}; MMAgent={c.PsMMAgent}; TaskScheduler={c.TaskSchedulerCom}; power={c.ModernPowerApi}; future-safe={safe}";
        return new PlatformCompatibilityAuditResult(c.Build, c.StandardCimv2, c.PsMMAgent, safe, summary);
    }
}
