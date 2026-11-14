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
}
