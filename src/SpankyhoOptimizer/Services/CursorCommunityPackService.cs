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

    private static List<(string Role, string Path)> ResolveValidatedRoles(string folder)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string ini = Path.Combine(folder, "pack.ini");
        var values = ReadIni(ini);
        var result = new List<(string Role, string Path)>();
        foreach (string role in Roles)
        {
            string? file = Get(values, role);
            if (string.IsNullOrWhiteSpace(file)) continue;

            if (Path.IsPathRooted(file) || file.IndexOf(':') >= 0 ||
                file.IndexOf('\0') >= 0)
                throw new InvalidDataException($"Kurzor {role} odkazuje na neplatnou cestu.");
            string full = Path.GetFullPath(Path.Combine(folder, file));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Kurzor {role} odkazuje mimo balíček.");
            if (!File.Exists(full))
                throw new FileNotFoundException($"Chybí kurzor {role} v balíčku.", full);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Kurzor {role} odkazuje na symbolický odkaz.");
            // Also prevent traversal through a symlinked directory.
            string? directory = Path.GetDirectoryName(full);
            while (directory != null &&
                   directory.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length)
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Kurzor {role} používá přesměrovanou složku.");
                if (string.Equals(directory, root.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) break;
                directory = Path.GetDirectoryName(directory);
            }
            result.Add((role, full));
        }
        if (result.Count == 0)
            throw new InvalidDataException("Balíček neobsahuje žádné platné role kurzorů.");
        return result;
    }

    internal static string RunCommunityCursorAtomicSmoke()
    {
        string parent = Path.Combine(Path.GetTempPath(),
            "spanky-cursor-atomic-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "pack");
        string outside = Path.Combine(parent, "outside.cur");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "arrow.cur"), "dummy cursor bytes");
            File.WriteAllText(Path.Combine(root, "pack.ini"),
                "Arrow=arrow.cur" + Environment.NewLine + "Hand=missing.cur");
            bool missingRejected = false;
            try { _ = ResolveValidatedRoles(root); }
            catch (FileNotFoundException) { missingRejected = true; }
            if (!missingRejected)
                throw new InvalidOperationException(
                    "Community pack missing a later cursor role; existing code would partially overwrite registry.");

            File.WriteAllText(Path.Combine(root, "pack.ini"),
                "Arrow=arrow.cur" + Environment.NewLine + "Hand=../outside.cur");
            File.WriteAllText(outside, "dummy");
            bool traversalRejected = false;
            try { _ = ResolveValidatedRoles(root); }
            catch (InvalidDataException) { traversalRejected = true; }
            if (!traversalRejected)
                throw new InvalidOperationException("Community cursor path traversal was accepted.");

            var plan = new[]
            {
                new CursorMutation(false, "Arrow", "new-arrow.cur", RegistryValueKind.String),
                new CursorMutation(true, "Skin", "WindowsDefault", RegistryValueKind.String)
            };
            string Key(CursorMutation change) => (change.Metadata ? "M:" : "C:") + change.Name;

            void VerifyRollback(bool failAfterWrite)
            {
                var originals = new Dictionary<string, CursorSavedValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["C:Arrow"] = new CursorSavedValue(true, "old-arrow.cur", RegistryValueKind.ExpandString)
                };
                var values = new Dictionary<string, CursorSavedValue>(originals, StringComparer.OrdinalIgnoreCase);
                bool injected = false;
                int rollbackRefreshes = 0;
                bool faulted = false;
                try
                {
                    ApplyCursorMutationBatch(
                        plan,
                        change => values.TryGetValue(Key(change), out var prior)
                            ? prior : new CursorSavedValue(false, null, RegistryValueKind.String),
                        change =>
                        {
                            values[Key(change)] = new CursorSavedValue(true, change.Value, change.Kind);
                            if (failAfterWrite && change.Metadata && !injected)
                            {
                                injected = true;
                                throw new IOException("synthetic post-write registry failure");
                            }
                        },
                        change => { values.Remove(Key(change)); },
                        () =>
                        {
                            if (!failAfterWrite)
                                throw new InvalidOperationException("synthetic scheme confirmation failure");
                        },
                        () => rollbackRefreshes++);
                }
                catch (IOException) when (failAfterWrite) { faulted = true; }
                catch (InvalidOperationException) when (!failAfterWrite) { faulted = true; }

                if (!faulted || rollbackRefreshes != 1 ||
                    values.Count != 1 || !values.TryGetValue("C:Arrow", out var retained) ||
                    (string?)retained.Value != "old-arrow.cur" ||
                    retained.Kind != RegistryValueKind.ExpandString)
                    throw new InvalidOperationException(
                        "Community cursor rollback lost an existing cursor or app metadata.");
            }
            VerifyRollback(failAfterWrite: true);
            VerifyRollback(failAfterWrite: false);

            return "COMMUNITY_CURSOR_ATOMIC_SMOKE_OK missingRoleRejected=true traversalRejected=true " +
                   "postWriteRollback=true refreshFailureRollback=true registryWrites=0";
        }
        finally
        {
            try { Directory.Delete(parent, true); } catch { }
        }
    }

    private readonly record struct CursorMutation(
        bool Metadata, string Name, object Value, RegistryValueKind Kind);

    private readonly record struct CursorSavedValue(
        bool Exists, object? Value, RegistryValueKind Kind);

    // Commit cursor roles AND app metadata together. In case a registry write,
    // Windows scheme refresh or verification fails, restore all previously
    // written values (including their original registry value kinds).
    private static void ApplyCursorMutationBatch(
        IReadOnlyList<CursorMutation> changes,
        Func<CursorMutation, CursorSavedValue> readOriginal,
        Action<CursorMutation> write,
        Action<CursorMutation> remove,
        Action verifyAndRefresh,
        Action restoreRefresh)
    {
        var original = new CursorSavedValue[changes.Count];
        for (int i = 0; i < changes.Count; i++)
            original[i] = readOriginal(changes[i]);

        int attempted = 0;
        try
        {
            for (int i = 0; i < changes.Count; i++)
            {
                attempted = i + 1; // a native registry write may mutate then throw
                write(changes[i]);
            }
            verifyAndRefresh();
        }
        catch (Exception primary)
        {
            var recoveryErrors = new List<Exception>();
            for (int i = attempted - 1; i >= 0; i--)
            {
                try
                {
                    if (original[i].Exists)
                        write(changes[i] with
                        {
                            Value = original[i].Value!,
                            Kind = original[i].Kind
                        });
                    else
                        remove(changes[i]);
                }
                catch (Exception recoveryError) { recoveryErrors.Add(recoveryError); }
            }
            if (attempted > 0)
            {
                try { restoreRefresh(); }
                catch (Exception recoveryError) { recoveryErrors.Add(recoveryError); }
            }
            if (recoveryErrors.Count > 0)
            {
                recoveryErrors.Insert(0, primary);
                throw new AggregateException(
                    "Community kurzory selhaly a původní schéma se nepodařilo plně obnovit.",
                    recoveryErrors);
            }
            throw;
        }
    }

    private static CursorSavedValue ReadCursorRegistryValue(RegistryKey key, string name)
    {
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return new CursorSavedValue(false, null, RegistryValueKind.String);
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value == null)
            throw new InvalidDataException($"Nelze zálohovat hodnotu kurzoru: {name}");
        return new CursorSavedValue(true, value, key.GetValueKind(name));
    }

    public static void Apply(CommunityCursorPack pack)
    {
        // Validate *all* source roles before staging files or touching HKCU.
        // Previously Arrow could be written, then a missing Hand would throw,
        // leaving Windows with a partly applied cursor scheme.
        _ = ResolveValidatedRoles(pack.Folder);
        CommunityCursorPack activePack = StagePersistentPack(pack);
        var resolved = ResolveValidatedRoles(activePack.Folder);

        using var key = Registry.CurrentUser.CreateSubKey(CursorKeyPath, true)
            ?? throw new InvalidOperationException("Nelze otevřít Windows cursor registry.");
        using var app = Registry.CurrentUser.CreateSubKey(AppKeyPath, true)
            ?? throw new InvalidOperationException("Nelze otevřít metadata community kurzorů.");

        var changes = new List<CursorMutation>(resolved.Count + 5);
        foreach (var (role, path) in resolved)
            changes.Add(new CursorMutation(false, role, path, RegistryValueKind.String));
        changes.Add(new CursorMutation(false, "", pack.Name, RegistryValueKind.String));
        changes.Add(new CursorMutation(false, "Scheme Source", 1, RegistryValueKind.DWord));
        changes.Add(new CursorMutation(true, "CommunityPack", pack.Id, RegistryValueKind.String));
        changes.Add(new CursorMutation(true, "CommunityPackName", pack.Name, RegistryValueKind.String));
        // Prevent the cursor startup watchdog from overriding a verified pack.
        changes.Add(new CursorMutation(true, "Skin", "WindowsDefault", RegistryValueKind.String));

        RegistryKey SelectKey(CursorMutation change) => change.Metadata ? app : key;
        void Refresh()
        {
            // Avoid synchronous WM_SETTINGCHANGE broadcasts to unrelated apps.
            if (!SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile))
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "Windows cursor scheme refresh selhal.");
        }

        ApplyCursorMutationBatch(
            changes,
            change => ReadCursorRegistryValue(SelectKey(change), change.Name),
            change => SelectKey(change).SetValue(change.Name, change.Value, change.Kind),
            change => SelectKey(change).DeleteValue(change.Name, throwOnMissingValue: false),
            () =>
            {
                Refresh();
                if (!IsApplied(activePack))
                    throw new InvalidOperationException(
                        "Windows nepotvrdil community cursor pack po Apply.");
            },
            Refresh);
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

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
}
