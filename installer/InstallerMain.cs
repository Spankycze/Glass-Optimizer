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
}
