using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static class TweakFactory
{
    public static readonly RegistryBackupStore Backups = new();

    public static TweakDefinition Dword(string id, string section, string group, string name, string desc,
        RegistryHive hive, string path, string valueName, int enabledValue, int? disabledValue = null,
        bool deleteOnDisable = true, bool recommended = false, TweakRisk risk = TweakRisk.Safe,
        bool restart = false, string impact = "Nízký", string? warning = null,
        int minWindowsBuild = 19041, string hardwareScope = "Any", bool requiresAdmin = true)
    {
        return new TweakDefinition
        {
            Id = id,
            Section = section,
            Group = group,
            Name = name,
            Description = desc,
            Recommended = recommended,
            Risk = risk,
            RestartRequired = restart,
            Impact = impact,
            Warning = warning,
            MinWindowsBuild = minWindowsBuild,
            HardwareScope = hardwareScope,
            TechnicalTarget = $"Registry: {hive}\\{path}\\{valueName} -> {enabledValue}",
            RequiresAdmin = requiresAdmin,
            RegistryHive = hive,
            RegistryPath = path,
            RegistryValueName = valueName,
            RegistryEnabledValue = enabledValue,
            RevertMode = "OriginalRegistryValue",
            RevertAsync = async _ =>
            {
                if (!await Backups.RestoreAsync(id).ConfigureAwait(false))
                    throw new InvalidOperationException("Pro tento tweak není uložená původní registry hodnota.");
            },
            DetectAsync = _ => Task.Run(() =>
            {
                if (RegistryBatchScope.IsActive)
                {
                    RegistryKey scopedKey = RegistryBatchScope.Open(hive, path, writable: true);
                    object? value = scopedKey.GetValue(valueName);
                    return value != null && Convert.ToInt64(value) == enabledValue;
                }
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
                object? direct = key?.GetValue(valueName);
                return direct != null && Convert.ToInt64(direct) == enabledValue;
            }),
            SetAsync = async (on, _) =>
            {
                await Backups.SaveOriginalAsync(id, hive, path, valueName);
                if (RegistryBatchScope.IsActive)
                {
                    RegistryKey scopedKey = RegistryBatchScope.Open(hive, path, writable: true);
                    if (on) scopedKey.SetValue(valueName, enabledValue, RegistryValueKind.DWord);
                    else if (!await Backups.RestoreAsync(id))
                    {
                        if (deleteOnDisable) scopedKey.DeleteValue(valueName, throwOnMissingValue: false);
                        else scopedKey.SetValue(valueName, disabledValue.GetValueOrDefault(), RegistryValueKind.DWord);
                    }
                }
                else
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using RegistryKey key = baseKey.CreateSubKey(path, writable: true);
                    if (on) key.SetValue(valueName, enabledValue, RegistryValueKind.DWord);
                    else if (!await Backups.RestoreAsync(id))
                    {
                        if (deleteOnDisable) key.DeleteValue(valueName, throwOnMissingValue: false);
                        else key.SetValue(valueName, disabledValue.GetValueOrDefault(), RegistryValueKind.DWord);
                    }
                }
            }
        };
    }

    public static TweakDefinition Command(string id, string section, string group, string name, string desc,
        Func<CancellationToken, Task<bool>> detect, Func<bool, CancellationToken, Task> set, bool recommended = false, TweakRisk risk = TweakRisk.Safe, bool restart = false,
        string impact = "Nízký", string? warning = null, int minWindowsBuild = 19041,
        string hardwareScope = "Any", bool requiresAdmin = true, bool lazyDetect = false,
        Func<CancellationToken, Task>? revert = null)
    {
        return new TweakDefinition
        {
            Id = id,
            Section = section,
            Group = group,
            Name = name,
            Description = desc,
            Recommended = recommended,
            Risk = risk,
            RestartRequired = restart,
            Impact = impact,
            Warning = warning,
            MinWindowsBuild = minWindowsBuild,
            HardwareScope = hardwareScope,
            TechnicalTarget = "Příkaz / systémové API (viz implementace tweaku)",
            RequiresAdmin = requiresAdmin,
            LazyDetect = lazyDetect,
            DetectAsync = detect,
            SetAsync = set,
            RevertAsync = revert,
            RevertMode = revert != null ? "Explicit" : "BooleanState"
        };
    }
}
