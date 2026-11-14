using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static class BackgroundUpdateService
{
    private const string RunValueName = "GlassOptimizerBackgroundUpdate";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(5);
    private static string StatePath => Path.Combine(LogService.Root, "background-update-state.json");
    private const string GateName = "Local\\GlassOptimizerBackgroundUpdateGate";

    public static void EnsureStartupRegistration()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            string executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(executable)) return;
            key.SetValue(RunValueName, $"\"{executable}\" --background-update", RegistryValueKind.String);
            LogService.Info("update", "BACKGROUND_UPDATE_STARTUP_REGISTERED");
        }
        catch (Exception ex)
        {
            LogService.Error("update", "Background updater startup registration failed", ex);
        }
    }

    public static async Task RunOnceAsync(CancellationToken token = default)
    {
        using var updateGate = new Semaphore(1, 1, GateName);
        bool ownsGate = false;
        try
        {
            ownsGate = updateGate.WaitOne(TimeSpan.Zero);
            if (!ownsGate) return;

            UpdateState state = LoadState();
            TimeSpan retryInterval = state.LastCheckSucceeded ? CheckInterval : TimeSpan.FromMinutes(30);
            if (DateTimeOffset.UtcNow - state.LastCheckUtc < retryInterval) return;
            state = state with { LastCheckUtc = DateTimeOffset.UtcNow, LastCheckSucceeded = false };
            SaveState(state);
            AppUpdateCheck check = await AppUpdateService.CheckOfficialGithubAsync(token).ConfigureAwait(false);
            if (!check.Available || check.Manifest == null)
            {
                SaveState(state with { LastCheckSucceeded = check.Configured });
                return;
            }

            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Updater executable is unavailable.");
            string encodedManifest = AppUpdateService.SerializePopupManifest(check.Manifest);
            var popupStart = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            popupStart.ArgumentList.Add("--automatic-update-popup");
            popupStart.ArgumentList.Add("--automatic-update-popup-manifest=" + encodedManifest);
            using Process? popup = Process.Start(popupStart);
            if (popup == null) throw new InvalidOperationException("Automatic update popup could not be started.");
            using var popupWait = CancellationTokenSource.CreateLinkedTokenSource(token);
            popupWait.CancelAfter(TimeSpan.FromMinutes(45));
            try
            {
                await popup.WaitForExitAsync(popupWait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (popupWait.IsCancellationRequested)
            {
                try { if (!popup.HasExited) popup.Kill(entireProcessTree: true); } catch { }
                try
                {
                    using var exitWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await popup.WaitForExitAsync(exitWait.Token).ConfigureAwait(false);
                }
                catch { }
                LogService.Error("update", token.IsCancellationRequested
                    ? "Background update popup cancelled."
                    : "Background update popup timed out after 45 minutes.");
                return;
            }
            if (popup.ExitCode == 0)
            {
                SaveState(state with
                {
                    LastCheckUtc = DateTimeOffset.UtcNow,
                    LastCheckSucceeded = true,
                    LastInstalledVersion = check.Manifest.Version
                });
                LogService.Info("update", $"BACKGROUND_UPDATE_INSTALLED version={check.Manifest.Version}");
            }
            else
            {
                SaveState(state with { LastCheckSucceeded = false });
                LogService.Error("update", $"BACKGROUND_UPDATE_FAILED version={check.Manifest.Version} exit={popup.ExitCode}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Error("update", "Background updater failed", ex); }
        finally
        {
            if (ownsGate)
            {
                try { updateGate.Release(); } catch { }
            }
        }
    }

    private static UpdateState LoadState() =>
        SelfHealingJsonService.ReadOrDefault(StatePath, static () => new UpdateState(), "background-update");

    private static void SaveState(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(LogService.Root);
            AtomicFileService.WriteText(StatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) { LogService.Error("update", "Background updater state save failed", ex); }
    }

    private sealed record UpdateState(
        DateTimeOffset LastCheckUtc = default,
        string? LastInstalledVersion = null,
        bool LastCheckSucceeded = true);
}
