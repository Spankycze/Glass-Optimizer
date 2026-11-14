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

internal static partial class ChrisTitusIntegrationService
{
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
