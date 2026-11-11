using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static partial class TweakFactory
{
    public static readonly RegistryBackupStore Backups = new();

    // Preserve the old public signature after adding an optional backup provider.
    public static TweakDefinition Dword(string id, string section, string group, string name, string desc,
        RegistryHive hive, string path, string valueName, int enabledValue, int? disabledValue,
        bool deleteOnDisable, bool recommended, TweakRisk risk, bool restart, string impact,
        string? warning, int minWindowsBuild, string hardwareScope, bool requiresAdmin) =>
        Dword(id, section, group, name, desc, hive, path, valueName, enabledValue, disabledValue,
            deleteOnDisable, recommended, risk, restart, impact, warning, minWindowsBuild,
            hardwareScope, requiresAdmin, null);

    public static TweakDefinition Dword(string id, string section, string group, string name, string desc,
        RegistryHive hive, string path, string valueName, int enabledValue, int? disabledValue = null,
        bool deleteOnDisable = true, bool recommended = false, TweakRisk risk = TweakRisk.Safe,
        bool restart = false, string impact = "Nízký", string? warning = null,
        int minWindowsBuild = 19041, string hardwareScope = "Any", bool requiresAdmin = true, RegistryBackupStore? backups = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\0') || valueName.Contains('\0')) throw new ArgumentException("Registry cíl obsahuje neplatný znak.");
        var backupStore = backups ?? Backups;
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
            SupportsSafeCrashRecovery = true,
            RevertAsync = async ct =>
            {
                if (!await backupStore.RestoreAsync(id, ct, enabledValue, hive, path, valueName).ConfigureAwait(false))
                    throw new InvalidOperationException("Pro tento tweak není uložená původní registry hodnota.");
            },
            DetectAsync = ct =>
            {
                ct.ThrowIfCancellationRequested();
                if (RegistryBatchScope.IsActive)
                {
                    RegistryKey? scopedKey = RegistryBatchScope.OpenReadOnly(hive, path);
                    object? value = scopedKey?.GetValue(valueName);
                    return Task.FromResult(value != null && scopedKey!.GetValueKind(valueName) == RegistryValueKind.DWord && Convert.ToInt64(value) == enabledValue);
                }
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
                object? direct = key?.GetValue(valueName);
                return Task.FromResult(direct != null && key!.GetValueKind(valueName) == RegistryValueKind.DWord && Convert.ToInt64(direct) == enabledValue);
            },
            SetAsync = (on, ct) => ExecuteDwordMutationAsync(
                on,
                // First ON only. Never recapture while disabling a tweak.
                token => backupStore.SaveOriginalAsync(id, hive, path, valueName, token),
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (RegistryBatchScope.IsActive)
                    {
                        RegistryKey scopedKey = RegistryBatchScope.Open(hive, path, writable: true);
                        scopedKey.SetValue(valueName, enabledValue, RegistryValueKind.DWord);
                        if (!DwordEnabled(scopedKey, valueName, enabledValue))
                            throw new System.IO.IOException("Windows nepotvrdil DWORD tweak " + id);
                    }
                    else
                    {
                        using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                        using RegistryKey target = root.CreateSubKey(path, writable: true);
                        target.SetValue(valueName, enabledValue, RegistryValueKind.DWord);
                        if (!DwordEnabled(target, valueName, enabledValue))
                            throw new System.IO.IOException("Windows nepotvrdil DWORD tweak " + id);
                    }
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                // OFF restores EXACTLY the saved original kind/value/absence.
                // A missing backup is an error, not permission to invent a
                // disabledValue or to delete a pre-existing enabled DWORD.
                token => backupStore.RestoreAsync(id, token, enabledValue, hive, path, valueName),
                ct)
        };
    }

    private static bool DwordEnabled(RegistryKey key, string name, int enabled)
    {
        object? value = key.GetValue(name);
        return value != null && key.GetValueKind(name) == RegistryValueKind.DWord && Convert.ToInt64(value) == enabled;
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
