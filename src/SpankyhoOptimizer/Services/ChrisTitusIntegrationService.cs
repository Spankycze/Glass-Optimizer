using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed record ChrisTitusIntegrationStatus(
    bool Installed,
    string Commit,
    string Detail,
    bool BackupAvailable);

internal static partial class ChrisTitusIntegrationService
{
    private const string ProfileOwner = "ChrisTitusTech";
    private const string ProfileRepo = "powershell-profile";
    private const string TitusOwner = "ChrisTitusTech";
    private const string TitusRepo = "titus-ai";
    private static readonly HttpClient Client = CreateClient();
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "Integrations");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly SemaphoreSlim UpdateCheckGate = new(1, 1);
    private static readonly SemaphoreSlim AutoUpdateGate = new(1, 1);
    private const string AutoUpdateKey = @"Software\SpankyhoOptimizer\Integrations";
    private const string ProfileAutoFlag = "ProfileAutoUpdate";
    private const string TitusAutoFlag = "TitusAiAutoUpdate";
    private static DateTime _updateCheckUtc;
    private static TimeSpan _updateCheckTtl = TimeSpan.FromMinutes(30);
    private static (bool ProfileUpdate, bool TitusUpdate) _updateCheckResult;

    // Explicit opt-in only, persisted per Windows user. No background writes
    // occur until the user enables the corresponding integration in the UI.
    internal static bool GetAutoUpdateEnabled(bool titus)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoUpdateKey);
        return (key?.GetValue(titus ? TitusAutoFlag : ProfileAutoFlag) as int?) == 1;
    }

    internal static void SetAutoUpdateEnabled(bool titus, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutoUpdateKey) ??
            throw new InvalidOperationException("Nelze uložit nastavení aktualizací.");
        key.SetValue(titus ? TitusAutoFlag : ProfileAutoFlag, enabled ? 1 : 0, RegistryValueKind.DWord);
        if (enabled) key.DeleteValue(titus ? "TitusLastAutoAttemptUtc" : "ProfileLastAutoAttemptUtc", false);
    }

    private static string ProfileRepoRoot => Path.Combine(Root, ProfileRepo);
    private static string TitusRepoRoot => Path.Combine(Root, TitusRepo);
    private static string PowerShellProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PowerShell", "Microsoft.PowerShell_profile.ps1");
    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string CodexHome => Path.Combine(UserHome, ".codex");
    private static string AgentsHome => Path.Combine(UserHome, ".agents");
    private static string PwshPath => File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
        ? @"C:\Program Files\PowerShell\7\pwsh.exe"
        : "powershell.exe";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpankyhoOptimizer/ChrisTitusIntegration");
        return client;
    }

    internal static ChrisTitusIntegrationStatus GetPowerShellProfileStatus()
    {
        string sha = ReadInstalledCommit(ProfileRepoRoot);
        bool installed = File.Exists(PowerShellProfilePath) && sha.Length > 0;
        bool backup = Directory.Exists(Path.GetDirectoryName(PowerShellProfilePath)!) &&
            Directory.GetFiles(Path.GetDirectoryName(PowerShellProfilePath)!, "oldprofile-*.ps1")
                .Length > 0;
        string detail = installed
            ? $"Profil aktivní • source {ShortSha(sha)}"
            : File.Exists(PowerShellProfilePath)
                ? "PowerShell profil existuje, ale není spravovaný touto integrací."
                : "Chris Titus PowerShell profil není nainstalovaný.";
        return new(installed, sha, detail, backup);
    }

    internal static ChrisTitusIntegrationStatus GetTitusAiStatus()
    {
        string sha = ReadInstalledCommit(TitusRepoRoot);
        bool config = File.Exists(Path.Combine(CodexHome, "config.toml"));
        bool agents = Directory.Exists(Path.Combine(AgentsHome, "skills"));
        bool installed = Directory.Exists(TitusRepoRoot) && sha.Length > 0 && config && agents;
        bool backup = Directory.Exists(Path.Combine(CodexHome, "backups")) &&
            Directory.EnumerateDirectories(Path.Combine(CodexHome, "backups"), "titus-ai-*")
                .Any();
        int skillCount = agents
            ? Directory.EnumerateDirectories(Path.Combine(AgentsHome, "skills")).Count()
            : 0;
        string detail = installed
            ? $"Titus AI aktivní • source {ShortSha(sha)} • skills {skillCount}"
            : "Titus AI není nainstalované.";
        return new(installed, sha, detail, backup);
    }

    private sealed class AutoUpdateDeferredException(string reason) : Exception(reason);

    internal static bool ShouldStartAutomaticInstaller(bool enabled, bool codexRunning) =>
        enabled && !codexRunning;

    internal static async Task<string> InstallOrUpdatePowerShellProfileAsync(CancellationToken token, bool automatic = false)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string sha = await GetMainCommitAsync(ProfileOwner, ProfileRepo, token).ConfigureAwait(false);
            if (IsCurrentManagedInstallation(ReadInstalledCommit(ProfileRepoRoot), sha,
                File.Exists(PowerShellProfilePath)))
                return "PowerShell Profile již používá aktuální verzi • " + ShortSha(sha);
            EnsureAdministrator("Chris Titus PowerShell Profile");
            using var stage = await StageImmutableRepoAsync(
                ProfileOwner, ProfileRepo, sha, "setup.ps1", ProfileRepoRoot, token).ConfigureAwait(false);
            string script = Path.Combine(stage.RootPath, "setup.ps1");
            using var profileSnapshot = new UserFileSnapshot(PowerShellProfilePath);
            if (automatic && !GetAutoUpdateEnabled(false))
                throw new AutoUpdateDeferredException("PowerShell Profile auto-update was disabled during download.");
            ProcessResult result = await RunPowerShellFileAsync(script, string.Empty, TimeSpan.FromMinutes(15), token)
                .ConfigureAwait(false);
            EnsureSuccess(result, "Chris Titus PowerShell Profile installer");
            if (!File.Exists(PowerShellProfilePath))
                throw new InvalidOperationException("Installer skončil bez chyby, ale PowerShell 7 profil nebyl nalezen.");
            WriteInstalledCommit(stage.RootPath, sha);
            profileSnapshot.Commit();
            stage.Commit(); // Only discard the previous repo after successful install and read-back.
            _updateCheckUtc = default; // Force fresh upstream check after successful install/update.
            return $"PowerShell Profile nainstalován/aktualizován • {ShortSha(sha)}";
        }
        finally
        {
            Gate.Release();
        }
    }
    internal static async Task<string> RestoreLatestPowerShellProfileBackupAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string? dir = Path.GetDirectoryName(PowerShellProfilePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                throw new InvalidOperationException("Složka PowerShell profilu neexistuje.");

            string? latest = Directory.GetFiles(dir, "oldprofile-*.ps1")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latest == null)
                throw new InvalidOperationException("Nebyla nalezena žádná záloha oldprofile-*.ps1.");

            Directory.CreateDirectory(dir);
            File.Copy(latest, PowerShellProfilePath, true);
            return "Obnovena záloha: " + Path.GetFileName(latest);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task<string> InstallOrUpdateTitusAiAsync(
        bool includePlugins,
        CancellationToken token, bool automatic = false)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string sha = await GetMainCommitAsync(TitusOwner, TitusRepo, token).ConfigureAwait(false);
            if (!includePlugins && IsCurrentManagedInstallation(ReadInstalledCommit(TitusRepoRoot), sha,
                File.Exists(Path.Combine(CodexHome, "config.toml")) &&
                Directory.Exists(Path.Combine(AgentsHome, "skills"))))
                return "Titus AI již používá aktuální verzi • " + ShortSha(sha);
            using var stage = await StageImmutableRepoAsync(
                TitusOwner, TitusRepo, sha, Path.Combine("scripts", "install.ps1"), TitusRepoRoot, token)
                .ConfigureAwait(false);
            string installer = Path.Combine(stage.RootPath, "scripts", "install.ps1");
            // Credentials and sessions are never backed up or touched. Only the
            // config.toml managed by the upstream integration is snapshotted.
            using var configSnapshot = new UserFileSnapshot(Path.Combine(CodexHome, "config.toml"));

            string pluginArg = includePlugins ? " -Plugins" : string.Empty;
            ProcessResult dryRun = await RunPowerShellFileAsync(
                installer, "-DryRun" + pluginArg, TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
            EnsureSuccess(dryRun, "Titus AI dry-run");
            if (!dryRun.StdOut.Contains("dry run complete", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Titus AI dry-run nepotvrdil dokončení.");

            // Codex might have started while GitHub/staging/dry-run was in progress.
            // Re-check opt-in and active Codex immediately before the first write.
            if (automatic)
            {
                using var active = new ProcessOwner(Process.GetProcessesByName("codex"));
                if (!ShouldStartAutomaticInstaller(GetAutoUpdateEnabled(true), active.Any))
                    throw new AutoUpdateDeferredException(
                        active.Any ? "Codex started while checking Titus AI update." :
                        "Titus AI auto-update was disabled during download.");
            }

            // Capture upstream backup directory names BEFORE the installer starts.
            // A terminated PowerShell process may never return 'backed up:' lines.
            var previousBackupFolders = SnapshotTitusBackupFolders(CodexHome);
            ProcessResult? install = null;
            try
            {
                install = await RunPowerShellFileAsync(
                    installer, pluginArg.Trim(), TimeSpan.FromMinutes(12), token).ConfigureAwait(false);
                EnsureSuccess(install, "Titus AI installer");
                if (!File.Exists(Path.Combine(CodexHome, "config.toml")))
                    throw new InvalidOperationException("Titus AI instalace nevytvořila ~/.codex/config.toml.");
                if (!Directory.Exists(Path.Combine(AgentsHome, "skills")))
                    throw new InvalidOperationException("Titus AI instalace nevytvořila ~/.agents/skills.");

                WriteInstalledCommit(stage.RootPath, sha);
                configSnapshot.Commit();
                stage.Commit(); // Only discard old repo after successful install + read-back.
            }
            catch
            {
                int restored = RestoreTitusManagedBackups(
                    (install?.StdOut ?? string.Empty) + Environment.NewLine + (install?.StdErr ?? string.Empty),
                    CodexHome, AgentsHome, stage.RootPath);
                restored += RestoreTitusNewBackupFolders(previousBackupFolders,
                    CodexHome, AgentsHome, stage.RootPath);
                LogService.Info("integrations", "TITUS_MANAGED_ROLLBACK restored=" + restored +
                    " (unmodified/locked items retain upstream backups)");
                throw;
            }
            _updateCheckUtc = default; // Force fresh upstream check after successful install/update.
            return includePlugins
                ? $"Titus AI + doporučené pluginy nainstalovány • {ShortSha(sha)}"
                : $"Titus AI nainstalováno/aktualizováno • {ShortSha(sha)}";
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static void OpenPowerShellProfileSource() =>
        ProcessRunner.Open("https://github.com/ChrisTitusTech/powershell-profile");

    internal static void OpenTitusAiSource() =>
        ProcessRunner.Open("https://github.com/ChrisTitusTech/titus-ai");

    internal static void OpenTitusAiBackups()
    {
        string backups = Path.Combine(CodexHome, "backups");
        Directory.CreateDirectory(backups);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{backups}\"") { UseShellExecute = true });
    }

    // Update checks use the GitHub API directly, with no powershell.exe/pwsh.exe process.
    // Installation is deliberately kept on the verified upstream-script path.
    internal static async Task<(bool ProfileUpdate, bool TitusUpdate)> CheckForUpdatesAsync(CancellationToken token)
    {
        await UpdateCheckGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            DateTime now = DateTime.UtcNow;
            if (now >= _updateCheckUtc && now - _updateCheckUtc < _updateCheckTtl)
                return _updateCheckResult;
            string profileLocal = ReadInstalledCommit(ProfileRepoRoot);
            // Titus AI was removed; only the PowerShell profile is checked.
            var profile = await CheckOneUpdateAsync(ProfileOwner, ProfileRepo, profileLocal, token).ConfigureAwait(false);
            _updateCheckResult = (profile.UpdateAvailable, false);
            _updateCheckTtl = profile.Success
                ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(2);
            _updateCheckUtc = DateTime.UtcNow;
            return _updateCheckResult;
        }
        finally { UpdateCheckGate.Release(); }
    }

    internal static bool IsGitHubCommit(string? sha) =>
        sha is { Length: 40 } && sha.All(Uri.IsHexDigit);

    internal static bool IsCurrentManagedInstallation(string? installedCommit, string? remoteCommit, bool healthy) =>
        healthy && IsGitHubCommit(installedCommit) && IsGitHubCommit(remoteCommit) &&
        string.Equals(installedCommit, remoteCommit, StringComparison.OrdinalIgnoreCase);

    internal static bool HasNewCommit(string? local, string? remote) =>
        IsGitHubCommit(local) && IsGitHubCommit(remote) &&
        !string.Equals(local, remote, StringComparison.OrdinalIgnoreCase);

    internal static bool IsAutoUpdateDue(bool enabled, bool updateAvailable, DateTime lastAttemptUtc, DateTime nowUtc) =>
        enabled && updateAvailable &&
        (lastAttemptUtc == default || nowUtc < lastAttemptUtc ||
         nowUtc - lastAttemptUtc >= TimeSpan.FromHours(24));

    internal static string GetAutoUpdateStatusLabel(bool titus)
    {
        if (!GetAutoUpdateEnabled(titus)) return "Auto-update: vypnuto";
        DateTime last = LastAutoUpdateAttempt(titus);
        string outcome;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoUpdateKey);
            outcome = key?.GetValue(titus ? "TitusAutoResult" : "ProfileAutoResult") as string ?? "neznámý výsledek";
        }
        catch { outcome = "stav není dostupný"; }
        if (last == default)
            return "Auto-update: zapnuto • " +
                (outcome.StartsWith("odloženo", StringComparison.OrdinalIgnoreCase)
                    ? outcome : "zatím neproběhl pokus");
        return "Auto-update: zapnuto • " + last.ToLocalTime().ToString("dd.MM. HH:mm") +
            " • " + outcome;
    }

    private static void SaveAutoUpdateResult(bool titus, string status)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AutoUpdateKey);
            key?.SetValue(titus ? "TitusAutoResult" : "ProfileAutoResult", status, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            LogService.Info("integrations", "Auto-update result write unavailable: " + ex.Message);
        }
    }

    private static DateTime LastAutoUpdateAttempt(bool titus)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoUpdateKey);
            object? value = key?.GetValue(titus ? "TitusLastAutoAttemptUtc" : "ProfileLastAutoAttemptUtc");
            long ticks = Convert.ToInt64(value ?? 0L);
            return ticks > DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc) : default;
        }
        catch { return default; }
    }

    private static void ClearAutoUpdateAttempt(bool titus)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoUpdateKey, writable: true);
            key?.DeleteValue(titus ? "TitusLastAutoAttemptUtc" : "ProfileLastAutoAttemptUtc", false);
        }
        catch (Exception ex) { LogService.Info("integrations", "Auto-update deferral write: " + ex.Message); }
    }

    private static void SaveAutoUpdateAttempt(bool titus)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutoUpdateKey) ??
            throw new InvalidOperationException("Nelze uložit čas poslední aktualizace.");
        key.SetValue(titus ? "TitusLastAutoAttemptUtc" : "ProfileLastAutoAttemptUtc",
            DateTime.UtcNow.Ticks, RegistryValueKind.QWord);
    }

    internal static async Task RunOptInAutoUpdatesAsync(
        (bool ProfileUpdate, bool TitusUpdate) updates, CancellationToken token)
    {
        bool profileEnabled = GetAutoUpdateEnabled(titus: false);
        if (!profileEnabled) return;
        await AutoUpdateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (GetAutoUpdateEnabled(false) && IsAutoUpdateDue(profileEnabled, updates.ProfileUpdate, LastAutoUpdateAttempt(false), DateTime.UtcNow))
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    LogService.Info("integrations", "Profile auto-update deferred: requires administrator.");
                else
                {
                    SaveAutoUpdateAttempt(false);
                    try
                    {
                        string result = await InstallOrUpdatePowerShellProfileAsync(token, automatic: true).ConfigureAwait(false);
                        SaveAutoUpdateResult(false, "úspěšná aktualizace");
                        LogService.Info("integrations", "PROFILE_AUTO_UPDATE_OK " + result);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (AutoUpdateDeferredException ex)
                    {
                        ClearAutoUpdateAttempt(false);
                        SaveAutoUpdateResult(false, "odloženo");
                        LogService.Info("integrations", "Profile auto-update deferred: " + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        SaveAutoUpdateResult(false, "chyba aktualizace");
                        LogService.Error("integrations", "Profile automatic update failed; rollback requested", ex);
                    }
                }
            }

        }
        finally { AutoUpdateGate.Release(); }
    }

    private sealed class ProcessOwner(Process[] processes) : IDisposable
    {
        public bool Any => processes.Length != 0;
        public void Dispose() { foreach (var process in processes) process.Dispose(); }
    }

    private static async Task<(bool UpdateAvailable, bool Success)> CheckOneUpdateAsync(
        string owner, string repo, string installedCommit, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(installedCommit)) return (false, true);
        try
        {
            string latest = await GetMainCommitAsync(owner, repo, token).ConfigureAwait(false);
            return (HasNewCommit(installedCommit, latest), true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Info("integrations", "GitHub commit check unavailable " + repo + ": " + ex.Message);
            return (false, false);
        }
    }

    private static async Task<string> GetMainCommitAsync(
        string owner,
        string repo,
        CancellationToken token)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/commits/main";
        using HttpResponseMessage response = await Client.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
        string? sha = json.RootElement.GetProperty("sha").GetString();
        if (!IsGitHubCommit(sha))
            throw new InvalidDataException($"GitHub nevrátil platný 40znakový commit pro {owner}/{repo}.");
        return sha.Trim();
    }
}
