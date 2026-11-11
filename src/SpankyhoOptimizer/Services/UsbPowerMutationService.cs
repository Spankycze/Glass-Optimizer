using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static class UsbPowerMutationService
{
    public static async Task<int> DisableDevicePowerSavingAsync(CancellationToken token)
    {
        if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator())
        {
            return await WorkerClientService.RunAsync<int>(
                "usb-power-disable",
                timeout: TimeSpan.FromMinutes(2),
                token: token).ConfigureAwait(false);
        }

        return await Task.Run(() => DisableDevicePowerSavingLocal(token), token).ConfigureAwait(false);
    }

    private static int DisableDevicePowerSavingLocal(CancellationToken token)
    {
        int changed = 0;
        foreach (string rootPath in new[]
                 {
                     @"SYSTEM\CurrentControlSet\Enum\USB",
                     @"SYSTEM\CurrentControlSet\Enum\USBSTOR",
                     @"SYSTEM\CurrentControlSet\Enum\HID"
                 })
        {
            token.ThrowIfCancellationRequested();
            using var rootKey = Registry.LocalMachine.OpenSubKey(rootPath);
            if (rootKey == null) continue;

            foreach (var a in rootKey.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                using var aKey = rootKey.OpenSubKey(a);
                if (aKey == null) continue;

                foreach (var b in aKey.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var dp = Registry.LocalMachine.OpenSubKey(
                            $@"{rootPath}\{a}\{b}\Device Parameters",
                            writable: true);
                        if (dp == null) continue;

                        dp.SetValue("EnhancedPowerManagementEnabled", 0, RegistryValueKind.DWord);
                        dp.SetValue("SelectiveSuspendEnabled", 0, RegistryValueKind.DWord);
                        dp.SetValue("DeviceSelectiveSuspended", 0, RegistryValueKind.DWord);
                        changed++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw;
                    }
                    catch (System.Security.SecurityException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("usb", $@"USB power registry update failed: {rootPath}\{a}\{b}", ex);
                    }
                }
            }
        }

        return changed;
    }
}
