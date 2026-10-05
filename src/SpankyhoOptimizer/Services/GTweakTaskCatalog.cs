using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static class GTweakTaskCatalog
{
    public static IEnumerable<TweakDefinition> TaskTweaks()
    {
        yield return Group("gt.task.winsat", "WinSAT maintenance", "Telemetry / diagnostika",
            x => Eq(x, @"\Microsoft\Windows\Maintenance\WinSAT"), TweakRisk.Optional,
            "Vypne automatický WinSAT maintenance task; ruční WinSAT zůstává dostupný.");

        yield return Group("gt.task.office", "Office telemetry tasks", "Office",
            x => OfficeTelemetryPaths.Contains(x.Path, StringComparer.OrdinalIgnoreCase), TweakRisk.Optional,
            "Vypne pouze známé Office telemetry/monitor tasky z Advancedu, ne aktualizace Office.");

        yield return Group("gt.task.nvidia", "NVIDIA telemetry tasks", "Vendor telemetry",
            x => NvidiaPaths.Contains(x.Path, StringComparer.OrdinalIgnoreCase), TweakRisk.Optional,
            "Platí jen pro nainstalované NVIDIA telemetry scheduled tasks.");
        yield return Group("gt.task.intel", "Intel telemetry task", "Vendor telemetry",
            x => Eq(x, @"\Intel\Intel Telemetry 2 (x86)"), TweakRisk.Optional,
            "Platí jen pokud Intel telemetry task existuje.");

        yield return Group("gt.task.speech", "Speech background tasks", "Speech",
            x => x.Path.StartsWith(@"\Microsoft\Windows\Speech\", StringComparison.OrdinalIgnoreCase),
            TweakRisk.Optional, "Může omezit background úlohy Windows Speech.");

        yield return Group("gt.task.xbox", "Xbox scheduled tasks", "Xbox",
            x => XboxPaths.Contains(x.Path, StringComparer.OrdinalIgnoreCase), TweakRisk.Advanced,
            "Může ovlivnit Xbox/Game Pass cloud save, služby a údržbu.");

        yield return Group("gt.task.print", "Printing maintenance tasks", "Tisk",
            x => PrintPaths.Contains(x.Path, StringComparer.OrdinalIgnoreCase), TweakRisk.Advanced,
            "Používej jen pokud netiskneš; může omezit údržbu tiskových úloh.");

        yield return Group("gt.task.bluetooth", "Bluetooth uninstall task", "Bluetooth",
            x => Eq(x, @"\Microsoft\Windows\Bluetooth\UninstallDeviceTask"), TweakRisk.Advanced,
            "Používej jen bez Bluetooth zařízení.");
        yield return Group("gt.task.onedrive", "OneDrive scheduled tasks", "OneDrive",
            x => x.Path.StartsWith(@"\Microsoft\Windows\OneDrive\", StringComparison.OrdinalIgnoreCase) ||
                 x.Name.StartsWith("OneDrive Reporting", StringComparison.OrdinalIgnoreCase) ||
                 x.Name.StartsWith("OneDrive Standalone Update", StringComparison.OrdinalIgnoreCase) ||
                 x.Name.StartsWith("OneDrive Startup", StringComparison.OrdinalIgnoreCase),
            TweakRisk.Advanced, "Může vypnout OneDrive startup/reporting/update tasky; nepoužívej při aktivním OneDrive syncu.");

        yield return Group("gt.task.retail", "Retail Demo tasks", "Retail Demo",
            x => x.Path.StartsWith(@"\Microsoft\Windows\RetailDemo\", StringComparison.OrdinalIgnoreCase) ||
                 x.Path.StartsWith(@"\Microsoft\Windows\Retail Demo\", StringComparison.OrdinalIgnoreCase),
            TweakRisk.Optional, "Vypne pouze Retail Demo scheduled tasks, pokud jsou přítomné.");

        yield return Group("gt.task.memorydiag", "Memory Diagnostic tasks", "Diagnostika",
            x => MemoryDiagnosticPaths.Contains(x.Path, StringComparer.OrdinalIgnoreCase), TweakRisk.Advanced,
            "Vypne automatické Memory Diagnostic tasky; ruční mdsched.exe zůstává dostupný.");
        yield return Group("gt.task.insider", "Windows Insider / Flighting tasks", "Windows Insider",
            x => x.Path.StartsWith(@"\Microsoft\Windows\Flighting\FeatureConfig\", StringComparison.OrdinalIgnoreCase) ||
                 x.Path.StartsWith(@"\Microsoft\Windows\Flighting\OneSettings\", StringComparison.OrdinalIgnoreCase),
            TweakRisk.Advanced, "Používej jen mimo Insider program; může rozbít flighting konfiguraci.");

        yield return Group("gt.task.defender", "Defender scheduled tasks", "Defender",
            x => DefenderPaths.Contains(x.Path, StringComparer.OrdinalIgnoreCase), TweakRisk.Advanced,
            "Může omezit plánované skeny, cleanup a verification Microsoft Defenderu.");

        yield return Group("gt.task.restore", "System Restore SR task", "Recovery",
            x => Eq(x, @"\Microsoft\Windows\SystemRestore\SR"), TweakRisk.Advanced,
            "Může omezit automatickou tvorbu bodů obnovení.");

    }
    private static TweakDefinition Group(string id, string name, string group,
        Func<TaskInfo, bool> match, TweakRisk risk, string warning)
    {
        return TweakFactory.Command(id, "components", group, name,
            "GTweak-compatible scheduled task group using the existing Task Scheduler COM backend.",
            async ct =>
            {
                var items = (await new TaskSchedulerService().GetBasicAsync(false, ct)).Where(match).ToArray();
                return items.Length > 0 && items.All(x => !x.Enabled);
            },
            async (on, ct) =>
            {
                var scheduler = new TaskSchedulerService();
                // Detection already keeps a fresh basic inventory; do not force another COM scan
                // immediately before mutation unless the cache is actually stale/missing.
                var items = (await scheduler.GetBasicAsync(false, ct)).Where(match).ToArray();
                if (items.Length == 0) throw new PlatformNotSupportedException($"Scheduled task group '{name}' není v systému přítomná.");
                var errors = await scheduler.SetEnabledBatchAsync(items.Select(x => (x.Path, !on)).ToArray(), ct);
                if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Take(8)));
                // Successful batch mutation already merges the verified state into both caches.
                // Read from that fresh cache instead of paying for a second full COM enumeration.
                var readback = (await scheduler.GetBasicAsync(false, ct)).Where(match).ToArray();
                bool expectedEnabled = !on;
                if (readback.Length == 0 || readback.Any(x => x.Enabled != expectedEnabled))
                    throw new InvalidOperationException($"Read-back task skupiny '{name}' nepotvrdil požadovaný stav.");
            },
            recommended: false, risk, restart: false, impact: risk == TweakRisk.Advanced ? "Střední" : "Nízký",
            warning: warning, lazyDetect: true);
    }

    private static bool Eq(TaskInfo task, string path) => task.Path.Equals(path, StringComparison.OrdinalIgnoreCase);

    private static readonly string[] OfficeTelemetryPaths =
    {
        @"\Microsoft\Office\Office ClickToRun Service Monitor",
        @"\Microsoft\Office\OfficeTelemetry\AgentFallBack2016",
        @"\Microsoft\Office\OfficeTelemetry\OfficeTelemetryAgentLogOn2016",
        @"\Microsoft\Office\OfficeTelemetryAgentFallBack2016",
        @"\Microsoft\Office\OfficeTelemetryAgentLogOn2016",
        @"\Microsoft\Office\OfficeTelemetryAgentFallBack",
        @"\Microsoft\Office\OfficeTelemetryAgentLogOn",
        @"\Microsoft\Office\Office 15 Subscription Heartbeat"
    };
    private static readonly string[] NvidiaPaths =
    {
        @"\NvTmRepOnLogon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
        @"\NvTmRep_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
        @"\NvTmMon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}"
    };

    private static readonly string[] XboxPaths =
    {
        @"\Microsoft\XblGameSave\XblGameSaveTask",
        @"\Microsoft\XblGameSave\XblGameSaveTaskLogon",
        @"\Microsoft\Xbox\XblGameSaveTask",
        @"\Microsoft\Xbox\Maintenance\MaintenanceTask",
        @"\Microsoft\Xbox\XGamingServices\GameServicesTask"
    };

    private static readonly string[] PrintPaths =
    {
        @"\Microsoft\Windows\Printing\EduPrintProv",
        @"\Microsoft\Windows\Printing\PrinterCleanupTask",
        @"\Microsoft\Windows\Printing\PrintJobCleanupTask"
    };
    private static readonly string[] MemoryDiagnosticPaths =
    {
        @"\Microsoft\Windows\MemoryDiagnostic\ProcessMemoryDiagnosticEvents",
        @"\Microsoft\Windows\MemoryDiagnostic\RunFullMemoryDiagnostic"
    };

    private static readonly string[] DefenderPaths =
    {
        @"\Microsoft\Windows\ExploitGuard\ExploitGuard MDM policy Refresh",
        @"\Microsoft\Windows\Windows Defender\Windows Defender Cache Maintenance",
        @"\Microsoft\Windows\Windows Defender\Windows Defender Cleanup",
        @"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan",
        @"\Microsoft\Windows\Windows Defender\Windows Defender Verification"
    };

    private static readonly string[] WindowsUpdatePaths =
    {
        @"\Microsoft\Windows\UpdateOrchestrator\Report policies",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Maintenance Work",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan Static Task",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Wake To Work",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Work",
        @"\Microsoft\Windows\UpdateOrchestrator\Start Oobe Expedite Work",
        @"\Microsoft\Windows\UpdateOrchestrator\StartOobeAppsScanAfterUpdate",
        @"\Microsoft\Windows\UpdateOrchestrator\StartOobeAppsScan_LicenseAccepted",
        @"\Microsoft\Windows\UpdateOrchestrator\UIEOrchestrator",
        @"\Microsoft\Windows\UpdateOrchestrator\USO_UxBroker",
        @"\Microsoft\Windows\UpdateOrchestrator\UUS Failover Task",
        @"\Microsoft\Windows\WindowsUpdate\Refresh Group Policy Cache",
        @"\Microsoft\Windows\WindowsUpdate\Scheduled Start"
    };
}
