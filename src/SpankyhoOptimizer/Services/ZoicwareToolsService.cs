using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class ZoicwareToolsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly string ToolsDir = Path.Combine(LogService.Root, "Tools", "Zoicware");
    private const string AiUrl = "https://raw.githubusercontent.com/iFryno/RemoveWindowsAI/%23freezoic/RemoveWindowsAi.ps1";
    private static readonly SemaphoreSlim AiRefreshGate = new(1, 1);
    private static readonly SemaphoreSlim AiRuntimeGate = new(1, 1);
    private static readonly TimeSpan AiScriptFreshness = TimeSpan.FromMinutes(10);
    private static DateTime _aiFallbackRefreshAtUtc;
    private const int TrustedInstallerChildTimeoutMs = 360000;
    private const int TrustedInstallerHeartbeatMs = 15000;
    private static readonly TimeSpan StandardAiOptionTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan HeavyAiOptionTimeout = TimeSpan.FromMinutes(15);
    private const string TrustedInstallerWaitNeedle = "$R.WaitForExit()";
    private const string TrustedInstallerNativeGuardMarker = "# [Spanky] TrustedInstaller upstream-native lifecycle guard accepted";

    private static readonly HashSet<string> AiOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DisableRegKeys", "PreventAIPackageReinstall", "DisableCopilotPolicies", "RemoveAppxPackages",
        "RemoveRecallFeature", "RemoveCBSPackages", "RemoveAIFiles", "HideAIComponents",
        "DisableRewrite", "RemoveWindowsAITasks", "UpdateCleanupCheck", "DisableDefenderAI", "DisableNPUDevice"
    };
    private static readonly HashSet<string> ClassicApps = new(StringComparer.OrdinalIgnoreCase)
        { "photoviewer", "mspaint", "snippingtool", "notepad", "photoslegacy" };
    public static async Task<string> RefreshRemoveWindowsAiAsync(CancellationToken token = default)
    {
        Directory.CreateDirectory(ToolsDir);
        string destination = Path.Combine(ToolsDir, "RemoveWindowsAi.ps1");
        if (IsFreshScript(destination) || IsFallbackRefreshFresh(destination)) return destination;
        await AiRefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsFreshScript(destination) || IsFallbackRefreshFresh(destination)) return destination;
            try { return await DownloadAtomicAsync(AiUrl, destination, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException ex) when (IsValidScript(destination))
            {
                CleanupTempDownload(destination);
                _aiFallbackRefreshAtUtc = DateTime.UtcNow;
                LogService.Info("ai", "RemoveWindowsAI update deferred in non-elevated/test context; using protected local copy: " + ex.Message);
                return destination;
            }
            catch (Exception ex) when (IsValidScript(destination))
            {
                CleanupTempDownload(destination);
                _aiFallbackRefreshAtUtc = DateTime.UtcNow;
                LogService.Info("ai", "RemoveWindowsAI update failed; using last valid local copy: " + ex.Message);
                return destination;
            }
        }
        finally { AiRefreshGate.Release(); }
    }

    private static bool IsFreshScript(string path) =>
        IsValidScript(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < AiScriptFreshness;

    private static bool IsFallbackRefreshFresh(string path) =>
        IsValidScript(path) && _aiFallbackRefreshAtUtc != default &&
        DateTime.UtcNow - _aiFallbackRefreshAtUtc < AiScriptFreshness;

    private static bool IsValidScript(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length >= 2048; } catch { return false; }
    }

    internal static bool HasNativeTrustedInstallerLifecycle(string text) =>
        text.Contains("sc.exe start TrustedInstaller", StringComparison.Ordinal) &&
        text.Contains("Stop-Service -Name TrustedInstaller -Force", StringComparison.Ordinal) &&
        text.Contains("taskkill /im trustedinstaller.exe /f", StringComparison.OrdinalIgnoreCase);

    public static async Task<ProcessResult> RunAiOptionAsync(string option, bool revert = false, bool backup = true, CancellationToken token = default) =>
        await RunAiOptionStreamingAsync(option, revert, backup, token, null, null);

    public static async Task<ProcessResult> RunAiOptionStreamingAsync(string option, bool revert, bool backup,
        CancellationToken token, Action<string>? onOutput, Action<string>? onError)
        => await RunAiOperationStreamingAsync(new AiWorkerRequest(option, revert, backup), token, onOutput, onError);

    internal static async Task<ProcessResult> RunAiOperationStreamingAsync(AiWorkerRequest request,
        CancellationToken token, Action<string>? onOutput = null, Action<string>? onError = null)
    {
        if (!WorkerExecutionContext.IsWorker)
        {
            TimeSpan optionTimeout = request.RunWinUpdateRepair || request.AllOptions || request.InstallClassicApps is { Length: > 0 }
                ? TimeSpan.FromMinutes(15) : GetAiOptionTimeout(request.Option);
            var w = await WorkerClientService.RunStreamingAsync<AiWorkerResult>("ai-option",
                WorkerPayloads.Encode(request), optionTimeout + TimeSpan.FromMinutes(1), token,
                onOutput, onError);
            return new ProcessResult(w.ExitCode, w.StdOut, w.StdErr);
        }
        return await RunAiOperationLocalAsync(request, token, onOutput, onError);
    }

    internal static async Task<ProcessResult> RunAiOptionLocalAsync(string option, bool revert, bool backup, CancellationToken token, Action<string>? onOutput = null, Action<string>? onError = null)
        => await RunAiOperationLocalAsync(new AiWorkerRequest(option, revert, backup), token, onOutput, onError);

    private static async Task<ProcessResult> RunAiOperationLocalAsync(AiWorkerRequest request, CancellationToken token,
        Action<string>? onOutput, Action<string>? onError)
    {
        string[] options = string.IsNullOrWhiteSpace(request.Option)
            ? Array.Empty<string>()
            : request.Option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!request.AllOptions && !request.RunWinUpdateRepair && options.Any(option => !AiOptions.Contains(option)) && request.InstallClassicApps is not { Length: > 0 })
            throw new ArgumentOutOfRangeException(nameof(request.Option));
        string[] apps = request.InstallClassicApps ?? Array.Empty<string>();
        if (apps.Any(app => !ClassicApps.Contains(app))) throw new ArgumentOutOfRangeException(nameof(request.InstallClassicApps));
        if (request.ExcludeOptions && (request.AllOptions || options.Length == 0))
            throw new ArgumentException("ExcludeOptions vyžaduje alespoň jednu volbu a nesmí být použito s AllOptions.", nameof(request));
        string sourceScript = await RefreshRemoveWindowsAiAsync(token);
        string script = await PrepareHardenedRuntimeScriptAsync(sourceScript, token).ConfigureAwait(false);
        string args = "-nonInteractive -EnableLogging";
        if (request.AllOptions) args += " -AllOptions";
        else if (request.Option.Length > 0) args += $" -Options {request.Option}";
        if (apps.Length > 0) args += $" -InstallClassicApps {string.Join(',', apps)}";
        if (request.RunWinUpdateRepair) args += " -RunWinUpdateRepair";
        if (request.ExcludeOptions) args += " -ExcludeOptions";
        if (request.Revert) args += " -revertMode";
        if (request.Backup && !request.Revert) args += " -backupMode";
        TimeSpan optionTimeout = request.RunWinUpdateRepair || request.AllOptions || apps.Length > 0
            ? TimeSpan.FromMinutes(15) : GetAiOptionTimeout(request.Option);
        LogService.Info("ai", $"RemoveWindowsAI option={request.Option} all={request.AllOptions} classic={apps.Length} repair={request.RunWinUpdateRepair} revert={request.Revert} backup={request.Backup} timeoutSec={optionTimeout.TotalSeconds:0}");
        return await PowerShellRunner.RunFileStreamingAsync(script, args, optionTimeout, token,
            line => { LogService.Info("ai-script", line); onOutput?.Invoke(line); },
            line => { LogService.Error("ai-script", line); onError?.Invoke(line); });
    }

    internal static async Task<string> PrepareHardenedRuntimeScriptAsync(string sourceScript, CancellationToken token = default)
    {
        if (!IsValidScript(sourceScript)) throw new InvalidDataException("RemoveWindowsAI source script není platný.");
        string runtime = Path.Combine(ToolsDir, "RemoveWindowsAi.runtime.ps1");
        if (IsFreshHardenedRuntime(sourceScript, runtime)) return runtime;

        await AiRuntimeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsFreshHardenedRuntime(sourceScript, runtime)) return runtime;

            string text = await File.ReadAllTextAsync(sourceScript, token).ConfigureAwait(false);
            int first = text.IndexOf(TrustedInstallerWaitNeedle, StringComparison.Ordinal);
            int second = first < 0 ? -1 : text.IndexOf(TrustedInstallerWaitNeedle, first + TrustedInstallerWaitNeedle.Length, StringComparison.Ordinal);
            string hardened;
            if (first >= 0 && second < 0)
            {
                string replacement = $"$__spankyWait=0;while(-not $R.WaitForExit({TrustedInstallerHeartbeatMs})){{$__spankyWait+={TrustedInstallerHeartbeatMs};Write-Output ('[Spanky] TrustedInstaller child still running... '+[int]($__spankyWait/1000)+'s');if($__spankyWait-ge {TrustedInstallerChildTimeoutMs}){{try{{$R.Kill()}}catch{{}};throw 'Spankyho Optimizer: TrustedInstaller child timeout after {TrustedInstallerChildTimeoutMs / 1000}s'}}}}";
                hardened = text[..first] + replacement + text[(first + TrustedInstallerWaitNeedle.Length)..];
            }
            else if (first < 0 && HasNativeTrustedInstallerLifecycle(text))
            {
                hardened = text + Environment.NewLine + TrustedInstallerNativeGuardMarker + Environment.NewLine;
            }
            else
            {
                throw new InvalidDataException("RemoveWindowsAI TrustedInstaller guard neodpovídá podporovanému upstream tvaru.");
            }

            string temp = runtime + ".new";
            await File.WriteAllTextAsync(temp, hardened, token).ConfigureAwait(false);
            File.Move(temp, runtime, true);
            File.SetLastWriteTimeUtc(runtime, File.GetLastWriteTimeUtc(sourceScript));
            LogService.Info("ai", $"AI_RUNTIME_HARDENED timeoutSec={TrustedInstallerChildTimeoutMs / 1000} sourceBytes={text.Length} runtimeBytes={hardened.Length}");
            return runtime;
        }
        finally { AiRuntimeGate.Release(); }
    }

    public static string GetAiBackendActivitySummary()
    {
        string[] names = { "TrustedInstaller", "TiWorker", "dism", "dismhost", "powershell", "pwsh" };
        var active = new List<string>();
        try
        {
            var processes = System.Diagnostics.Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    string name;
                    try { name = process.ProcessName; } catch { continue; }
                    if (names.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) &&
                        !active.Contains(name, StringComparer.OrdinalIgnoreCase))
                        active.Add(name);
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { }
        return active.Count == 0 ? "žádný známý CBS/DISM/TI proces" : string.Join(", ", active);
    }

    private static TimeSpan GetAiOptionTimeout(string option) =>
        option is "RemoveCBSPackages" or "RemoveAIFiles" ? HeavyAiOptionTimeout : StandardAiOptionTimeout;

    internal static bool HasHardenedTrustedInstallerRuntime(string text)
    {
        bool legacyHardened = text.Contains($"WaitForExit({TrustedInstallerHeartbeatMs})", StringComparison.Ordinal) &&
                              text.Contains("TrustedInstaller child still running", StringComparison.Ordinal) &&
                              text.Contains($"TrustedInstaller child timeout after {TrustedInstallerChildTimeoutMs / 1000}s", StringComparison.Ordinal) &&
                              !text.Contains(TrustedInstallerWaitNeedle, StringComparison.Ordinal);
        bool nativeGuarded = text.Contains(TrustedInstallerNativeGuardMarker, StringComparison.Ordinal) &&
                             HasNativeTrustedInstallerLifecycle(text);
        return legacyHardened || nativeGuarded;
    }

    private static bool IsFreshHardenedRuntime(string sourceScript, string runtime)
    {
        try
        {
            if (!File.Exists(runtime) || File.GetLastWriteTimeUtc(runtime) != File.GetLastWriteTimeUtc(sourceScript)) return false;
            return HasHardenedTrustedInstallerRuntime(File.ReadAllText(runtime));
        }
        catch { return false; }
    }

    private static void CleanupTempDownload(string destination)
    {
        try
        {
            string temp = destination + ".new";
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch { }
    }

    private static async Task<string> DownloadAtomicAsync(string url, string destination, CancellationToken token)
    {
        string temp = destination + ".new";
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, token);
            var info = new FileInfo(temp);
            if (info.Length < 2048) throw new InvalidDataException("Stažený Zoicware skript je neplatný nebo příliš malý.");
            string downloaded = await File.ReadAllTextAsync(temp, token).ConfigureAwait(false);
            if (!downloaded.Contains("-Options", StringComparison.Ordinal) ||
                (!downloaded.Contains(TrustedInstallerWaitNeedle, StringComparison.Ordinal) &&
                 !HasNativeTrustedInstallerLifecycle(downloaded)))
                throw new InvalidDataException("Stažený Zoicware skript nemá očekávanou podporovanou strukturu.");
            File.Move(temp, destination, true);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
