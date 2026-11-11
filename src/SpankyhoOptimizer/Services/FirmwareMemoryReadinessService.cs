using System;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// User-invoked H01/H02/H03 firmware and memory prerequisite check.
/// Raw SMBIOS data is evidence for BIOS version and configured RAM speed;
/// it cannot prove an XMP/EXPO profile or Resizable BAR is enabled.
/// </summary>
internal static class FirmwareMemoryReadinessService
{
    internal sealed record Snapshot(string BiosVersion, string BiosManufacturer,
        DateTime? BiosReleaseDate, string FirmwareMode, string Motherboard,
        string RamHardware, string Gpu);

    private enum FirmwareType : uint { Unknown = 0, Bios = 1, Uefi = 2, Max = 3 }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out FirmwareType firmware);

    internal static async Task<Snapshot> InspectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<OverviewSnapshot> existing = SystemInfoService.GetSnapshotAsync();
        Task<(string Version, string Manufacturer, DateTime? Date, string Mode)> bios =
            Task.Run(ReadBios, token);
        var overview = await existing.WaitAsync(token).ConfigureAwait(false);
        var info = await bios.WaitAsync(token).ConfigureAwait(false);
        return new Snapshot(info.Version, info.Manufacturer, info.Date,
            info.Mode, overview.Motherboard, overview.RamHardware, overview.Gpu);
    }

    private static (string Version, string Manufacturer, DateTime? Date, string Mode) ReadBios()
    {
        string version = "Nezjištěno", manufacturer = "Nezjištěno";
        DateTime? date = null;
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2");
            scope.Options.Timeout = TimeSpan.FromSeconds(3);
            scope.Connect();
            using var search = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Manufacturer,SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS"),
                new EnumerationOptions
                {
                    Timeout = TimeSpan.FromSeconds(3),
                    ReturnImmediately = false,
                    Rewindable = false
                });
            using var rows = search.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    version = Clean(row["SMBIOSBIOSVersion"], 64);
                    manufacturer = Clean(row["Manufacturer"], 64);
                    string? raw = Convert.ToString(row["ReleaseDate"], CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        try { date = ManagementDateTimeConverter.ToDateTime(raw).Date; }
                        catch (FormatException) { }
                        catch (ArgumentOutOfRangeException) { }
                    }
                }
                break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or
            UnauthorizedAccessException or SecurityException or InvalidOperationException)
        { /* All unknown values remain explicit. */ }

        string mode = "Nezjištěno";
        try
        {
            if (GetFirmwareType(out var firmware))
                mode = firmware switch
                {
                    FirmwareType.Uefi => "UEFI",
                    FirmwareType.Bios => "Legacy BIOS",
                    _ => "Nezjištěno"
                };
        }
        catch (EntryPointNotFoundException) { }
        return (version, manufacturer, date, mode);
    }

    private static string Clean(object? value, int max)
    {
        string text = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
            .Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0) return "Nezjištěno";
        return text.Length <= max ? text : text[..max];
    }

    internal static string Format(Snapshot snapshot)
    {
        string biosDate = snapshot.BiosReleaseDate is DateTime d
            ? d.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("cs-CZ")) : "Nezjištěno";
        string ram = string.IsNullOrWhiteSpace(snapshot.RamHardware)
            ? "Nezjištěno" : snapshot.RamHardware;
        string board = string.IsNullOrWhiteSpace(snapshot.Motherboard)
            ? "Nezjištěno" : snapshot.Motherboard;
        string gpu = string.IsNullOrWhiteSpace(snapshot.Gpu)
            ? "Nezjištěno" : snapshot.Gpu;
        return
            "BIOS / firmware\n" +
            "Deska: " + board + "\n" +
            "Výrobce BIOS: " + snapshot.BiosManufacturer + "\n" +
            "Verze: " + snapshot.BiosVersion + " • Datum: " + biosDate +
            " • Režim: " + snapshot.FirmwareMode + "\n\n" +
            "RAM / XMP / EXPO\n" +
            "Aktuálně nakonfigurované parametry podle SMBIOS: " + ram + "\n" +
            "XMP/EXPO profil: nezjištěno – Windows samotné neprokazují, " +
            "zda je profil XMP/EXPO v BIOS aktivovaný.\n\n" +
            "GPU / Resizable BAR\n" +
            "Grafika: " + gpu + "\n" +
            "ReBAR aktivní: nezjištěno – samotný UEFI režim ani model GPU " +
            "neprokazuje, že funguje. U NVIDIA ověř v NVIDIA Control Panel → " +
            "System Information → Resizable BAR (Yes/No).\n\n" +
            "Jde pouze o čtení, bez změn BIOSu, XMP, PCIe ani ovladačů.";
    }

    internal static string RunPolicySmoke()
    {
        string output = Format(new Snapshot("F36", "GIGABYTE",
            new DateTime(2025, 1, 17), "UEFI", "X570 AORUS MASTER",
            "32 GB DDR4-3600", "RTX 3060 Ti"));
        if (!output.Contains("F36", StringComparison.Ordinal) ||
            !output.Contains("DDR4-3600", StringComparison.Ordinal) ||
            !output.Contains("XMP/EXPO profil: nezjištěno", StringComparison.Ordinal) ||
            !output.Contains("ReBAR aktivní: nezjištěno", StringComparison.Ordinal) ||
            output.Contains("ReBAR aktivní: ano", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Firmware summary inferred an unsupported BIOS/XMP/ReBAR state.");
        return "FIRMWARE_MEMORY_READONLY_POLICY_SMOKE_OK biosVersion=true ramClock=true xmpUnknown=true rebarUnknown=true writes=0";
    }
}
