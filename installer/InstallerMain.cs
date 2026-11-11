using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using Microsoft.Win32;

namespace SpankyhoOptimizer.ModernSetup;

internal static class Program
{
	internal const string AppName = "Glass Optimizer";

	internal static readonly string Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    internal static readonly string DisplayVersion = typeof(Program).Assembly.GetName().Version is Version v
        ? $"{v.Major}.{v.Minor}" : "0.0";

	// .NET Desktop Runtime includes Microsoft.NETCore.App. The plain .NET
	// Runtime does not include the WindowsDesktop.App framework used by UI apps.
	// Pinned official Microsoft 10.0.12 x64 release with SHA-512 from releases.json.
	internal const string DotNetUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe";

	internal const string DotNetSha512 = "0b907e9312867172a4eb82f4b5ab3f7c2d25e27d8349546d77eeb5d5b8cbecab9ebebfed189b13e9669dc578d892756c548366da539d47b3ddac5efcb7ae72fe";

	internal const string WebView2Url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

	internal static int UpdateFromPid { get; private set; }

	internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Spankyho Optimizer");

	// Fixture/test switches can extract, overwrite or recursively delete paths.
	// They are permitted ONLY under this process's disposable TEMP tree, never
	// against the user's real Program Files / ProgramData installation.
	private static bool IsIsolatedTestDestination(string candidate)
	{
		try
		{
			string full = Path.GetFullPath(candidate)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			static bool Below(string path, string root)
			{
				string prefix = Path.GetFullPath(root)
					.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
					+ Path.DirectorySeparatorChar;
				return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
			}
			static bool HasNoReparseParents(string candidate)
			{
				// Lexically safe paths can still escape through an NTFS junction or
				// symlink. Inspect every existing ancestor, including the TEMP root,
				// before running fixture modes with elevated installer privileges.
				for (string? cursor = candidate; !string.IsNullOrWhiteSpace(cursor);
					cursor = Path.GetDirectoryName(cursor))
				{
					try
					{
						FileAttributes attributes = File.GetAttributes(cursor);
						if ((attributes & FileAttributes.ReparsePoint) != 0 ||
							(attributes & FileAttributes.Directory) == 0)
							return false;
					}
					catch (FileNotFoundException) { /* Future child, check its parent. */ }
					catch (DirectoryNotFoundException) { /* Future child. */ }
				}
				return true;
			}
			if (!HasNoReparseParents(full)) return false;
			if (Below(full, Path.GetTempPath())) return true;
			// The installer requires elevation. UAC can reset TEMP to a different
			// directory than the unprivileged build process. Accept ONLY the
			// explicitly configured Dev Drive build TEMP location as a second root;
			// never allow arbitrary caller-provided paths or Program Files/Data.
			string? drive = Environment.GetEnvironmentVariable("SPANKY_DEV_DRIVE",
				EnvironmentVariableTarget.User);
			if (!string.IsNullOrWhiteSpace(drive))
			{
				string trimmed = drive.Trim().TrimEnd('\\', '/');
				if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
				{
					string devTemp = Path.Combine(trimmed + "\\", "SpankyDevCache",
						"SpankyhoOptimizer", "Temp");
					if (Below(full, devTemp)) return true;
				}
			}
			return false;
		}
		catch { return false; }
	}

	// Test-release extraction must never purge a caller-owned existing TEMP
	// folder. Unlike production recovery, the fixture gets a fresh unique root.
	internal static bool IsFreshIsolatedReleaseBundleDestination(string candidate) =>
		IsIsolatedTestDestination(candidate) &&
		!Directory.Exists(candidate) && !File.Exists(candidate);

	[STAThread]
	private static int Main(string[] args)
	{
		try
		{
			for (int i = 0; i + 1 < args.Length; i++)
			{
				if (string.Equals(args[i], "--update-from", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var result))
				{
					UpdateFromPid = result;
				}
			}
			// Enforce before any fixture extraction, recursive deletion or installation.
			if (args.Length > 1 && (string.Equals(args[0], "--test-release-bundle", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(args[0], "--test-transactional-install", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(args[0], "--test-install", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(args[0], "--test-recovery", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(args[0], "--test-settings-preservation", StringComparison.OrdinalIgnoreCase)) &&
				!((string.Equals(args[0], "--test-release-bundle", StringComparison.OrdinalIgnoreCase) ||
				   string.Equals(args[0], "--test-recovery", StringComparison.OrdinalIgnoreCase))
					? IsFreshIsolatedReleaseBundleDestination(args[1])
					: IsIsolatedTestDestination(args[1])))
				return 32; // Refuse non-TEMP or already-owned test roots without touching them.
			if (args.Length != 0 && string.Equals(args[0], "--repair-registration", StringComparison.OrdinalIgnoreCase))
			{
				string target = args.Length > 1 ? Path.GetFullPath(args[1]) : DefaultDirectory;
				return RepairRegistration(target) ? 0 : 41;
			}
			if (args.Length != 0 && string.Equals(args[0], "--silent-install", StringComparison.OrdinalIgnoreCase))
			{
				string target = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
					? Path.GetFullPath(args[1])
					: DefaultDirectory;
				bool desktop = !args.Any(x => string.Equals(
					x, "--no-desktop-shortcut", StringComparison.OrdinalIgnoreCase));
				EnsureRuntimePrerequisitesAsync().GetAwaiter().GetResult();
				InstallPayloadTransactional(target, desktop, null, finishInstall: true);
				return ProvisionHardwareBridge(target) ? 0 : 42;
			}
			if (args.Length != 0 && string.Equals(args[0], "--verify-embedded-payload-sha256", StringComparison.OrdinalIgnoreCase))
			{
				if (args.Length != 2) return 45;
				using Stream embedded = OpenResource(PayloadArchive.ResourceName);
				return MatchesEmbeddedPayloadSha256(embedded, args[1]) ? 0 : 45;
			}
			if (args.Length != 0 && string.Equals(args[0], "--smoketest", StringComparison.OrdinalIgnoreCase))
			{
				using (Stream stream = OpenResource("payload.tar.zst"))
				{
					return (stream.Length <= 1048576) ? 10 : 0;
				}
			}
			if (args.Length != 0 && string.Equals(args[0], "--ui-smoketest", StringComparison.OrdinalIgnoreCase))
			{
				FrameworkElement ui = LoadUi();
				string[] required = { "DragArea", "CloseButton", "MinimizeButton", "ProgressPercent", "InstallPath", "DesktopShortcut", "LaunchAfter", "InstallProgress", "StatusText", "PhaseText", "InstallButton", "BrowseButton", "DotNetCard", "WinAppCard", "DotNetDot", "WinAppDot", "DotNetState", "WinAppState", "StepInstall", "StepComponents", "StepDone", "StepInstallText", "StepComponentsText", "StepDoneText", "LiveLog", "FinalActions", "RunButton", "OpenFolderButton", "FinishCloseButton", "FooterHint", "InstallerContent", "InstallHeading", "InstallDescription", "InstallBadge", "ErrorOverlay", "ErrorHeading", "ErrorSummary", "ErrorGuidance", "ErrorDetails", "ErrorClose", "ErrorDismiss", "ErrorLog", "ErrorRetry" };
				foreach (string name in required)
				{
					if (ui.FindName(name) == null) return 11;
				}
				return 0;
			}
			string? version;
			if (args.Length != 0 && string.Equals(args[0], "--release-installer-smoke-bundle", StringComparison.OrdinalIgnoreCase))
			{
				// Validate test sandbox isolation on every strict release.
				if (!IsIsolatedTestDestination(Path.Combine(Path.GetTempPath(), "SpankyhoOptimizer-ReleaseGate-smoke", "fixture")) ||
					(Environment.GetEnvironmentVariable("SPANKY_DEV_DRIVE", EnvironmentVariableTarget.User) is string smokeDrive &&
					 smokeDrive.Trim().TrimEnd('\\', '/').Length == 2 &&
					 !IsIsolatedTestDestination(Path.Combine(smokeDrive.Trim().TrimEnd('\\', '/') + "\\", "SpankyDevCache", "SpankyhoOptimizer", "Temp", "SpankyhoOptimizer-ReleaseGate-smoke", "fixture"))) ||
					IsIsolatedTestDestination(Path.GetTempPath()) ||
					IsIsolatedTestDestination(DefaultDirectory) ||
					IsIsolatedTestDestination(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
					return 32;
				using (Stream stream = OpenResource("payload.tar.zst"))
				{
					if (stream.Length <= 1048576) return 10;
					// When the release passes its manifest digest, verify the
					// actual SINGLE-FILE installer resource before accepting this EXE.
					if (args.Length > 2 ||
						(args.Length == 2 && !MatchesEmbeddedPayloadSha256(stream, args[1])))
						return 45;
				}
				FrameworkElement ui = LoadUi();
				string[] required = { "DragArea", "CloseButton", "MinimizeButton", "ProgressPercent", "InstallPath", "DesktopShortcut", "LaunchAfter", "InstallProgress", "StatusText", "PhaseText", "InstallButton", "BrowseButton", "DotNetCard", "WinAppCard", "DotNetDot", "WinAppDot", "DotNetState", "WinAppState", "StepInstall", "StepComponents", "StepDone", "StepInstallText", "StepComponentsText", "StepDoneText", "LiveLog", "FinalActions", "RunButton", "OpenFolderButton", "FinishCloseButton", "FooterHint", "InstallerContent", "InstallHeading", "InstallDescription", "InstallBadge", "ErrorOverlay", "ErrorHeading", "ErrorSummary", "ErrorGuidance", "ErrorDetails", "ErrorClose", "ErrorDismiss", "ErrorLog", "ErrorRetry" };
				foreach (string name in required)
				{
					if (ui.FindName(name) == null) return 11;
				}
				Application smokeApp = new Application();
				InstallerWindow smokeWindow = new InstallerWindow();
				smokeWindow.Close();
				return 0;
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-release-bundle", StringComparison.OrdinalIgnoreCase))
			{
				string root = Path.GetFullPath(args[1]);
				// Freshness was checked before any fixture file operations. Never
				// delete a directory we did not create ourselves.
				Directory.CreateDirectory(root);

				string extract = Path.Combine(root, "extract");
				ExtractPayload(extract, null);
				if (!File.Exists(Path.Combine(extract, "SpankyhoOptimizer.exe"))) return 21;

				string upgrade = Path.Combine(root, "upgrade");
				Directory.CreateDirectory(upgrade);
				File.WriteAllText(Path.Combine(upgrade, "old-version.marker"), "old");
				InstallPayloadTransactional(upgrade, desktop: false, null, finishInstall: false);
				if (!File.Exists(Path.Combine(upgrade, "SpankyhoOptimizer.exe")) ||
				    File.Exists(Path.Combine(upgrade, "old-version.marker")))
					return 22;
				string upgradeParent = Path.GetDirectoryName(upgrade) ?? string.Empty;
				if (Directory.Exists(upgradeParent) && Directory.GetDirectories(upgradeParent, ".SpankyhoOptimizer-*").Length != 0)
					return 22;

				string preserve = Path.Combine(root, "settings-preservation");
				string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer");
				string uiState = Path.Combine(dataRoot, "ui-state.json");
				string featureProfile = Path.Combine(dataRoot, "user-feature-profile.json");
				string hardwareCache = Path.Combine(dataRoot, "HardwareCache");
				string uiHashBefore = HashPath(uiState);
				string profileHashBefore = HashPath(featureProfile);
				string hardwareHashBefore = HashPath(hardwareCache);
				string? themeBefore;
				using (RegistryKey? key = Registry.CurrentUser.OpenSubKey("Software\\SpankyhoOptimizer\\UI"))
					themeBefore = key?.GetValue("Theme")?.ToString();
				Directory.CreateDirectory(preserve);
				File.WriteAllText(Path.Combine(preserve, "old-version.marker"), "old");
				InstallPayloadTransactional(preserve, desktop: false, null, finishInstall: false);
				string? themeAfter;
				using (RegistryKey? key = Registry.CurrentUser.OpenSubKey("Software\\SpankyhoOptimizer\\UI"))
					themeAfter = key?.GetValue("Theme")?.ToString();
				if (uiHashBefore != HashPath(uiState) ||
				    profileHashBefore != HashPath(featureProfile) ||
				    hardwareHashBefore != HashPath(hardwareCache) ||
				    !string.Equals(themeBefore, themeAfter, StringComparison.Ordinal))
					return 24;

				string recovery = Path.Combine(root, "recovery");
				string recoveryParent = Path.GetDirectoryName(recovery) ?? string.Empty;
				Directory.CreateDirectory(recovery);
				File.WriteAllText(Path.Combine(recovery, "broken.marker"), "broken");
				string staging = Path.Combine(recoveryParent, ".SpankyhoOptimizer-staging-smoke");
				string backup = BackupDirectoryForTarget(recovery, "smoke");
				Directory.CreateDirectory(staging);
				Directory.CreateDirectory(backup);
				File.WriteAllText(Path.Combine(staging, "stale.tmp"), "stale");
				// Use the extracted, versioned PE rather than a text-file fixture:
				// production recovery correctly rejects malformed EXE payloads.
				File.Copy(Path.Combine(extract, "SpankyhoOptimizer.exe"), Path.Combine(backup, "SpankyhoOptimizer.exe"));
				File.WriteAllText(Path.Combine(backup, "backup.marker"), "ok");
				RecoverInterruptedInstall(recovery, allowSyntheticTestExecutables: true);
				// This fixture owns only the explicit staging directory it created;
				// recovery must never delete matching siblings from another installer.
				Directory.Delete(staging, recursive: true);
				bool recovered = File.Exists(Path.Combine(recovery, "backup.marker")) &&
				                 File.Exists(Path.Combine(recovery, "SpankyhoOptimizer.exe"));
				bool cleaned = !Directory.Exists(staging) && !Directory.Exists(backup) &&
				               !File.Exists(Path.Combine(recovery, "broken.marker"));
				// An invalid former target is deliberately preserved for inspection.
				// A passing recovery test must prove those original files survived.
				bool displacedPreserved = GetDisplacedInstallations(recovery)
					.Any(dir => File.Exists(Path.Combine(dir, "broken.marker")));
				return (recovered && cleaned && displacedPreserved) ? 0 : 23;
			}
			if (args.Length != 0 && string.Equals(args[0], "--window-smoketest", StringComparison.OrdinalIgnoreCase))
			{
				Application smokeApp = new Application();
				InstallerWindow smokeWindow = new InstallerWindow();
				smokeWindow.Close();
				return 0;
			}
			if (args.Length != 0 && string.Equals(args[0], "--detect-dotnet", StringComparison.OrdinalIgnoreCase))
			{
				return (!HasDotNet10(out version)) ? 30 : 0;
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-backup-retention-policy", StringComparison.OrdinalIgnoreCase))
			{
				string fixture = Path.Combine(Path.GetTempPath(), "SpankyBackupRetention-" + Guid.NewGuid().ToString("N"));
				try
				{
					return ShouldRetainCommittedBackup(true) && !ShouldRetainCommittedBackup(false) &&
					       VerifyBackupRetentionOnDisk(fixture) ? 0 : 24;
				}
				finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true); }
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-transactional-install", StringComparison.OrdinalIgnoreCase))
			{
				string fullPath = Path.GetFullPath(args[1]);
				Directory.CreateDirectory(fullPath);
				File.WriteAllText(Path.Combine(fullPath, "old-version.marker"), "old");
				InstallPayloadTransactional(fullPath, desktop: false, null, finishInstall: false);
				bool flag = File.Exists(Path.Combine(fullPath, "SpankyhoOptimizer.exe"));
				bool flag2 = !File.Exists(Path.Combine(fullPath, "old-version.marker"));
				string path = Path.GetDirectoryName(fullPath) ?? string.Empty;
				bool flag3 = Directory.Exists(path) && Directory.GetDirectories(path, ".SpankyhoOptimizer-*").Length != 0;
				return (!(flag & flag2) || flag3) ? 22 : 0;
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-fan-backup-health", StringComparison.OrdinalIgnoreCase))
			{
				DateTime now = DateTime.UtcNow;
				int[] rpm = { 0, 1550, 1520, 0 };
				bool Check(string version, bool hid, DateTime stamp, int[] channels) =>
					IsFanHealthEligibleForBackupCleanup("15.4.1474", version, true, hid, stamp, now, now, channels);
				return Check("15.4.1474", true, now, rpm) &&
				       !Check("15.4.1473", true, now, rpm) &&
				       !Check("15.4.1474", false, now, rpm) &&
				       !Check("15.4.1474", true, now.AddSeconds(-30), rpm) &&
				       !Check("15.4.1474", true, now.AddSeconds(3), rpm) &&
				       !Check("15.4.1474", true, now, new[] { 0, 0, 0, 0 }) &&
				       !IsFanHealthEligibleForBackupCleanup("15.4.1474", "15.4.1474", true, true, now, now.AddSeconds(-31), now, rpm) &&
				       !IsFanHealthEligibleForBackupCleanup("15.4.1474", "15.4.1474", true, true, now, now, default, rpm) ? 0 : 27;
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-backup-prune-selection", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyBackupPrune-" + Guid.NewGuid().ToString("N"));
				try
				{
					Directory.CreateDirectory(sandbox);
					string target = Path.Combine(sandbox, "installed");
					for (int index = 0; index < 3; index++)
					{
						string backup = BackupDirectoryForTarget(target, "test" + index);
						Directory.CreateDirectory(backup);
						File.WriteAllText(Path.Combine(backup, "SpankyhoOptimizer.exe"), "old" + index);
						Directory.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddHours(index - 4));
					}
					return GetPrunableBackups(target, false).Length == 0 &&
					       GetPrunableBackups(target, true).Length == 2 &&
					       GetPrunableBackups(target, true, 3).Length == 0 &&
					       GetPrunableBackups(target, true, 0).Length == 0 ? 0 : 26;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-backup-sha256", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyHashGate-" + Guid.NewGuid().ToString("N"));
				try
				{
					Directory.CreateDirectory(sandbox);
					string sample = Path.Combine(sandbox, "sample.exe");
					File.WriteAllText(sample, "original");
					string trusted = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sample)));
					bool valid = MatchesTrustedBackupHash(sample, trusted);
					File.AppendAllText(sample, "tampered");
					return valid && !MatchesTrustedBackupHash(sample, trusted) &&
					       !MatchesTrustedBackupHash(sample, "invalid") &&
					       !MatchesTrustedBackupHash(sample, "") &&
					       !MatchesTrustedBackupHash(sample, new string('Z', 64)) ? 0 : 38;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-displaced-inventory", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyDisplacedInventory-" + Guid.NewGuid().ToString("N"));
				try
				{
					Directory.CreateDirectory(sandbox);
					string target = Path.Combine(sandbox, "installed");
					Directory.CreateDirectory(target + ".recovery-displaced-first");
					Directory.CreateDirectory(target + ".recovery-displaced-second");
					Directory.CreateDirectory(Path.Combine(sandbox, "unrelated.recovery-displaced-first"));
					string[] found = GetDisplacedInstallations(target);
					return found.Length == 2 && found.All(Directory.Exists) && Directory.Exists(target + ".recovery-displaced-first") ? 0 : 37;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-recovery-crash-invalid", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyRecoveryInvalidCrash-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(sandbox, "installed");
					string displaced = target + ".recovery-displaced-invalid";
					Directory.CreateDirectory(displaced);
					File.WriteAllText(Path.Combine(displaced, "SpankyhoOptimizer.exe"), "invalid");
					RecoverInterruptedInstall(target);
					return !Directory.Exists(target) && File.Exists(Path.Combine(displaced, "SpankyhoOptimizer.exe")) ? 0 : 36;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-recovery-crash-displaced", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyRecoveryCrash-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(sandbox, "installed");
					string displaced = target + ".recovery-displaced-test";
					Directory.CreateDirectory(displaced);
					File.Copy(args[1], Path.Combine(displaced, "SpankyhoOptimizer.exe"));
					RecoverInterruptedInstall(target);
					return File.Exists(Path.Combine(target, "SpankyhoOptimizer.exe")) && !Directory.Exists(displaced) ? 0 : 35;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-recovery-priority-real", StringComparison.OrdinalIgnoreCase))
			{
				string root = Path.Combine(Path.GetTempPath(), "SpankyRecoveryPriority-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(root, "installed");
					string valid = target + ".recovery-displaced-valid";
					string corrupt = target + ".recovery-displaced-corrupt";
					Directory.CreateDirectory(valid); Directory.CreateDirectory(corrupt);
					File.Copy(args[1], Path.Combine(valid, "SpankyhoOptimizer.exe"));
					File.WriteAllText(Path.Combine(valid, "valid.marker"), "preserved");
					File.WriteAllText(Path.Combine(corrupt, "SpankyhoOptimizer.exe"), "corrupt");
					Directory.SetLastWriteTimeUtc(valid, DateTime.UtcNow.AddMinutes(-10));
					Directory.SetLastWriteTimeUtc(corrupt, DateTime.UtcNow);
					RecoverInterruptedInstall(target);
					return File.Exists(Path.Combine(target, "valid.marker")) && Directory.Exists(corrupt) &&
					       !Directory.Exists(valid) ? 0 : 47;
				}
				finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-recovery-valid-target", StringComparison.OrdinalIgnoreCase))
			{
				string root = Path.Combine(Path.GetTempPath(), "SpankyRecoveryStable-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(root, "installed");
					string backup = Path.Combine(root, ".SpankyhoOptimizer-backup-older");
					Directory.CreateDirectory(target); Directory.CreateDirectory(backup);
					File.Copy(args[1], Path.Combine(target, "SpankyhoOptimizer.exe"));
					File.Copy(args[1], Path.Combine(backup, "SpankyhoOptimizer.exe"));
					File.WriteAllText(Path.Combine(target, "current.marker"), "current");
					RecoverInterruptedInstall(target);
					return File.Exists(Path.Combine(target, "current.marker")) && Directory.Exists(backup) ? 0 : 48;
				}
				finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-transaction-first-install-failure", StringComparison.OrdinalIgnoreCase))
			{
				string root = Path.Combine(Path.GetTempPath(), "SpankyFirstInstallFail-" + Guid.NewGuid().ToString("N"));
				try
				{
					Directory.CreateDirectory(root);
					string target = Path.Combine(root, "installed");
					try { InstallPayloadTransactional(target, false, null, finishInstall: false, simulatePostMoveFailure: true); return 44; }
					catch (IOException)
					{
						bool saved = Directory.GetDirectories(root, "installed.failed-first-install-*")
							.Any(dir => File.Exists(Path.Combine(dir, "SpankyhoOptimizer.exe")));
						bool excludedFromRecovery = GetDisplacedInstallations(target).Length == 0;
						RecoverInterruptedInstall(target);
						if (Directory.Exists(target) || !saved || !excludedFromRecovery) return 45;
						InstallPayloadTransactional(target, false, null, finishInstall: false);
						return File.Exists(Path.Combine(target, "SpankyhoOptimizer.exe")) &&
						       Directory.GetDirectories(root, "installed.failed-first-install-*").Length == 1 ? 0 : 46;
					}
				}
				finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-transaction-post-move-rollback", StringComparison.OrdinalIgnoreCase))
			{
				string root = Path.Combine(Path.GetTempPath(), "SpankyPostMove-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(root, "installed");
					Directory.CreateDirectory(target);
					File.WriteAllText(Path.Combine(target, "original.marker"), "previous version");
					try { InstallPayloadTransactional(target, false, null, finishInstall: false, simulatePostMoveFailure: true); return 42; }
					catch (IOException)
					{
						return File.Exists(Path.Combine(target, "original.marker")) &&
						       GetDisplacedInstallations(target).Any(dir => File.Exists(Path.Combine(dir, "SpankyhoOptimizer.exe"))) ? 0 : 43;
					}
				}
				finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
			}
			if (args.Length == 2 && string.Equals(args[0], "--test-transaction-scoped-post-move-rollback", StringComparison.OrdinalIgnoreCase))
			{
				// Fully isolated upgrade, simulated failure, rollback and retry:
				// a side-by-side installation's backup must remain untouched.
				string previousExe = Path.GetFullPath(args[1]);
				if (!IsSupportedRecoveryVersion(previousExe, typeof(PayloadArchive).Assembly.GetName().Version))
					return 64;
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyScopedRollback-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(sandbox, "primary");
					string otherTarget = Path.Combine(sandbox, "secondary");
					string foreignBackup = BackupDirectoryForTarget(otherTarget, "untouched");
					string legacyBackup = Path.Combine(sandbox, ".SpankyhoOptimizer-backup-legacy");
					Directory.CreateDirectory(target);
					Directory.CreateDirectory(foreignBackup);
					Directory.CreateDirectory(legacyBackup);
					File.Copy(previousExe, Path.Combine(target, "SpankyhoOptimizer.exe"));
					File.WriteAllText(Path.Combine(target, "original.marker"), "OLD_PAYLOAD");
					File.Copy(previousExe, Path.Combine(foreignBackup, "SpankyhoOptimizer.exe"));
					File.WriteAllText(Path.Combine(foreignBackup, "foreign.marker"), "FOREIGN_BACKUP");
					File.Copy(previousExe, Path.Combine(legacyBackup, "SpankyhoOptimizer.exe"));
					File.WriteAllText(Path.Combine(legacyBackup, "legacy.marker"), "LEGACY_BACKUP");
					try
					{
						InstallPayloadTransactional(target, false, null, finishInstall: false, simulatePostMoveFailure: true);
						return 65; // Expected synthetic failure was not triggered.
					}
					catch (IOException) { }
					bool oldRestored = File.Exists(Path.Combine(target, "original.marker")) &&
						IsSupportedRecoveryVersion(Path.Combine(target, "SpankyhoOptimizer.exe"),
							typeof(PayloadArchive).Assembly.GetName().Version);
					bool unrelatedPreserved = File.ReadAllText(Path.Combine(foreignBackup, "foreign.marker")) == "FOREIGN_BACKUP" &&
						File.ReadAllText(Path.Combine(legacyBackup, "legacy.marker")) == "LEGACY_BACKUP";
					bool failedCandidateRetained = GetDisplacedInstallations(target)
						.Any(dir => File.Exists(Path.Combine(dir, "SpankyhoOptimizer.exe")));
					if (!oldRestored || !unrelatedPreserved || !failedCandidateRetained ||
						GetOwnedRecoveryBackups(target).Length != 0 ||
						Directory.GetDirectories(sandbox, ".SpankyhoOptimizer-staging-*").Length != 0)
						return 66;
					InstallPayloadTransactional(target, false, null, finishInstall: false);
					return !File.Exists(Path.Combine(target, "original.marker")) &&
						IsSupportedRecoveryVersion(Path.Combine(target, "SpankyhoOptimizer.exe"),
							typeof(PayloadArchive).Assembly.GetName().Version) &&
						File.Exists(Path.Combine(foreignBackup, "foreign.marker")) &&
						File.Exists(Path.Combine(legacyBackup, "legacy.marker")) &&
						GetOwnedRecoveryBackups(target).Length == 0 &&
						Directory.GetDirectories(sandbox, ".SpankyhoOptimizer-staging-*").Length == 0 ? 0 : 67;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-recovery-staging-isolation", StringComparison.OrdinalIgnoreCase))
			{
				string root = Path.Combine(Path.GetTempPath(), "SpankyStagingIsolation-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(root, "installed");
					string unrelated = Path.Combine(root, ".SpankyhoOptimizer-staging-other-installer");
					Directory.CreateDirectory(unrelated);
					File.WriteAllText(Path.Combine(unrelated, "owned.marker"), "untouched");
					RecoverInterruptedInstall(target);
					return File.Exists(Path.Combine(unrelated, "owned.marker")) ? 0 : 41;
				}
				finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-recovery-double-failure", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyDoubleFailure-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(sandbox, "installed"), backup = Path.Combine(sandbox, "backup");
					Directory.CreateDirectory(target); Directory.CreateDirectory(backup);
					File.WriteAllText(Path.Combine(target, "old.marker"), "old");
					int calls = 0;
					try { RestoreBackupWithRollback(target, backup, (src, dst) => {
						if (++calls > 1) throw new IOException("simulated inaccessible destination");
						Directory.Move(src, dst);
					}); return 39; }
					catch (AggregateException ex)
					{
						return ex.InnerExceptions.Count == 2 && calls == 3 && !Directory.Exists(target) &&
						       Directory.Exists(backup) && GetDisplacedInstallations(target).Any(dir => File.Exists(Path.Combine(dir, "old.marker"))) ? 0 : 40;
					}
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-recovery-move-failure", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyRecoveryFailure-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(sandbox, "installed");
					string backup = Path.Combine(sandbox, "backup");
					Directory.CreateDirectory(target);
					Directory.CreateDirectory(backup);
					File.WriteAllText(Path.Combine(target, "old.marker"), "old");
					File.WriteAllText(Path.Combine(backup, "backup.marker"), "backup");
					int moves = 0;
					try { RestoreBackupWithRollback(target, backup, (from, to) => {
						if (++moves == 2) throw new IOException("simulated backup move failure");
						Directory.Move(from, to);
					}); return 33; }
					catch (IOException)
					{
						return moves == 3 && File.Exists(Path.Combine(target, "old.marker")) &&
						       File.Exists(Path.Combine(backup, "backup.marker")) ? 0 : 34;
					}
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-recovery-no-valid-backup", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyRecoveryNoValid-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(sandbox, "installed");
					string invalidBackup = Path.Combine(sandbox, ".SpankyhoOptimizer-backup-corrupt");
					Directory.CreateDirectory(target);
					Directory.CreateDirectory(invalidBackup);
					File.WriteAllText(Path.Combine(target, "SpankyhoOptimizer.exe"), "corrupt-current");
					File.WriteAllText(Path.Combine(invalidBackup, "SpankyhoOptimizer.exe"), "corrupt-backup");
					RecoverInterruptedInstall(target);
					return File.ReadAllText(Path.Combine(target, "SpankyhoOptimizer.exe")) == "corrupt-current" &&
					       File.Exists(Path.Combine(invalidBackup, "SpankyhoOptimizer.exe")) ? 0 : 32;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-recovery-real-version", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyRecoveryReal-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(sandbox, "installed");
					string backup = BackupDirectoryForTarget(target, "previous");
					Directory.CreateDirectory(backup);
					File.Copy(args[1], Path.Combine(backup, "SpankyhoOptimizer.exe"));
					Directory.CreateDirectory(target);
					File.WriteAllText(Path.Combine(target, "SpankyhoOptimizer.exe"), "corrupt current");
					RecoverInterruptedInstall(target);
					return IsSupportedRecoveryVersion(Path.Combine(target, "SpankyhoOptimizer.exe"),
					           typeof(PayloadArchive).Assembly.GetName().Version) && !Directory.Exists(backup) ? 0 : 31;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-recovery-version", StringComparison.OrdinalIgnoreCase))
			{
				string executable = args[1];
				return IsSupportedRecoveryVersion(executable, new System.Version(15, 4, 1474)) &&
				       !IsSupportedRecoveryVersion(executable, new System.Version(15, 4, 1472)) &&
				       !IsSupportedRecoveryVersion(executable, new System.Version(16, 0, 0)) ? 0 : 29;
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-recovery-valid-backup", StringComparison.OrdinalIgnoreCase))
			{
				string sandbox = Path.Combine(Path.GetTempPath(), "SpankyValidBackup-" + Guid.NewGuid().ToString("N"));
				try
				{
					string target = Path.Combine(sandbox, "installed");
					string good = BackupDirectoryForTarget(target, "good");
					string bad = BackupDirectoryForTarget(target, "bad");
					Directory.CreateDirectory(good);
					Directory.CreateDirectory(bad);
					File.WriteAllText(Path.Combine(bad, "SpankyhoOptimizer.exe"), "corrupt header");
					WriteTestExecutableHeader(Path.Combine(good, "SpankyhoOptimizer.exe"));
					Directory.SetLastWriteTimeUtc(good, DateTime.UtcNow.AddMinutes(-10));
					Directory.SetLastWriteTimeUtc(bad, DateTime.UtcNow);
					RecoverInterruptedInstall(target, allowSyntheticTestExecutables: true);
					return HasValidExecutableHeader(Path.Combine(target, "SpankyhoOptimizer.exe")) && Directory.Exists(bad) ? 0 : 28;
				}
				finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
			}
			if (args.Length > 0 && string.Equals(args[0], "--test-recovery-retain", StringComparison.OrdinalIgnoreCase))
			{
				string testRoot = Path.Combine(Path.GetTempPath(), "SpankyRecoveryRetention-" + Guid.NewGuid().ToString("N"));
				try
				{
					string installed = Path.Combine(testRoot, "installed");
					string backup = Path.Combine(testRoot, ".SpankyhoOptimizer-backup-fixture");
					Directory.CreateDirectory(installed);
					Directory.CreateDirectory(backup);
					File.WriteAllText(Path.Combine(installed, "SpankyhoOptimizer.exe"), "current");
					File.WriteAllText(Path.Combine(backup, "SpankyhoOptimizer.exe"), "previous");
					RecoverInterruptedInstall(installed);
					return File.Exists(Path.Combine(backup, "SpankyhoOptimizer.exe")) &&
					       File.Exists(Path.Combine(installed, "SpankyhoOptimizer.exe")) ? 0 : 25;
				}
				finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true); }
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-recovery", StringComparison.OrdinalIgnoreCase))
			{
				string fullPath2 = Path.GetFullPath(args[1]);
				string? path2 = Path.GetDirectoryName(fullPath2) ?? string.Empty;
				string text = Path.Combine(path2, ".SpankyhoOptimizer-staging-smoke");
				string text2 = BackupDirectoryForTarget(fullPath2, "smoke");
				// Do not create even a marker in a fresh target when a neighbouring
				// installer owns either synthetic staging/backup directory.
				if (Directory.Exists(text) || Directory.Exists(text2) ||
					File.Exists(text) || File.Exists(text2)) return 32;
				Directory.CreateDirectory(fullPath2);
				File.WriteAllText(Path.Combine(fullPath2, "broken.marker"), "broken");
				Directory.CreateDirectory(text);
				Directory.CreateDirectory(text2);
				File.WriteAllText(Path.Combine(text, "stale.tmp"), "stale");
				WriteTestExecutableHeader(Path.Combine(text2, "SpankyhoOptimizer.exe"));
				File.WriteAllText(Path.Combine(text2, "backup.marker"), "ok");
				RecoverInterruptedInstall(fullPath2, allowSyntheticTestExecutables: true);
				Directory.Delete(text, recursive: true); // Only the fixture's own staging.
				bool num = File.Exists(Path.Combine(fullPath2, "backup.marker")) && File.Exists(Path.Combine(fullPath2, "SpankyhoOptimizer.exe"));
				bool flag4 = !Directory.Exists(text) && !Directory.Exists(text2) && !File.Exists(Path.Combine(fullPath2, "broken.marker"));
				return (!(num & flag4)) ? 23 : 0;
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-install", StringComparison.OrdinalIgnoreCase))
			{
				string fullPath3 = Path.GetFullPath(args[1]);
				if (Directory.Exists(fullPath3))
				{
					return 20;
				}
				ExtractPayload(fullPath3, null);
				return (!File.Exists(Path.Combine(fullPath3, "SpankyhoOptimizer.exe"))) ? 21 : 0;
			}
			if (args.Length > 1 && string.Equals(args[0], "--test-settings-preservation", StringComparison.OrdinalIgnoreCase))
			{
				string fullPath4 = Path.GetFullPath(args[1]);
				string path3 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer");
				string path4 = Path.Combine(path3, "ui-state.json");
				string path5 = Path.Combine(path3, "user-feature-profile.json");
				string path6 = Path.Combine(path3, "HardwareCache");
				string text3 = HashPath(path4);
				string text4 = HashPath(path5);
				string text5 = HashPath(path6);
				string? a;
				using (RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey("Software\\SpankyhoOptimizer\\UI"))
				{
					a = registryKey?.GetValue("Theme")?.ToString();
				}
				Directory.CreateDirectory(fullPath4);
				File.WriteAllText(Path.Combine(fullPath4, "old-version.marker"), "old");
				InstallPayloadTransactional(fullPath4, desktop: false, null, finishInstall: false);
				string text6 = HashPath(path4);
				string text7 = HashPath(path5);
				string text8 = HashPath(path6);
				string? b;
				using (RegistryKey? registryKey2 = Registry.CurrentUser.OpenSubKey("Software\\SpankyhoOptimizer\\UI"))
				{
					b = registryKey2?.GetValue("Theme")?.ToString();
				}
				return (!(text3 == text6) || !(text4 == text7) || !(text5 == text8) || !string.Equals(a, b, StringComparison.Ordinal)) ? 24 : 0;
			}
			Application application = new Application();
			application.ShutdownMode = ShutdownMode.OnMainWindowClose;
			application.Run(new InstallerWindow());
			return Environment.ExitCode;
		}
		catch (Exception ex)
		{
			Log("FATAL " + ex);
			bool silentInstall = args.Length != 0 && string.Equals(
				args[0], "--silent-install", StringComparison.OrdinalIgnoreCase);
			if (!silentInstall)
				MessageBox.Show(ex.Message, "Glass Optimizer Setup", MessageBoxButton.OK, MessageBoxImage.Hand);
			return 1;
		}
	}

	private static string HashPath(string path)
	{
		if (!File.Exists(path) && !Directory.Exists(path))
		{
			return "<missing>";
		}
		using SHA256 sHA = SHA256.Create();
		if (File.Exists(path))
		{
			return Convert.ToHexString(sHA.ComputeHash(File.ReadAllBytes(path)));
		}
		StringBuilder stringBuilder = new StringBuilder();
		foreach (string item in Directory.GetFiles(path, "*", SearchOption.AllDirectories).OrderBy<string, string>((string x) => x, StringComparer.OrdinalIgnoreCase))
		{
			stringBuilder.Append(item.Substring(path.Length)).Append('|');
			stringBuilder.Append(Convert.ToHexString(sHA.ComputeHash(File.ReadAllBytes(item)))).Append('\n');
		}
		return Convert.ToHexString(sHA.ComputeHash(Encoding.UTF8.GetBytes(stringBuilder.ToString())));
	}

	internal static bool MatchesEmbeddedPayloadSha256(Stream embedded, string? expectedSha256)
	{
		if (embedded is null || !embedded.CanRead || expectedSha256 is null ||
			expectedSha256.Length != 64) return false;
		try
		{
			byte[] expected = Convert.FromHexString(expectedSha256);
			byte[] actual = SHA256.HashData(embedded);
			return CryptographicOperations.FixedTimeEquals(actual, expected);
		}
		catch (FormatException) { return false; }
	}

	internal static Stream OpenResource(string name)
	{
		return Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("Instalační data nebyla nalezena: " + name);
	}

	internal static FrameworkElement LoadUi()
	{
		using Stream input = OpenResource("installer-ui.xaml");
		var settings = new XmlReaderSettings
		{
			IgnoreComments = true,
			IgnoreWhitespace = true,
			DtdProcessing = DtdProcessing.Prohibit,
			CloseInput = false
		};
		using XmlReader reader = XmlReader.Create(input, settings);
		return (FrameworkElement)XamlReader.Load(reader);
	}

	internal static bool HasDotNet10(out string? version) =>
		InstallerPrerequisites.HasDesktopRuntime10(out version);

	// A corrupted server response must never consume unlimited disk space.
	internal const long MaxDependencyDownloadBytes = 250L * 1024 * 1024;
	internal static void ValidateDownloadBounds(long bytes, long? declaredLength)
	{
		if (bytes < 0 || bytes > MaxDependencyDownloadBytes ||
			(declaredLength.HasValue && (declaredLength.Value < 0 || declaredLength.Value > MaxDependencyDownloadBytes || bytes > declaredLength.Value)))
			throw new InvalidDataException("Stažená komponenta překročila bezpečný limit velikosti.");
	}

	internal static void RequireHttpsDownload(Uri? uri)
	{
		if (uri is null || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
			!string.IsNullOrEmpty(uri.UserInfo))
			throw new InvalidDataException("Zdroj komponenty musí používat důvěryhodné HTTPS.");
	}

	internal static async Task DownloadAsync(string url, string destination, Action<int>? progress)
	{
		RequireHttpsDownload(new Uri(url, UriKind.Absolute));
		using HttpClient client = new HttpClient
		{
			Timeout = TimeSpan.FromMinutes(10L)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("SpankyhoOptimizerSetup/" + Version);
		using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
		response.EnsureSuccessStatusCode();
		RequireHttpsDownload(response.RequestMessage?.RequestUri);
		long? total = response.Content.Headers.ContentLength;
		ValidateDownloadBounds(0, total);
		await using Stream input = await response.Content.ReadAsStreamAsync();
		await using FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true);
		byte[] buffer = new byte[131072];
		long written = 0L;
		while (true)
		{
			int num;
			int read = (num = await input.ReadAsync(buffer, 0, buffer.Length));
			if (num <= 0)
			{
				break;
			}
			ValidateDownloadBounds(checked(written + read), total);
			await output.WriteAsync(buffer, 0, read);
			written += read;
			if (total.HasValue && total.Value > 0)
			{
				progress?.Invoke((int)Math.Min(100L, written * 100 / total.Value));
			}
		}
		if (total.HasValue && written != total.Value)
			throw new InvalidDataException("Stažená komponenta je neúplná.");
		progress?.Invoke(100);
	}

	// A Microsoft-signed Evergreen WebView2 bootstrapper can change over time;
	// unlike the pinned .NET Desktop installer we must verify its Authenticode
	// chain and expected publisher before executing downloaded bytes.
	internal static async Task VerifyMicrosoftSignatureAsync(string file)
	{
		string safePath = file.Replace("'", "''", StringComparison.Ordinal);
		string script = "$s=Get-AuthenticodeSignature -LiteralPath '" + safePath +
			"';if($s.Status -eq 'Valid' -and $s.SignerCertificate -and " +
			"$s.SignerCertificate.Subject -match '(^|, )O=Microsoft Corporation(,|$)')" +
			"{Write-Output 'MICROSOFT_SIGNATURE_OK';exit 0};exit 64";
		string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
		using Process process = Process.Start(new ProcessStartInfo("powershell.exe",
			"-NoProfile -NonInteractive -EncodedCommand " + encoded)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		}) ?? throw new InvalidOperationException("Ověření digitálního podpisu nelze spustit.");
		Task<string> stdout = process.StandardOutput.ReadToEndAsync();
		Task<string> stderr = process.StandardError.ReadToEndAsync();
		using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(40));
		try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
		catch (OperationCanceledException) when (timeout.IsCancellationRequested)
		{
			try { process.Kill(entireProcessTree: true); } catch { }
			throw new TimeoutException("Ověření podpisu Microsoftu trvalo příliš dlouho.");
		}
		if (process.ExitCode != 0 ||
			!(await stdout.ConfigureAwait(false)).Contains("MICROSOFT_SIGNATURE_OK", StringComparison.Ordinal))
			throw new InvalidDataException("Stažený instalátor WebView2 nemá platný podpis Microsoft Corporation: " +
				(await stderr.ConfigureAwait(false)));
	}

	internal static async Task EnsureRuntimePrerequisitesAsync(
		Action<int, string>? progress = null, Action<string, string>? onStatus = null)
	{
		if (!InstallerPrerequisites.IsSupportedWindows(
				System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
					System.Runtime.InteropServices.Architecture.X64,
				Environment.OSVersion.Version.Build))
			throw new PlatformNotSupportedException(
				"Glass Optimizer potřebuje 64bit Windows 10 (build 19041+) nebo Windows 11.");

		onStatus?.Invoke("windows", "Windows x64: připraveno");
		bool runtimeRestartRequired = false;
		bool webviewRestartRequired = false;
		if (!HasDotNet10(out var desktopVersion))
		{
			string file = Path.Combine(Path.GetTempPath(), "spanky-desktop10-" +
				Guid.NewGuid().ToString("N") + ".exe");
			try
			{
				onStatus?.Invoke("dotnet", "Stahuji .NET Desktop Runtime 10 x64…");
				await DownloadAsync(DotNetUrl, file,
					p => progress?.Invoke(5 + p / 5, "Stahuji .NET Desktop Runtime 10…"));
				VerifySha512(file, DotNetSha512);
				onStatus?.Invoke("dotnet", "Instaluji .NET Desktop Runtime 10…");
				progress?.Invoke(26, "Instaluji .NET Desktop Runtime 10…");
				int runtimeExit = await RunPackageAsync(file, "/install /quiet /norestart");
				if (runtimeExit == 1641 || runtimeExit == 3010)
				{
					runtimeRestartRequired = true;
					onStatus?.Invoke("dotnet", "Instalátor požaduje restart Windows.");
				}
			}
			finally
			{
				try { File.Delete(file); } catch { }
			}
			if (!HasDotNet10(out desktopVersion))
				throw new InvalidOperationException(
					(runtimeRestartRequired ? "Instalace .NET vyžaduje restart Windows. Poté spusť instalátor znovu." : ".NET Desktop Runtime 10 po instalaci nebyl nalezen. Zkus instalaci znovu."));
		}
		onStatus?.Invoke("dotnet", ".NET Desktop 10: " + desktopVersion);

		if (!InstallerPrerequisites.HasWebView2(out var webviewVersion))
		{
			string file = Path.Combine(Path.GetTempPath(), "spanky-webview2-" +
				Guid.NewGuid().ToString("N") + ".exe");
			try
			{
				onStatus?.Invoke("webview2", "Stahuji Microsoft WebView2…");
				await DownloadAsync(WebView2Url, file,
					p => progress?.Invoke(30 + p / 10, "Stahuji WebView2…"));
				await VerifyMicrosoftSignatureAsync(file);
				onStatus?.Invoke("webview2", "Instaluji Microsoft WebView2…");
				progress?.Invoke(43, "Instaluji Microsoft WebView2…");
				int webviewExit = await RunPackageAsync(file, "/silent /install");
				if (webviewExit == 1641 || webviewExit == 3010)
				{
					webviewRestartRequired = true;
					onStatus?.Invoke("webview2", "Instalátor požaduje restart Windows.");
				}
			}
			finally
			{
				try { File.Delete(file); } catch { }
			}
			if (!InstallerPrerequisites.HasWebView2(out webviewVersion))
				throw new InvalidOperationException(
					(webviewRestartRequired ? "Instalace WebView2 vyžaduje restart Windows. Poté spusť instalátor znovu." : "Microsoft WebView2 nebyl po instalaci nalezen. Zkus instalaci znovu."));
		}
		onStatus?.Invoke("webview2", "WebView2: " + webviewVersion);
		onStatus?.Invoke("bundled", "Windows App SDK: součástí balíčku");
		progress?.Invoke(51, "Všechny požadované systémové komponenty jsou připravené.");
	}

	internal static void VerifySha512(string file, string expected)
	{
		using SHA512 sHA = SHA512.Create();
		using FileStream inputStream = File.OpenRead(file);
		if (!string.Equals(BitConverter.ToString(sHA.ComputeHash(inputStream)).Replace("-", "").ToLowerInvariant(), expected, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Kontrolní součet staženého .NET instalátoru nesouhlasí.");
		}
	}

	internal static bool IsSuccessfulInstallerExitCode(int code) => code == 0 || code == 1641 || code == 3010;

	internal static async Task<int> RunPackageAsync(string file, string arguments)
	{
		using Process? process = Process.Start(new ProcessStartInfo(file, arguments)
		{
			UseShellExecute = false,
			CreateNoWindow = true
		});
		if (process == null)
			throw new InvalidOperationException("Instal?tor z?vislosti se nepoda?ilo spustit.");

		using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10));
		try
		{
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested)
		{
			try { process.Kill(entireProcessTree: true); } catch { }
			throw new TimeoutException("Instalace z?vislosti p?ekro?ila ?asov? limit.");
		}

		if (!IsSuccessfulInstallerExitCode(process.ExitCode))
			throw new InvalidOperationException("Instalace závislosti selhala (kód " + process.ExitCode + ").");
		return process.ExitCode;
	}

	internal static void WaitForUpdateSourceExit()
	{
		if (UpdateFromPid <= 0)
			return;

		try
		{
			using Process process = Process.GetProcessById(UpdateFromPid);
			if (!process.HasExited && !process.WaitForExit(2500))
				Log("Update source still running after 2.5s; continuing to bounded process cleanup.");
		}
		catch
		{
		}
	}

	internal static void StopRunningOptimizer(bool preserveUserRuntimeHosts = false)
	{
		IEnumerable<Process> runtimeHosts = Process.GetProcessesByName("SpankyhoOptimizer.RuntimeHost");
		if (preserveUserRuntimeHosts)
			runtimeHosts = runtimeHosts.Where(IsProgramFilesProcess);

		Process[] processes = Process.GetProcessesByName("SpankyhoOptimizer")
			.Concat(runtimeHosts)
			.GroupBy(p => p.Id)
			.Select(g => g.First())
			.ToArray();
		if (processes.Length == 0)
			return;

		Log("Stopping optimizer processes: " + string.Join(", ", processes.Select(p => p.ProcessName + "#" + p.Id)));

		try
		{
			foreach (Process process in processes)
			{
				try
				{
					if (!process.HasExited)
						process.CloseMainWindow();
				}
				catch
				{
				}
			}

			var grace = Stopwatch.StartNew();
			while (grace.ElapsedMilliseconds < 1800)
			{
				bool anyRunning = false;
				foreach (Process process in processes)
				{
					try
					{
						if (!process.HasExited) { anyRunning = true; break; }
					}
					catch
					{
					}
				}
				if (!anyRunning)
					return;
				System.Threading.Thread.Sleep(50);
			}

			foreach (Process process in processes)
			{
				try
				{
					if (!process.HasExited)
					{
						Log($"Force-stopping stale optimizer PID {process.Id}.");
						process.Kill(entireProcessTree: true);
					}
				}
				catch
				{
				}
			}

			var force = Stopwatch.StartNew();
			while (force.ElapsedMilliseconds < 1200)
			{
				bool anyRunning = false;
				foreach (Process process in processes)
				{
					try
					{
						if (!process.HasExited) { anyRunning = true; break; }
					}
					catch
					{
					}
				}
				if (!anyRunning)
					return;
				System.Threading.Thread.Sleep(40);
			}
		}
		finally
		{
			foreach (Process process in processes)
				process.Dispose();
		}
	}

	private static bool IsProgramFilesProcess(Process process)
	{
		try
		{
			string? executable = process.MainModule?.FileName;
			if (string.IsNullOrWhiteSpace(executable))
				return false;

			string fullPath = Path.GetFullPath(executable);
			string[] roots =
			{
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
			};

			return roots
				.Where(root => !string.IsNullOrWhiteSpace(root))
				.Select(root => Path.GetFullPath(root)
					.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
				.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
		}
		catch
		{
			return false;
		}
	}

	internal static void MoveDirectoryWithRetry(string source, string destination)
	{
		Exception? last = null;
		for (int attempt = 1; attempt <= 20; attempt++)
		{
			try
			{
				Directory.Move(source, destination);
				return;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				last = ex;
				Log($"Directory move retry {attempt}/20: {ex.Message}");
				if (attempt % 4 == 0)
					StopRunningOptimizer(preserveUserRuntimeHosts: true);
				System.Threading.Thread.Sleep(150);
			}
		}
		throw new IOException("Instalační soubory jsou stále používány jiným procesem.", last);
	}

	internal static void ExtractPayload(string target, Action<int, string>? progress)
	{
		using Stream archive = OpenResource(PayloadArchive.ResourceName);
		PayloadArchive.Extract(archive, target, progress);
	}

	internal static void WriteTestExecutableHeader(string path)
	{
		byte[] fixture = new byte[512];
		fixture[0] = 0x4D; fixture[1] = 0x5A;
		fixture[0x3C] = 0x80;
		fixture[0x80] = 0x50; fixture[0x81] = 0x45;
		File.WriteAllBytes(path, fixture);
	}

	internal static bool HasValidExecutableHeader(string path)
	{
		try
		{
			using FileStream stream = File.OpenRead(path);
			if (stream.Length < 512) return false;
			using BinaryReader reader = new(stream);
			if (reader.ReadUInt16() != 0x5A4D) return false;
			stream.Position = 0x3C;
			int peOffset = reader.ReadInt32();
			if (peOffset < 64 || peOffset > stream.Length - 4) return false;
			stream.Position = peOffset;
			return reader.ReadUInt32() == 0x00004550;
		}
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
	}

	// In our 16.x product versioning, Minor is the sequential release
	// (16.139 -> 16.140), not a compatibility boundary. Keep the same Major
	// and reject a newer installed payload, without blocking the predecessor.
	internal static bool IsCompatibleRecoveryVersion(System.Version? prior, System.Version? current) =>
		prior is not null && current is not null && prior.Major == current.Major && prior <= current;

	internal static bool IsSupportedRecoveryVersion(string executablePath, System.Version? currentVersion)
	{
		if (!HasValidExecutableHeader(executablePath)) return false;
		if (currentVersion is null) return false;
		try
		{
			string raw = FileVersionInfo.GetVersionInfo(executablePath).FileVersion ?? string.Empty;
			if (!System.Version.TryParse(raw, out System.Version? prior)) return false;
			return IsCompatibleRecoveryVersion(prior, currentVersion);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
	}

	// The expected SHA-256 must come from an independently trusted manifest.
	// Never treat a hash stored beside the backup as proof of authenticity.
	internal static bool MatchesTrustedBackupHash(string executablePath, string expectedSha256)
	{
		if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64) return false;
		try
		{
			byte[] expected = Convert.FromHexString(expectedSha256);
			using FileStream input = File.OpenRead(executablePath);
			byte[] actual = System.Security.Cryptography.SHA256.HashData(input);
			return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return false; }
	}

	internal static void RestoreBackupWithRollback(string target, string backup, Action<string, string> move)
	{
		string displaced = target + ".recovery-displaced-" + Guid.NewGuid().ToString("N");
		bool oldMoved = false;
		try
		{
			if (Directory.Exists(target)) { move(target, displaced); oldMoved = true; }
			move(backup, target);
		}
		catch (Exception primary)
		{
			if (oldMoved && !Directory.Exists(target) && Directory.Exists(displaced))
			{
				try { move(displaced, target); }
				catch (Exception rollback)
				{
					Log("RECOVERY CRITICAL: rollback failed; displaced preserved at " + displaced);
					throw new AggregateException("Backup restore and original-directory rollback both failed; original retained in displaced directory.", primary, rollback);
				}
			}
			throw;
		}
		// Preserve the displaced directory until it can be safely inspected.
	}

	// Backup names are bound to a single normalized destination. Legacy
	// unbound backups remain on disk for manual recovery but cannot be silently
	// adopted by a different side-by-side installation in the same parent.
	internal static string BackupDirectoryForTarget(string target, string suffix)
	{
		string full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string identity = Convert.ToHexString(SHA256.HashData(
			Encoding.UTF8.GetBytes(full.ToUpperInvariant())).AsSpan(0, 16));
		return Path.Combine(Path.GetDirectoryName(full) ?? throw new InvalidOperationException("Backup destination has no parent."),
			".SpankyhoOptimizer-backup-" + identity + "-" + suffix);
	}

	internal static string[] GetOwnedRecoveryBackups(string target)
	{
		string path = BackupDirectoryForTarget(target, string.Empty);
		string? parent = Path.GetDirectoryName(path);
		return parent is not null && Directory.Exists(parent)
			? Directory.GetDirectories(parent, Path.GetFileName(path) + "*")
			: Array.Empty<string>();
	}

	internal static void RecoverInterruptedInstall(string target, bool allowSyntheticTestExecutables = false)
	{
		string text = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
		string path = Path.GetDirectoryName(text) ?? throw new InvalidOperationException("Neplatná cílová cesta.");
		Directory.CreateDirectory(path);
		Log("RECOVERY begin target=" + text);
		// Even synthetic recovery must NOT delete sibling staging folders.
		// The fixture caller cleans ONLY the exact directory it created. Another
		// installer may be concurrently staging in the same parent.
		foreach (string staging in Directory.GetDirectories(path, ".SpankyhoOptimizer-staging-*"))
			Log("RECOVERY retained unowned staging " + staging);
		System.Version? recoveryVersion = typeof(PayloadArchive).Assembly.GetName().Version;
		// A power loss can happen after displacing target but before moving backup.
		// Restore the displaced original if target is missing; never delete it here.
		if (!Directory.Exists(text))
		{
			string[] displaced = GetDisplacedInstallations(text);
			foreach (string candidate in displaced)
			{
				bool acceptable = allowSyntheticTestExecutables
					? HasValidExecutableHeader(Path.Combine(candidate, "SpankyhoOptimizer.exe"))
					: IsSupportedRecoveryVersion(Path.Combine(candidate, "SpankyhoOptimizer.exe"), recoveryVersion);
				if (!acceptable) continue;
				Directory.Move(candidate, text);
				Log("RECOVERY restored displaced target " + candidate);
				break;
			}
		}
		string[] ownedBackups = GetOwnedRecoveryBackups(text);
		// Legacy backups from earlier versions have no destination binding.
		// Preserve them and unrelated installs' backups for manual inspection;
		// never silently promote either one to the current destination.
		int unboundCount = Directory.GetDirectories(path, ".SpankyhoOptimizer-backup-*")
			.Count(dir => !ownedBackups.Contains(dir, StringComparer.OrdinalIgnoreCase));
		if (unboundCount > 0)
			Log("RECOVERY retained unbound/other-target backups=" + unboundCount);
		string[] array = ownedBackups
			.Where(dir => allowSyntheticTestExecutables
				? HasValidExecutableHeader(Path.Combine(dir, "SpankyhoOptimizer.exe"))
				: IsSupportedRecoveryVersion(Path.Combine(dir, "SpankyhoOptimizer.exe"), recoveryVersion))
			.OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
		bool flag = Directory.Exists(text) && (allowSyntheticTestExecutables
			? File.Exists(Path.Combine(text, "SpankyhoOptimizer.exe"))
			: IsSupportedRecoveryVersion(Path.Combine(text, "SpankyhoOptimizer.exe"), recoveryVersion));
		if (!flag && array.Length != 0)
		{
			RestoreBackupWithRollback(text, array[0], Directory.Move);
			Log("RECOVERY restored backup " + array[0]);
			array = ((array.Length > 1) ? array[1..] : Array.Empty<string>());
			flag = true;
		}
		// A valid target does not prove the new fan agent has taken over.
		// Retain backup directories for manual rollback rather than deleting them.
		Log("RECOVERY retained backups=" + array.Length);
		Log("RECOVERY complete targetValid=" + flag + " backupsRemaining=" + array.Length);
	}

	// Preserve the previous installed payload after a real commit until fan
	// health is independently confirmed. Isolated test upgrades clean staging.
	internal static bool ShouldRetainCommittedBackup(bool finishInstall) => finishInstall;

	// Read-only inventory: displaced installations are NEVER deleted automatically.
	internal static string[] GetDisplacedInstallations(string target)
	{
		string full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
		string? parent = Path.GetDirectoryName(full);
		if (parent is null || !Directory.Exists(parent)) return Array.Empty<string>();
		return Directory.GetDirectories(parent, Path.GetFileName(full) + ".recovery-displaced-*")
			.OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
	}

	// Backup cleanup is opt-in: a completed file transaction is not a health gate.
	// Keep at least the newest valid predecessor, even after external health verification.
	internal static string[] GetPrunableBackups(string target, bool fanHealthVerified, int keepValid = 1)
	{
		if (!fanHealthVerified || keepValid < 1)
			return Array.Empty<string>();
		// Selection must never include other installs' or unbound legacy backups.
		// This is only a read-only inventory, not authorization to delete files.
		string[] valid = GetOwnedRecoveryBackups(target)
			.Where(dir => File.Exists(Path.Combine(dir, "SpankyhoOptimizer.exe")))
			.OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
		return valid.Skip(keepValid).ToArray();
	}

	// A cleanup token is valid only if it belongs to the newly installed build.
	// Version strings must agree; healthy HID/RPM samples must be fresh.
	internal static bool IsFanHealthEligibleForBackupCleanup(
		string expectedVersion, string reportedVersion, bool healthy, bool hidOk,
		DateTime updatedUtc, DateTime lastHidUtc, DateTime nowUtc, int[]? rpms)
	{
		if (string.IsNullOrWhiteSpace(expectedVersion) ||
		    !string.Equals(expectedVersion, reportedVersion, StringComparison.OrdinalIgnoreCase) ||
		    !healthy || !hidOk || rpms is not { Length: 4 } || !rpms.Any(rpm => rpm > 0))
			return false;
		return nowUtc != default && updatedUtc != default && lastHidUtc != default &&
		       updatedUtc <= nowUtc.AddSeconds(2) && lastHidUtc <= nowUtc.AddSeconds(2) &&
		       nowUtc - updatedUtc <= TimeSpan.FromSeconds(15) &&
		       nowUtc - lastHidUtc <= TimeSpan.FromSeconds(30);
	}

	internal static void CleanupCommittedBackup(string backup, bool finishInstall)
	{
		if (!ShouldRetainCommittedBackup(finishInstall) && Directory.Exists(backup))
			Directory.Delete(backup, recursive: true);
	}

	internal static void CleanupPostCommitBackups(string target, string committedBackup)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(committedBackup) && Directory.Exists(committedBackup))
			{
				Directory.Delete(committedBackup, recursive: true);
				Log("BACKUP_CLEANUP deleted committed backup=" + committedBackup);
			}
		}
		catch (Exception ex)
		{
			Log("BACKUP_CLEANUP skip committed backup=" + committedBackup + " reason=" + ex.Message);
		}

		try
		{
			foreach (string owned in GetOwnedRecoveryBackups(target))
			{
				try
				{
					if (Directory.Exists(owned))
					{
						Directory.Delete(owned, recursive: true);
						Log("BACKUP_CLEANUP deleted owned backup=" + owned);
					}
				}
				catch (Exception ex)
				{
					Log("BACKUP_CLEANUP skip owned backup=" + owned + " reason=" + ex.Message);
				}
			}

			string normalizedTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string normalizedDefault = Path.GetFullPath(DefaultDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.Equals(normalizedTarget, normalizedDefault, StringComparison.OrdinalIgnoreCase))
			{
				string? parent = Path.GetDirectoryName(normalizedTarget);
				if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
				{
					foreach (string legacy in Directory.GetDirectories(parent, ".SpankyhoOptimizer-backup-*"))
					{
						try
						{
							Directory.Delete(legacy, recursive: true);
							Log("BACKUP_CLEANUP deleted legacy backup=" + legacy);
						}
						catch (Exception ex)
						{
							Log("BACKUP_CLEANUP skip legacy backup=" + legacy + " reason=" + ex.Message);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log("BACKUP_CLEANUP failed: " + ex.Message);
		}
	}

	// Exercise the same post-commit backup decision on real temp directories
	// without triggering FinishInstall, services, registry or fan hardware.
	internal static bool VerifyBackupRetentionOnDisk(string sandbox)
	{
		string parent = Path.GetFullPath(sandbox);
		Directory.CreateDirectory(parent);
		foreach (bool productionMode in new[] { true, false })
		{
			string dir = Path.Combine(parent, productionMode ? "production" : "isolated");
			string backup = Path.Combine(dir, ".SpankyhoOptimizer-backup-fixture");
			Directory.CreateDirectory(backup);
			File.WriteAllText(Path.Combine(backup, "old-version.marker"), "previous payload");
			CleanupCommittedBackup(backup, productionMode);
			bool exists = File.Exists(Path.Combine(backup, "old-version.marker"));
			if (exists != productionMode) return false;
		}
		return true;
	}

	internal static void InstallPayloadTransactional(string target, bool desktop, Action<int, string>? progress, bool finishInstall = true, bool simulatePostMoveFailure = false)
	{
		string text = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
		string? path = Path.GetDirectoryName(text) ?? throw new InvalidOperationException("Neplatná cílová cesta.");
		if (finishInstall)
		{
			WaitForUpdateSourceExit();
			StopRunningOptimizer(preserveUserRuntimeHosts: true);
			System.Threading.Thread.Sleep(250);
		}
		RecoverInterruptedInstall(text);
		Log("TRANSACTION begin target=" + text);
		string text2 = Path.Combine(path, ".SpankyhoOptimizer-staging-" + Guid.NewGuid().ToString("N"));
		string text3 = BackupDirectoryForTarget(text, Guid.NewGuid().ToString("N"));
		bool flag = false;
		bool flag2 = false;
		bool flag3 = false;
		try
		{
			Log("TRANSACTION staging=" + text2);
			ExtractPayload(text2, progress);
			string text4 = Path.Combine(text2, "SpankyhoOptimizer.exe");
			if (!File.Exists(text4) || new FileInfo(text4).Length < 131072)
			{
				throw new InvalidDataException("Staging payload není platný.");
			}
			if (Directory.Exists(text))
			{
				MoveDirectoryWithRetry(text, text3);
				flag = true;
				Log("TRANSACTION old->backup " + text3);
			}
			MoveDirectoryWithRetry(text2, text);
			flag2 = true;
			Log("TRANSACTION staging->target");
			if (simulatePostMoveFailure && !finishInstall)
				throw new IOException("isolated simulated post-move failure");
			if (finishInstall)
			{
				FinishInstall(text, desktop);
			}
			flag3 = true;
			Log("TRANSACTION committed target=" + text);
			// In production keep the previous payload until the new fan agent has
			// been verified independently. A scheduled-task launch alone is not
			// evidence of restored HID/RPM control. Test installs still clean up.
			if (flag)
				CleanupCommittedBackup(text3, finishInstall);
		}
		catch (Exception ex)
		{
			Log("TRANSACTION rollback reason=" + ex.GetType().Name + ": " + ex.Message);
			try
			{
				if (flag && Directory.Exists(text3))
				{
					if (Directory.Exists(text))
						RestoreBackupWithRollback(text, text3, Directory.Move);
					else
						Directory.Move(text3, text);
				}
				else if (!flag && flag2 && Directory.Exists(text))
				{
					// Failed clean install: do not leave a partially committed target as installed.
					string failed = text + ".failed-first-install-" + Guid.NewGuid().ToString("N");
					Directory.Move(text, failed);
					Log("TRANSACTION preserved failed first install at " + failed);
				}
			}
			catch (Exception rollback)
			{
				Log("TRANSACTION CRITICAL rollback failed: " + rollback);
				throw new AggregateException("Install failed and rollback could not complete; preserved directories require inspection.", ex, rollback);
			}
			Log("TRANSACTION rollback complete oldMoved=" + flag + " newMoved=" + flag2);
			throw;
		}
		finally
		{
			try
			{
				if (Directory.Exists(text2))
				{
					Directory.Delete(text2, recursive: true);
				}
			}
			catch
			{
			}
			if (flag3)
			{
				try { CleanupCommittedBackup(text3, finishInstall); }
				catch { }
			}
		}
		// Only a committed real installation may resume the fan agent. The task
		// is owned by Windows Task Scheduler, so it keeps working with the GUI closed.
		if (flag3 && finishInstall)
		{
			ResumeFanAgentForInstalledTarget(text, TryStartRegisteredFanAgentTask);
			CleanupPostCommitBackups(text, text3);
		}
	}

	internal static bool ResumeFanAgentForInstalledTarget(string target, Func<bool> startTask)
	{
		try
		{
			if (!string.Equals(
				Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
				Path.GetFullPath(DefaultDirectory).TrimEnd(Path.DirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase))
				return false; // Never touch a real task from an isolated test installation.
			bool started = startTask();
			Log("FAN_AGENT_POST_INSTALL restart=" + (started ? "scheduled" : "unavailable"));
			return started;
		}
		catch (Exception ex)
		{
			// Agent recovery must not invalidate a successfully committed installation.
			Log("FAN_AGENT_POST_INSTALL failed: " + ex.Message);
			return false;
		}
	}

	private static bool TryStartRegisteredFanAgentTask()
	{
		using Process? process = Process.Start(new ProcessStartInfo("schtasks.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			ArgumentList = { "/Run", "/TN", @"\SpankyhoOptimizer-FanAgent" }
		});
		if (process == null) return false;
		if (!process.WaitForExit(5000))
		{
			try { process.Kill(entireProcessTree: true); } catch { }
			return false;
		}
		return process.ExitCode == 0;
	}

	// ProgramData is machine-wide. Its default inherited ACL lets BUILTIN\Users
	// write, which would let a standard user replace the SYSTEM power-plan guard
	// script or a local update payload. Harden it during a real install; isolated
	// test destinations must never touch the live machine-wide folder.
	private static void HardenDataRootAclIfRealInstall(string target)
	{
		try
		{
			string normalizedTarget = Path.GetFullPath(target)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string normalizedDefault = Path.GetFullPath(DefaultDirectory)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (!string.Equals(normalizedTarget, normalizedDefault, StringComparison.OrdinalIgnoreCase))
				return;

			string root = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				"SpankyhoOptimizer");
			Directory.CreateDirectory(root);
			using Process? process = Process.Start(new ProcessStartInfo("icacls.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				ArgumentList =
				{
					root, "/inheritance:r",
					"/grant:r", "*S-1-5-18:(OI)(CI)F",
					"*S-1-5-32-544:(OI)(CI)F",
					"*S-1-5-32-545:(OI)(CI)RX"
				}
			});
			if (process == null)
			{
				Log("DATA_ROOT_ACL_START_FAILED");
				return;
			}
			if (!process.WaitForExit(15000))
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				Log("DATA_ROOT_ACL_TIMEOUT");
				return;
			}
			Log(process.ExitCode == 0
				? "DATA_ROOT_ACL_OK users=read"
				: "DATA_ROOT_ACL_FAILED exit=" + process.ExitCode);
		}
		catch (Exception ex)
		{
			Log("DATA_ROOT_ACL_ERROR " + ex.Message);
		}
	}

	internal static void FinishInstall(string target, bool desktop)
	{
		HardenDataRootAclIfRealInstall(target);
		string text = Path.Combine(target, "SpankyhoOptimizer.exe");
		if (!File.Exists(text))
		{
			throw new FileNotFoundException("Po instalaci nebyla nalezena aplikace.", text);
		}
		string text2 = Path.Combine(target, "Uninstall-SpankyhoOptimizer.ps1");
		File.WriteAllText(text2, BuildUninstallScript(target), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Glass Optimizer.lnk"), text);
		if (desktop)
		{
			CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Glass Optimizer.lnk"), text);
		}
		MigrateObsoleteDataDirectories();
		CleanupObsoleteUninstallEntries();
		using RegistryKey registryKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SpankyhoOptimizer");
		if (registryKey == null)
		{
			throw new InvalidOperationException("Nelze zaregistrovat odinstalaci.");
		}
		string text3 = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + text2 + "\"";
		registryKey.SetValue("DisplayName", "Glass Optimizer");
		registryKey.SetValue("DisplayVersion", Version);
		registryKey.SetValue("Publisher", "Spankyho");
		registryKey.SetValue("InstallLocation", target);
		registryKey.SetValue("DisplayIcon", text);
		registryKey.SetValue("UninstallString", text3);
		registryKey.SetValue("QuietUninstallString", text3 + " -Silent");
		registryKey.SetValue("EstimatedSize", CalculateInstalledSizeKb(target), RegistryValueKind.DWord);
		registryKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
		registryKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
	}

	private static bool RepairRegistration(string target)
	{
		try
		{
			string exe = Path.Combine(target, "SpankyhoOptimizer.exe");
			if (!File.Exists(exe)) return false;
			MigrateObsoleteDataDirectories();
			CleanupObsoleteUninstallEntries();
			using RegistryKey? key = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SpankyhoOptimizer");
			if (key == null) return false;
			string uninstall = Path.Combine(target, "Uninstall-SpankyhoOptimizer.ps1");
			string command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + uninstall + "\"";
			key.SetValue("DisplayName", "Glass Optimizer");
			key.SetValue("DisplayVersion", Version);
			key.SetValue("Publisher", "Spankyho");
			key.SetValue("InstallLocation", target);
			key.SetValue("DisplayIcon", exe);
			key.SetValue("UninstallString", command);
			key.SetValue("QuietUninstallString", command + " -Silent");
			key.SetValue("EstimatedSize", CalculateInstalledSizeKb(target), RegistryValueKind.DWord);
			key.SetValue("NoModify", 1, RegistryValueKind.DWord);
			key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
			return true;
		}
		catch { return false; }
	}

	private static bool ProvisionHardwareBridge(string target)
	{
		try
		{
			string exe = Path.Combine(target, "SpankyhoOptimizer.exe");
			if (!File.Exists(exe)) return false;

			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = exe,
				Arguments = "--provision-hardware-bridge",
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			if (process == null) return false;
			if (!process.WaitForExit(12000))
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				return false;
			}
			return process.ExitCode == 0;
		}
		catch (Exception ex)
		{
			Log("Hardware bridge provisioning failed: " + ex.Message);
			return false;
		}
	}

	private static int CalculateInstalledSizeKb(string target)
	{
		long bytes = 0;
		try
		{
			foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
			{
				try { bytes += new FileInfo(file).Length; } catch { }
			}
		}
		catch { }
		long kb = Math.Max(1L, (bytes + 1023L) / 1024L);
		return (int)Math.Min(int.MaxValue, kb);
	}

	private static void MigrateObsoleteDataDirectories()
	{
		foreach (Environment.SpecialFolder folder in new[]
		{
			Environment.SpecialFolder.CommonApplicationData,
			Environment.SpecialFolder.LocalApplicationData,
			Environment.SpecialFolder.ApplicationData
		})
		{
			string root = Environment.GetFolderPath(folder);
			if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
				continue;

			string target = Path.Combine(root, "SpankyhoOptimizer");
			Directory.CreateDirectory(target);

			foreach (string source in Directory.EnumerateDirectories(root, "Spankyho*", SearchOption.TopDirectoryOnly))
			{
				if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
					!IsRecognizedLegacyDataRoot(source))
					continue;

				try
				{
					foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
					{
						string relative = Path.GetRelativePath(source, directory);
						Directory.CreateDirectory(Path.Combine(target, relative));
					}
					foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
					{
						string relative = Path.GetRelativePath(source, file);
						string destination = Path.Combine(target, relative);
						Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
						if (!File.Exists(destination) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(destination))
							File.Copy(file, destination, overwrite: true);
					}
					Directory.Delete(source, recursive: true);
					Log("DATA_MIGRATION removed obsolete path=" + source);
				}
				catch (Exception ex)
				{
					Log("DATA_MIGRATION skip path=" + source + " reason=" + ex.Message);
				}
			}
		}
	}

	private static bool IsRecognizedLegacyDataRoot(string path)
	{
		string name = Path.GetFileName(path);
		if (name.StartsWith("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase))
			return true;

		try
		{
			return File.Exists(Path.Combine(path, "terminal-bg.png"));
		}
		catch
		{
			return false;
		}
	}

	private static void CleanupObsoleteUninstallEntries()
	{
		foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
		{
			try
			{
				using RegistryKey? uninstall = hive.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", writable: true);
				if (uninstall == null) continue;
				foreach (string name in uninstall.GetSubKeyNames())
				{
					if (!name.StartsWith("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase) ||
						name.Equals("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase)) continue;
					try { uninstall.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch { }
				}
			}
			catch { }
		}
		try { Registry.LocalMachine.DeleteSubKeyTree("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{D37F4278-6135-4A1B-9A9D-0D59B44B0D21}_is1", false); } catch { }
	}

	private static void CreateShortcut(string linkPath, string targetPath)
	{
		Type? typeFromProgID = Type.GetTypeFromProgID("WScript.Shell");
		if (typeFromProgID == null)
		{
			throw new InvalidOperationException("Windows Script Host není dostupný.");
		}
		object? obj = Activator.CreateInstance(typeFromProgID);
		dynamic val = ((dynamic)(obj ?? throw new InvalidOperationException("Windows Script Host nelze vytvořit."))).CreateShortcut(linkPath);
		val.TargetPath = targetPath;
		val.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
		val.IconLocation = Path.Combine(Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory, "Assets", "App.ico") + ",0";
		val.Save();
	}

	private static string BuildUninstallScript(string target)
	{
		string text = target.Replace("'", "''");
		return "param([switch]$Silent)\r\n$ErrorActionPreference = 'Stop'\r\n$installDir = '" + text + "'\r\n$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)\r\nif (-not $isAdmin) {\r\n    $extra = if ($Silent) { ' -Silent' } else { '' }\r\n    $a = '-NoProfile -ExecutionPolicy Bypass -File \"' + $PSCommandPath + '\"' + $extra\r\n    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $a -Wait -PassThru\r\n    exit $p.ExitCode\r\n}\r\n$resolved = [IO.Path]::GetFullPath($installDir).TrimEnd([IO.Path]::DirectorySeparatorChar)\r\nif ([IO.Path]::GetFileName($resolved) -ne 'Spankyho Optimizer') { throw 'Bezpečnostní kontrola cesty odinstalace selhala.' }\r\nif (-not $Silent) {\r\n    Add-Type -AssemblyName PresentationFramework\r\n    if ([Windows.MessageBox]::Show('Opravdu odinstalovat Spankyho Optimizer?', 'Odinstalace', 'YesNo', 'Question') -ne 'Yes') { exit 0 }\r\n}\r\n$names = @('SpankyhoOptimizer','SpankyhoOptimizer.RuntimeHost')\r\nGet-Process -Name $names -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\r\nfor ($i = 0; $i -lt 50; $i++) { if (-not (Get-Process -Name $names -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 100 }\r\n$tasks = @('\\SpankyhoOptimizer-PowerPlanGuard','\\SpankyhoOptimizer-PowerPlanGuard-Boot','\\SpankyhoOptimizer-FanAgent','\\SpankyhoOptimizer-FanAgent-Watchdog','\\SpankyhoOptimizer-HardwareTelemetryBridge','\\SpankyhoOptimizer-RuntimeHost-Watchdog','\\SpankyhoOptimizer-RuntimeHost-Watchdog-User')\r\nforeach ($task in $tasks) { & schtasks.exe /Delete /TN $task /F 2>$null | Out-Null }\r\nRemove-Item -LiteralPath ([IO.Path]::Combine([Environment]::GetFolderPath('CommonDesktopDirectory'),'Spankyho Optimizer.lnk')) -Force -ErrorAction SilentlyContinue\r\nRemove-Item -LiteralPath ([IO.Path]::Combine([Environment]::GetFolderPath('CommonPrograms'),'Spankyho Optimizer.lnk')) -Force -ErrorAction SilentlyContinue\r\nRemove-Item -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SpankyhoOptimizer' -Recurse -Force -ErrorAction SilentlyContinue\r\nRemove-Item -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{D37F4278-6135-4A1B-9A9D-0D59B44B0D21}_is1' -Recurse -Force -ErrorAction SilentlyContinue\r\n$quoted = '\"' + $resolved + '\"'\r\nStart-Process cmd.exe -WindowStyle Hidden -ArgumentList ('/c timeout /t 2 /nobreak >nul & rmdir /s /q ' + $quoted)\r\n";
	}

    private static readonly object SetupLogGate = new();
    private const long SetupLogLimitBytes = 2L * 1024 * 1024;

    // The old setup.log was unbounded (21.3 MB on the release host). Keep
    // only the most recent 2 MB in each of two archives. Never load an
    // unbounded historical log into RAM during an installer operation.
    internal static bool RotateSetupLogIfNeeded(string logPath, long maxBytes = SetupLogLimitBytes)
    {
        if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var info = new FileInfo(logPath);
        if (!info.Exists || info.Length < maxBytes) return false;
        string archive = Path.ChangeExtension(logPath, ".1.log");
        string previousArchive = Path.ChangeExtension(logPath, ".2.log");
        string staging = logPath + ".rotate";
        try
        {
            using (var input = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.Seek(Math.Max(0, input.Length - maxBytes), SeekOrigin.Begin);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(archive)) File.Move(archive, previousArchive, overwrite: true);
            File.Move(staging, archive, overwrite: true);
            File.Delete(logPath);
            return true;
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { }
        }
    }

    internal static void Log(string text)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SpankyhoOptimizer");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "setup.log");
            lock (SetupLogGate)
            {
                RotateSetupLogIfNeeded(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " " + text + Environment.NewLine);
            }
        }
        catch
        {
            // A logging failure must not fail an otherwise valid installation.
        }
    }
}
