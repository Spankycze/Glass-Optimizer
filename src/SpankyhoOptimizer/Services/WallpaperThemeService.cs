using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public enum SpankyWallpaperTheme
{
    DarkWave,
    NeonWave,
    PurpleValley
}

public static class WallpaperThemeService
{
    private const uint SpiSetDeskWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendWinIniChange = 0x0002;

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spankyho Optimizer", "Wallpapers");

    private static string DarkWavePath => Path.Combine(Root, "Spanky-Dark-Wave.jpg");
    private static string NeonWavePath => Path.Combine(Root, "Windows-Neon-Wave.jpg");
    private static string PurpleValleyPath => Path.Combine(Root, "Windows-Purple-Valley.jpg");
    private static string DarkWaveSource => Path.Combine(AppContext.BaseDirectory, "Assets", "Wallpapers", "Spanky-Dark-Wave.jpg");
    private static string NeonWaveSource => Path.Combine(AppContext.BaseDirectory, "Assets", "Wallpapers", "Windows-Neon-Wave.jpg");
    private static string PurpleValleySource => Path.Combine(AppContext.BaseDirectory, "Assets", "Wallpapers", "Windows-Purple-Valley.jpg");

    public static string GetPreviewSource(SpankyWallpaperTheme theme) => theme switch
    {
        SpankyWallpaperTheme.DarkWave => DarkWaveSource,
        SpankyWallpaperTheme.NeonWave => NeonWaveSource,
        SpankyWallpaperTheme.PurpleValley => PurpleValleySource,
        _ => NeonWaveSource
    };

    public static string CurrentWallpaper
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                return key?.GetValue("WallPaper") as string ?? string.Empty;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                LogService.Error("themes", "Wallpaper registry read failed", ex);
                return string.Empty;
            }
        }
    }

    public static SpankyWallpaperTheme? CurrentTheme
    {
        get
        {
            string current = CurrentWallpaper;
            if (string.Equals(current, DarkWavePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current, DarkWaveSource, StringComparison.OrdinalIgnoreCase))
                return SpankyWallpaperTheme.DarkWave;
            if (string.Equals(current, NeonWavePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current, NeonWaveSource, StringComparison.OrdinalIgnoreCase))
                return SpankyWallpaperTheme.NeonWave;
            if (string.Equals(current, PurpleValleyPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current, PurpleValleySource, StringComparison.OrdinalIgnoreCase))
                return SpankyWallpaperTheme.PurpleValley;
            return null;
        }
    }

    private readonly record struct WallpaperChange(
        bool History, string Name, object Value, RegistryValueKind Kind);

    private readonly record struct WallpaperSaved(
        bool Exists, object? Value, RegistryValueKind Kind);

    private static void CommitWallpaperChanges(
        IReadOnlyList<WallpaperChange> changes,
        Func<WallpaperChange, WallpaperSaved> read,
        Action<WallpaperChange> write,
        Action<WallpaperChange> remove,
        Action applySystem,
        Action restoreSystem)
    {
        var original = new WallpaperSaved[changes.Count];
        // Snapshot ALL fields before the first write, including registry kinds
        // and the fact that a value was absent in the user's original theme.
        for (int i = 0; i < changes.Count; i++)
            original[i] = read(changes[i]);

        int attempted = 0;
        try
        {
            for (int i = 0; i < changes.Count; i++)
            {
                attempted = i + 1; // a native setter may mutate before throwing
                write(changes[i]);
            }
            applySystem();
        }
        catch (Exception primary)
        {
            var errors = new List<Exception>();
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
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            if (attempted > 0)
            {
                try { restoreSystem(); }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            if (errors.Count != 0)
            {
                errors.Insert(0, primary);
                throw new AggregateException(
                    "Aplikování tapety selhalo a původní nastavení se nepodařilo plně obnovit.",
                    errors);
            }
            throw;
        }
    }

    private static WallpaperSaved ReadOriginalWallpaperValue(RegistryKey key, string name)
    {
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return new WallpaperSaved(false, null, RegistryValueKind.String);
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value == null)
            throw new InvalidDataException("Nelze zálohovat původní nastavení tapety: " + name);
        return new WallpaperSaved(true, value, key.GetValueKind(name));
    }

    internal static string RunWallpaperRollbackSmoke()
    {
        WallpaperChange[] changes =
        {
            new(false, "WallPaper", "new.jpg", RegistryValueKind.String),
            new(false, "WallpaperStyle", "10", RegistryValueKind.String),
            new(false, "TileWallpaper", "0", RegistryValueKind.String),
            new(false, "JPEGImportQuality", 100, RegistryValueKind.DWord),
            new(true, "BackgroundType", 0, RegistryValueKind.DWord),
            new(true, "BackgroundHistoryPath0", "new.jpg", RegistryValueKind.String)
        };

        static string Key(WallpaperChange change) =>
            (change.History ? "H:" : "D:") + change.Name;

        void Case(bool failDuringWrite)
        {
            var initial = new Dictionary<string, WallpaperSaved>(StringComparer.OrdinalIgnoreCase)
            {
                ["D:WallPaper"] = new(true, "old.jpg", RegistryValueKind.ExpandString),
                ["D:WallpaperStyle"] = new(true, "6", RegistryValueKind.String),
                ["D:JPEGImportQuality"] = new(true, 85, RegistryValueKind.DWord),
                ["H:BackgroundType"] = new(true, 1, RegistryValueKind.DWord),
                ["H:BackgroundHistoryPath0"] = new(true, "history.jpg", RegistryValueKind.String)
            };
            var values = new Dictionary<string, WallpaperSaved>(initial, StringComparer.OrdinalIgnoreCase);
            int recoveryCount = 0;
            bool injected = false;
            bool rejected = false;
            try
            {
                CommitWallpaperChanges(
                    changes,
                    change => values.TryGetValue(Key(change), out var existing)
                        ? existing : new WallpaperSaved(false, null, RegistryValueKind.String),
                    change =>
                    {
                        values[Key(change)] = new WallpaperSaved(true, change.Value, change.Kind);
                        if (failDuringWrite && change.History &&
                            change.Name == "BackgroundHistoryPath0" && !injected)
                        {
                            injected = true;
                            throw new IOException("synthetic write succeeded then reported failure");
                        }
                    },
                    change => { values.Remove(Key(change)); },
                    () =>
                    {
                        if (!failDuringWrite)
                            throw new InvalidOperationException("synthetic Windows wallpaper confirmation failure");
                    },
                    () => recoveryCount++);
            }
            catch (IOException) when (failDuringWrite) { rejected = true; }
            catch (InvalidOperationException) when (!failDuringWrite) { rejected = true; }

            if (!rejected || recoveryCount != 1 || values.Count != initial.Count ||
                initial.Any(item =>
                    !values.TryGetValue(item.Key, out var restored) ||
                    !Equals(restored.Value, item.Value.Value) ||
                    restored.Kind != item.Value.Kind))
                throw new InvalidOperationException(
                    "Failed wallpaper apply lost original path/style/history or registry value types.");
        }

        Case(failDuringWrite: true);
        Case(failDuringWrite: false);
        return "WALLPAPER_TRANSACTION_SMOKE_OK postWriteRollback=true " +
            "systemRejectRollback=true metadataKindsPreserved=true registryWrites=0";
    }

    public static void Apply(SpankyWallpaperTheme theme)
    {
        Directory.CreateDirectory(Root);

        string source = theme switch
        {
            SpankyWallpaperTheme.DarkWave => DarkWaveSource,
            SpankyWallpaperTheme.NeonWave => NeonWaveSource,
            SpankyWallpaperTheme.PurpleValley => PurpleValleySource,
            _ => NeonWaveSource
        };
        string target = theme switch
        {
            SpankyWallpaperTheme.DarkWave => DarkWavePath,
            SpankyWallpaperTheme.NeonWave => NeonWavePath,
            SpankyWallpaperTheme.PurpleValley => PurpleValleyPath,
            _ => NeonWavePath
        };
        // First verify/copy packaged image, then snapshot all Windows values.
        // A missing installation asset must never partially change the theme.
        EnsurePackagedImage(source, target);
        CaptureOriginalWallpaperOnce();

        using var desktop = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop")
            ?? throw new InvalidOperationException("Nelze otevřít nastavení plochy.");
        using var wallpapers = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers")
            ?? throw new InvalidOperationException("Nelze otevřít historii tapet.");

        var changes = new WallpaperChange[]
        {
            new(false, "WallPaper", target, RegistryValueKind.String),
            new(false, "WallpaperStyle", "10", RegistryValueKind.String),
            new(false, "TileWallpaper", "0", RegistryValueKind.String),
            new(false, "JPEGImportQuality", 100, RegistryValueKind.DWord),
            new(true, "BackgroundType", 0, RegistryValueKind.DWord),
            new(true, "BackgroundHistoryPath0", target, RegistryValueKind.String)
        };
        string originalPath = desktop.GetValue(
            "WallPaper", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string ?? string.Empty;

        RegistryKey SelectKey(WallpaperChange change) =>
            change.History ? wallpapers : desktop;

        CommitWallpaperChanges(
            changes,
            change => ReadOriginalWallpaperValue(SelectKey(change), change.Name),
            change => SelectKey(change).SetValue(change.Name, change.Value, change.Kind),
            change => SelectKey(change).DeleteValue(change.Name, throwOnMissingValue: false),
            () =>
            {
                if (!SystemParametersInfo(
                    SpiSetDeskWallpaper, 0, target, SpifUpdateIniFile | SpifSendWinIniChange))
                    throw new InvalidOperationException("Windows pozadí se nepodařilo aplikovat.");
                if (!string.Equals(CurrentWallpaper, target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Windows nepotvrdil nové obrazové pozadí.");
            },
            () =>
            {
                if (!SystemParametersInfo(
                    SpiSetDeskWallpaper, 0, originalPath, SpifUpdateIniFile | SpifSendWinIniChange))
                    throw new InvalidOperationException("Windows nepotvrdil obnovení původní tapety.");
            });
    }

    public static bool RestoreOriginal()
    {
        string? original;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SpankyhoOptimizer\Themes");
            original = key?.GetValue("OriginalWallpaper") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            LogService.Error("themes", "Original wallpaper lookup failed", ex);
            return false;
        }

        if (string.IsNullOrWhiteSpace(original) || !File.Exists(original))
            return false;

        return SystemParametersInfo(SpiSetDeskWallpaper, 0, original, SpifUpdateIniFile | SpifSendWinIniChange);
    }

    private static void EnsurePackagedImage(string source, string target)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("Obrazová tapeta chybí v instalaci Optimizeru.", source);

        bool copy = !File.Exists(target);
        if (!copy)
        {
            try
            {
                var src = new FileInfo(source);
                var dst = new FileInfo(target);
                copy = src.Length != dst.Length || src.LastWriteTimeUtc > dst.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                LogService.Error("themes", "Packaged wallpaper file comparison failed; retrying copy", ex);
                copy = true;
            }
        }

        if (copy)
            File.Copy(source, target, true);
    }

    private static void CaptureOriginalWallpaperOnce()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\SpankyhoOptimizer\Themes");
        if (key?.GetValue("OriginalWallpaper") is string existing && !string.IsNullOrWhiteSpace(existing))
            return;

        string current = CurrentWallpaper;
        if (!string.IsNullOrWhiteSpace(current) &&
            !current.Contains(Path.Combine("Spankyho Optimizer", "Wallpapers"), StringComparison.OrdinalIgnoreCase))
            key?.SetValue("OriginalWallpaper", current, RegistryValueKind.String);
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
}
