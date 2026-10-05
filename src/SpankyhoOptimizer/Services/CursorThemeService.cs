using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record CursorSkin(string Id, string Name, string Subtitle, string AccentHex, string Family);
public sealed record CursorDiagnostics(bool FilesValid, bool HotspotsValid, int Dpi, int EffectiveSize, string ActiveSkin, int EnabledRoles, int TotalRoles, bool StartupWatchdog, bool AutoDpi, bool AutoContrast);

public static class CursorThemeService
{
    private const string CursorKeyPath = @"Control Panel\Cursors";
    private const string AppKeyPath = @"Software\SpankyhoOptimizer\Cursor";
    private const uint ImageCursor = 2;
    private const uint LrLoadFromFile = 0x0010;
    private const uint SpiSetCursors = 0x0057;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    private static readonly (string File, string Registry, uint Ocr, string Label)[] CursorMap =
    {
        ("Arrow","Arrow",32512,"Pointer"), ("IBeam","IBeam",32513,"Text"), ("Wait","Wait",32514,"Busy"),
        ("Cross","Crosshair",32515,"Crosshair"), ("UpArrow","UpArrow",32516,"Up"),
        ("SizeNWSE","SizeNWSE",32642,"Resize NW-SE"), ("SizeNESW","SizeNESW",32643,"Resize NE-SW"),
        ("SizeWE","SizeWE",32644,"Resize W-E"), ("SizeNS","SizeNS",32645,"Resize N-S"),
        ("SizeAll","SizeAll",32646,"Move"), ("No","No",32648,"Unavailable"), ("Hand","Hand",32649,"Link"),
        ("AppStarting","AppStarting",32650,"Working"), ("Help","Help",32651,"Help")
    };

    public static IReadOnlyList<CursorSkin> Skins { get; } = new[]
    {
        new CursorSkin("BlackGlass", "Blue", "Čistý modrý edge.", "#4EA1FF", "Black Glass"),
        new CursorSkin("BlackGlassRed", "Red", "Červený performance edge.", "#FF3B5F", "Black Glass"),
        new CursorSkin("BlackGlassGreen", "Green", "Emerald edge.", "#35E38C", "Black Glass"),
        new CursorSkin("FullBlack", "Full Black", "Téměř čistě černý cursor.", "#6F7680", "Minimal"),
        new CursorSkin("MinimalWhite", "Minimal White", "Bílý cursor s tmavým obrysem.", "#AAB3BF", "Minimal"),
        new CursorSkin("NeonBlue", "Neon Blue", "Výraznější modrý neon.", "#008CFF", "Neon"),
        new CursorSkin("IceCyan", "Ice Cyan", "Ledově cyan tech varianta.", "#27E8F2", "Neon"),
        new CursorSkin("PurpleNeon", "Purple Neon", "Fialový neon.", "#A85CFF", "Neon"),
        new CursorSkin("OrangeLava", "Orange Lava", "Teplý orange accent.", "#FF7A1A", "Neon")
    };

    public static IReadOnlyList<string> RoleIds => CursorMap.Select(x => x.File).ToArray();
    public static IReadOnlyList<(string Id, string Label)> Roles => CursorMap.Select(x => (x.File, x.Label)).ToArray();

    public static string CurrentSkin
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(AppKeyPath); return k?.GetValue("Skin") as string ?? "WindowsDefault"; }
    }

    public static int CurrentSize
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(AppKeyPath); return Convert.ToInt32(k?.GetValue("Size") ?? 48); }
    }

    public static bool AutoDpi
    {
        get => ReadBool("AutoDpi", true);
        set => WriteBool("AutoDpi", value);
    }

    public static bool AutoContrast
    {
        get => ReadBool("AutoContrast", false);
        set => WriteBool("AutoContrast", value);
    }

    public static bool StartupWatchdog
    {
        get => ReadBool("StartupWatchdog", true);
        set => WriteBool("StartupWatchdog", value);
    }

    public static bool IsRoleEnabled(string role) => ReadBool("Role." + role, true);
    public static void SetRoleEnabled(string role, bool enabled) => WriteBool("Role." + role, enabled);

    public static bool IsFavorite(string skinId)
    {
        using var k = Registry.CurrentUser.OpenSubKey(AppKeyPath);
        string raw = k?.GetValue("Favorites") as string ?? string.Empty;
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(skinId, StringComparer.OrdinalIgnoreCase);
    }

    public static void ToggleFavorite(string skinId)
    {
        using var k = Registry.CurrentUser.CreateSubKey(AppKeyPath, true);
        var set = new HashSet<string>((k?.GetValue("Favorites") as string ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        if (!set.Add(skinId)) set.Remove(skinId);
        k?.SetValue("Favorites", string.Join(';', set), RegistryValueKind.String);
    }

    public static string ResolveSkinForContrast(string skinId)
    {
        if (!AutoContrast) return skinId;
        using var p = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool appsLight = Convert.ToInt32(p?.GetValue("AppsUseLightTheme") ?? 0) != 0;
        if (appsLight && string.Equals(skinId, "MinimalWhite", StringComparison.OrdinalIgnoreCase)) return "FullBlack";
        if (!appsLight && string.Equals(skinId, "FullBlack", StringComparison.OrdinalIgnoreCase)) return "MinimalWhite";
        return skinId;
    }

    public static int GetSystemDpi()
    {
        try { int dpi = (int)GetDpiForSystem(); return dpi > 0 ? dpi : 96; }
        catch { return 96; }
    }

    public static int ResolveSize(int requested)
    {
        if (!AutoDpi) return requested <= 32 ? 32 : requested <= 48 ? 48 : 64;
        int dpi = GetSystemDpi();
        return dpi <= 120 ? 32 : dpi <= 168 ? 48 : 64;
    }

    public static void Apply(string skinId, int size)
    {
        // Explicit Apply must preserve the exact skin selected by the user.
        // Automatic contrast may inform UI, but it must never substitute another skin.
        size = ResolveSize(size);
        string root = Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors", skinId);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Cursor skin nebyl nalezen: " + root);

        ValidatePack(root);
        WriteScheme(root, skinId, size);

        foreach (var item in CursorMap)
        {
            if (!IsRoleEnabled(item.File)) continue;
            string animated = Path.Combine(root, item.File + ".ani");
            string path = File.Exists(animated) ? animated : Path.Combine(root, item.File + ".cur");
            ApplyOne(path, item.File, item.Ocr, size, File.Exists(animated));
        }

        using var app = Registry.CurrentUser.CreateSubKey(AppKeyPath, true);
        app?.SetValue("Skin", skinId, RegistryValueKind.String);
        app?.SetValue("Size", size, RegistryValueKind.DWord);
        app?.DeleteValue("CommunityPack", false);
        app?.DeleteValue("CommunityPackName", false);

        _ = SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange);
    }

    public static void PreviewRole(string skinId, string role, int size)
    {
        var item = CursorMap.FirstOrDefault(x => string.Equals(x.File, role, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(item.File)) return;
        string root = Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors", ResolveSkinForContrast(skinId));
        string animated = Path.Combine(root, item.File + ".ani");
        string path = File.Exists(animated) ? animated : Path.Combine(root, item.File + ".cur");
        ApplyOne(path, item.File, item.Ocr, ResolveSize(size), File.Exists(animated));
    }

    public static void EnsureCurrentApplied()
    {
        // Watchdog is verification-only. Never re-apply or overwrite the user's
        // active Windows cursor scheme automatically.
        if (!StartupWatchdog) return;
        _ = CurrentSkin;
        _ = CurrentSize;
    }

    public static string RunWatchdogNonMutatingSmoke()
    {
        using var beforeKey = Registry.CurrentUser.OpenSubKey(CursorKeyPath);
        var before = CursorMap.ToDictionary(
            item => item.Registry,
            item => beforeKey?.GetValue(item.Registry)?.ToString() ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        string beforeScheme = beforeKey?.GetValue(string.Empty)?.ToString() ?? string.Empty;

        EnsureCurrentApplied();

        using var afterKey = Registry.CurrentUser.OpenSubKey(CursorKeyPath);
        foreach (var item in CursorMap)
        {
            string after = afterKey?.GetValue(item.Registry)?.ToString() ?? string.Empty;
            if (!string.Equals(before[item.Registry], after, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Cursor watchdog změnil roli {item.Registry}.");
        }

        string afterScheme = afterKey?.GetValue(string.Empty)?.ToString() ?? string.Empty;
        if (!string.Equals(beforeScheme, afterScheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cursor watchdog změnil aktivní Windows cursor scheme.");

        return $"CURSOR_WATCHDOG_NON_MUTATING_OK roles={CursorMap.Length}";
    }

    public static void RefreshCurrentScheme()
    {
        _ = SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange);
    }

    public static CursorDiagnostics GetDiagnostics(string skinId, int requestedSize)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors", ResolveSkinForContrast(skinId));
        bool files = Directory.Exists(root) && CursorMap.All(x => File.Exists(Path.Combine(root, x.File + ".cur")));
        bool hotspots = files && CursorMap.All(x => HotspotValid(Path.Combine(root, x.File + ".cur")));
        return new CursorDiagnostics(
            files, hotspots, GetSystemDpi(), ResolveSize(requestedSize), CurrentSkin,
            CursorMap.Count(x => IsRoleEnabled(x.File)), CursorMap.Length,
            StartupWatchdog, AutoDpi, AutoContrast);
    }

    public static void RestoreWindowsDefault()
    {
        using var key = Registry.CurrentUser.CreateSubKey(CursorKeyPath, true)
            ?? throw new InvalidOperationException("Nelze otevřít Windows cursor registry.");
        foreach (var item in CursorMap) key.SetValue(item.Registry, string.Empty, RegistryValueKind.String);
        key.SetValue("NWPen", string.Empty, RegistryValueKind.String);
        key.SetValue("", string.Empty, RegistryValueKind.String);
        key.SetValue("CursorBaseSize", 32, RegistryValueKind.DWord);

        using var app = Registry.CurrentUser.CreateSubKey(AppKeyPath, true);
        app?.SetValue("Skin", "WindowsDefault", RegistryValueKind.String);
        app?.SetValue("Size", 32, RegistryValueKind.DWord);
        app?.DeleteValue("CommunityPack", false);
        app?.DeleteValue("CommunityPackName", false);

        if (!SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows default kurzory se nepodařilo obnovit.");
    }

    private static void ValidatePack(string root)
    {
        foreach (var item in CursorMap)
        {
            string path = Path.Combine(root, item.File + ".cur");
            if (!File.Exists(path)) throw new FileNotFoundException("Chybí cursor soubor: " + item.File + ".cur", path);
            nint h = LoadImage(IntPtr.Zero, path, ImageCursor, 0, 0, LrLoadFromFile);
            if (h == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Neplatný cursor soubor: " + item.File + ".cur");
            DestroyCursor(h);
        }
    }

    private static bool HotspotValid(string path)
    {
        try
        {
            byte[] h = new byte[14];
            using var fs = File.OpenRead(path);
            if (fs.Read(h, 0, h.Length) != h.Length) return false;
            int x = BitConverter.ToUInt16(h, 10);
            int y = BitConverter.ToUInt16(h, 12);
            return x >= 0 && x < 64 && y >= 0 && y < 64;
        }
        catch { return false; }
    }

    private static void WriteScheme(string root, string skinId, int size)
    {
        using var key = Registry.CurrentUser.CreateSubKey(CursorKeyPath, true)
            ?? throw new InvalidOperationException("Nelze otevřít Windows cursor registry.");
        key.SetValue("", "Spankyho Optimizer - " + skinId, RegistryValueKind.String);
        foreach (var item in CursorMap)
        {
            if (!IsRoleEnabled(item.File))
            {
                key.SetValue(item.Registry, string.Empty, RegistryValueKind.String);
                continue;
            }
            string animated = Path.Combine(root, item.File + ".ani");
            key.SetValue(item.Registry, File.Exists(animated) ? animated : Path.Combine(root, item.File + ".cur"), RegistryValueKind.ExpandString);
        }
        key.SetValue("NWPen", IsRoleEnabled("Cross") ? Path.Combine(root, "Cross.cur") : string.Empty, RegistryValueKind.ExpandString);
        key.SetValue("CursorBaseSize", size, RegistryValueKind.DWord);
    }

    private static void ApplyOne(string path, string role, uint ocr, int size, bool animated)
    {
        nint loaded = animated
            ? LoadCursorFromFile(path)
            : LoadImage(IntPtr.Zero, path, ImageCursor, size, size, LrLoadFromFile);
        if (loaded == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Načtení cursoru selhalo pro " + role + ".");

        try
        {
            nint copy = CopyImage(loaded, ImageCursor, size, size, 0);
            if (copy == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CopyImage selhal pro " + role + ".");
            if (!SetSystemCursor(copy, ocr))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetSystemCursor selhal pro " + role + ".");
        }
        finally
        {
            DestroyCursor(loaded);
        }
    }

    private static bool ReadBool(string name, bool fallback)
    {
        using var k = Registry.CurrentUser.OpenSubKey(AppKeyPath);
        object? v = k?.GetValue(name);
        return v == null ? fallback : Convert.ToInt32(v) != 0;
    }

    private static void WriteBool(string name, bool value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(AppKeyPath, true);
        k?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadCursorFromFile(string fileName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CopyImage(nint h, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemCursor(nint hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}

