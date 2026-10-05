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
	internal const string AppName = "Spankyho Optimizer";

	internal static readonly string Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

	internal const string DotNetUrl = "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-win-x64.exe";

	internal const string DotNetSha512 = "b937ae539c4ab21f885b033178dadea98018baad777252c76251e760d81c18463b2f802e86b6a67ea066690e9ede2d66a6bc3c3e614e391505e98c4c41121400";

	internal static int UpdateFromPid { get; private set; }

	internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Spankyho Optimizer");

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
				InstallPayloadTransactional(target, desktop, null, finishInstall: true);
				return ProvisionHardwareBridge(target) ? 0 : 42;
			}
			if (args.Length != 0 && string.Equals(args[0], "--smoketest", StringComparison.OrdinalIgnoreCase))
			{
				using (Stream stream = OpenResource("payload.zip"))
				{
					return (stream.Length <= 1048576) ? 10 : 0;
				}
			}
			if (args.Length != 0 && string.Equals(args[0], "--ui-smoketest", StringComparison.OrdinalIgnoreCase))
			{
				FrameworkElement ui = LoadUi();
				string[] required = { "DragArea", "CloseButton", "InstallPath", "DesktopShortcut", "LaunchAfter", "InstallProgress", "StatusText", "PhaseText", "InstallButton", "BrowseButton", "DotNetCard", "WinAppCard", "DotNetDot", "WinAppDot", "DotNetState", "WinAppState", "StepInstall", "StepComponents", "StepDone", "StepInstallText", "StepComponentsText", "StepDoneText", "LiveLog", "FinalActions", "RunButton", "OpenFolderButton", "FinishCloseButton", "FooterHint" };
				foreach (string name in required)
				{
					if (ui.FindName(name) == null) return 11;
				}
				return 0;
			}
			string? version;
			if (args.Length != 0 && string.Equals(args[0], "--release-installer-smoke-bundle", StringComparison.OrdinalIgnoreCase))
			{
				using (Stream stream = OpenResource("payload.zip"))
				{
					if (stream.Length <= 1048576) return 10;
				}
				FrameworkElement ui = LoadUi();
				string[] required = { "DragArea", "CloseButton", "InstallPath", "DesktopShortcut", "LaunchAfter", "InstallProgress", "StatusText", "PhaseText", "InstallButton", "BrowseButton", "DotNetCard", "WinAppCard", "DotNetDot", "WinAppDot", "DotNetState", "WinAppState", "StepInstall", "StepComponents", "StepDone", "StepInstallText", "StepComponentsText", "StepDoneText", "LiveLog", "FinalActions", "RunButton", "OpenFolderButton", "FinishCloseButton", "FooterHint" };
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
				if (Directory.Exists(root)) Directory.Delete(root, true);
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
				string backup = Path.Combine(recoveryParent, ".SpankyhoOptimizer-backup-smoke");
				Directory.CreateDirectory(staging);
				Directory.CreateDirectory(backup);
				File.WriteAllText(Path.Combine(staging, "stale.tmp"), "stale");
				File.WriteAllText(Path.Combine(backup, "SpankyhoOptimizer.exe"), "backup");
				File.WriteAllText(Path.Combine(backup, "backup.marker"), "ok");
				RecoverInterruptedInstall(recovery);
				bool recovered = File.Exists(Path.Combine(recovery, "backup.marker")) &&
				                 File.Exists(Path.Combine(recovery, "SpankyhoOptimizer.exe"));
				bool cleaned = !Directory.Exists(staging) && !Directory.Exists(backup) &&
				               !File.Exists(Path.Combine(recovery, "broken.marker"));
				return (recovered && cleaned) ? 0 : 23;
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
			if (args.Length > 1 && string.Equals(args[0], "--test-recovery", StringComparison.OrdinalIgnoreCase))
			{
				string fullPath2 = Path.GetFullPath(args[1]);
				string? path2 = Path.GetDirectoryName(fullPath2) ?? string.Empty;
				Directory.CreateDirectory(fullPath2);
				File.WriteAllText(Path.Combine(fullPath2, "broken.marker"), "broken");
				string text = Path.Combine(path2, ".SpankyhoOptimizer-staging-smoke");
				string text2 = Path.Combine(path2, ".SpankyhoOptimizer-backup-smoke");
				Directory.CreateDirectory(text);
				Directory.CreateDirectory(text2);
				File.WriteAllText(Path.Combine(text, "stale.tmp"), "stale");
				File.WriteAllText(Path.Combine(text2, "SpankyhoOptimizer.exe"), "backup");
				File.WriteAllText(Path.Combine(text2, "backup.marker"), "ok");
				RecoverInterruptedInstall(fullPath2);
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
				MessageBox.Show(ex.Message, "Spankyho Optimizer Setup", MessageBoxButton.OK, MessageBoxImage.Hand);
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

	internal static Stream OpenResource(string name)
	{
		return Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("Instalační data nebyla nalezena: " + name);
	}

	internal static FrameworkElement LoadUi()
	{
		using Stream input = OpenResource("installer-ui.xaml");
		using XmlReader reader = XmlReader.Create(input);
		return (FrameworkElement)XamlReader.Load(reader);
	}

	internal static bool HasDotNet10(out string? version)
	{
		version = null;
		try
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet\\shared\\Microsoft.NETCore.App");
			if (!Directory.Exists(path))
			{
				return false;
			}
			Version? version2 = null;
			string[] directories = Directory.GetDirectories(path);
			for (int i = 0; i < directories.Length; i++)
			{
				if (System.Version.TryParse(Path.GetFileName(directories[i]), out Version? result) && result.Major == 10 && (version2 == null || result > version2))
				{
					version2 = result;
				}
			}
			if (version2 != null)
			{
				version = version2.ToString();
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	internal static async Task DownloadAsync(string url, string destination, Action<int>? progress)
	{
		using HttpClient client = new HttpClient
		{
			Timeout = TimeSpan.FromMinutes(10L)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("SpankyhoOptimizerSetup/" + Version);
		using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
		response.EnsureSuccessStatusCode();
		long? total = response.Content.Headers.ContentLength;
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
			await output.WriteAsync(buffer, 0, read);
			written += read;
			if (total.HasValue && total.Value > 0)
			{
				progress?.Invoke((int)Math.Min(100L, written * 100 / total.Value));
			}
		}
		progress?.Invoke(100);
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

	internal static async Task RunPackageAsync(string file, string arguments)
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

		if (process.ExitCode != 0 && process.ExitCode != 1641 && process.ExitCode != 3010)
			throw new InvalidOperationException("Instalace z?vislosti selhala (k?d " + process.ExitCode + ").");
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
		string text = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		Directory.CreateDirectory(text);
		using Stream stream = OpenResource("payload.zip");
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
		int num = 0;
		foreach (ZipArchiveEntry entry in zipArchive.Entries)
		{
			string fullPath = Path.GetFullPath(Path.Combine(text, entry.FullName));
			if (!fullPath.StartsWith(text, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("Neplatná cesta v instalačních datech.");
			}
			if (string.IsNullOrEmpty(entry.Name))
			{
				Directory.CreateDirectory(fullPath);
			}
			else
			{
				string? directoryName = Path.GetDirectoryName(fullPath);
				if (!string.IsNullOrEmpty(directoryName))
				{
					Directory.CreateDirectory(directoryName);
				}
				entry.ExtractToFile(fullPath, overwrite: true);
			}
			num++;
			progress?.Invoke(55 + (int)(35.0 * (double)num / (double)Math.Max(1, zipArchive.Entries.Count)), "Instaluji soubory Optimizeru…");
		}
	}

	internal static void RecoverInterruptedInstall(string target)
	{
		string text = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
		string path = Path.GetDirectoryName(text) ?? throw new InvalidOperationException("Neplatná cílová cesta.");
		Directory.CreateDirectory(path);
		Log("RECOVERY begin target=" + text);
		string[] directories = Directory.GetDirectories(path, ".SpankyhoOptimizer-staging-*");
		foreach (string text2 in directories)
		{
			try
			{
				Directory.Delete(text2, recursive: true);
				Log("RECOVERY removed stale staging " + text2);
			}
			catch
			{
			}
		}
		string[] array = Directory.GetDirectories(path, ".SpankyhoOptimizer-backup-*");
		Array.Sort(array, (string a, string b) => Directory.GetLastWriteTimeUtc(b).CompareTo(Directory.GetLastWriteTimeUtc(a)));
		bool flag = Directory.Exists(text) && File.Exists(Path.Combine(text, "SpankyhoOptimizer.exe"));
		if (!flag && array.Length != 0)
		{
			try
			{
				if (Directory.Exists(text))
				{
					Directory.Delete(text, recursive: true);
				}
			}
			catch
			{
			}
			Directory.Move(array[0], text);
			Log("RECOVERY restored backup " + array[0]);
			array = ((array.Length > 1) ? array[1..] : Array.Empty<string>());
			flag = true;
		}
		if (flag)
		{
			directories = array;
			foreach (string path2 in directories)
			{
				try
				{
					Directory.Delete(path2, recursive: true);
				}
				catch
				{
				}
			}
		}
		Log("RECOVERY complete targetValid=" + flag + " backupsRemaining=" + array.Length);
	}

	internal static void InstallPayloadTransactional(string target, bool desktop, Action<int, string>? progress, bool finishInstall = true)
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
		string text3 = Path.Combine(path, ".SpankyhoOptimizer-backup-" + Guid.NewGuid().ToString("N"));
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
			if (finishInstall)
			{
				FinishInstall(text, desktop);
			}
			flag3 = true;
			Log("TRANSACTION committed target=" + text);
			if (flag && Directory.Exists(text3))
			{
				Directory.Delete(text3, recursive: true);
			}
		}
		catch (Exception ex)
		{
			Log("TRANSACTION rollback reason=" + ex.GetType().Name + ": " + ex.Message);
			try
			{
				if (flag2 && Directory.Exists(text))
				{
					Directory.Delete(text, recursive: true);
				}
			}
			catch
			{
			}
			try
			{
				if (flag && Directory.Exists(text3) && !Directory.Exists(text))
				{
					Directory.Move(text3, text);
				}
			}
			catch
			{
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
				try
				{
					if (Directory.Exists(text3))
					{
						Directory.Delete(text3, recursive: true);
					}
				}
				catch
				{
				}
			}
		}
	}

	internal static void FinishInstall(string target, bool desktop)
	{
		string text = Path.Combine(target, "SpankyhoOptimizer.exe");
		if (!File.Exists(text))
		{
			throw new FileNotFoundException("Po instalaci nebyla nalezena aplikace.", text);
		}
		string text2 = Path.Combine(target, "Uninstall-SpankyhoOptimizer.ps1");
		File.WriteAllText(text2, BuildUninstallScript(target), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Spankyho Optimizer.lnk"), text);
		if (desktop)
		{
			CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Spankyho Optimizer.lnk"), text);
		}
		MigrateObsoleteDataDirectories();
		CleanupObsoleteUninstallEntries();
		using RegistryKey registryKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SpankyhoOptimizer");
		if (registryKey == null)
		{
			throw new InvalidOperationException("Nelze zaregistrovat odinstalaci.");
		}
		string text3 = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + text2 + "\"";
		registryKey.SetValue("DisplayName", "Spankyho Optimizer");
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
			key.SetValue("DisplayName", "Spankyho Optimizer");
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
		return "param([switch]$Silent)\r\n$ErrorActionPreference = 'Stop'\r\n$installDir = '" + text + "'\r\n$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)\r\nif (-not $isAdmin) {\r\n    $extra = if ($Silent) { ' -Silent' } else { '' }\r\n    $a = '-NoProfile -ExecutionPolicy Bypass -File \"' + $PSCommandPath + '\"' + $extra\r\n    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $a -Wait -PassThru\r\n    exit $p.ExitCode\r\n}\r\n$resolved = [IO.Path]::GetFullPath($installDir).TrimEnd([IO.Path]::DirectorySeparatorChar)\r\nif ([IO.Path]::GetFileName($resolved) -ne 'Spankyho Optimizer') { throw 'Bezpečnostní kontrola cesty odinstalace selhala.' }\r\nif (-not $Silent) {\r\n    Add-Type -AssemblyName PresentationFramework\r\n    if ([Windows.MessageBox]::Show('Opravdu odinstalovat Spankyho Optimizer?', 'Odinstalace', 'YesNo', 'Question') -ne 'Yes') { exit 0 }\r\n}\r\nGet-Process SpankyhoOptimizer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\r\n& schtasks.exe /Delete /TN '\\SpankyhoOptimizer-PowerPlanGuard' /F 2>$null | Out-Null\r\nRemove-Item -LiteralPath ([IO.Path]::Combine([Environment]::GetFolderPath('CommonDesktopDirectory'),'Spankyho Optimizer.lnk')) -Force -ErrorAction SilentlyContinue\r\nRemove-Item -LiteralPath ([IO.Path]::Combine([Environment]::GetFolderPath('CommonPrograms'),'Spankyho Optimizer.lnk')) -Force -ErrorAction SilentlyContinue\r\nRemove-Item -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SpankyhoOptimizer' -Recurse -Force -ErrorAction SilentlyContinue\r\nRemove-Item -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{D37F4278-6135-4A1B-9A9D-0D59B44B0D21}_is1' -Recurse -Force -ErrorAction SilentlyContinue\r\n$quoted = '\"' + $resolved + '\"'\r\nStart-Process cmd.exe -WindowStyle Hidden -ArgumentList ('/c timeout /t 2 /nobreak >nul & rmdir /s /q ' + $quoted)\r\n";
	}

	internal static void Log(string text)
	{
		try
		{
			string text2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer");
			Directory.CreateDirectory(text2);
			File.AppendAllText(Path.Combine(text2, "setup.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + Environment.NewLine);
		}
		catch
		{
		}
	}
}
