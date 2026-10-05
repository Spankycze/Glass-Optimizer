using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace SpankyhoOptimizer.Services;

internal static class WorkerMutationService
{
    public static Task<WorkerAck> SetServiceAsync(ServiceChangeRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.StartType == 4 && !ServiceSafetyService.CanDisable(request.Name))
            throw new InvalidOperationException(ServiceSafetyService.ExplainBlock(request.Name));
        int original = ServiceSafetyService.ReadStartType(request.Name) ?? 3;
        if (!NativeCore.ChangeServiceStartType(request.Name, request.StartType, out int error))
            throw new InvalidOperationException($"{request.Name}: změnu se nepodařilo použít. Win32 chyba {error}.");
        int actual = ServiceSafetyService.ReadStartType(request.Name) ?? -1;
        if (actual != request.StartType)
        {
            NativeCore.ChangeServiceStartType(request.Name, (uint)original, out _);
            throw new InvalidOperationException($"{request.Name}: read-back nepotvrdil změnu; původní stav byl obnoven.");
        }
        ServiceCatalogService.InvalidateCache();
        return Task.FromResult(new WorkerAck());
    }
    public static async Task<string[]> SetServicesBatchAsync(ServiceBatchRequest request, CancellationToken token)
    {
        var errors = new System.Collections.Generic.List<string>();
        foreach (var item in request.Items)
        {
            token.ThrowIfCancellationRequested();
            try { await SetServiceAsync(item, token).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add("Service " + item.Name + ": " + ex.Message); }
        }
        ServiceCatalogService.InvalidateCache();
        return errors.ToArray();
    }

    public static async Task<WorkerAck> RemovePackageAsync(DebloatPackageRequest request, CancellationToken token)
    {
        var pm = new PackageManager();
        var deployment = request.AllUsers
            ? await pm.RemovePackageAsync(request.PackageFullName, RemovalOptions.RemoveForAllUsers).AsTask(token)
            : await pm.RemovePackageAsync(request.PackageFullName).AsTask(token);
        if (deployment.ExtendedErrorCode != null)
            throw new InvalidOperationException(deployment.ErrorText ?? deployment.ExtendedErrorCode.Message);
        bool stillInstalled = pm.FindPackagesForUser(string.Empty)
            .Any(p => p.Id.FullName.Equals(request.PackageFullName, StringComparison.OrdinalIgnoreCase));
        if (stillInstalled) throw new InvalidOperationException("Read-back stále vidí balík jako nainstalovaný.");
        return new WorkerAck();
    }

    public static async Task<WorkerAck> SetFeatureAsync(FeatureRequest request, CancellationToken token)
    {
        string args = request.Disable ? $"/Online /Disable-Feature /FeatureName:{request.Name} /NoRestart /English" : $"/Online /Enable-Feature /FeatureName:{request.Name} /All /NoRestart /English";
        var r = await ProcessRunner.RunAsync("dism.exe", args, TimeSpan.FromMinutes(5), token);
        if (!r.Success) throw new InvalidOperationException(r.StdErr + r.StdOut);
        WindowsFeatureStateService.Invalidate(); return new WorkerAck();
    }

    public static async Task<WorkerAck> RunSfcAsync(CancellationToken token)
    {
        var r = await ProcessRunner.RunAsync("sfc.exe", "/scannow", TimeSpan.FromMinutes(30), token);
        if (!r.Success && r.ExitCode != 1) throw new InvalidOperationException(r.StdErr + r.StdOut);
        return new WorkerAck();
    }

    public static async Task<WorkerAck> RunDismRepairAsync(CancellationToken token)
    {
        var r = await ProcessRunner.RunAsync("dism.exe", "/Online /Cleanup-Image /RestoreHealth /NoRestart /English", TimeSpan.FromMinutes(45), token);
        if (!r.Success) throw new InvalidOperationException(r.StdErr + r.StdOut);
        return new WorkerAck();
    }

    public static async Task<AiWorkerResult> RunAiOptionAsync(AiWorkerRequest request, CancellationToken token,
        Action<string>? onStdOut = null, Action<string>? onStdErr = null)
    {
        var r = await ZoicwareToolsService.RunAiOptionLocalAsync(request.Option, request.Revert, request.Backup, token, onStdOut, onStdErr);
        return new AiWorkerResult(r.ExitCode, r.StdOut, r.StdErr);
    }

}
