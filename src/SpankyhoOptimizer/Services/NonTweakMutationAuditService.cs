using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record NonTweakMutationAuditResult(int Audited, int Failed, IReadOnlyList<string> Details);

public static class NonTweakMutationAuditService
{
    public static async Task<NonTweakMutationAuditResult> RunAsync(CancellationToken token = default)
    {
        var details = new List<string>();
        int failed = 0;
        async Task Check(string name, Func<Task> check)
        {
            try { token.ThrowIfCancellationRequested(); await check(); details.Add(name + "=OK"); }
            catch (Exception ex) { failed++; details.Add(name + "=FAIL:" + ex.Message); }
        }

        await Check("power-readback", async () =>
        {
            var s = await PowerPlanStateService.ReadAsync(token);
            if (string.IsNullOrWhiteSpace(s.ActivePlan)) throw new InvalidOperationException("active plan missing");
        });
        await Check("task-scheduler-com", async () =>
        {
            // Audit only validates that Task Scheduler inventory is reachable/non-empty.
            // Avoid full XML/startup-trigger parsing here.
            var x = await new TaskSchedulerService().GetBasicAsync(false, token);
            if (x.Count == 0) throw new InvalidOperationException("inventory empty");
        });
        await Check("nic-verified-backend", async () =>
        {
            if (!WindowsCapabilityService.Current.StandardCimv2) return;
            string x = await PerformanceProfilesService.GetNicTuningReadbackAsync(token);
            if (string.IsNullOrWhiteSpace(x)) throw new InvalidOperationException("read-back empty");
        });
        await Check("defender-atomic", async () =>
        {
            var x = CatalogService.GetDefenderTweaks();
            await new TweakEngine().DetectAsync(x, token);
            if (x.Count == 0 || x.All(t => !t.IsLoaded && string.IsNullOrWhiteSpace(t.Status)))
                throw new InvalidOperationException("defender state unavailable");
        });
        await Check("debloat-recovery", async () =>
        {
            var apps = await InstalledAppService.GetInstalledAsync(token, forceRefresh: false, includeLogos: false);
            if (apps.Count == 0) throw new InvalidOperationException("package inventory empty");
        });
        await Check("windows-update", () => AdvancedCenterService.GetWindowsUpdateStatusAsync(token));
        await Check("memory-compression", async () =>
        {
            if (!WindowsCapabilityService.Current.PsMMAgent) return;
            string state = await WindowsActions.GetMemoryCompressionAsync(token);
            if (!state.Contains("True", StringComparison.OrdinalIgnoreCase) && !state.Contains("False", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("invalid MMAgent state");
        });
        return new NonTweakMutationAuditResult(details.Count, failed, details);
    }
}
