using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record ShellHealthFinding(string Area, string Severity, string Message, bool Repairable);
public sealed record ShellHealthReport(IReadOnlyList<ShellHealthFinding> Findings)
{
    public int Errors => Findings.Count(x => x.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
    public int Warnings => Findings.Count(x => x.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
}
public sealed record ShellStartupCost(string Name, double Milliseconds, bool Available);

public static class ShellHealthService
{
    private const string EnvKeyPath = @"Environment";
    private const string BackupKeyPath = @"Software\SpankyhoOptimizer\Backups\ShellHealth";
    private const string CommandProcessorKeyPath = @"Software\Microsoft\Command Processor";

    public static async Task<ShellHealthReport> AnalyzeAsync(CancellationToken token = default)
    {
        var list = new List<ShellHealthFinding>();
        AnalyzeUserPath(list);
        AnalyzeCmdAutorun(list);
        AnalyzeTerminalJson(list);
        await AnalyzePowerShellProfileAsync(list, token).ConfigureAwait(false);
        return new ShellHealthReport(list);
    }

    public static string RepairRecommended()
    {
        var changed = new List<string>();
        if (RepairUserPath()) changed.Add("User PATH");
        if (RepairTerminalJson()) changed.Add("Windows Terminal JSON");
        return changed.Count == 0
            ? "Nebyla nalezena bezpečně opravitelná odchylka."
            : "Opraveno: " + string.Join(", ", changed) + ".";
    }    public static async Task<IReadOnlyList<ShellStartupCost>> MeasureKnownProfileCostsAsync(
        CancellationToken token = default)
    {
        var probes = new (string Name, string Script)[]
        {
            ("PSReadLine", "Import-Module PSReadLine -ErrorAction Stop"),
            ("Terminal-Icons", "Import-Module Terminal-Icons -ErrorAction Stop"),
            ("PSFzf", "Import-Module PSFzf -ErrorAction Stop"),
            ("posh-git", "Import-Module posh-git -ErrorAction Stop"),
            ("zoxide", "zoxide init powershell | Out-Null"),
            ("Oh My Posh", "oh-my-posh init pwsh | Out-Null")
        };

        var result = new List<ShellStartupCost>();
        foreach (var probe in probes)
        {
            token.ThrowIfCancellationRequested();
            result.Add(await MeasureProbeAsync(probe.Name, probe.Script, token).ConfigureAwait(false));
        }
        return result;
    }

    private static async Task<ShellStartupCost> MeasureProbeAsync(
        string name,
        string body,
        CancellationToken token)
    {
        string exe = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
            ? @"C:\Program Files\PowerShell\7\pwsh.exe"
            : "pwsh.exe";
        string safeBody = body.Replace("\"", "\\\"");
        string command =
            "$ErrorActionPreference='Stop';" +
            "$sw=[Diagnostics.Stopwatch]::StartNew();" +
            $"try{{ {safeBody}; $sw.Stop(); [Console]::Write($sw.Elapsed.TotalMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)) }} " +
            "catch { exit 7 }";
        var run = await ProcessRunner.RunAsync(
            exe,
            "-NoLogo -NoProfile -NonInteractive -Command \"" + command + "\"",
            TimeSpan.FromSeconds(15),
            token).ConfigureAwait(false);
        if (!run.Success ||
            !double.TryParse((run.StdOut ?? string.Empty).Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double ms))
            return new ShellStartupCost(name, 0, false);
        return new ShellStartupCost(name, ms, true);
    }    private static void AnalyzeUserPath(List<ShellHealthFinding> list)
    {
        string raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        string[] entries = raw.Split(';', StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0;
        int empty = 0;
        int missing = 0;

        foreach (string entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) { empty++; continue; }
            string normalized = NormalizePathEntry(entry);
            if (!seen.Add(normalized)) duplicates++;
            string expanded = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (!Directory.Exists(expanded)) missing++;
        }

        if (duplicates > 0 || empty > 0)
            list.Add(new ShellHealthFinding(
                "PATH", "Warning",
                $"User PATH: duplicity {duplicates}, prázdné položky {empty}.",
                true));
        if (missing > 0)
            list.Add(new ShellHealthFinding(
                "PATH", "Info",
                $"User PATH obsahuje {missing} aktuálně neexistujících cest; nemažu je automaticky.",
                false));
    }

    private static void AnalyzeCmdAutorun(List<ShellHealthFinding> list)
    {
        string user = ReadAutorun(Registry.CurrentUser, CommandProcessorKeyPath);
        string machine = ReadAutorun(Registry.LocalMachine, @"SOFTWARE\Microsoft\Command Processor");
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(machine))
            list.Add(new ShellHealthFinding(
                "CMD AutoRun", "Warning",
                "Je nastavený HKCU i HKLM AutoRun; příkazy se mohou řetězit a zpomalovat start CMD.",
                false));
        if (!string.IsNullOrWhiteSpace(user) &&
            !user.Contains("clink", StringComparison.OrdinalIgnoreCase))
            list.Add(new ShellHealthFinding(
                "CMD AutoRun", "Info",
                "HKCU AutoRun obsahuje vlastní příkaz mimo Clink; ponecháno beze změny.",
                false));
    }    private static void AnalyzeTerminalJson(List<ShellHealthFinding> list)
    {
        foreach (string path in TerminalSettingsPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (Exception ex)
            {
                list.Add(new ShellHealthFinding(
                    "Windows Terminal", "Error",
                    $"{Path.GetFileName(path)} není validní JSON: {ex.Message}",
                    true));
            }
        }
    }

    private static async Task AnalyzePowerShellProfileAsync(
        List<ShellHealthFinding> list,
        CancellationToken token)
    {
        string profile = PowerShellCenterService.PowerShell7ProfilePath;
        if (!File.Exists(profile)) return;

        string escaped = profile.Replace("'", "''");
        string script =
            "$t=$null;$e=$null;" +
            "[System.Management.Automation.Language.Parser]::ParseFile('" + escaped + "',[ref]$t,[ref]$e)|Out-Null;" +
            "if($e.Count -gt 0){$e|ForEach-Object{$_.Message};exit 9}";
        string exe = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
            ? @"C:\Program Files\PowerShell\7\pwsh.exe"
            : "pwsh.exe";
        var result = await ProcessRunner.RunAsync(
            exe,
            "-NoLogo -NoProfile -NonInteractive -Command \"" + script + "\"",
            TimeSpan.FromSeconds(12),
            token).ConfigureAwait(false);
        if (!result.Success)
        {
            string message = (result.StdOut + " " + result.StdErr).Trim();
            if (message.Length > 240) message = message[..240];
            list.Add(new ShellHealthFinding(
                "PowerShell profile", "Error",
                string.IsNullOrWhiteSpace(message) ? "Profil obsahuje syntaktickou chybu." : message,
                false));
        }
    }    private static bool RepairUserPath()
    {
        string raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        string[] entries = raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();
        foreach (string entry in entries)
        {
            string normalized = NormalizePathEntry(entry);
            if (seen.Add(normalized)) cleaned.Add(entry.Trim());
        }

        string next = string.Join(';', cleaned);
        if (string.Equals(raw.Trim(';'), next, StringComparison.Ordinal)) return false;

        BackupStringOnce("UserPath", raw);
        Environment.SetEnvironmentVariable("PATH", next, EnvironmentVariableTarget.User);
        return true;
    }

    private static bool RepairTerminalJson()
    {
        bool changed = false;
        foreach (string path in TerminalSettingsPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch
            {
                string backup = path + ".shell-health.backup";
                if (!File.Exists(backup)) File.Copy(path, backup, false);
                File.WriteAllText(path, "{}");
                changed = true;
            }
        }
        return changed;
    }    private static string NormalizePathEntry(string entry) =>
        Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ReadAutorun(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue("AutoRun")?.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static void BackupStringOnce(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(BackupKeyPath, true)
            ?? throw new InvalidOperationException("Nelze vytvořit Shell Health backup.");
        if (key.GetValue(name + ".Saved") is int) return;
        key.SetValue(name + ".Saved", 1, RegistryValueKind.DWord);
        key.SetValue(name + ".Value", value ?? string.Empty, RegistryValueKind.String);
    }

    private static IEnumerable<string> TerminalSettingsPaths()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(local, @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(local, @"Microsoft\Windows Terminal\settings.json");
    }
}