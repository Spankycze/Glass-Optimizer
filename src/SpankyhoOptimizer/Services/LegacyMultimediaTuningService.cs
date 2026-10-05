using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record LegacyMultimediaSnapshot(
    string SystemResponsiveness,
    string NetworkThrottlingIndex,
    string GamesGpuPriority,
    string GamesPriority,
    string GamesSchedulingCategory,
    string GamesSfioPriority);

public static class LegacyMultimediaTuningService
{
    public static Task<LegacyMultimediaSnapshot> ReadAsync(CancellationToken token = default) =>
        Task.Run(ReadCore, token);

    private static LegacyMultimediaSnapshot ReadCore()
    {
        const string profilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        using var profile = Registry.LocalMachine.OpenSubKey(profilePath);
        using var games = Registry.LocalMachine.OpenSubKey(profilePath + @"\Tasks\Games");

        return new LegacyMultimediaSnapshot(
            FormatDword(profile?.GetValue("SystemResponsiveness")),
            FormatThrottle(profile?.GetValue("NetworkThrottlingIndex")),
            FormatDword(games?.GetValue("GPU Priority")),
            FormatDword(games?.GetValue("Priority")),
            Convert.ToString(games?.GetValue("Scheduling Category")) ?? "Not set",
            Convert.ToString(games?.GetValue("SFIO Priority")) ?? "Not set");
    }

    private static string FormatDword(object? value)
    {
        if (value == null) return "Not set / Windows default";
        try
        {
            uint raw = Convert.ToUInt32(value);
            return $"{raw} (0x{raw:X})";
        }
        catch
        {
            return Convert.ToString(value) ?? "Unknown";
        }
    }

    private static string FormatThrottle(object? value)
    {
        if (value == null) return "Not set / Windows default";
        try
        {
            uint raw = Convert.ToUInt32(value);
            return raw == uint.MaxValue
                ? "FFFFFFFF / throttling disabled"
                : $"{raw} (0x{raw:X})";
        }
        catch
        {
            return Convert.ToString(value) ?? "Unknown";
        }
    }
}
