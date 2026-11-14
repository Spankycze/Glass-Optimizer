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

	internal static bool VerifyUpdateReadback(string target, string? registeredVersion, string? registeredPath)
	{
		try
		{
			var expected = new Version(Version + ".0");
			string exe = Path.Combine(target, "SpankyhoOptimizer.exe");
			string dll = Path.Combine(target, "SpankyhoOptimizer.dll");
			return File.Exists(exe) && File.Exists(dll) &&
				System.Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var actual) && actual == expected &&
				AssemblyName.GetAssemblyName(dll).Version == expected &&
				string.Equals(registeredVersion, Version, StringComparison.Ordinal) &&
				!string.IsNullOrWhiteSpace(registeredPath) &&
				string.Equals(Path.GetFullPath(registeredPath).TrimEnd(Path.DirectorySeparatorChar),
					Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
		}
		catch { return false; }
	}

	internal static bool ProvisionHardwareBridge(string target)
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
