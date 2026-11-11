using System;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App
{
    // Safe subset of the full release-service bundle. These checks use only
    // synthetic values / in-process objects, without registry, adapter,
    // service, power-plan or user-profile changes.
    private static int RunReadOnlyServicePolicySmoke()
    {
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            // Unsupported and failed NIC providers must not be confused.
            // This method formats synthetic result objects; no NIC is queried.
            NativeNicTuningService.ValidateProviderReadStateClassificationForSmoke();

            // Reuse is legal only for the same payload AND the same auth epoch.
            DateTime now = DateTime.UtcNow;
            bool accepted = AiCloudProfileService.CanReuseRecentUpload(
                "payload", "payload", "auth-A", "auth-A", now.AddMinutes(-1), now);
            bool differentUser = AiCloudProfileService.CanReuseRecentUpload(
                "payload", "payload", "auth-A", "auth-B", now.AddMinutes(-1), now);
            bool differentPayload = AiCloudProfileService.CanReuseRecentUpload(
                "payload", "changed", "auth-A", "auth-A", now.AddMinutes(-1), now);
            bool expired = AiCloudProfileService.CanReuseRecentUpload(
                "payload", "payload", "auth-A", "auth-A", now.AddMinutes(-4), now);
            bool future = AiCloudProfileService.CanReuseRecentUpload(
                "payload", "payload", "auth-A", "auth-A", now.AddMinutes(1), now);
            if (!accepted || differentUser || differentPayload || expired || future)
                throw new InvalidOperationException("Cloud-upload dedup policy regressed.");

            // In-memory metrics with 100k iterations: no fan agent is started,
            // no hardware is polled, and no Windows settings are modified.
            string metrics = FanAgentStageMetrics.RunPolicySmoke();
            if (!metrics.Contains("hotAllocatedB=0", StringComparison.Ordinal))
                throw new InvalidOperationException("Fan metric hot path allocated memory.");

            timer.Stop();
            LogService.Info("release-smoke",
                $"READONLY_SERVICE_POLICY_SMOKE_OK checks=3 elapsedMs={timer.ElapsedMilliseconds} " +
                "nicSynthetic=true cloudSynthetic=true fanSynthetic=true " + metrics);
            return 0;
        }
        catch (Exception ex)
        {
            LogService.Error("release-smoke", "READONLY_SERVICE_POLICY_SMOKE_FAILED", ex);
            return 73;
        }
    }
}
