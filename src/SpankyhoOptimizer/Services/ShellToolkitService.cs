using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record CliToolDefinition(string Name, string Id, string Command);
public sealed record CliToolStatus(string Name, string Id, string Command, bool Installed, string Version);
public sealed record CmdOptimizerStatus(
    bool ClinkInstalled,
    string ClinkVersion,
    bool ClinkAutorun,
    bool ForceV2,
    bool VirtualTerminal);

public sealed record ShellSetupStepResult(string Name, bool Success, string Message);
public sealed record ShellSetupResult(IReadOnlyList<ShellSetupStepResult> Steps)
{
    public int SuccessCount => Steps.Count(x => x.Success);
    public int ErrorCount => Steps.Count(x => !x.Success);
    public int TotalCount => Steps.Count;
    public IReadOnlyList<ShellSetupStepResult> Errors => Steps.Where(x => !x.Success).ToArray();
}

public static class ShellToolkitService
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private const string ConsoleKeyPath = @"Console";
    private const string CommandProcessorKeyPath = @"Software\Microsoft\Command Processor";
    private const string BackupKeyPath = @"Software\SpankyhoOptimizer\Backups\CmdConsole";

    public static IReadOnlyList<CliToolDefinition> Tools { get; } = new[]
    {
        new CliToolDefinition("Clink", "chrisant996.Clink", "clink"),
        new CliToolDefinition("zoxide", "ajeetdsouza.zoxide", "zoxide"),
        new CliToolDefinition("fzf", "junegunn.fzf", "fzf"),
        new CliToolDefinition("ripgrep", "BurntSushi.ripgrep.MSVC", "rg"),        new CliToolDefinition("fd", "sharkdp.fd", "fd"),
        new CliToolDefinition("bat", "sharkdp.bat", "bat")
    };

    public static async Task<IReadOnlyList<CliToolStatus>> GetToolStatusesAsync(
        CancellationToken token = default)
    {
        // Executable discovery / first process starts can be synchronous before the
        // individual probes yield. Start the whole fan-out away from the UI thread.
        return await Task.Run(async () =>
        {
            var tasks = Tools.Select(x => GetToolStatusAsync(x, token)).ToArray();
            return (IReadOnlyList<CliToolStatus>)await Task.WhenAll(tasks).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    private static async Task<CliToolStatus> GetToolStatusAsync(
        CliToolDefinition tool,
        CancellationToken token)
    {
        string args = tool.Command.Equals("clink", StringComparison.OrdinalIgnoreCase)
            ? "--version"
            : "--version";
        try
        {
            string exe = tool.Command.Equals("clink", StringComparison.OrdinalIgnoreCase)
                ? ResolveClinkExecutable(false)
                : tool.Command + ".exe";
            if (string.IsNullOrWhiteSpace(exe))
                return new CliToolStatus(tool.Name, tool.Id, tool.Command, false, string.Empty);

            var result = await ProcessRunner.RunAsync(
                exe,
                args,
                TimeSpan.FromSeconds(4),
                token).ConfigureAwait(false);
            string text = ((result.StdOut ?? string.Empty) + " " + (result.StdErr ?? string.Empty)).Trim();
            string version = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (version.Length > 80) version = version[..80];
            return new CliToolStatus(tool.Name, tool.Id, tool.Command, result.Success, version);
        }
        catch
        {
            return new CliToolStatus(tool.Name, tool.Id, tool.Command, false, string.Empty);
        }
    }    public static async Task<string> InstallOrUpdateToolAsync(
        CliToolDefinition tool,
        CancellationToken token = default)
    {
        string args =
            $"install --id {tool.Id} --exact --silent --accept-source-agreements " +
            "--accept-package-agreements --disable-interactivity";
        var result = await UnelevatedProcessRunner.RunAsync(
            "winget.exe", args, TimeSpan.FromMinutes(12), token).ConfigureAwait(false);

        if (!result.Success && result.ExitCode != unchecked((int)0x8A15002B))
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? result.StdOut.Trim()
                    : result.StdErr.Trim());

        return string.IsNullOrWhiteSpace(result.StdOut)
            ? $"{tool.Name} je připraven."
            : result.StdOut.Trim();
    }

    public static async Task<string> InstallOrUpdateAllAsync(CancellationToken token = default)
    {
        var lines = new List<string>();
        foreach (var tool in Tools)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                _ = await InstallOrUpdateToolAsync(tool, token).ConfigureAwait(false);
                lines.Add(tool.Name + ": OK");
            }
            catch (Exception ex)
            {
                lines.Add(tool.Name + ": ERROR " + ex.Message);
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static async Task<ShellSetupResult> InstallAndApplyEverythingAsync(CancellationToken token = default)
    {
        await SetupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
        var steps = new List<ShellSetupStepResult>();

        async Task Step(string name, Func<Task> action)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await action().ConfigureAwait(false);
                steps.Add(new ShellSetupStepResult(name, true, "OK"));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                steps.Add(new ShellSetupStepResult(name, false, CleanSetupError(ex.Message)));
            }
        }

        async Task StepProfile(string name, Func<Task> action)
        {
            string profile = PowerShellCenterService.PowerShell7ProfilePath;
            bool existed = File.Exists(profile);
            string before = existed ? await File.ReadAllTextAsync(profile, token).ConfigureAwait(false) : string.Empty;
            int beforeCount = steps.Count;
            await Step(name, action).ConfigureAwait(false);
            if (steps.Count <= beforeCount || steps[^1].Success) return;
            try
            {
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
                    await File.WriteAllTextAsync(profile, before, new System.Text.UTF8Encoding(false), token).ConfigureAwait(false);
                }
                else if (File.Exists(profile))
                {
                    File.Delete(profile);
                }
            }
            catch (Exception rollbackEx)
            {
                LogService.Error("shell", $"Profile rollback failed after {name}", rollbackEx);
            }
        }

        foreach (var tool in Tools)
        {
            var current = tool;
            await Step(current.Name, async () =>
            {
                _ = await InstallOrUpdateToolAsync(current, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        await Step("PSResourceGet", async () =>
        {
            _ = await PowerShellCenterService.EnsurePSResourceGetAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Step("PSReadLine", async () =>
        {
            _ = await PowerShellCenterService.EnsurePSReadLineAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Step("PSScriptAnalyzer", async () =>
        {
            _ = await PowerShellCenterService.EnsurePSScriptAnalyzerAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Step("WinGet Client", async () =>
        {
            _ = await PowerShellCenterService.EnsureWinGetClientAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await StepProfile("Terminal-Icons", async () =>
        {
            _ = await PowerShellCenterService.EnsureTerminalIconsAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await StepProfile("PSFzf", async () =>
        {
            _ = await PowerShellCenterService.EnsurePSFzfAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await StepProfile("posh-git", async () =>
        {
            _ = await PowerShellCenterService.EnsurePoshGitAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await StepProfile("Oh My Posh", async () =>
        {
            _ = await PowerShellCenterService.EnsureOhMyPoshAsync(token).ConfigureAwait(false);
            PowerShellCenterService.RemoveManagedProfileTag("OhMyPosh");
        }).ConfigureAwait(false);

        await StepProfile("zoxide profile", () =>
        {
            PowerShellCenterService.EnableZoxideInProfile();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await StepProfile("PSReadLine Recommended", () =>
        {
            PowerShellCenterService.ApplyPsReadLineProfile(new PsReadLineProfileOptions(
                "Windows", "History", "ListView", 10000,
                true, true, true, true, true));
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await Step("CMD Recommended", () =>
        {
            ApplyCmdRecommended();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await Step("Clink AutoRun", async () =>
        {
            _ = await SetClinkAutorunAsync(true, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return new ShellSetupResult(steps);
        }
        finally
        {
            SetupGate.Release();
        }
    }

    private static string CleanSetupError(string? message)
    {
        string text = string.IsNullOrWhiteSpace(message) ? "Neznámá chyba." : message.Trim();

        if (text.Contains("clink.exe", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("nelze nalézt", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("trying to start process", StringComparison.OrdinalIgnoreCase)))
            return "Clink byl nainstalován, ale jeho executable nebyl nalezen.";

        text = text.Replace("\r", " ").Replace("\n", " ");
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length > 220 ? text[..220] + "…" : text;
    }

    public static async Task<CmdOptimizerStatus> GetCmdStatusAsync(CancellationToken token = default)
    {
        // Registry reads and Clink executable discovery are tiny when warm but can
        // occasionally stall a first page render. Run the complete cold path off-UI.
        return await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();

            bool forceV2 = false;
            bool vt = false;
            bool autorun = false;

            using (var console = Registry.CurrentUser.OpenSubKey(ConsoleKeyPath))
            {
                forceV2 = ReadDword(console, "ForceV2") == 1;
                vt = ReadDword(console, "VirtualTerminalLevel") == 1;
            }

            using (var command = Registry.CurrentUser.OpenSubKey(CommandProcessorKeyPath))
            {
                string auto = command?.GetValue("AutoRun")?.ToString() ?? string.Empty;
                autorun = auto.Contains("clink", StringComparison.OrdinalIgnoreCase);
            }

            return await GetCmdStatusCoreAsync(forceV2, vt, autorun, token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    private static async Task<CmdOptimizerStatus> GetCmdStatusCoreAsync(
        bool forceV2,
        bool vt,
        bool autorun,
        CancellationToken token)
    {
        CliToolStatus clink = await GetToolStatusAsync(Tools[0], token).ConfigureAwait(false);
        return new CmdOptimizerStatus(
            clink.Installed,
            clink.Version,
            autorun,
            forceV2,
            vt);
    }    public static void ApplyCmdRecommended()
    {
        using var console = Registry.CurrentUser.CreateSubKey(ConsoleKeyPath, true)
            ?? throw new InvalidOperationException("Nelze otevřít HKCU\\Console.");
        SaveOriginalDword(console, "ForceV2");
        SaveOriginalDword(console, "VirtualTerminalLevel");
        SaveOriginalDword(console, "HistoryBufferSize");
        SaveOriginalDword(console, "NumberOfHistoryBuffers");
        SaveOriginalDword(console, "HistoryNoDup");

        console.SetValue("ForceV2", 1, RegistryValueKind.DWord);
        console.SetValue("VirtualTerminalLevel", 1, RegistryValueKind.DWord);
        console.SetValue("HistoryBufferSize", 999, RegistryValueKind.DWord);
        console.SetValue("NumberOfHistoryBuffers", 5, RegistryValueKind.DWord);
        console.SetValue("HistoryNoDup", 1, RegistryValueKind.DWord);
    }

    public static void ApplyCmdTheme(string theme)
    {
        using var console = Registry.CurrentUser.CreateSubKey(ConsoleKeyPath, true)
            ?? throw new InvalidOperationException("Nelze otevřít HKCU\\Console.");

        foreach (string name in new[] { "ScreenColors", "ColorTable00", "ColorTable10", "ColorTable12", "ColorTable15" })
            SaveOriginalDword(console, name);

        console.SetValue("ColorTable00", 0x00000000, RegistryValueKind.DWord);

        switch (theme)
        {
            case "Black Green":
                console.SetValue("ColorTable10", 0x0073FF46, RegistryValueKind.DWord);
                console.SetValue("ScreenColors", 0x0A, RegistryValueKind.DWord);
                break;
            case "Pure Black":
                console.SetValue("ColorTable15", 0x00FFFFFF, RegistryValueKind.DWord);
                console.SetValue("ScreenColors", 0x0F, RegistryValueKind.DWord);
                break;
            default:
                console.SetValue("ColorTable12", 0x005555FF, RegistryValueKind.DWord);
                console.SetValue("ScreenColors", 0x0C, RegistryValueKind.DWord);
                break;
        }
    }

    public static void RestoreCmdRecommended()
    {
        using var console = Registry.CurrentUser.CreateSubKey(ConsoleKeyPath, true)
            ?? throw new InvalidOperationException("Nelze otevřít HKCU\\Console.");
        RestoreOriginalDword(console, "ForceV2");
        RestoreOriginalDword(console, "VirtualTerminalLevel");
        RestoreOriginalDword(console, "HistoryBufferSize");
        RestoreOriginalDword(console, "NumberOfHistoryBuffers");
        RestoreOriginalDword(console, "HistoryNoDup");
        RestoreOriginalDword(console, "ScreenColors");
        RestoreOriginalDword(console, "ColorTable00");
        RestoreOriginalDword(console, "ColorTable10");
        RestoreOriginalDword(console, "ColorTable12");
        RestoreOriginalDword(console, "ColorTable15");
    }

    public static async Task<string> SetClinkAutorunAsync(bool enabled, CancellationToken token = default)
    {
        string args = enabled ? "autorun install" : "autorun uninstall";
        string exe = ResolveClinkExecutable();
        var result = await ProcessRunner.RunAsync(
            exe, args, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException((result.StdErr + result.StdOut).Trim());
        return enabled ? "Clink AutoRun zapnut." : "Clink AutoRun vypnut.";
    }

    private static string ResolveClinkExecutable(bool throwIfMissing = true)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] directCandidates =
        {
            Path.Combine(local, "Microsoft", "WinGet", "Links", "clink.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Links", "clink_x64.exe"),
            Path.Combine(local, "Microsoft", "WindowsApps", "clink.exe"),
            Path.Combine(local, "Programs", "clink", "clink.exe"),
            Path.Combine(local, "Programs", "clink", "clink_x64.exe"),
            Path.Combine(programFiles, "clink", "clink.exe"),
            Path.Combine(programFiles, "clink", "clink_x64.exe"),
            Path.Combine(programFilesX86, "clink", "clink.exe"),
            Path.Combine(programFilesX86, "clink", "clink_x64.exe")
        };

        foreach (string candidate in directCandidates)
            if (File.Exists(candidate)) return candidate;

        string[] roots =
        {
            Path.Combine(local, "Microsoft", "WinGet", "Packages"),
            Path.Combine(local, "Programs", "clink"),
            Path.Combine(programFiles, "clink"),
            Path.Combine(programFilesX86, "clink")
        };

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                string? found = Directory.EnumerateFiles(root, "clink*.exe", SearchOption.AllDirectories)
                    .Where(x => x.Contains("clink", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Contains("x64", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(x => x.Length)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
            catch { }
        }

        foreach (var source in new[]
        {
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
        })
        {
            string? fromRegistry = TryResolveClinkFromUninstall(source.Item1, source.Item2);
            if (!string.IsNullOrWhiteSpace(fromRegistry)) return fromRegistry;
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                foreach (string name in new[] { "clink.exe", "clink_x64.exe" })
                {
                    string candidate = Path.Combine(raw.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }

        if (throwIfMissing)
            throw new FileNotFoundException("Clink byl nainstalován, ale jeho executable nebyl nalezen.");
        return string.Empty;
    }    private static string? TryResolveClinkFromUninstall(RegistryKey root, string path)
    {
        try
        {
            using var uninstall = root.OpenSubKey(path);
            if (uninstall == null) return null;

            foreach (string subName in uninstall.GetSubKeyNames())
            {
                using var item = uninstall.OpenSubKey(subName);
                if (item == null) continue;

                string displayName = item.GetValue("DisplayName")?.ToString() ?? string.Empty;
                if (!displayName.Contains("Clink", StringComparison.OrdinalIgnoreCase)) continue;

                string installLocation = Environment.ExpandEnvironmentVariables(
                    item.GetValue("InstallLocation")?.ToString() ?? string.Empty).Trim().Trim('"');

                if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
                {
                    foreach (string name in new[] { "clink_x64.exe", "clink.exe" })
                    {
                        string direct = Path.Combine(installLocation, name);
                        if (File.Exists(direct)) return direct;
                    }

                    string? recursive = Directory.EnumerateFiles(
                            installLocation,
                            "clink*.exe",
                            SearchOption.AllDirectories)
                        .OrderByDescending(x => x.Contains("x64", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(recursive)) return recursive;
                }

                string displayIcon = item.GetValue("DisplayIcon")?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(displayIcon))
                {
                    string iconPath = displayIcon.Trim().Trim('"');
                    int comma = iconPath.LastIndexOf(',');
                    if (comma > 2) iconPath = iconPath[..comma].Trim().Trim('"');
                    if (File.Exists(iconPath) &&
                        Path.GetFileName(iconPath).Contains("clink", StringComparison.OrdinalIgnoreCase))
                        return iconPath;
                }
            }
        }
        catch { }

        return null;
    }

    private static int? ReadDword(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is int i ? i : null;
    }

    private static void SaveOriginalDword(RegistryKey source, string name)
    {
        using var backup = Registry.CurrentUser.CreateSubKey(BackupKeyPath, true)
            ?? throw new InvalidOperationException("Nelze vytvořit CMD backup.");
        string marker = name + ".Saved";
        if (backup.GetValue(marker) is int) return;

        object? value = source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        backup.SetValue(marker, 1, RegistryValueKind.DWord);
        if (value is int i)
        {
            backup.SetValue(name + ".Exists", 1, RegistryValueKind.DWord);
            backup.SetValue(name + ".Value", i, RegistryValueKind.DWord);
        }
        else
        {
            backup.SetValue(name + ".Exists", 0, RegistryValueKind.DWord);
        }
    }

    private static void RestoreOriginalDword(RegistryKey target, string name)
    {
        using var backup = Registry.CurrentUser.OpenSubKey(BackupKeyPath, false);
        if (backup?.GetValue(name + ".Saved") is not int) return;

        bool existed = backup.GetValue(name + ".Exists") is int exists && exists == 1;
        if (existed && backup.GetValue(name + ".Value") is int value)
            target.SetValue(name, value, RegistryValueKind.DWord);
        else
            target.DeleteValue(name, false);
    }
}