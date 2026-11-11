using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class WorkerHostService
{
    public static async Task<bool> TryHandleAsync(string[] args)
    {
        int index = Array.FindIndex(args, x => x.Equals("--worker", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return false;
        WorkerExecutionContext.IsWorker = true;
        string action = args[index + 1];
        if (WorkerPrivilegePolicy.RequiresElevation(action) && !WorkerPrivilegePolicy.IsAdministrator())
            throw new UnauthorizedAccessException("Privileged worker action requires an elevated worker process.");

        string output = WorkerIpcSecurity.ValidateOutputPath(
            GetArg(args, "--worker-out") ?? throw new InvalidOperationException("Missing --worker-out."));
        string? arg = GetArg(args, "--worker-arg");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        object result = action.Equals("ai-option", StringComparison.OrdinalIgnoreCase)
            ? await WorkerMutationService.RunAiOptionAsync(WorkerPayloads.Decode<AiWorkerRequest>(arg), cts.Token,
                line => Console.Out.WriteLine(line), line => Console.Error.WriteLine(line)).ConfigureAwait(false)
            : await ExecuteAsync(action, arg, cts.Token).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // Never truncate an existing file or follow a pre-created leaf symlink.
        // Re-check the output scope after long-running privileged work.
        await using (var stream = new FileStream(WorkerIpcSecurity.ValidateOutputPath(output),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, result, result.GetType(), cancellationToken: cts.Token).ConfigureAwait(false);
        return true;
    }

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    internal static readonly HashSet<string> HandledActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "drivers", "apps", "services",
        "service-set", "service-set-batch", "debloat-remove",
        "repair-scan", "repair-network", "repair-bad-tweaks", "default-guard-repair", "green-ethernet-disable",
        "feature-state", "feature-states", "feature-set",
        "tweak-set", "tweak-restore", "process-run", "windows-update-pause",
        "task-set", "task-set-batch", "usb-power-disable", "startup-set", "startup-delete",
        "modernizer-adaptive", "modernizer-startup-apply", "modernizer-startup-restore",
        "modernizer-maintenance-apply", "modernizer-maintenance-restore",
        "modernizer-vendor-apply", "modernizer-vendor-restore",
        "modernizer-network-optimize", "modernizer-network-restore", "modernizer-retrim",
        "sfc", "dism-repair", "ai-option",
        "stream-smoke", "delay-smoke", "selfdiag"
    };

    private static async Task<object> ExecuteAsync(string action, string? arg, CancellationToken token)
    {
        return action.ToLowerInvariant() switch
        {
            "drivers" => await DriverInventoryService.GetAsync(true, token),
            "apps" => await InstalledAppService.GetInstalledAsync(token, true, string.Equals(arg, "logos", StringComparison.OrdinalIgnoreCase)),
            "services" => await new ServiceCatalogService().GetAsync(true, token),
            "service-set" => await WorkerMutationService.SetServiceAsync(WorkerPayloads.Decode<ServiceChangeRequest>(arg), token),
            "service-set-batch" => await WorkerMutationService.SetServicesBatchAsync(WorkerPayloads.Decode<ServiceBatchRequest>(arg), token),
            "debloat-remove" => await WorkerMutationService.RemovePackageAsync(WorkerPayloads.Decode<DebloatPackageRequest>(arg), token),
            "repair-scan" => await SystemRepairDiagnosticsService.ScanLocalAsync(token),
            "repair-network" => await SystemRepairDiagnosticsService.FlushNetworkLocalAsync(token),
            "repair-bad-tweaks" => await SystemRepairDiagnosticsService.RepairBadTweaksLocalAsync(token),
            "default-guard-repair" => WindowsDefaultGuardService.RepairApprovedMachineOnce(
                WorkerPayloads.Decode<DefaultGuardMachineRepairRequest>(arg), token),
            "green-ethernet-disable" => NativeNicTuningService.DisableRealtekGreenEthernetNoRestart(token),
            "feature-state" => await WindowsFeatureStateService.IsDisabledLocalAsync(WorkerPayloads.Decode<FeatureRequest>(arg).Name, token),
            "feature-states" => await WindowsFeatureStateService.GetStatesLocalSnapshotAsync(token),
            "feature-set" => await WorkerMutationService.SetFeatureAsync(WorkerPayloads.Decode<FeatureRequest>(arg), token),
            "tweak-set" => await WorkerMutationService.SetTweakAsync(WorkerPayloads.Decode<TweakWorkerRequest>(arg), token),
            "tweak-restore" => await WorkerMutationService.RestoreTweakAsync(WorkerPayloads.Decode<TweakWorkerRequest>(arg), token),
            "process-run" => await WorkerMutationService.RunPrivilegedProcessAsync(WorkerPayloads.Decode<PrivilegedProcessRequest>(arg), token),
            "windows-update-pause" => await WorkerMutationService.SetWindowsUpdatePauseAsync(WorkerPayloads.Decode<BooleanMutationRequest>(arg), token),
            "task-set" => await WorkerMutationService.SetTaskAsync(WorkerPayloads.Decode<TaskStateChangeRequest>(arg), token),
            "task-set-batch" => await WorkerMutationService.SetTasksBatchAsync(WorkerPayloads.Decode<TaskStateBatchRequest>(arg), token),
            "usb-power-disable" => await UsbPowerMutationService.DisableDevicePowerSavingAsync(token),
            "startup-set" => await WorkerMutationService.SetStartupAsync(WorkerPayloads.Decode<StartupStateChangeRequest>(arg), token),
            "startup-delete" => await WorkerMutationService.DeleteStartupAsync(WorkerPayloads.Decode<StartupDeleteRequest>(arg), token),
            "modernizer-adaptive" => await WorkerMutationService.ApplyModernizerAdaptiveAsync(WorkerPayloads.Decode<ModernizerAdaptiveRequest>(arg), token),
            "modernizer-startup-apply" => await WorkerMutationService.ApplyModernizerStartupAsync(token),
            "modernizer-startup-restore" => await WorkerMutationService.RestoreModernizerStartupAsync(token),
            "modernizer-maintenance-apply" => await WorkerMutationService.ApplyModernizerMaintenanceAsync(token),
            "modernizer-maintenance-restore" => await WorkerMutationService.RestoreModernizerMaintenanceAsync(token),
            "modernizer-vendor-apply" => await WorkerMutationService.ApplyModernizerVendorAsync(token),
            "modernizer-vendor-restore" => await WorkerMutationService.RestoreModernizerVendorAsync(token),
            "modernizer-network-optimize" => await WorkerMutationService.OptimizeModernizerNetworkAsync(token),
            "modernizer-network-restore" => await WorkerMutationService.RestoreModernizerNetworkAsync(token),
            "modernizer-retrim" => await WorkerMutationService.RetrimModernizerAsync(token),
            "sfc" => await WorkerMutationService.RunSfcAsync(token),
            "dism-repair" => await WorkerMutationService.RunDismRepairAsync(token),
            "ai-option" => await WorkerMutationService.RunAiOptionAsync(WorkerPayloads.Decode<AiWorkerRequest>(arg), token),
            "stream-smoke" => await RunStreamSmokeAsync(token),
            "delay-smoke" => await RunDelaySmokeAsync(token),
            "selfdiag" => await SelfDiagnosticsService.RunLocalAsync(token),
            _ => throw new InvalidOperationException("Unknown worker action: " + action)
        };
    }

    private static async Task<WorkerAck> RunStreamSmokeAsync(CancellationToken token)
    {
        await Console.Out.WriteLineAsync("STREAM_OUT_1").ConfigureAwait(false);
        await Task.Delay(80, token).ConfigureAwait(false);
        await Console.Error.WriteLineAsync("STREAM_ERR_1").ConfigureAwait(false);
        await Task.Delay(80, token).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("STREAM_OUT_2").ConfigureAwait(false);
        return new WorkerAck();
    }

    private static async Task<WorkerAck> RunDelaySmokeAsync(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        return new WorkerAck();
    }}
