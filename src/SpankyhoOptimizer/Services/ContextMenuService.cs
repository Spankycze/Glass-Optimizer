using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record ContextHandlerInfo(string Name, string Clsid, string Location, bool Enabled);

public static class ContextMenuService
{
    private const string ClassicClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string BlockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string TerminalPath = @"Software\Classes\Directory\Background\shell\SpankyTerminal";
    private const string CmdPath = @"Software\Classes\Directory\Background\shell\SpankyCmd";
    private const string PowerShellPath = @"Software\Classes\Directory\Background\shell\SpankyPowerShell";
    private const string AdminPowerShellPath = @"Software\Classes\Directory\Background\shell\SpankyAdminPowerShell";
    private const string AdminTerminalPath = @"Software\Classes\Directory\Background\shell\SpankyAdminTerminal";
    private const string NotepadPath = @"Software\Classes\*\shell\SpankyNotepad";
    private const string CopyPath = @"Software\Classes\AllFilesystemObjects\shell\SpankyCopyPath";
    private const string TakePath = @"Software\Classes\*\shell\SpankyTakeOwnership";
    private const string HashPath = @"Software\Classes\*\shell\SpankySha256";
    private const string NewFilePath = @"Software\Classes\Directory\Background\shell\SpankyNewEmptyFile";
    private const string VsCodePath = @"Software\Classes\Directory\Background\shell\SpankyVsCode";
    private const string AdvancedStartupPath = @"Software\Classes\Directory\Background\shell\SpankyAdvancedStartup";
    private const string RestartExplorerPath = @"Software\Classes\Directory\Background\shell\SpankyRestartExplorer";

    public static bool ClassicMenuEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(ClassicClsid + @"\InprocServer32");
        return k != null;
    }

    public static bool TerminalHereEnabled() => CommandEnabled(TerminalPath);
    public static bool CmdHereEnabled() => CommandEnabled(CmdPath);
    public static bool PowerShellHereEnabled() => CommandEnabled(PowerShellPath);
    public static bool AdminPowerShellHereEnabled() => CommandEnabled(AdminPowerShellPath);
    public static bool AdminTerminalHereEnabled() => CommandEnabled(AdminTerminalPath);
    public static bool NotepadEnabled() => CommandEnabled(NotepadPath);
    public static bool CopyPathEnabled() => CommandEnabled(CopyPath);
    public static bool TakeOwnershipEnabled() => CommandEnabled(TakePath);
    public static bool Sha256Enabled() => CommandEnabled(HashPath);
    public static bool NewEmptyFileEnabled() => CommandEnabled(NewFilePath);
    public static bool VsCodeEnabled() => CommandEnabled(VsCodePath);
    public static bool AdvancedStartupEnabled() => CommandEnabled(AdvancedStartupPath);
    public static bool RestartExplorerEnabled() => CommandEnabled(RestartExplorerPath);
    private static bool CommandEnabled(string path)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path + @"\command");
        return k != null;
    }
    public static void SetClassicMenu(bool enabled)
    {
        bool before = ClassicMenuEnabled();
        try
        {
            if (enabled) Registry.CurrentUser.CreateSubKey(ClassicClsid + @"\InprocServer32")?.SetValue(null, "", RegistryValueKind.String);
            else Registry.CurrentUser.DeleteSubKeyTree(ClassicClsid, false);
            if (ClassicMenuEnabled() != enabled) throw new InvalidOperationException("Read-back klasického kontextového menu nesouhlasí.");
        }
        catch
        {
            if (before) Registry.CurrentUser.CreateSubKey(ClassicClsid + @"\InprocServer32")?.SetValue(null, "", RegistryValueKind.String);
            else Registry.CurrentUser.DeleteSubKeyTree(ClassicClsid, false);
            throw;
        }
    }

    public static void SetTerminalHere(bool enabled) => SetCommand(TerminalPath, "Otevřít Terminal zde", "wt.exe -d \"%V\"", enabled);
    public static void SetCmdHere(bool enabled) => SetCommand(CmdPath, "Otevřít CMD zde", "cmd.exe /s /k pushd \"%V\"", enabled);
    private static string PowerShell7Path()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(path) ? path : "powershell.exe";
    }

    public static void SetPowerShellHere(bool enabled)
    {
        string ps = PowerShell7Path();
        SetCommand(PowerShellPath, "Otevřít PowerShell 7 zde", $"\"{ps}\" -NoExit -NoProfile -Command Set-Location -LiteralPath \"%V\"", enabled);
    }

    public static void SetAdminPowerShellHere(bool enabled)
    {
        string ps = PowerShell7Path();
        SetCommand(AdminPowerShellPath, "PowerShell 7 jako správce zde",
            $"powershell.exe -NoProfile -WindowStyle Hidden -Command \"Start-Process '{ps}' -Verb RunAs -WorkingDirectory '%V'\"", enabled, true);
    }
    public static void SetAdminTerminalHere(bool enabled) => SetCommand(AdminTerminalPath, "Terminal jako správce zde", "powershell.exe -NoProfile -WindowStyle Hidden -Command \"Start-Process wt.exe -Verb RunAs -ArgumentList '-d','%V'\"", enabled, true);
    public static void SetNotepad(bool enabled) => SetCommand(NotepadPath, "Otevřít v Poznámkovém bloku", "notepad.exe \"%1\"", enabled);
    public static void SetCopyPath(bool enabled) => SetCommand(CopyPath, "Kopírovat úplnou cestu", "powershell.exe -NoProfile -WindowStyle Hidden -Command \"& { param([string]$p) Set-Clipboard -Value $p }\" \"%1\"", enabled);
    public static void SetTakeOwnership(bool enabled) => SetCommand(TakePath, "Převzít vlastnictví", "cmd.exe /c takeown /f \"%1\" && icacls \"%1\" /grant *S-1-5-32-544:F", enabled, true);
    public static void SetSha256(bool enabled) => SetCommand(HashPath, "SHA256 hash", "powershell.exe -NoProfile -Command \"Get-FileHash -Algorithm SHA256 -LiteralPath '%1' | Format-List\"", enabled);
    public static void SetNewEmptyFile(bool enabled) => SetCommand(NewFilePath, "Nový prázdný soubor", "powershell.exe -NoProfile -WindowStyle Hidden -Command \"$p=Join-Path '%V' 'New File.txt';$i=1;while(Test-Path $p){$p=Join-Path '%V' ('New File ('+$i+').txt');$i++};New-Item -ItemType File -Path $p|Out-Null\"", enabled);
    public static void SetVsCode(bool enabled)
    {
        string? code = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        string userCode = string.IsNullOrWhiteSpace(code) ? string.Empty : System.IO.Path.Combine(code, "Programs", "Microsoft VS Code", "Code.exe");
        string exe = System.IO.File.Exists(userCode) ? userCode : "code.exe";
        SetCommand(VsCodePath, "Otevřít ve VS Code", $"\"{exe}\" \"%V\"", enabled);
    }
    public static void SetAdvancedStartup(bool enabled)
    {
        SetCommand(AdvancedStartupPath, "Advanced startup…",
            "powershell.exe -NoProfile -Command \"Add-Type -AssemblyName PresentationFramework;if([System.Windows.MessageBox]::Show('Restart Windows into Advanced Startup?','Advanced Startup','YesNo','Question') -eq 'Yes'){shutdown.exe /r /o /t 0}\"", enabled, true);
        SetExtended(AdvancedStartupPath, enabled);
    }

    public static void SetRestartExplorer(bool enabled)
    {
        SetCommand(RestartExplorerPath, "Restartovat Explorer",
            "cmd.exe /c taskkill /f /im explorer.exe & start explorer.exe", enabled, true);
        SetExtended(RestartExplorerPath, enabled);
    }

    private static void SetExtended(string path, bool enabled)
    {
        if (!enabled) return;
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key?.SetValue("Extended", "", RegistryValueKind.String);
    }

    private static void SetCommand(string path, string label, string command, bool enabled, bool elevated = false)
    {
        bool before = CommandEnabled(path);
        try
        {
            if (!enabled) Registry.CurrentUser.DeleteSubKeyTree(path, false);
            else
            {
                using var k = Registry.CurrentUser.CreateSubKey(path);
                k?.SetValue(null, label, RegistryValueKind.String);
                if (elevated) k?.SetValue("HasLUAShield", "", RegistryValueKind.String);
                using var cmd = Registry.CurrentUser.CreateSubKey(path + @"\command");
                cmd?.SetValue(null, command, RegistryValueKind.String);
            }
            if (CommandEnabled(path) != enabled) throw new InvalidOperationException("Read-back položky kontextového menu nesouhlasí.");
        }
        catch
        {
            if (!before) Registry.CurrentUser.DeleteSubKeyTree(path, false);
            else
            {
                using var k = Registry.CurrentUser.CreateSubKey(path);
                k?.SetValue(null, label, RegistryValueKind.String);
                if (elevated) k?.SetValue("HasLUAShield", "", RegistryValueKind.String);
                using var cmd = Registry.CurrentUser.CreateSubKey(path + @"\command");
                cmd?.SetValue(null, command, RegistryValueKind.String);
            }
            throw;
        }
    }

    public static Task<IReadOnlyList<ContextHandlerInfo>> GetHandlersAsync() => Task.Run(() =>
    {
        var roots = new[]
        {
            (Registry.ClassesRoot, @"*\shellex\ContextMenuHandlers", "Soubory"),
            (Registry.ClassesRoot, @"Directory\shellex\ContextMenuHandlers", "Složky"),
            (Registry.ClassesRoot, @"Directory\Background\shellex\ContextMenuHandlers", "Pozadí složky")
        };
        var list = new List<ContextHandlerInfo>();
        using var blocked = Registry.CurrentUser.OpenSubKey(BlockedPath);
        foreach (var root in roots)
        {
            using var parent = root.Item1.OpenSubKey(root.Item2);
            if (parent == null) continue;
            foreach (string name in parent.GetSubKeyNames())
            {
                using var k = parent.OpenSubKey(name);
                string clsid = (k?.GetValue(null)?.ToString() ?? name).Trim();
                if (!clsid.StartsWith('{')) continue;
                bool enabled = blocked?.GetValue(clsid) == null;
                list.Add(new ContextHandlerInfo(name, clsid, root.Item3, enabled));
            }
        }
        return (IReadOnlyList<ContextHandlerInfo>)list.GroupBy(x => x.Clsid, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => x.Name).ToList();
    });
    public static void SetHandlerEnabled(string clsid, bool enabled)
    {
        bool before = IsHandlerEnabled(clsid);
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(BlockedPath);
            if (enabled) k?.DeleteValue(clsid, false); else k?.SetValue(clsid, "", RegistryValueKind.String);
            if (IsHandlerEnabled(clsid) != enabled) throw new InvalidOperationException("Read-back shell handleru nesouhlasí: " + clsid);
        }
        catch
        {
            using var k = Registry.CurrentUser.CreateSubKey(BlockedPath);
            if (before) k?.DeleteValue(clsid, false); else k?.SetValue(clsid, "", RegistryValueKind.String);
            throw;
        }
    }

    public static bool IsHandlerEnabled(string clsid)
    {
        using var k = Registry.CurrentUser.OpenSubKey(BlockedPath);
        return k?.GetValue(clsid) == null;
    }

    public static IReadOnlyDictionary<string, bool> ReadOurEntryStates() => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        ["Klasické menu"] = ClassicMenuEnabled(),
        ["Terminal"] = TerminalHereEnabled(),
        ["CMD"] = CmdHereEnabled(),
        ["PowerShell"] = PowerShellHereEnabled(),
        ["Admin PowerShell"] = AdminPowerShellHereEnabled(),
        ["Admin Terminal"] = AdminTerminalHereEnabled(),
        ["Notepad"] = NotepadEnabled(),
        ["Copy path"] = CopyPathEnabled(),
        ["Take Ownership"] = TakeOwnershipEnabled(),
        ["SHA256"] = Sha256Enabled(),
        ["New empty file"] = NewEmptyFileEnabled(),
        ["VS Code"] = VsCodeEnabled(),
        ["Advanced startup"] = AdvancedStartupEnabled(),
        ["Restart Explorer"] = RestartExplorerEnabled()
    };

    public static void RestoreOurEntries()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ClassicClsid, false);
        Registry.CurrentUser.DeleteSubKeyTree(TerminalPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(CmdPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(PowerShellPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(AdminPowerShellPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(AdminTerminalPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(NotepadPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(CopyPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(TakePath, false);
        Registry.CurrentUser.DeleteSubKeyTree(HashPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(NewFilePath, false);
        Registry.CurrentUser.DeleteSubKeyTree(VsCodePath, false);
        Registry.CurrentUser.DeleteSubKeyTree(AdvancedStartupPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(RestartExplorerPath, false);
        if (ClassicMenuEnabled() || TerminalHereEnabled() || CmdHereEnabled() || PowerShellHereEnabled() ||
            AdminPowerShellHereEnabled() || AdminTerminalHereEnabled() || NotepadEnabled() || CopyPathEnabled() || TakeOwnershipEnabled() ||
            Sha256Enabled() || NewEmptyFileEnabled() || VsCodeEnabled() || AdvancedStartupEnabled() || RestartExplorerEnabled())
            throw new InvalidOperationException("Obnova kontextového menu nebyla potvrzena read-backem.");
    }
}
