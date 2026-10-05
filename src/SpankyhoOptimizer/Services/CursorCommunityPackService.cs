using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record CommunityCursorPack(
    string Id, string Name, string Category, string Author, string License, string Url, string Folder)
{
    public string? PreviewPath => ResolveRolePath("Arrow") ?? ResolveRolePath("Hand") ?? ResolveRolePath("IBeam") ?? ResolveRolePath("Wait");

    public string? ResolveRolePath(string role)
    {
        string ini = Path.Combine(Folder, "pack.ini");
        if (!File.Exists(ini)) return null;
        var values = CursorCommunityPackService.ReadIniInternal(ini);
        if (!values.TryGetValue(role, out string? file) || string.IsNullOrWhiteSpace(file)) return null;
        string full = Path.Combine(Folder, file);
        return File.Exists(full) ? full : null;
    }
}

public static class CursorCommunityPackService
{
    private const string CursorKeyPath = @"Control Panel\Cursors";
    private const string AppKeyPath = @"Software\SpankyhoOptimizer\Cursor";
    private const uint SpiSetCursors = 0x0057;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    private static string PersistentRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "Cursors",
        "Community");

    private static readonly string[] Roles =
    {
        "Arrow","Help","AppStarting","Wait","Crosshair","IBeam","NWPen","No",
        "SizeNS","SizeWE","SizeNWSE","SizeNESW","SizeAll","UpArrow","Hand","Pin","Person"
    };

    private static readonly Lazy<CommunityCursorPack[]> BundledPacks = new(LoadBundledPacksCore, true);

    public static IReadOnlyList<CommunityCursorPack> LoadBundledPacks() => BundledPacks.Value;

    private static CommunityCursorPack[] LoadBundledPacksCore()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Assets", "Tools", "CursorsCommunity", "Packs");
        if (!Directory.Exists(root)) return Array.Empty<CommunityCursorPack>();

        var list = new List<CommunityCursorPack>();
        foreach (string dir in Directory.GetDirectories(root))
        {
            string ini = Path.Combine(dir, "pack.ini");
            if (!File.Exists(ini)) continue;
            var values = ReadIni(ini);
            string id = Path.GetFileName(dir);
            list.Add(new CommunityCursorPack(
                id,
                Get(values, "name") ?? id,
                Get(values, "category") ?? "Community",
                Get(values, "author") ?? "Unknown",
                Get(values, "license") ?? "Unknown",
                Get(values, "url") ?? string.Empty,
                dir));
        }

        return list.OrderBy(x => x.Category).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static CommunityCursorPack StagePersistentPack(CommunityCursorPack pack)
    {
        string target = Path.Combine(PersistentRoot, pack.Id);
        Directory.CreateDirectory(target);

        foreach (string source in Directory.GetFiles(pack.Folder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(pack.Folder, source);
            string destination = Path.Combine(target, relative);
            string? parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            File.Copy(source, destination, true);
        }

        return pack with { Folder = target };
    }

    public static CommunityCursorPack? DetectAppliedPack()
    {
        foreach (CommunityCursorPack pack in LoadBundledPacks())
        {
            if (IsApplied(pack))
                return pack;
        }
        return null;
    }

    public static void SyncDetectedActivePackMetadata()
    {
        CommunityCursorPack? detected = DetectAppliedPack();
        if (detected == null)
            return;

        using var app = Registry.CurrentUser.CreateSubKey(AppKeyPath, true);
        app?.SetValue("CommunityPack", detected.Id, RegistryValueKind.String);
        app?.SetValue("CommunityPackName", detected.Name, RegistryValueKind.String);
        app?.SetValue("Skin", "WindowsDefault", RegistryValueKind.String);
    }

    public static void Apply(CommunityCursorPack pack)
    {
        CommunityCursorPack activePack = StagePersistentPack(pack);
        string ini = Path.Combine(activePack.Folder, "pack.ini");
        var values = ReadIni(ini);
        using var key = Registry.CurrentUser.CreateSubKey(CursorKeyPath, true)
            ?? throw new InvalidOperationException("Nelze otevřít Windows cursor registry.");

        int written = 0;
        foreach (string role in Roles)
        {
            string? file = Get(values, role);
            if (string.IsNullOrWhiteSpace(file)) continue;

            string full = Path.Combine(activePack.Folder, file);
            if (!File.Exists(full))
                throw new FileNotFoundException($"Community cursor pack '{activePack.Name}' odkazuje na chybějící soubor pro roli {role}.", full);

            key.SetValue(role, full, RegistryValueKind.String);
            written++;
        }

        if (written == 0)
            throw new InvalidOperationException("Vybraný community cursor pack neobsahuje žádné použitelné cursor role.");

        key.SetValue("", pack.Name, RegistryValueKind.String);
        key.SetValue("Scheme Source", 1, RegistryValueKind.DWord);

        if (!SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows cursor scheme refresh selhal.");

        if (!IsApplied(activePack))
            throw new InvalidOperationException("Windows nepotvrdil community cursor pack po Apply.");

        using var app = Registry.CurrentUser.CreateSubKey(AppKeyPath, true);
        app?.SetValue("CommunityPack", pack.Id, RegistryValueKind.String);
        app?.SetValue("CommunityPackName", pack.Name, RegistryValueKind.String);
        // Prevent the internal cursor startup watchdog from overwriting a verified
        // community pack with the previously selected bundled skin.
        app?.SetValue("Skin", "WindowsDefault", RegistryValueKind.String);
    }

    public static bool IsApplied(CommunityCursorPack pack)
    {
        if (IsAppliedFromFolder(pack, pack.Folder))
            return true;

        string persistentFolder = Path.Combine(PersistentRoot, pack.Id);
        if (!string.Equals(
                Path.GetFullPath(persistentFolder).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(pack.Folder).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) &&
            IsAppliedFromFolder(pack, persistentFolder))
            return true;

        return false;
    }

    private static bool IsAppliedFromFolder(CommunityCursorPack pack, string folder)
    {
        string ini = Path.Combine(folder, "pack.ini");
        if (!File.Exists(ini)) return false;

        var values = ReadIni(ini);
        using var key = Registry.CurrentUser.OpenSubKey(CursorKeyPath);
        if (key == null) return false;

        bool any = false;
        foreach (string role in Roles)
        {
            string? file = Get(values, role);
            if (string.IsNullOrWhiteSpace(file)) continue;

            string expected = Path.GetFullPath(Path.Combine(folder, file));
            string actual = key.GetValue(role)?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(actual))
                return false;
            if (!string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                return false;
            any = true;
        }

        return any;
    }

    public static string CurrentCommunityPackId
    {
        get
        {
            using var app = Registry.CurrentUser.OpenSubKey(AppKeyPath);
            return app?.GetValue("CommunityPack") as string ?? string.Empty;
        }
    }

    public static string CurrentCommunityPackName
    {
        get
        {
            using var app = Registry.CurrentUser.OpenSubKey(AppKeyPath);
            return app?.GetValue("CommunityPackName") as string ?? string.Empty;
        }
    }

    internal static Dictionary<string, string> ReadIniInternal(string path) => ReadIni(path);

    private static Dictionary<string, string> ReadIni(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            d[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return d;
    }

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
}
