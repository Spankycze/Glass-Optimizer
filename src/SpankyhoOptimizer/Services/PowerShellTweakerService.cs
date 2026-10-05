using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record PowerShellTweakerState(
    bool PowerShell7,
    bool WindowsTerminal,
    bool Profiles,
    bool Backup);

public static class PowerShellTweakerService
{
    private static readonly string[] ProfilePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell", "Microsoft.PowerShell_profile.ps1")
    };

    public static string ApplyScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Tools", "PowerShellTerminalApply.ps1");

    public static PowerShellTweakerState ReadState()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string terminal = Path.Combine(local, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        bool ps7 = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe");
        bool profiles = File.Exists(ProfilePaths[0]) && File.Exists(ProfilePaths[1]);
        bool term = File.Exists(terminal) || File.Exists(Path.Combine(local, @"Microsoft\Windows Terminal\settings.json"));
        bool backup = File.Exists(terminal + ".terminal.backup") || File.Exists(terminal + ".spanky.backup");
        return new PowerShellTweakerState(ps7, term, profiles, backup);
    }

    public static async Task<string> ApplyAsync(
        string mode,
        string theme,
        int fontSize,
        int backgroundPercent,
        bool acrylic,
        bool banner,
        string cursorStyle,
        string paddingPreset,
        string promptDensity,
        CancellationToken token)
    {
        if (!File.Exists(ApplyScriptPath))
            throw new FileNotFoundException("PowerShell tweaker apply engine chybí.", ApplyScriptPath);

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{ApplyScriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["TERMINAL_MODE"] = mode;
        psi.Environment["TERMINAL_THEME"] = theme;
        psi.Environment["TERMINAL_FONT_SIZE"] = fontSize.ToString();
        psi.Environment["TERMINAL_BG_OPACITY"] = (backgroundPercent / 100d).ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["TERMINAL_ACRYLIC"] = acrylic ? "1" : "0";
        psi.Environment["TERMINAL_BANNER"] = banner ? "1" : "0";
        psi.Environment["TERMINAL_CURSOR"] = cursorStyle;
        psi.Environment["TERMINAL_PADDING"] = paddingPreset;
        psi.Environment["TERMINAL_PROMPT_DENSITY"] = promptDensity;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell apply engine se nepodařilo spustit.");
        string stdout = await process.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"Tweaker skončil kódem {process.ExitCode}." : stderr.Trim());
        return stdout.Trim();
    }

    public static async Task<double> BenchmarkAsync(bool noProfile, int runs, CancellationToken token)
    {
        string exe = @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (!File.Exists(exe)) return -1;
        var values = new double[Math.Max(1, runs)];
        for (int i = 0; i < values.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var args = noProfile ? "-NoLogo -NoProfile -Command exit" : "-NoLogo -Command exit";
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Benchmark PowerShellu se nepodařilo spustit.");
            await p.WaitForExitAsync(token).ConfigureAwait(false);
            values[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(values);
        return values[values.Length / 2];
    }

    public static int RestoreAll()
    {
        int restored = 0;
        foreach (string path in AllManagedPaths())
            restored += RestoreOne(path);
        return restored;
    }

    private static int RestoreOne(string path)
    {
        foreach (string ext in new[] { ".terminal.backup", ".spanky.backup" })
        {
            string backup = path + ext;
            if (!File.Exists(backup)) continue;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.Copy(backup, path, true);
            return 1;
        }
        return 0;
    }

    public static void ResetDefault()
    {
        foreach (string path in ProfilePaths)
            if (File.Exists(path)) File.Delete(path);

        foreach (string path in TerminalSettingsPaths())
            if (File.Exists(path)) File.Delete(path);

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string bg = Path.Combine(local, "PowerShellTerminal");
        if (Directory.Exists(bg)) Directory.Delete(bg, true);
    }

    private static IEnumerable<string> AllManagedPaths()
    {
        foreach (string path in ProfilePaths) yield return path;
        foreach (string path in TerminalSettingsPaths()) yield return path;
    }
    private static IEnumerable<string> TerminalSettingsPaths()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(local, @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(local, @"Microsoft\Windows Terminal\settings.json");
    }
}
