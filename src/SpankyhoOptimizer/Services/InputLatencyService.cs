using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record InputLatencySnapshot(
    bool MouseAccelerationEnabled,
    string MouseSpeed,
    string MouseThreshold1,
    string MouseThreshold2,
    string KeyboardSpeed,
    string KeyboardDelay,
    bool HasMouseRollback);

public static class InputLatencyService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private const uint SpiSetMouse = 0x0004;
    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        [In] int[] pvParam,
        uint fWinIni);
    private sealed record MouseBackup(
        string MouseSpeed,
        string MouseThreshold1,
        string MouseThreshold2,
        DateTime SavedUtc);

    private static string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "State", "mouse-acceleration-backup.json");

    public static Task<InputLatencySnapshot> ReadAsync(CancellationToken token = default) =>
        Task.Run(ReadCore, token);

    public static async Task<InputLatencySnapshot> DisableMouseAccelerationAsync(CancellationToken token = default)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var before = ReadCore();
            SaveBackupIfNeeded(before);

            ApplyMouseParameters(0, 0, 0);

            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse", true)
                ?? throw new InvalidOperationException("Mouse registry není dostupný.");
            key.SetValue("MouseSpeed", "0", RegistryValueKind.String);
            key.SetValue("MouseThreshold1", "0", RegistryValueKind.String);
            key.SetValue("MouseThreshold2", "0", RegistryValueKind.String);

            var after = ReadCore();
            if (after.MouseAccelerationEnabled ||
                after.MouseSpeed != "0" ||
                after.MouseThreshold1 != "0" ||
                after.MouseThreshold2 != "0")
                throw new InvalidOperationException("Mouse acceleration read-back nepotvrdil OFF.");

            LogService.Info("input", "INPUT_MOUSE_ACCELERATION_OFF_OK");
            return after;
        }, token).ConfigureAwait(false);
    }

    public static async Task<InputLatencySnapshot> RestoreMouseAccelerationAsync(CancellationToken token = default)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(BackupPath))
                throw new InvalidOperationException("Původní mouse acceleration stav není uložen.");

            var backup = JsonSerializer.Deserialize<MouseBackup>(File.ReadAllText(BackupPath))
                ?? throw new InvalidOperationException("Mouse acceleration backup je neplatný.");

            int speed = ParseMouseInt(backup.MouseSpeed);
            int threshold1 = ParseMouseInt(backup.MouseThreshold1);
            int threshold2 = ParseMouseInt(backup.MouseThreshold2);
            ApplyMouseParameters(threshold1, threshold2, speed);

            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse", true)
                ?? throw new InvalidOperationException("Mouse registry není dostupný.");
            key.SetValue("MouseSpeed", backup.MouseSpeed, RegistryValueKind.String);
            key.SetValue("MouseThreshold1", backup.MouseThreshold1, RegistryValueKind.String);
            key.SetValue("MouseThreshold2", backup.MouseThreshold2, RegistryValueKind.String);

            var after = ReadCore();
            if (after.MouseSpeed != backup.MouseSpeed ||
                after.MouseThreshold1 != backup.MouseThreshold1 ||
                after.MouseThreshold2 != backup.MouseThreshold2)
                throw new InvalidOperationException("Mouse acceleration restore read-back nesouhlasí.");

            File.Delete(BackupPath);
            LogService.Info("input", "INPUT_MOUSE_ACCELERATION_RESTORE_OK");
            return after with { HasMouseRollback = false };
        }, token).ConfigureAwait(false);
    }

    private static void ApplyMouseParameters(int threshold1, int threshold2, int acceleration)
    {
        int[] values = { threshold1, threshold2, acceleration };
        if (!SystemParametersInfo(
                SpiSetMouse,
                0,
                values,
                SpifUpdateIniFile | SpifSendChange))
            throw new InvalidOperationException(
                "SystemParametersInfo(SPI_SETMOUSE) selhalo: Win32=" +
                Marshal.GetLastWin32Error());
    }

    private static int ParseMouseInt(string value) =>
        int.TryParse(value, out int parsed) ? parsed : 0;

    private static InputLatencySnapshot ReadCore()
    {
        using var mouse = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
        string mouseSpeed = Convert.ToString(mouse?.GetValue("MouseSpeed")) ?? "0";
        string threshold1 = Convert.ToString(mouse?.GetValue("MouseThreshold1")) ?? "0";
        string threshold2 = Convert.ToString(mouse?.GetValue("MouseThreshold2")) ?? "0";

        bool acceleration =
            !mouseSpeed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            !threshold1.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            !threshold2.Equals("0", StringComparison.OrdinalIgnoreCase);

        using var keyboard = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
        string keyboardSpeed = Convert.ToString(keyboard?.GetValue("KeyboardSpeed")) ?? "Nezjištěno";
        string keyboardDelay = Convert.ToString(keyboard?.GetValue("KeyboardDelay")) ?? "Nezjištěno";

        return new InputLatencySnapshot(
            acceleration,
            mouseSpeed,
            threshold1,
            threshold2,
            keyboardSpeed,
            keyboardDelay,
            File.Exists(BackupPath));
    }

    private static void SaveBackupIfNeeded(InputLatencySnapshot snapshot)
    {
        if (File.Exists(BackupPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        var backup = new MouseBackup(
            snapshot.MouseSpeed,
            snapshot.MouseThreshold1,
            snapshot.MouseThreshold2,
            DateTime.UtcNow);
        AtomicFileService.WriteText(
            BackupPath,
            JsonSerializer.Serialize(backup, IndentedJson));
    }
}
