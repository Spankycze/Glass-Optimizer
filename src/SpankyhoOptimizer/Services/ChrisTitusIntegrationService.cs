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

internal static class ChrisTitusIntegrationService
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
            string titusLocal = ReadInstalledCommit(TitusRepoRoot);
            // Run independently: failed Profile discovery must not suppress a
            // successful Titus AI update result, and vice versa.
            var profileTask = CheckOneUpdateAsync(ProfileOwner, ProfileRepo, profileLocal, token);
            var titusTask = CheckOneUpdateAsync(TitusOwner, TitusRepo, titusLocal, token);
            var profile = await profileTask.ConfigureAwait(false);
            var titus = await titusTask.ConfigureAwait(false);
            _updateCheckResult = (profile.UpdateAvailable, titus.UpdateAvailable);
            _updateCheckTtl = profile.Success && titus.Success
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
        bool titusEnabled = GetAutoUpdateEnabled(titus: true);
        if (!profileEnabled && !titusEnabled) return;
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

            if (GetAutoUpdateEnabled(true) && IsAutoUpdateDue(titusEnabled, updates.TitusUpdate, LastAutoUpdateAttempt(true), DateTime.UtcNow))
            {
                // Never replace Codex config while a Codex process is running.
                bool codexRunning;
                using (var processes = new ProcessOwner(Process.GetProcessesByName("codex")))
                    codexRunning = processes.Any;
                if (codexRunning)
                    LogService.Info("integrations", "Titus AI auto-update deferred: Codex running.");
                else
                {
                    SaveAutoUpdateAttempt(true);
                    try
                    {
                        string result = await InstallOrUpdateTitusAiAsync(false, token, automatic: true).ConfigureAwait(false);
                        SaveAutoUpdateResult(true, "úspěšná aktualizace");
                        LogService.Info("integrations", "TITUS_AUTO_UPDATE_OK " + result);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (AutoUpdateDeferredException ex)
                    {
                        ClearAutoUpdateAttempt(true);
                        SaveAutoUpdateResult(true, "odloženo: Codex nebo vypnuté aktualizace");
                        LogService.Info("integrations", "Titus auto-update deferred: " + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        SaveAutoUpdateResult(true, "chyba aktualizace");
                        LogService.Error("integrations", "Titus AI automatic update failed; rollback requested", ex);
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

    // The upstream installers can alter a user-owned config file. Snapshot it
    // in memory before invoking PowerShell, restore on failure, clear bytes at end.
    private sealed class UserFileSnapshot : IDisposable
    {
        private readonly string _path;
        private readonly byte[]? _before;
        private bool _committed;
        public UserFileSnapshot(string path)
        {
            _path = path;
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > 8L * 1024 * 1024)
                    throw new InvalidDataException("Refusing to snapshot a configuration file over 8 MiB.");
                _before = File.ReadAllBytes(path);
            }
        }
        public void Commit() => _committed = true;
        public void Dispose()
        {
            try
            {
                if (_committed) return;
                if (_before == null)
                {
                    if (File.Exists(_path)) File.Delete(_path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllBytes(_path, _before);
                }
                LogService.Info("integrations", "User configuration rollback completed");
            }
            finally
            {
                if (_before != null)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(_before);
            }
        }
    }

    // The upstream installer uses <codex>/backups/titus-ai-<timestamp>-<PID>.
    // Snapshotting only folder names is O(n) on backup count and allocates no
    // copies of skills. We do not read or modify users' skill content.
    private static HashSet<string> SnapshotTitusBackupFolders(string codexHome)
    {
        string root = Path.Combine(codexHome, "backups");
        if (!Directory.Exists(root)) return new(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateDirectories(root, "titus-ai-*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Recovery without process stdout: consider only ONE newly-created backup
    // folder. If another installer concurrently produced folders, skip to avoid
    // claiming backups that belong to a separate invocation.
    internal static int RestoreTitusNewBackupFolders(
        HashSet<string> before, string codexHome, string agentsHome, string stagedRepoRoot)
    {
        string backups = Path.Combine(codexHome, "backups");
        if (!Directory.Exists(backups)) return 0;
        string[] created = Directory.EnumerateDirectories(backups, "titus-ai-*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath).Where(p => !before.Contains(p)).ToArray();
        if (created.Length != 1)
        {
            if (created.Length > 1)
                LogService.Info("integrations", "Titus recovery ambiguous: multiple new backup folders; retained for manual recovery");
            return 0;
        }
        string folder = created[0];
        string name = Path.GetFileName(folder);
        // Match the upstream timestamp-PID convention; reject symlinked backup
        // folders and untrusted locations.
        if (!System.Text.RegularExpressions.Regex.IsMatch(name,
                @"^titus-ai-\d{8}-\d{6}-\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            new DirectoryInfo(folder).LinkTarget != null)
            return 0;

        var generated = new List<string>();
        void Candidate(string target, string backup)
        {
            if (File.Exists(backup) || Directory.Exists(backup) ||
                new FileInfo(backup).LinkTarget != null || new DirectoryInfo(backup).LinkTarget != null)
                generated.Add("backed up: " + target + " -> " + backup);
        }
        Candidate(Path.Combine(codexHome, "AGENTS.md"), Path.Combine(folder, "AGENTS.md"));
        Candidate(Path.Combine(codexHome, "rules"), Path.Combine(folder, "rules"));
        foreach (string cfg in Directory.EnumerateFiles(folder, "*.config.toml", SearchOption.TopDirectoryOnly))
            Candidate(Path.Combine(codexHome, Path.GetFileName(cfg)), cfg);
        string skillsBackup = Path.Combine(folder, "agents", "skills");
        string agentsBackup = Path.Combine(folder, "agents");
        if (Directory.Exists(skillsBackup) &&
            new DirectoryInfo(agentsBackup).LinkTarget == null &&
            new DirectoryInfo(skillsBackup).LinkTarget == null)
        {
            foreach (string skill in Directory.EnumerateFileSystemEntries(skillsBackup,
                         "*", SearchOption.TopDirectoryOnly))
                Candidate(Path.Combine(agentsHome, "skills", Path.GetFileName(skill)), skill);
        }
        return RestoreTitusManagedBackups(string.Join(Environment.NewLine, generated),
            codexHome, agentsHome, stagedRepoRoot);
    }

    // Recover paths that the pinned upstream installer itself moved into its
    // titus-ai-* backup folder. Only missing targets or symlinks pointing at
    // the staged repo are eligible; unrelated user files are never overwritten.
    internal static int RestoreTitusManagedBackups(
        string installerOutput, string codexHome, string agentsHome, string stagedRepoRoot)
    {
        int restored = 0;
        string backupPrefix = Path.GetFullPath(Path.Combine(codexHome, "backups", "titus-ai-"));
        string skillsRoot = Path.GetFullPath(Path.Combine(agentsHome, "skills")) +
            Path.DirectorySeparatorChar;
        string codexRoot = Path.GetFullPath(codexHome);
        foreach (string line in installerOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            const string marker = "backed up: ";
            if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
            int arrow = line.LastIndexOf(" -> ", StringComparison.Ordinal);
            if (arrow <= marker.Length) continue;
            string target;
            string backup;
            try
            {
                target = Path.GetFullPath(line[marker.Length..arrow].Trim());
                backup = Path.GetFullPath(line[(arrow + 4)..].Trim());
            }
            catch { continue; }
            bool managedSkill = target.StartsWith(skillsRoot, StringComparison.OrdinalIgnoreCase) &&
                !target[skillsRoot.Length..].Contains(Path.DirectorySeparatorChar);
            bool managedCodex = Path.GetDirectoryName(target)?.Equals(codexRoot,
                StringComparison.OrdinalIgnoreCase) == true &&
                (Path.GetFileName(target).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(target).Equals("rules", StringComparison.OrdinalIgnoreCase) ||
                 (Path.GetFileName(target).EndsWith(".config.toml", StringComparison.OrdinalIgnoreCase)));
            if (!managedSkill && !managedCodex) continue;
            if (!backup.StartsWith(backupPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = backup[backupPrefix.Length..];
            int slash = rest.IndexOf(Path.DirectorySeparatorChar);
            if (slash <= 0) continue;
            string backupRelative = rest[(slash + 1)..];
            string expectedRelative = managedSkill
                ? Path.Combine("agents", "skills", Path.GetFileName(target))
                : Path.GetFileName(target);
            if (!backupRelative.Equals(expectedRelative, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var backupDir = new DirectoryInfo(backup);
                var backupFile = new FileInfo(backup);
                bool dirBackup = backupDir.Exists || backupDir.LinkTarget != null;
                bool fileBackup = backupFile.Exists || backupFile.LinkTarget != null;
                if (!dirBackup && !fileBackup) continue;

                var targetDir = new DirectoryInfo(target);
                var targetFile = new FileInfo(target);
                string? link = targetDir.LinkTarget ?? targetFile.LinkTarget;
                bool targetExists = targetDir.Exists || targetFile.Exists || link != null;
                if (targetExists)
                {
                    if (string.IsNullOrWhiteSpace(link)) continue;
                    string resolvedLink = Path.GetFullPath(
                        Path.IsPathFullyQualified(link) ? link : Path.Combine(
                            Path.GetDirectoryName(target)!, link));
                    string stage = Path.GetFullPath(stagedRepoRoot).TrimEnd(
                        Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!resolvedLink.StartsWith(stage, StringComparison.OrdinalIgnoreCase)) continue;
                    if (targetDir.LinkTarget != null) Directory.Delete(target);
                    else File.Delete(target);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (dirBackup) Directory.Move(backup, target);
                else File.Move(backup, target);
                restored++;
            }
            catch (Exception ex)
            {
                // Backup is retained for manual recovery if Windows denies a
                // move (e.g. locked symlink or concurrent Codex process).
                LogService.Info("integrations", "Managed skill recovery skipped: " + ex.Message);
            }
        }
        return restored;
    }

    // Owns the previous checkout until its replacement has completed installation
    // and read-back. Dispose rolls back failed/cancelled installers automatically.
    private sealed class StagedRepository(string rootPath, string? previousPath) : IDisposable
    {
        public string RootPath { get; } = rootPath;
        private bool _committed;
        public void Commit()
        {
            _committed = true;
            if (previousPath != null && Directory.Exists(previousPath))
            {
                try { Directory.Delete(previousPath, recursive: true); }
                catch (Exception ex) { LogService.Info("integrations", "Old repo backup retained: " + ex.Message); }
            }
        }

        public void Dispose()
        {
            if (_committed) return;
            try
            {
                if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
                if (previousPath != null && Directory.Exists(previousPath))
                    Directory.Move(previousPath, RootPath);
                LogService.Info("integrations", "Integration repository rollback completed");
            }
            catch (Exception ex)
            {
                // Keep .previous-* on disk for manual recovery if files are locked.
                LogService.Error("integrations", "Integration repository rollback failed; backup retained at " +
                    (previousPath ?? "(none)"), ex);
                throw;
            }
        }
    }

    // Disk-only smoke; never opens the user's PowerShell/Codex configuration.
    internal static void RunStagingTransactionSmoke()
    {
        string temp = Path.Combine(Path.GetTempPath(), "SpankyRepoTx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string root = Path.Combine(temp, "repo");
            string old = root + ".previous-test";
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "original.txt"), "old");
            Directory.Move(root, old);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "replacement.txt"), "new");
            using (new StagedRepository(root, old)) { /* simulate installer failure */ }
            if (!File.Exists(Path.Combine(root, "original.txt")) ||
                File.Exists(Path.Combine(root, "replacement.txt")) || Directory.Exists(old))
                throw new InvalidOperationException("Failed integration staging did not roll back.");
            Directory.Move(root, old);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "replacement.txt"), "new");
            using (var transaction = new StagedRepository(root, old))
                transaction.Commit();
            if (!File.Exists(Path.Combine(root, "replacement.txt")) || Directory.Exists(old))
                throw new InvalidOperationException("Successful integration staging did not commit.");

            // Verify profile/config snapshots on failure, success and new file.
            string config = Path.Combine(temp, "config.toml");
            File.WriteAllText(config, "existing=1");
            using (new UserFileSnapshot(config))
                File.WriteAllText(config, "broken=2");
            if (File.ReadAllText(config) != "existing=1")
                throw new InvalidOperationException("Config rollback failed.");
            using (var snapshot = new UserFileSnapshot(config))
            {
                File.WriteAllText(config, "updated=3");
                snapshot.Commit();
            }
            if (File.ReadAllText(config) != "updated=3")
                throw new InvalidOperationException("Committed config was reverted.");
            string newConfig = Path.Combine(temp, "created.toml");
            using (new UserFileSnapshot(newConfig))
                File.WriteAllText(newConfig, "temporary=1");
            if (File.Exists(newConfig))
                throw new InvalidOperationException("Failed new config was not removed.");

            string codex = Path.Combine(temp, "codex");
            string agents = Path.Combine(temp, "agents");
            string skills = Path.Combine(agents, "skills");
            string stageRepo = Path.Combine(temp, "stagedRepo");
            string oldBackup = Path.Combine(codex, "backups", "titus-ai-test", "agents", "skills", "sample");
            string recovered = Path.Combine(skills, "sample");
            Directory.CreateDirectory(oldBackup);
            Directory.CreateDirectory(skills);
            File.WriteAllText(Path.Combine(oldBackup, "SKILL.md"), "before");
            string stdout = "backed up: " + recovered + " -> " + oldBackup;
            if (RestoreTitusManagedBackups(stdout, codex, agents, stageRepo) != 1 ||
                !File.Exists(Path.Combine(recovered, "SKILL.md")) ||
                Directory.Exists(oldBackup))
                throw new InvalidOperationException("Titus AI skill backup was not restored.");
            Directory.Move(recovered, oldBackup);
            Directory.CreateDirectory(recovered);
            File.WriteAllText(Path.Combine(recovered, "user.txt"), "protected");
            if (RestoreTitusManagedBackups(stdout, codex, agents, stageRepo) != 0 ||
                !File.Exists(Path.Combine(recovered, "user.txt")) ||
                !File.Exists(Path.Combine(oldBackup, "SKILL.md")))
                throw new InvalidOperationException("Titus AI rollback overwrote an unrelated user directory.");
            string escape = Path.Combine(temp, "unrelated");
            string badLine = "backed up: " + escape + " -> " + oldBackup;
            if (RestoreTitusManagedBackups(badLine, codex, agents, stageRepo) != 0)
                throw new InvalidOperationException("Titus AI rollback accepted an unmanaged destination.");

            // Simulate cancellation / killed upstream process with NO stdout.
            // Recover only the new, unambiguous timestamp-PID backup folder.
            var existing = SnapshotTitusBackupFolders(codex);
            string journal = Path.Combine(codex, "backups", "titus-ai-20261011-105500-18721");
            string retained = Path.Combine(journal, "agents", "skills", "interrupted");
            Directory.CreateDirectory(retained);
            File.WriteAllText(Path.Combine(retained, "SKILL.md"), "old-version");
            if (RestoreTitusNewBackupFolders(existing, codex, agents, stageRepo) != 1 ||
                !File.Exists(Path.Combine(skills, "interrupted", "SKILL.md")) ||
                Directory.Exists(retained))
                throw new InvalidOperationException("Titus backup-directory fallback failed without process output.");

            // Competing backup creation must never be guessed as ours.
            var known = SnapshotTitusBackupFolders(codex);
            string first = Path.Combine(codex, "backups", "titus-ai-20261011-105501-18722",
                "agents", "skills", "first");
            string second = Path.Combine(codex, "backups", "titus-ai-20261011-105502-18723",
                "agents", "skills", "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            if (RestoreTitusNewBackupFolders(known, codex, agents, stageRepo) != 0 ||
                !Directory.Exists(first) || !Directory.Exists(second))
                throw new InvalidOperationException("Ambiguous Titus backups were incorrectly restored.");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static async Task<StagedRepository> StageImmutableRepoAsync(
        string owner,
        string repo,
        string sha,
        string requiredRelativePath,
        string destination,
        CancellationToken token)
    {
        if (!IsGitHubCommit(sha))
            throw new InvalidDataException("Refusing to stage a non-immutable GitHub ref.");
        Directory.CreateDirectory(Root);
        string work = Path.Combine(Root, ".stage-" + repo + "-" + Guid.NewGuid().ToString("N"));
        string zip = work + ".zip";
        string extracted = work + "-extract";
        string? previous = null;
        try
        {
            Directory.CreateDirectory(work);
            Directory.Delete(work, true);

            string url = $"https://codeload.github.com/{owner}/{repo}/zip/{sha}";
            using HttpResponseMessage response = await Client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            const long maxCompressed = 32L * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxCompressed)
                throw new InvalidDataException("GitHub integration archive exceeds 32 MiB compressed limit.");
            await using (Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(
                zip, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[131072];
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > maxCompressed)
                        throw new InvalidDataException("GitHub integration archive exceeds 32 MiB compressed limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
            }
            // Validate archive accounting before extraction to avoid zip bombs.
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count > 5000 ||
                    archive.Entries.Sum(x => x.Length) > 128L * 1024 * 1024)
                    throw new InvalidDataException("GitHub integration archive exceeds safe extraction limits.");
            }
            Directory.CreateDirectory(extracted);
            await ZipFile.ExtractToDirectoryAsync(zip, extracted, overwriteFiles: true, token)
                .ConfigureAwait(false);
            string source = Directory.EnumerateDirectories(extracted).Single();
            string required = Path.Combine(source, requiredRelativePath);
            if (!File.Exists(required))
                throw new InvalidDataException($"Archiv {owner}/{repo}@{ShortSha(sha)} neobsahuje {requiredRelativePath}.");

            if (Directory.Exists(destination))
            {
                previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(destination, previous);
            }

            Directory.Move(source, destination);
            return new StagedRepository(destination, previous);
        }
        catch
        {
            if (previous != null && Directory.Exists(previous))
            {
                // Preserve the previous version even if the target rename failed.
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                Directory.Move(previous, destination);
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            try { if (Directory.Exists(extracted)) Directory.Delete(extracted, true); } catch { }
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }
    private static async Task<ProcessResult> RunPowerShellFileAsync(
        string script,
        string arguments,
        TimeSpan timeout,
        CancellationToken token)
    {
        string args = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
        if (!string.IsNullOrWhiteSpace(arguments))
            args += " " + arguments.Trim();
        return await ProcessRunner.RunAsync(PwshPath, args, timeout, token).ConfigureAwait(false);
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.Success) return;
        string detail = (result.StdErr + Environment.NewLine + result.StdOut).Trim();
        if (detail.Length > 1800) detail = detail[^1800..];
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"{operation} selhal s kódem {result.ExitCode}."
                : $"{operation} selhal: {detail}");
    }

    private static void EnsureAdministrator(string operation)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException($"{operation} vyžaduje spuštění Spankyho Optimizeru jako správce.");
    }

    private static void WriteInstalledCommit(string repoRoot, string sha)
    {
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, ".spanky-source-sha"), sha.Trim());
    }

    private static string ReadInstalledCommit(string repoRoot)
    {
        try
        {
            string path = Path.Combine(repoRoot, ".spanky-source-sha");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ShortSha(string sha) =>
        string.IsNullOrWhiteSpace(sha)
            ? "unknown"
            : sha[..Math.Min(8, sha.Length)];
}
