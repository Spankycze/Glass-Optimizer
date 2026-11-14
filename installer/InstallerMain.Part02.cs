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

internal static partial class Program
{
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
				if (UpdateFromPid > 0)
				{
					var updateApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
					updateApp.Run(new UpdatePopupWindow(target, desktop));
					return Environment.ExitCode;
				}
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
}
