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
    private static string MutexName => "Local\\GlassOptimizerBackgroundUpdate";

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
        using var mutex = new Mutex(false, MutexName);
        bool ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex) return;

            UpdateState state = LoadState();
            if (DateTimeOffset.UtcNow - state.LastCheckUtc < CheckInterval) return;
            SaveState(state with { LastCheckUtc = DateTimeOffset.UtcNow });

            AppUpdateCheck check = await AppUpdateService.CheckOfficialGithubAsync(token).ConfigureAwait(false);
            if (!check.Available || check.Manifest == null) return;
            string installer = await AppUpdateService.DownloadAsync(check.Manifest, token).ConfigureAwait(false);
            AppUpdateService.LaunchInstallerForUpdate(installer, check.Manifest.Sha256, silent: true);
            SaveState(LoadState() with { LastInstalledVersion = check.Manifest.Version });
            LogService.Info("update", $"BACKGROUND_UPDATE_LAUNCHED version={check.Manifest.Version}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Error("update", "Background updater failed", ex); }
        finally
        {
            if (ownsMutex)
            {
                try { mutex.ReleaseMutex(); } catch { }
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

    private sealed record UpdateState(DateTimeOffset LastCheckUtc = default, string? LastInstalledVersion = null);
}
