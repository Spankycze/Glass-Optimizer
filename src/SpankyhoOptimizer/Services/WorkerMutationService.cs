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
            throw new System.ComponentModel.Win32Exception(error, $"{request.Name}: změnu se nepodařilo použít. Win32 chyba {error}.");
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
        var result = await ServiceBatchExecution.RunAsync(request.Items,
            async (item, ct) => { await SetServiceAsync(item, ct).ConfigureAwait(false); },
            request.SkipMissing, token).ConfigureAwait(false);
        foreach (string name in result.Skipped)
            LogService.Info("services", "Recommended skipped uninstalled service: " + name);
        ServiceCatalogService.InvalidateCache();
        return result.Errors;
    }

    public static async Task<WorkerAck> RemovePackageAsync(DebloatPackageRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Enforce package protection in the privileged worker too. A stale
        // inventory or a direct worker request must not bypass the UI.
        string fullName = request.PackageFullName ?? string.Empty;
        int separator = fullName.IndexOf('_');
        if (separator <= 0 || separator == fullName.Length - 1)
            throw new InvalidOperationException("Neplatná identita balíčku; odebrání zablokováno.");
        if (InstalledAppService.IsProtectedPackageName(fullName[..separator]))
            throw new InvalidOperationException("Chráněný systémový balík nelze odebrat.");
        var pm = new PackageManager();
        // Revalidate the actual package at the privileged boundary rather
        // than trusting a possibly stale or fabricated UI inventory row.
        var package = (request.AllUsers ? pm.FindPackages() : pm.FindPackagesForUser(string.Empty))
            .FirstOrDefault(p => p.Id.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase));
        if (package is null || package.IsFramework || package.IsResourcePackage ||
            InstalledAppService.IsProtectedPackageName(package.Id.Name))
            throw new InvalidOperationException("Balík neexistuje nebo je chráněnou součástí Windows.");
        var deployment = request.AllUsers
            ? await pm.RemovePackageAsync(request.PackageFullName, RemovalOptions.RemoveForAllUsers).AsTask(token)
            : await pm.RemovePackageAsync(request.PackageFullName).AsTask(token);
        if (deployment.ExtendedErrorCode != null)
            throw new InvalidOperationException(deployment.ErrorText ?? deployment.ExtendedErrorCode.Message);
        bool stillInstalled = request.AllUsers
            ? pm.FindPackages().Any(p => p.Id.FullName.Equals(request.PackageFullName, StringComparison.OrdinalIgnoreCase))
            : pm.FindPackagesForUser(string.Empty)
                .Any(p => p.Id.FullName.Equals(request.PackageFullName, StringComparison.OrdinalIgnoreCase));
        if (stillInstalled)
            throw new InvalidOperationException(request.AllUsers
                ? "Read-back stále vidí balík registrovaný alespoň pro jednoho uživatele."
                : "Read-back stále vidí balík jako nainstalovaný pro aktuálního uživatele.");
        return new WorkerAck();
    }

    public static async Task<WorkerAck> SetFeatureAsync(FeatureRequest request, CancellationToken token)
    {
        string args = request.Disable
            ? $"/Online /Disable-Feature /FeatureName:{request.Name} /NoRestart /English"
            : $"/Online /Enable-Feature /FeatureName:{request.Name} /All /NoRestart /English";
        var r = await ProcessRunner.RunAsync("dism.exe", args, TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException(r.StdErr + r.StdOut);

        WindowsFeatureStateService.Invalidate();
        var verify = await ProcessRunner.RunAsync(
            "dism.exe",
            $"/Online /Get-FeatureInfo /FeatureName:{request.Name} /English",
            TimeSpan.FromSeconds(30),
            token).ConfigureAwait(false);
        if (!verify.Success)
            throw new InvalidOperationException("Windows Feature read-back selhal: " + verify.StdErr + verify.StdOut);

        string output = verify.StdOut ?? string.Empty;
        bool confirmed = request.Disable
            ? output.IndexOf("State : Disabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
              output.IndexOf("State : Disable Pending", StringComparison.OrdinalIgnoreCase) >= 0
            : output.IndexOf("State : Enabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
              output.IndexOf("State : Enable Pending", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!confirmed)
            throw new InvalidOperationException(
                $"Windows Feature read-back nepotvrdil požadovaný stav {request.Name}. DISM výstup: {output.Trim()}");

        WindowsFeatureStateService.Invalidate();
        return new WorkerAck();
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
        var r = await ZoicwareToolsService.RunAiOperationStreamingAsync(request, token, onStdOut, onStdErr);
        return new AiWorkerResult(r.ExitCode, r.StdOut, r.StdErr);
    }

    public static async Task<WorkerAck> SetWindowsUpdatePauseAsync(BooleanMutationRequest request, CancellationToken token)
    {
        await AdvancedCenterService.SetWindowsUpdatePauseAsync(request.Enabled, token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static async Task<string[]> ApplyModernizerAdaptiveAsync(ModernizerAdaptiveRequest request, CancellationToken token)
    {
        var result = await WindowsModernizerService.ApplyAdaptiveRecommendedAsync(request.Report, token).ConfigureAwait(false);
        return result.ToArray();
    }

    public static async Task<WorkerAck> ApplyModernizerStartupAsync(CancellationToken token)
    {
        await WindowsModernizerService.ApplyStartupProfileAsync(token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static async Task<WorkerAck> RestoreModernizerStartupAsync(CancellationToken token)
    {
        await WindowsModernizerService.RestoreStartupProfileAsync(token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static async Task<WorkerAck> ApplyModernizerMaintenanceAsync(CancellationToken token)
    {
        await WindowsModernizerService.ApplySystemMaintenanceAsync(token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static async Task<WorkerAck> RestoreModernizerMaintenanceAsync(CancellationToken token)
    {
        await WindowsModernizerService.RestoreSystemMaintenanceAsync(token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static async Task<WorkerAck> ApplyModernizerVendorAsync(CancellationToken token)
    {
        await WindowsModernizerService.ApplyVendorIdleAsync(token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static async Task<WorkerAck> RestoreModernizerVendorAsync(CancellationToken token)
    {
        await WindowsModernizerService.RestoreVendorIdleAsync(token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static Task<int> OptimizeModernizerNetworkAsync(CancellationToken token) =>
        WindowsModernizerService.OptimizeNetworkAsync(token);

    public static async Task<WorkerAck> RestoreModernizerNetworkAsync(CancellationToken token)
    {
        await WindowsModernizerService.RestoreNetworkAsync(token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static Task<string> RetrimModernizerAsync(CancellationToken token) =>
        WindowsModernizerService.RetrimFixedDrivesAsync(token);

    public static async Task<WorkerAck> SetStartupAsync(StartupStateChangeRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await StartupManagerService.SetEnabledAsync(request.Item, request.Enabled, token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static async Task<WorkerAck> DeleteStartupAsync(StartupDeleteRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await StartupManagerService.DeleteAsync(request.Item, token).ConfigureAwait(false);
        return new WorkerAck();
    }

    public static Task<WorkerAck> SetTaskAsync(TaskStateChangeRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Worker payloads are untrusted. Recheck before any privileged COM.
        TaskSchedulerService.EnsureTaskMutationAllowed(request.Path, request.Enabled);
        TaskSchedulerComService.SetEnabled(request.Path, request.Enabled, token);
        TaskRuntimeState actual = TaskSchedulerComService.GetRuntimeState(request.Path, token);
        if (actual.Enabled != request.Enabled)
            throw new InvalidOperationException($"Task worker read-back nepotvrdil stav {request.Path}. Požadováno={request.Enabled}, skutečnost={actual.Enabled}.");
        return Task.FromResult(new WorkerAck());
    }

    public static Task<string[]> SetTasksBatchAsync(TaskStateBatchRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var changes = TaskSchedulerService.PrepareTaskMutationBatch(
            request.Items.Select(x => (x.Path, x.Enabled)).ToArray());
        // A forged batch must not partially modify tasks, including
        // conflicting duplicate states of the same physical task.
        if (changes.Length == 0) return Task.FromResult(Array.Empty<string>());
        string[] errors = TaskSchedulerComService.SetEnabledBatch(changes, token);
        if (errors.Length != 0) return Task.FromResult(errors);

        var paths = changes.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var states = TaskSchedulerComService.GetRuntimeStates(paths, token);
        var verifyErrors = changes
            .Where(x => !states.TryGetValue(x.Path, out var state) || state.Enabled != x.Enabled)
            .Select(x => $"{x.Path}: worker read-back nepotvrdil stav {x.Enabled}.")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult(verifyErrors);
    }

    public static async Task<ProcessResult> RunPrivilegedProcessAsync(PrivilegedProcessRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PrivilegedProcessRunner.ValidateRequest(request.File, request.Arguments);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 3600));
        return await ProcessRunner.RunAsync(request.File, request.Arguments, timeout, token).ConfigureAwait(false);
    }

    public static async Task<TweakWorkerResult> SetTweakAsync(TweakWorkerRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tweak = ResolveTweak(request.Id);
        var module = OptimizerModuleRuntime.From(tweak);
        await module.ApplyAsync(request.Enabled, token).ConfigureAwait(false);

        bool actual = await module.VerifyAsync(token).ConfigureAwait(false);
        if (actual != request.Enabled)
            throw new InvalidOperationException($"{tweak.Name}: elevated worker read-back nepotvrdil požadovaný stav {request.Enabled}.");

        return new TweakWorkerResult(actual, "Verified");
    }

    public static async Task<TweakWorkerResult> RestoreTweakAsync(TweakWorkerRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tweak = ResolveTweak(request.Id);
        var module = OptimizerModuleRuntime.From(tweak);
        if (!module.CanRestoreOriginal)
            throw new InvalidOperationException($"{tweak.Name}: explicitní obnova původní hodnoty není dostupná.");

        await module.RestoreOriginalAsync(token).ConfigureAwait(false);
        bool actual = await module.VerifyAsync(token).ConfigureAwait(false);
        return new TweakWorkerResult(actual, "Restored");
    }

    private static SpankyhoOptimizer.Models.TweakDefinition ResolveTweak(string id)
    {
        foreach (var source in CatalogService.EnumerateTweakSources())
        {
            SpankyhoOptimizer.Models.TweakDefinition? match = null;
            try
            {
                match = source.Factory().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                LogService.Error("worker-tweak", $"Catalog source failed while resolving {id}: {source.Section}/{source.Sub}", ex);
            }

            if (match != null)
                return match;
        }

        throw new InvalidOperationException("Worker tweak ID nebylo nalezeno v katalogu: " + id);
    }

}
