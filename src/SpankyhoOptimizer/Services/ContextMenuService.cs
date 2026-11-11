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
    private const string PowerToolsRoot = @"Software\Classes\Directory\Background\shell\SpankyPowerTools";
    private const string PowerToolsClassRoot = @"Software\Classes\Spanky.PowerTools\shell";
    private const string TerminalPath = PowerToolsClassRoot + @"\01Terminal";
    private const string AdminTerminalPath = PowerToolsClassRoot + @"\02TerminalAdmin";
    private const string PowerShellPath = PowerToolsClassRoot + @"\03PowerShell";
    private const string AdminPowerShellPath = PowerToolsClassRoot + @"\04PowerShellAdmin";
    private const string CmdPath = PowerToolsClassRoot + @"\05Cmd";
    private const string SystemToolsRoot = @"Software\Classes\Directory\Background\shell\SpankySystemTools";
    private const string SystemToolsClassRoot = @"Software\Classes\Spanky.SystemTools\shell";
    private const string TaskManagerPath = SystemToolsClassRoot + @"\01TaskManager";
    private const string DeviceManagerPath = SystemToolsClassRoot + @"\02DeviceManager";
    private const string RestartExplorerPath = SystemToolsClassRoot + @"\03RestartExplorer";
    private const string AdvancedStartupPath = SystemToolsClassRoot + @"\04AdvancedStartup";
    private const string LegacyPowerShellHerePath = @"Software\Classes\Directory\Background\shell\PowerShell7Here";
    private const string LegacyPowerShellAdminPath = @"Software\Classes\Directory\Background\shell\PowerShell7Admin";
    private const string LegacyDirectoryPowerShellHerePath = @"Software\Classes\Directory\shell\PowerShell7Here";
    private const string OfficialPowerShellBackgroundMaskPath = @"Software\Classes\Directory\Background\shell\PowerShell7x64";
    private const string OfficialPowerShellDirectoryMaskPath = @"Software\Classes\Directory\shell\PowerShell7x64";
    private const string NotepadPath = @"Software\Classes\*\shell\SpankyNotepad";
    private const string CopyPath = @"Software\Classes\AllFilesystemObjects\shell\SpankyCopyPath";
    private const string TakePath = @"Software\Classes\*\shell\SpankyTakeOwnership";
    private const string HashPath = @"Software\Classes\*\shell\SpankySha256";
    private const string NewFilePath = @"Software\Classes\Directory\Background\shell\SpankyNewEmptyFile";
    private const string VsCodePath = @"Software\Classes\Directory\Background\shell\SpankyVsCode";
    private static readonly string[] StaleUserShellPaths =
    {
        @"Software\Classes\Directory\Background\shell\CmdHere",
        @"Software\Classes\Directory\Background\shell\WindowsTerminalHere",
        @"Software\Classes\Directory\Background\shell\AdvancedStartup",
        @"Software\Classes\Directory\Background\shell\RestartExplorer",
        @"Software\Classes\Directory\Background\shell\SpankyTerminal",
        @"Software\Classes\Directory\Background\shell\SpankyAdminTerminal",
        @"Software\Classes\Directory\Background\shell\SpankyPowerShell",
        @"Software\Classes\Directory\Background\shell\SpankyAdminPowerShell",
        @"Software\Classes\Directory\Background\shell\SpankyCmd",
        @"Software\Classes\Directory\Background\shell\SpankyTaskManager",
        @"Software\Classes\Directory\Background\shell\SpankyDeviceManager",
        @"Software\Classes\Directory\Background\shell\SpankyRestartExplorer",
        @"Software\Classes\Directory\Background\shell\SpankyAdvancedStartup",
        @"Software\Classes\Directory\shell\CopyFullPath",
        LegacyPowerShellHerePath,
        LegacyPowerShellAdminPath,
        LegacyDirectoryPowerShellHerePath
    };

    public static bool ClassicMenuEnabled() => ClassicMenuEnabledAt(ClassicClsid);

    private static bool ClassicMenuEnabledAt(string path)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path + @"\InprocServer32");
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
    public static bool TaskManagerEnabled() => CommandEnabled(TaskManagerPath);
    public static bool DeviceManagerEnabled() => CommandEnabled(DeviceManagerPath);
    private static bool CommandEnabled(string path) => CommandEnabledAt(path);

    private static bool CommandEnabledAt(string path)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path + @"\command");
        return k != null;
    }

    // After an application update, upgrade ONLY already-enabled Spankyho shell
    // verbs. Never enable a disabled item or overwrite unrelated Explorer verbs.
    public static void MigrateExistingNativeCommands()
    {
        static void Migrate(string keyPath, string expected, Action install)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath + @"\command");
            string? current = key?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(current) &&
                !string.Equals(current, expected, StringComparison.Ordinal)) install();
        }
        Migrate(CopyPath, NativeContextCommand("--context-copy-path", "%1"), () => SetCopyPath(true));
        Migrate(HashPath, NativeContextCommand("--context-sha256", "%1"), () => SetSha256(true));
        Migrate(NewFilePath, NativeContextCommand("--context-new-file", "%V"), () => SetNewEmptyFile(true));
        Migrate(AdminTerminalPath, NativeContextCommand("--context-admin-terminal", "%V"), () => SetAdminTerminalHere(true));
        Migrate(AdminPowerShellPath, NativeContextCommand("--context-admin-powershell", "%V"), () => SetAdminPowerShellHere(true));
        Migrate(AdvancedStartupPath, NativeContextCommand("--context-advanced-startup"), () => SetAdvancedStartup(true));
    }

    public static void SetClassicMenu(bool enabled) => SetClassicMenuAt(ClassicClsid, enabled);

    private static void SetClassicMenuAt(string path, bool enabled)
    {
        bool before = ClassicMenuEnabledAt(path);
        try
        {
            if (enabled) Registry.CurrentUser.CreateSubKey(path + @"\InprocServer32")?.SetValue(null, "", RegistryValueKind.String);
            else Registry.CurrentUser.DeleteSubKeyTree(path, false);
            if (ClassicMenuEnabledAt(path) != enabled) throw new InvalidOperationException("Read-back klasického kontextového menu nesouhlasí.");
        }
        catch
        {
            if (before) Registry.CurrentUser.CreateSubKey(path + @"\InprocServer32")?.SetValue(null, "", RegistryValueKind.String);
            else Registry.CurrentUser.DeleteSubKeyTree(path, false);
            throw;
        }
    }

    public static void SetTerminalHere(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        SetCommand(TerminalPath, "Otevřít Terminal zde", "wt.exe -d \"%V\"", enabled);
    }
    public static void SetCmdHere(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        SetCommand(CmdPath, "Otevřít CMD zde", "cmd.exe /s /k pushd \"%V\"", enabled);
    }
    private static string PowerShell7Path()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(path) ? path : "powershell.exe";
    }

    public static void SetPowerShellHere(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        string ps = PowerShell7Path();
        bool isPs7 = !string.Equals(ps, "powershell.exe", StringComparison.OrdinalIgnoreCase);
        SetCommand(PowerShellPath, isPs7 ? "Otevřít PowerShell 7 zde" : "Otevřít Windows PowerShell zde",
            $"\"{ps}\" -NoExit -NoProfile -Command Set-Location -LiteralPath \"%V\"", enabled);
    }

    public static void SetAdminPowerShellHere(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        SetCommand(AdminPowerShellPath, "PowerShell 7 jako správce zde",
            enabled ? NativeContextCommand("--context-admin-powershell", "%V") : string.Empty, enabled, true);
    }

    private static void NormalizePowerShellContextMenu() => OptimizeContextMenuExperience();

    public static void OptimizeContextMenuExperience()
    {
        SetClassicMenu(true);
        EnsureContextMenuTransparency();
        OptimizeContextMenuLayout();

        SetTerminalHere(true);
        SetAdminTerminalHere(true);
        SetPowerShellHere(true);
        SetAdminPowerShellHere(true);
        SetCmdHere(true);
        SetTaskManager(true);
        SetDeviceManager(true);
        SetRestartExplorer(true);
        SetAdvancedStartup(true);
    }

    public static void OptimizeContextMenuLayout()
    {
        foreach (string stale in StaleUserShellPaths)
            Registry.CurrentUser.DeleteSubKeyTree(stale, false);

        foreach (string maskPath in new[] { OfficialPowerShellBackgroundMaskPath, OfficialPowerShellDirectoryMaskPath })
        {
            using var key = Registry.CurrentUser.CreateSubKey(maskPath);
            key?.SetValue("LegacyDisable", "", RegistryValueKind.String);
            key?.SetValue("ProgrammaticAccessOnly", "", RegistryValueKind.String);
            key?.SetValue("ExtendedSubCommandsKey", "Spanky.Hidden.PowerShell7", RegistryValueKind.String);
            key?.DeleteValue("Extended", false);
        }

        EnsureHoverMenuRoots();
    }

    private static void EnsureHoverMenuRoots()
    {
        using (var power = Registry.CurrentUser.CreateSubKey(PowerToolsRoot))
        {
            power?.SetValue("MUIVerb", "Terminál a PowerShell", RegistryValueKind.String);
            power?.SetValue("Icon", "wt.exe", RegistryValueKind.String);
            power?.SetValue("ExtendedSubCommandsKey", "Spanky.PowerTools", RegistryValueKind.String);
            power?.DeleteValue("Extended", false);
        }

        using (var system = Registry.CurrentUser.CreateSubKey(SystemToolsRoot))
        {
            system?.SetValue("MUIVerb", "Systémové nástroje", RegistryValueKind.String);
            system?.SetValue("Icon", "taskmgr.exe", RegistryValueKind.String);
            system?.SetValue("ExtendedSubCommandsKey", "Spanky.SystemTools", RegistryValueKind.String);
            system?.DeleteValue("Extended", false);
        }
    }

    private static void EnsureContextMenuTransparency()
    {
        using var personalize = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        personalize?.SetValue("EnableTransparency", 1, RegistryValueKind.DWord);
        using var dwm = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM");
        dwm?.SetValue("EnableWindowColorization", 1, RegistryValueKind.DWord);
    }

    public static void SetAdminTerminalHere(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        SetCommand(AdminTerminalPath, "Terminal jako správce zde", enabled ? NativeContextCommand("--context-admin-terminal", "%V") : string.Empty, enabled, true);
    }
    public static void SetNotepad(bool enabled) => SetCommand(NotepadPath, "Otevřít v Poznámkovém bloku", "notepad.exe \"%1\"", enabled);
    private static string NativeContextCommand(string action, string? target = null)
    {
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SpankyhoOptimizer.exe");
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("SpankyhoOptimizer.exe", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(AppContext.BaseDirectory, "SpankyhoOptimizer.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Chybí nativní kontextový pomocník.", executable);
        return "\"" + executable + "\" " + action + (target == null ? string.Empty : " \"" + target + "\"");
    }

    public static void SetCopyPath(bool enabled) => SetCommand(CopyPath, "Kopírovat úplnou cestu", enabled ? NativeContextCommand("--context-copy-path", "%1") : string.Empty, enabled);
    public static void SetTakeOwnership(bool enabled)
    {
        SetCommand(TakePath, "Převzít vlastnictví", "cmd.exe /c takeown /f \"%1\" && icacls \"%1\" /grant *S-1-5-32-544:F", enabled, true);
    }
    public static void SetSha256(bool enabled)
    {
        SetCommand(HashPath, "SHA256 hash", enabled ? NativeContextCommand("--context-sha256", "%1") : string.Empty, enabled);
    }
    public static void SetNewEmptyFile(bool enabled) => SetCommand(NewFilePath, "Nový prázdný soubor", enabled ? NativeContextCommand("--context-new-file", "%V") : string.Empty, enabled);
    public static void SetVsCode(bool enabled)
    {
        string? code = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        string userCode = string.IsNullOrWhiteSpace(code) ? string.Empty : System.IO.Path.Combine(code, "Programs", "Microsoft VS Code", "Code.exe");
        string exe = System.IO.File.Exists(userCode) ? userCode : "code.exe";
        SetCommand(VsCodePath, "Otevřít ve VS Code", $"\"{exe}\" \"%V\"", enabled);
    }
    public static void SetAdvancedStartup(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        SetCommand(AdvancedStartupPath, "Pokročilé spuštění",
            enabled ? NativeContextCommand("--context-advanced-startup") : string.Empty, enabled, true);
    }

    public static void SetRestartExplorer(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        SetCommand(RestartExplorerPath, "Restartovat Explorer",
            "cmd.exe /c taskkill /f /im explorer.exe & start explorer.exe", enabled, true);
    }

    public static void SetTaskManager(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        SetCommand(TaskManagerPath, "Správce úloh", "taskmgr.exe", enabled);
    }

    public static void SetDeviceManager(bool enabled)
    {
        if (enabled) EnsureHoverMenuRoots();
        SetCommand(DeviceManagerPath, "Správce zařízení", "mmc.exe devmgmt.msc", enabled, true);
    }

    private static void SetCommand(string path, string label, string command, bool enabled, bool elevated = false)
        => SetCommandAt(path, label, command, enabled, elevated);

    private static void SetCommandAt(string path, string label, string command, bool enabled, bool elevated = false)
    {
        bool before = CommandEnabledAt(path);
        try
        {
            if (!enabled) Registry.CurrentUser.DeleteSubKeyTree(path, false);
            else
            {
                using var k = Registry.CurrentUser.CreateSubKey(path);
                k?.SetValue(null, label, RegistryValueKind.String);
                k?.SetValue("MUIVerb", label, RegistryValueKind.String);
                k?.DeleteValue("Extended", false);
                if (elevated) k?.SetValue("HasLUAShield", "", RegistryValueKind.String);
                else k?.DeleteValue("HasLUAShield", false);
                using var cmd = Registry.CurrentUser.CreateSubKey(path + @"\command");
                cmd?.SetValue(null, command, RegistryValueKind.String);
            }
            if (CommandEnabledAt(path) != enabled) throw new InvalidOperationException("Read-back položky kontextového menu nesouhlasí.");
        }
        catch
        {
            if (!before) Registry.CurrentUser.DeleteSubKeyTree(path, false);
            else
            {
                using var k = Registry.CurrentUser.CreateSubKey(path);
                k?.SetValue(null, label, RegistryValueKind.String);
                k?.SetValue("MUIVerb", label, RegistryValueKind.String);
                k?.DeleteValue("Extended", false);
                if (elevated) k?.SetValue("HasLUAShield", "", RegistryValueKind.String);
                else k?.DeleteValue("HasLUAShield", false);
                using var cmd = Registry.CurrentUser.CreateSubKey(path + @"\command");
                cmd?.SetValue(null, command, RegistryValueKind.String);
            }
            throw;
        }
    }

    // Exercise the real HKCU enable/disable/read-back helpers in a unique
    // app-owned test key. Never touch real Explorer/system menu paths.
    internal static string RunIsolatedPersistenceSmoke()
    {
        string root = @"Software\SpankyhoOptimizer\ContextMenuSmoke-" + Guid.NewGuid().ToString("N");
        string commandPath = root + @"\Command";
        string classicPath = root + @"\Classic";
        try
        {
            SetCommandAt(commandPath, "Spanky smoke", "cmd.exe /c exit 0", true);
            if (!CommandEnabledAt(commandPath))
                throw new InvalidOperationException("Context menu isolated command enable read-back failed.");
            using (var key = Registry.CurrentUser.OpenSubKey(commandPath))
            {
                if (!string.Equals(key?.GetValue("MUIVerb") as string, "Spanky smoke", StringComparison.Ordinal) ||
                    key?.GetValue("HasLUAShield") != null)
                    throw new InvalidOperationException("Context menu isolated command values are incorrect.");
            }
            using (var cmd = Registry.CurrentUser.OpenSubKey(commandPath + @"\command"))
            {
                if (!string.Equals(cmd?.GetValue(null) as string, "cmd.exe /c exit 0", StringComparison.Ordinal))
                    throw new InvalidOperationException("Context menu isolated command payload is incorrect.");
            }

            SetCommandAt(commandPath, "Spanky smoke", "cmd.exe /c exit 0", false);
            if (CommandEnabledAt(commandPath))
                throw new InvalidOperationException("Context menu isolated command disable read-back failed.");

            SetCommandAt(commandPath, "Spanky smoke", "cmd.exe /c exit 0", true, elevated: true);
            using (var key = Registry.CurrentUser.OpenSubKey(commandPath))
            {
                if (key?.GetValue("HasLUAShield") == null)
                    throw new InvalidOperationException("Context menu isolated elevated command marker is missing.");
            }
            SetCommandAt(commandPath, "Spanky smoke", "cmd.exe /c exit 0", false);

            SetClassicMenuAt(classicPath, true);
            if (!ClassicMenuEnabledAt(classicPath))
                throw new InvalidOperationException("Context menu isolated classic enable read-back failed.");
            SetClassicMenuAt(classicPath, false);
            if (ClassicMenuEnabledAt(classicPath))
                throw new InvalidOperationException("Context menu isolated classic disable read-back failed.");

            using (var leftoverCommand = Registry.CurrentUser.OpenSubKey(commandPath))
            using (var leftoverClassic = Registry.CurrentUser.OpenSubKey(classicPath))
            {
                if (leftoverCommand != null || leftoverClassic != null)
                    throw new InvalidOperationException("Context menu isolated smoke left real keys behind.");
            }

            return "CONTEXT_MENU_PERSISTENCE_ISOLATED_SMOKE_OK writeReadback=true cleanup=true actualUserSettingsTouched=false";
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(root, false); } catch { }
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
        ["Restart Explorer"] = RestartExplorerEnabled(),
        ["Task Manager"] = TaskManagerEnabled(),
        ["Device Manager"] = DeviceManagerEnabled()
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
        Registry.CurrentUser.DeleteSubKeyTree(TaskManagerPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(DeviceManagerPath, false);
        if (ClassicMenuEnabled() || TerminalHereEnabled() || CmdHereEnabled() || PowerShellHereEnabled() ||
            AdminPowerShellHereEnabled() || AdminTerminalHereEnabled() || NotepadEnabled() || CopyPathEnabled() || TakeOwnershipEnabled() ||
            Sha256Enabled() || NewEmptyFileEnabled() || VsCodeEnabled() || AdvancedStartupEnabled() || RestartExplorerEnabled() ||
            TaskManagerEnabled() || DeviceManagerEnabled())
            throw new InvalidOperationException("Obnova kontextového menu nebyla potvrzena read-backem.");
    }
}
