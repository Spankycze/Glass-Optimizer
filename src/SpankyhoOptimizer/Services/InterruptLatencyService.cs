using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record DeviceInterruptState(
    string Category,
    string Name,
    string InstanceId,
    bool? MsiSupported,
    int? DevicePolicy,
    string PolicyText,
    string CpuMaskText,
    bool HasExplicitCpuMask);

public sealed record NicAffinityExperimentResult(
    bool KeptTrial,
    double BaselineScore,
    double TrialScore,
    string Message);

public static class InterruptLatencyService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private sealed record AffinityBackup(
        string InstanceId,
        bool HadDevicePolicy,
        int? DevicePolicy,
        bool HadAssignmentMask,
        byte[]? AssignmentMask,
        DateTime SavedUtc);

    private static readonly SemaphoreSlim AffinityGate = new(1, 1);
    private static readonly object BackupFileGate = new();
    private static string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "State", "interrupt-affinity-backups.json");
    public static async Task<IReadOnlyList<DeviceInterruptState>> ReadAsync(CancellationToken token = default)
    {
        var hardware = await HardwareSnapshotService.GetAsync(false, token).ConfigureAwait(false);
        return await Task.Run(() => ReadDevices(hardware.Devices, token), token).ConfigureAwait(false);
    }

    public static bool HasAffinityBackup(string instanceId)
    {
        lock (BackupFileGate)
            return LoadBackups().Any(x => x.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<NicAffinityExperimentResult> RunNicAffinityExperimentAsync(
        DeviceInterruptState device,
        CancellationToken token = default)
    {
        if (!device.Category.Equals("NIC", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Affinity A/B experiment je povolen pouze pro NIC.");
        if (HasAffinityBackup(device.InstanceId))
            throw new InvalidOperationException("Pro toto NIC už existuje uložený rollback. Nejdřív vrať původní affinity.");

        await AffinityGate.WaitAsync(token).ConfigureAwait(false);
        bool mutated = false;
        try
        {
            NetworkDiagnosticsService.Invalidate();
            var baseline = await NetworkDiagnosticsService.MeasureAsync(samples: 10, token: token).ConfigureAwait(false);
            if (baseline.Received == 0)
                throw new InvalidOperationException("ICMP baseline nemá odpovědi; affinity test nelze vyhodnotit.");

            var backup = CaptureAffinity(device.InstanceId);
            SaveBackup(backup);

            ApplySpreadMessagesPolicy(device.InstanceId);
            mutated = true;
            await RestartDeviceAsync(device.InstanceId, token).ConfigureAwait(false);
            await Task.Delay(1200, token).ConfigureAwait(false);

            NetworkDiagnosticsService.Invalidate();
            var trial = await NetworkDiagnosticsService.MeasureAsync(samples: 10, token: token).ConfigureAwait(false);
            if (trial.Received == 0)
                throw new InvalidOperationException("Po změně affinity NIC nemá ICMP odpovědi.");

            double baselineScore = Score(baseline);
            double trialScore = Score(trial);
            double requiredGain = Math.Max(0.5, baselineScore * 0.03);
            bool improved =
                trial.LossPercent <= baseline.LossPercent &&
                trialScore + requiredGain < baselineScore;

            if (!improved)
            {
                RestoreAffinityValues(backup);
                await RestartDeviceAsync(device.InstanceId, CancellationToken.None).ConfigureAwait(false);
                mutated = false;
                RemoveBackup(device.InstanceId);
            }

            string message = improved
                ? $"NIC affinity test lepší — Spread messages ponecháno. Score {baselineScore:0.0} → {trialScore:0.0}. Původní stav je uložen pro rollback."
                : $"NIC affinity test nebyl lepší — původní stav automaticky obnoven. Score {baselineScore:0.0} → {trialScore:0.0}.";

            LogService.Info("interrupt",
                $"NIC_AFFINITY_AB_TEST keptTrial={improved} baselineScore={baselineScore:0.00} trialScore={trialScore:0.00} instance={device.InstanceId}");
            return new NicAffinityExperimentResult(improved, baselineScore, trialScore, message);
        }
        catch
        {
            if (mutated)
            {
                try
                {
                    var backup = LoadBackups().FirstOrDefault(x =>
                        x.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase));
                    if (backup != null)
                    {
                        RestoreAffinityValues(backup);
                        await RestartDeviceAsync(device.InstanceId, CancellationToken.None).ConfigureAwait(false);
                        RemoveBackup(device.InstanceId);
                    }
                }
                catch (Exception rollbackEx)
                {
                    LogService.Error("interrupt", "NIC affinity experiment rollback failed", rollbackEx);
                }
            }
            throw;
        }
        finally
        {
            AffinityGate.Release();
            NetworkDiagnosticsService.Invalidate();
        }
    }

    public static async Task RestoreNicAffinityAsync(
        string instanceId,
        CancellationToken token = default)
    {
        await AffinityGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var backup = LoadBackups().FirstOrDefault(x =>
                x.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Pro toto NIC není uložený affinity rollback.");

            RestoreAffinityValues(backup);
            await RestartDeviceAsync(instanceId, token).ConfigureAwait(false);
            RemoveBackup(instanceId);
            LogService.Info("interrupt", "NIC_AFFINITY_RESTORE_OK instance=" + instanceId);
        }
        finally
        {
            AffinityGate.Release();
            NetworkDiagnosticsService.Invalidate();
        }
    }

    private static AffinityBackup CaptureAffinity(string instanceId)
    {
        string path = AffinityRegistryPath(instanceId);
        using var key = Registry.LocalMachine.OpenSubKey(path, false);
        object? policyRaw = key?.GetValue("DevicePolicy");
        object? maskRaw = key?.GetValue("AssignmentSetOverride");
        byte[]? mask = maskRaw as byte[];
        if (maskRaw != null && mask == null)
            throw new InvalidOperationException(
                "AssignmentSetOverride má nepodporovaný registry typ; affinity experiment nebude proveden.");

        return new AffinityBackup(
            instanceId,
            policyRaw != null,
            policyRaw == null ? null : Convert.ToInt32(policyRaw),
            mask != null,
            mask,
            DateTime.UtcNow);
    }

    private static void ApplySpreadMessagesPolicy(string instanceId)
    {
        using var key = Registry.LocalMachine.CreateSubKey(AffinityRegistryPath(instanceId), true)
            ?? throw new InvalidOperationException("Interrupt Affinity Policy registry není dostupný.");
        key.SetValue("DevicePolicy", 5, RegistryValueKind.DWord);
        key.DeleteValue("AssignmentSetOverride", throwOnMissingValue: false);
        int readBack = Convert.ToInt32(key.GetValue("DevicePolicy", -1));
        if (readBack != 5)
            throw new InvalidOperationException("Affinity DevicePolicy read-back nepotvrdil SpreadMessagesAcrossAllProcessors.");
    }

    private static void RestoreAffinityValues(AffinityBackup backup)
    {
        using var key = Registry.LocalMachine.CreateSubKey(AffinityRegistryPath(backup.InstanceId), true)
            ?? throw new InvalidOperationException("Interrupt Affinity Policy registry není dostupný.");

        if (backup.HadDevicePolicy && backup.DevicePolicy.HasValue)
            key.SetValue("DevicePolicy", backup.DevicePolicy.Value, RegistryValueKind.DWord);
        else
            key.DeleteValue("DevicePolicy", throwOnMissingValue: false);

        if (backup.HadAssignmentMask && backup.AssignmentMask != null)
            key.SetValue("AssignmentSetOverride", backup.AssignmentMask, RegistryValueKind.Binary);
        else
            key.DeleteValue("AssignmentSetOverride", throwOnMissingValue: false);
    }

    private static async Task RestartDeviceAsync(string instanceId, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(
            "pnputil.exe",
            $"/restart-device \"{instanceId}\"",
            TimeSpan.FromSeconds(35),
            token).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException("pnputil /restart-device selhal: " + (result.StdErr + " " + result.StdOut).Trim());
    }

    private static double Score(NetworkLatencyReport report) =>
        report.AvgMs + (report.JitterMs * 1.5) + (report.LossPercent * 10.0);

    private static string AffinityRegistryPath(string instanceId) =>
        @"SYSTEM\CurrentControlSet\Enum\" + instanceId +
        @"\Device Parameters\Interrupt Management\Affinity Policy";

    private static AffinityBackup[] LoadBackups()
    {
        lock (BackupFileGate)
        {
            try
            {
                if (!File.Exists(BackupPath)) return Array.Empty<AffinityBackup>();
                return JsonSerializer.Deserialize<AffinityBackup[]>(File.ReadAllText(BackupPath))
                       ?? Array.Empty<AffinityBackup>();
            }
            catch (Exception ex)
            {
                LogService.Error("interrupt", "Affinity backup load failed", ex);
                return Array.Empty<AffinityBackup>();
            }
        }
    }

    private static void SaveBackup(AffinityBackup backup)
    {
        lock (BackupFileGate)
        {
            var list = LoadBackups()
                .Where(x => !x.InstanceId.Equals(backup.InstanceId, StringComparison.OrdinalIgnoreCase))
                .Append(backup)
                .ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            AtomicFileService.WriteText(
                BackupPath,
                JsonSerializer.Serialize(list, IndentedJson));
        }
    }

    private static void RemoveBackup(string instanceId)
    {
        lock (BackupFileGate)
        {
            var list = LoadBackups()
                .Where(x => !x.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (list.Length == 0)
            {
                if (File.Exists(BackupPath)) File.Delete(BackupPath);
                return;
            }
            AtomicFileService.WriteText(
                BackupPath,
                JsonSerializer.Serialize(list, IndentedJson));
        }
    }

    private static DeviceInterruptState[] ReadDevices(
        IReadOnlyList<DeviceInfo> devices,
        CancellationToken token)
    {
        var result = new List<DeviceInterruptState>();
        foreach (var device in devices)
        {
            token.ThrowIfCancellationRequested();
            if (!device.Present) continue;
            string? category = Classify(device);
            if (category == null) continue;

            string basePath = @"SYSTEM\CurrentControlSet\Enum\" + device.InstanceId +
                              @"\Device Parameters\Interrupt Management";
            bool? msi = null;
            int? policy = null;
            string mask = "Driver / Windows default";
            bool explicitMask = false;

            try
            {
                using var msiKey = Registry.LocalMachine.OpenSubKey(
                    basePath + @"\MessageSignaledInterruptProperties");
                object? rawMsi = msiKey?.GetValue("MSISupported");
                if (rawMsi != null) msi = Convert.ToInt32(rawMsi) != 0;
            }
            catch (Exception ex)
            {
                LogService.Error("interrupt", "MSI read failed: " + device.InstanceId, ex);
            }

            try
            {
                using var affinity = Registry.LocalMachine.OpenSubKey(basePath + @"\Affinity Policy");
                object? rawPolicy = affinity?.GetValue("DevicePolicy");
                if (rawPolicy != null) policy = Convert.ToInt32(rawPolicy);

                object? rawMask = affinity?.GetValue("AssignmentSetOverride");
                if (rawMask is byte[] bytes && bytes.Length > 0)
                {
                    explicitMask = true;
                    mask = "0x" + string.Concat(bytes.Reverse().Select(x => x.ToString("X2")));
                }
                else if (rawMask != null)
                {
                    string text = Convert.ToString(rawMask) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        explicitMask = true;
                        mask = text;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("interrupt", "Affinity read failed: " + device.InstanceId, ex);
            }

            result.Add(new DeviceInterruptState(
                category,
                string.IsNullOrWhiteSpace(device.FriendlyName) ? device.InstanceId : device.FriendlyName,
                device.InstanceId,
                msi,
                policy,
                FormatPolicy(policy),
                mask,
                explicitMask));
        }

        return result
            .OrderBy(x => CategoryOrder(x.Category))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(24)
            .ToArray();
    }

    private static string? Classify(DeviceInfo device)
    {
        string cls = device.Class ?? string.Empty;
        string name = device.FriendlyName ?? string.Empty;
        if (cls.Equals("Display", StringComparison.OrdinalIgnoreCase)) return "GPU";
        if (cls.Equals("Net", StringComparison.OrdinalIgnoreCase)) return "NIC";
        if (name.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("NVM Express", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase))
            return "NVMe / Storage";
        if (name.Contains("USB", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Controller", StringComparison.OrdinalIgnoreCase))
            return "USB Controller";
        return null;
    }

    private static int CategoryOrder(string category) => category switch
    {
        "GPU" => 0,
        "NIC" => 1,
        "NVMe / Storage" => 2,
        "USB Controller" => 3,
        _ => 9
    };

    private static string FormatPolicy(int? policy) => policy switch
    {
        null => "Windows / driver default",
        0 => "Machine default",
        1 => "All close processors",
        2 => "One close processor",
        3 => "All processors",
        4 => "Specified processors",
        5 => "Spread messages across processors",
        _ => "Policy " + policy
    };
}
