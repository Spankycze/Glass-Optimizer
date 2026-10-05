using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static class ShortcutAppearanceService
{
    private const string ShellIconsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string UserShellIconsPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string ExplorerPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string BackupPath = @"Software\SpankyhoOptimizer\ShortcutAppearance";
    private const int OverlayIconFormatVersion = 3;

    private static string NoArrowIconPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "Assets", "blank-shortcut.ico");

    private static string NoArrowIconRegistryValue => NoArrowIconPath + ",0";

    public static bool ShortcutArrowHidden()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ShellIconsPath);
        string value = Convert.ToString(key?.GetValue("29", null, RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? string.Empty;
        return (string.Equals(value, NoArrowIconPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, NoArrowIconRegistryValue, StringComparison.OrdinalIgnoreCase)) &&
               IsTransparentIconCurrent();
    }

    public static bool ShortcutSuffixDisabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ExplorerPath);
        return key?.GetValue("link") is byte[] bytes && bytes.Length >= 4 && bytes.Take(4).All(b => b == 0);
    }

    public static async Task SetShortcutArrowHiddenAsync(bool hidden, CancellationToken token = default)
    {
        CleanupLegacyOverride();
        using var backup = Registry.CurrentUser.CreateSubKey(BackupPath, true);
        using var machine = Registry.LocalMachine.CreateSubKey(ShellIconsPath, true);

        if (hidden)
        {
            EnsureTransparentIcon();
            backup.SetValue("OverlayIconFormatVersion", OverlayIconFormatVersion, RegistryValueKind.DWord);
            if (backup.GetValue("Saved") is null)
            {
                object? current = machine.GetValue("29", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string currentText = Convert.ToString(current) ?? string.Empty;
                bool legacySpanky = currentText.Contains("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase) ||
                                    currentText.Contains("blank.ico", StringComparison.OrdinalIgnoreCase) ||
                                    currentText.Contains("shell32.dll,-50", StringComparison.OrdinalIgnoreCase);
                backup.SetValue("Saved", 1, RegistryValueKind.DWord);
                backup.SetValue("Had29", current is not null && !legacySpanky ? 1 : 0, RegistryValueKind.DWord);
                if (current is not null && !legacySpanky)
                    backup.SetValue("Original29", currentText, RegistryValueKind.String);
            }
            machine.SetValue("29", NoArrowIconRegistryValue, RegistryValueKind.String);
        }
        else
        {
            bool had29 = Convert.ToInt32(backup.GetValue("Had29", 0)) == 1;
            if (had29)
                machine.SetValue("29", Convert.ToString(backup.GetValue("Original29")) ?? string.Empty, RegistryValueKind.String);
            else
                machine.DeleteValue("29", false);
        }

        // Explicit user action: do the expensive but reliable refresh.
        await GTweakMaintenanceService.ClearIconCacheAsync(token);
        await RefreshShellAsync(token);
        backup.SetValue("NeedsCacheRefresh", 0, RegistryValueKind.DWord);
    }

    public static async Task<bool> MigrateLegacyShortcutOverlayAsync(CancellationToken token = default)
    {
        bool iconRepaired = EnsureTransparentIcon();
        using var machine = Registry.LocalMachine.OpenSubKey(ShellIconsPath, writable: true);
        string current = Convert.ToString(machine?.GetValue("29", null, RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? string.Empty;
        bool targetCurrent = string.Equals(current, NoArrowIconRegistryValue, StringComparison.OrdinalIgnoreCase);
        bool legacy = current.Contains("shell32.dll,-50", StringComparison.OrdinalIgnoreCase) ||
                      current.Contains("shell32.dll,50", StringComparison.OrdinalIgnoreCase) ||
                      current.Contains("SpankyhoOptimizer\\blank-shortcut.ico", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(current, NoArrowIconPath, StringComparison.OrdinalIgnoreCase);

        using var backup = Registry.CurrentUser.CreateSubKey(BackupPath, true);

        // Current registry target is already correct. Upgrade/repair the icon file silently,
        // but never restart Explorer during application startup. Explicit tweak Apply performs
        // the full cache rebuild; startup only invalidates the shell view.
        if (targetCurrent)
        {
            int formatVersion = Convert.ToInt32(backup.GetValue("OverlayIconFormatVersion", 0));
            if (iconRepaired || formatVersion != OverlayIconFormatVersion)
            {
                backup.SetValue("OverlayIconFormatVersion", OverlayIconFormatVersion, RegistryValueKind.DWord);
                backup.SetValue("NeedsCacheRefresh", 1, RegistryValueKind.DWord);
                await RefreshShellAsync(token);
                LogService.Info("explorer", "Transparent shortcut overlay upgraded to multi-size format; full cache rebuild queued for explicit shortcut Apply.");
                return true;
            }
            return false;
        }

        if (!legacy) return false;

        backup.SetValue("Saved", 1, RegistryValueKind.DWord);
        backup.SetValue("Had29", 0, RegistryValueKind.DWord);
        backup.SetValue("OverlayIconFormatVersion", OverlayIconFormatVersion, RegistryValueKind.DWord);
        machine!.SetValue("29", NoArrowIconRegistryValue, RegistryValueKind.String);
        backup.SetValue("NeedsCacheRefresh", 1, RegistryValueKind.DWord);
        CleanupLegacyOverride();
        await RefreshShellAsync(token);
        LogService.Info("explorer", "Legacy shortcut overlay migrated without restarting Explorer; full cache rebuild deferred until explicit shortcut tweak apply.");
        return true;
    }

    public static async Task SetShortcutSuffixDisabledAsync(bool disabled, CancellationToken token = default)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(ExplorerPath, true))
            key.SetValue("link", disabled ? new byte[] { 0, 0, 0, 0 } : new byte[] { 0x1E, 0, 0, 0 }, RegistryValueKind.Binary);
        await WindowsActions.RestartExplorerAsync(token);
    }

    internal static bool EnsureTransparentIconForSmoke() => EnsureTransparentIcon();
    internal static string TransparentIconPathForSmoke => NoArrowIconPath;

    internal static bool ManagedOverlayConfiguredForSmoke()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ShellIconsPath);
        string value = Convert.ToString(key?.GetValue("29", null, RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? string.Empty;
        return string.Equals(value, NoArrowIconPath, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, NoArrowIconRegistryValue, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PrepareManagedOverlayForExplorerRestart()
    {
        if (!ManagedOverlayConfiguredForSmoke()) return false;
        EnsureTransparentIcon();
        return true;
    }

    private static void CleanupLegacyOverride()
    {
        using (var user = Registry.CurrentUser.OpenSubKey(UserShellIconsPath, true))
            user?.DeleteValue("29", false);

        string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpankyhoOptimizer", "blank-shortcut.ico");
        try { if (File.Exists(old)) File.Delete(old); } catch { }
    }

    private static bool EnsureTransparentIcon()
    {
        string path = NoArrowIconPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] expected = BuildTransparentIcon();
        if (File.Exists(path))
        {
            try
            {
                if (File.ReadAllBytes(path).SequenceEqual(expected)) return false;
            }
            catch { }
        }
        File.WriteAllBytes(path, expected);
        return true;
    }

    private static bool IsTransparentIconCurrent()
    {
        try
        {
            return File.Exists(NoArrowIconPath) &&
                   File.ReadAllBytes(NoArrowIconPath).SequenceEqual(BuildTransparentIcon());
        }
        catch { return false; }
    }

    private static byte[] BuildTransparentIcon()
    {
        // Shell overlays are requested at multiple sizes depending on DPI/view mode.
        // A single 32x32 frame can be cached by Explorer as an opaque black tile on some
        // Windows 11 builds. Ship a real multi-frame transparent ICO instead.
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        byte[][] frames = sizes.Select(BuildTransparentDibFrame).ToArray();

        const int iconHeaderBytes = 6;
        const int directoryEntryBytes = 16;
        int imageOffset = iconHeaderBytes + directoryEntryBytes * sizes.Length;
        int totalBytes = imageOffset + frames.Sum(x => x.Length);

        using var stream = new MemoryStream(totalBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // icon
        writer.Write((ushort)sizes.Length);

        int offset = imageOffset;
        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }

        foreach (byte[] frame in frames) writer.Write(frame);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildTransparentDibFrame(int size)
    {
        const int bitmapHeaderBytes = 40;
        int xorBytes = checked(size * size * 4);
        int maskStride = ((size + 31) / 32) * 4;
        int maskBytes = checked(maskStride * size);

        using var stream = new MemoryStream(bitmapHeaderBytes + xorBytes + maskBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write(bitmapHeaderBytes);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(xorBytes + maskBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // Do not emit a mathematically empty all-alpha-zero frame. Some Explorer/icon-cache
        // paths can persist that as a black fallback tile after a later shell restart.
        // Keep one practically invisible alpha=1 white pixel so the frame is a valid non-empty
        // 32-bit image, while the AND mask still keeps every other pixel transparent.
        var xor = new byte[xorBytes];
        xor[0] = 0xFF;
        xor[1] = 0xFF;
        xor[2] = 0xFF;
        xor[3] = 0x01;
        writer.Write(xor);

        var mask = Enumerable.Repeat((byte)0xFF, maskBytes).ToArray();
        if (mask.Length > 0) mask[0] = 0x7F;
        writer.Write(mask);
        writer.Flush();
        return stream.ToArray();
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static async Task RefreshShellAsync(CancellationToken token)
    {
        string ie4uinit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "ie4uinit.exe");
        if (File.Exists(ie4uinit))
        {
            try { await ProcessRunner.RunAsync(ie4uinit, "-ClearIconCache", TimeSpan.FromSeconds(10), token); } catch { }
            try { await ProcessRunner.RunAsync(ie4uinit, "-show", TimeSpan.FromSeconds(10), token); } catch { }
        }
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }
}
