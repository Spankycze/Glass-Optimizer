using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string WallpaperQualityId = "gt.ui.wallpaperquality";
    // Versioned ID avoids reusing stale snapshots from the old generic DWORD toggle.
    private const string WallpaperQualityBackupId = "gt.ui.wallpaperquality.original-v1";
    private const string WallpaperQualityKey = @"Control Panel\Desktop";
    private const string WallpaperQualityValue = "JPEGImportQuality";
    private const int WallpaperQualityMaximum = 100;
    private static readonly SemaphoreSlim WallpaperQualityGate = new(1, 1);

    private static bool IsWallpaperQualityMaximum(RegistryKey? key)
    {
        if (key is null ||
            !key.GetValueNames().Contains(WallpaperQualityValue, StringComparer.OrdinalIgnoreCase))
            return false;
        return key.GetValueKind(WallpaperQualityValue) == RegistryValueKind.DWord &&
            key.GetValue(WallpaperQualityValue, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is int value &&
            value == WallpaperQualityMaximum;
    }

    private static Task<bool> DetectWallpaperQualityAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey hive = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? key = hive.OpenSubKey(WallpaperQualityKey, writable: false);
            return IsWallpaperQualityMaximum(key);
        }, ct);

    // Prevent a restore overwriting a newer value that another application or
    // the user selected after the Optimizer's operation.
    internal static bool CanRestoreWallpaperQuality(
        bool targetKeyExists, bool enabledValueIsPresent) =>
        targetKeyExists && enabledValueIsPresent;

    // This is the same production transaction exercised by pure callback fakes.
    internal static async Task ExecuteWallpaperQualityMutationAsync(
        bool maximum,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!maximum)
        {
            await restore(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        await capture(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await apply(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try { await restore(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "Nastavení JPEG kvality a obnova původní hodnoty selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static async Task CaptureWallpaperQualityOriginalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using RegistryKey hive = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = hive.OpenSubKey(WallpaperQualityKey, writable: false);
        if (key is null)
            throw new InvalidOperationException(
                "Původní klíč nastavení tapety neexistuje; nebudu jej vytvářet.");

        await TweakFactory.Backups.SaveOriginalAsync(
            WallpaperQualityBackupId, RegistryHive.CurrentUser,
            WallpaperQualityKey, WallpaperQualityValue, ct).ConfigureAwait(false);
    }

    private static Task ApplyWallpaperQualityAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey hive = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey key = hive.OpenSubKey(WallpaperQualityKey, writable: true)
                ?? throw new IOException("Windows Desktop nastavení není dostupné k zápisu.");
            key.SetValue(WallpaperQualityValue, WallpaperQualityMaximum, RegistryValueKind.DWord);
            ct.ThrowIfCancellationRequested();
            if (!IsWallpaperQualityMaximum(key))
                throw new IOException("Windows nepotvrdil JPEGImportQuality DWORD 100.");
        }, ct);

    private static async Task RestoreWallpaperQualityOriginalAsync(CancellationToken ct)
    {
        using RegistryKey hive = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = hive.OpenSubKey(WallpaperQualityKey, writable: false);
        if (!CanRestoreWallpaperQuality(key is not null, IsWallpaperQualityMaximum(key)))
            throw new InvalidOperationException(
                "Kvalita tapety byla změněna mimo Optimizer nebo klíč chybí; nebudu přepisovat cizí hodnotu.");

        // Revert exactly the original registry kind/value/absence, even if
        // the original DWORD was ALSO 100. Never use a synthetic default.
        if (!await TweakFactory.Backups.RestoreAsync(
                WallpaperQualityBackupId, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Není uložená původní hodnota kvality tapety.");
    }

    private static async Task SetWallpaperQualityAsync(bool maximum, CancellationToken ct)
    {
        await WallpaperQualityGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteWallpaperQualityMutationAsync(
                maximum, CaptureWallpaperQualityOriginalAsync,
                ApplyWallpaperQualityAsync, RestoreWallpaperQualityOriginalAsync, ct)
                .ConfigureAwait(false);
        }
        finally { WallpaperQualityGate.Release(); }
    }

    private static TweakDefinition WallpaperQualityTweak() => new()
    {
        Id = WallpaperQualityId,
        Section = "ui",
        Group = "Vzhled Windows",
        Name = "JPEG wallpaper bez komprese",
        Description = "JPEGImportQuality = 100 pro maximální kvalitu importu JPEG tapety.",
        Recommended = false,
        Risk = TweakRisk.Optional,
        RestartRequired = false,
        Impact = "Nízký",
        MinWindowsBuild = 22000,
        HardwareScope = "Any",
        RequiresAdmin = false,
        RegistryHive = RegistryHive.CurrentUser,
        RegistryPath = WallpaperQualityKey,
        RegistryValueName = WallpaperQualityValue,
        RegistryEnabledValue = WallpaperQualityMaximum,
        TechnicalTarget = "Registry: CurrentUser\\" + WallpaperQualityKey +
            "\\JPEGImportQuality -> 100",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = DetectWallpaperQualityAsync,
        SetAsync = SetWallpaperQualityAsync,
        RevertAsync = ct => SetWallpaperQualityAsync(false, ct)
    };
}
