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
}
