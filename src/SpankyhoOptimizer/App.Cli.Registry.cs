using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchRegistryAsync(string[] cli)
    {
        if (HasArg(cli, "--registry-backup-identity-smoke"))
        {
            try
            {
                await RegistryBackupStore.RunBackupTargetIdentitySmokeAsync();
                LogService.Info("REGISTRY_BACKUP_IDENTITY_SMOKE_OK cases=24 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("REGISTRY_BACKUP_IDENTITY_SMOKE_FAIL", ex); Environment.Exit(111); }
            return true;
        }

        if (HasArg(cli, "--registry-existing-value-smoke"))
        {
            try
            {
                await RegistryBackupStore.RunExistingValueRestoreSmokeAsync();
                LogService.Info("REGISTRY_EXISTING_VALUE_SMOKE_OK cases=20 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("REGISTRY_EXISTING_VALUE_SMOKE_FAIL", ex); Environment.Exit(110); }
            return true;
        }

        if (HasArg(cli, "--registry-restore-feedback-smoke"))
        {
            try
            {
                await RegistryRestoreFeedbackSmoke.RunAsync();
                LogService.Info("REGISTRY_RESTORE_FEEDBACK_SMOKE_OK cases=18 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("REGISTRY_RESTORE_FEEDBACK_SMOKE_FAIL", ex); Environment.Exit(109); }
            return true;
        }

        if (HasArg(cli, "--registry-restore-all-smoke"))
        {
            try
            {
                await RegistryBackupStore.RunRestoreAllSmokeAsync();
                LogService.Info("REGISTRY_RESTORE_ALL_SMOKE_OK cases=18 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("REGISTRY_RESTORE_ALL_SMOKE_FAIL", ex); Environment.Exit(108); }
            return true;
        }

        if (HasArg(cli, "--registry-owned-value-smoke"))
        {
            try
            {
                await RegistryBackupStore.RunOwnedValueSmokeAsync();
                LogService.Info("REGISTRY_OWNED_VALUE_SMOKE_OK cases=18 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("REGISTRY_OWNED_VALUE_SMOKE_FAIL", ex); Environment.Exit(107); }
            return true;
        }

        if (HasArg(cli, "--registry-original-key-smoke"))
        {
            try
            {
                await RegistryBackupStore.RunOriginalKeySmokeAsync();
                LogService.Info("REGISTRY_ORIGINAL_KEY_SMOKE_OK cases=20 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("REGISTRY_ORIGINAL_KEY_SMOKE_FAIL", ex); Environment.Exit(106); }
            return true;
        }

        return false;
    }
}
