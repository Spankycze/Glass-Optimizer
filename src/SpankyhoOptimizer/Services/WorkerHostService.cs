using System;
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
        string output = GetArg(args, "--worker-out") ?? throw new InvalidOperationException("Missing --worker-out.");
        string? arg = GetArg(args, "--worker-arg");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        object result = action.Equals("ai-option", StringComparison.OrdinalIgnoreCase)
            ? await WorkerMutationService.RunAiOptionAsync(WorkerPayloads.Decode<AiWorkerRequest>(arg), cts.Token,
                line => Console.Out.WriteLine(line), line => Console.Error.WriteLine(line)).ConfigureAwait(false)
            : await ExecuteAsync(action, arg, cts.Token).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, result, result.GetType(), cancellationToken: cts.Token).ConfigureAwait(false);
        return true;
    }

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
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
            "feature-state" => await WindowsFeatureStateService.IsDisabledLocalAsync(WorkerPayloads.Decode<FeatureRequest>(arg).Name, token),
            "feature-states" => await WindowsFeatureStateService.GetStatesLocalSnapshotAsync(token),
            "feature-set" => await WorkerMutationService.SetFeatureAsync(WorkerPayloads.Decode<FeatureRequest>(arg), token),
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
