using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class OptimizationAdvisorSmokeService
{
    public static async Task RunAsync(CancellationToken token = default)
    {
        var memoryTask = MemoryStateService.ReadAsync(token);
        var storageTask = StorageOptimizationService.ReadAsync(token);
        var inputTask = InputLatencyService.ReadAsync(token);
        var interruptTask = InterruptLatencyService.ReadAsync(token);
        var multimediaTask = LegacyMultimediaTuningService.ReadAsync(token);
        var nicTask = PerformanceProfilesService.GetNicCapabilitiesAsync(token);

        await Task.WhenAll(
            memoryTask, storageTask, inputTask, interruptTask,
            multimediaTask, nicTask).ConfigureAwait(false);

        var memory = await memoryTask.ConfigureAwait(false);
        var storage = await storageTask.ConfigureAwait(false);
        var input = await inputTask.ConfigureAwait(false);
        var interrupts = await interruptTask.ConfigureAwait(false);
        var multimedia = await multimediaTask.ConfigureAwait(false);
        var nic = await nicTask.ConfigureAwait(false);

        if (memory.TotalRamGb <= 0)
            throw new InvalidOperationException("Optimization advisor smoke: RAM read-back is invalid.");
        if (string.IsNullOrWhiteSpace(input.MouseSpeed) ||
            string.IsNullOrWhiteSpace(input.KeyboardSpeed))
            throw new InvalidOperationException("Optimization advisor smoke: input read-back is empty.");
        if (string.IsNullOrWhiteSpace(multimedia.SystemResponsiveness) ||
            string.IsNullOrWhiteSpace(multimedia.NetworkThrottlingIndex))
            throw new InvalidOperationException("Optimization advisor smoke: MMCSS read-back is empty.");
        if (!nic.ContainsKey("RSS") ||
            !nic.ContainsKey("Energy Efficient Ethernet") ||
            !nic.ContainsKey("LSO IPv4") ||
            !nic.ContainsKey("Receive Buffers"))
            throw new InvalidOperationException("Optimization advisor smoke: NIC capability map is incomplete.");

        var startup = StartupManagerService.TryGetMemoryDisplaySnapshot()
                      ?? Array.Empty<StartupEntry>();
        var background = await BackgroundActivityService.ReadAsync(startup, token).ConfigureAwait(false);

        int rxOptions = nic.TryGetValue("Receive Buffers Options", out var rxPacked) && !string.IsNullOrEmpty(rxPacked)
            ? rxPacked.Split('\u001F', StringSplitOptions.RemoveEmptyEntries).Length : 0;
        int txOptions = nic.TryGetValue("Transmit Buffers Options", out var txPacked) && !string.IsNullOrEmpty(txPacked)
            ? txPacked.Split('\u001F', StringSplitOptions.RemoveEmptyEntries).Length : 0;

        LogService.Info("release-smoke",
            $"OPTIMIZATION_ADVISOR_SMOKE_OK ram={memory.TotalRamGb:0.0}GB trim={storage.TrimEnabled} inputAccel={input.MouseAccelerationEnabled} interrupts={interrupts.Count} background={background.Count} nicKeys={nic.Count} rxOptions={rxOptions} txOptions={txOptions}");
    }
}
