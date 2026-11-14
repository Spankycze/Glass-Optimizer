using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchTweaksAsync(string[] cli)
    {
        if (HasArg(cli, "--generic-dword-mutation-smoke"))
        {
            try
            {
                await TweakFactory.RunDwordMutationSmokeAsync();
                LogService.Info("GENERIC_DWORD_MUTATION_SMOKE_OK cases=19 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("GENERIC_DWORD_MUTATION_SMOKE_FAIL", ex); Environment.Exit(105); }
            return true;
        }

        if (HasArg(cli, "--uar-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunUarMutationSmokeAsync();
                LogService.Info("UAR_MUTATION_SMOKE_OK cases=25 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("UAR_MUTATION_SMOKE_FAIL", ex); Environment.Exit(104); }
            return true;
        }

        if (HasArg(cli, "--speech-model-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunSpeechModelMutationSmokeAsync();
                LogService.Info("SPEECH_MODEL_MUTATION_SMOKE_OK cases=25 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SPEECH_MODEL_MUTATION_SMOKE_FAIL", ex); Environment.Exit(103); }
            return true;
        }

        if (HasArg(cli, "--ceip-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunCeipMutationSmokeAsync();
                LogService.Info("CEIP_MUTATION_SMOKE_OK cases=25 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("CEIP_MUTATION_SMOKE_FAIL", ex); Environment.Exit(102); }
            return true;
        }

        if (HasArg(cli, "--pchealth-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunPcHealthMutationSmokeAsync();
                LogService.Info("PCHEALTH_MUTATION_SMOKE_OK cases=23 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PCHEALTH_MUTATION_SMOKE_FAIL", ex); Environment.Exit(101); }
            return true;
        }

        if (HasArg(cli, "--wallpaper-quality-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunWallpaperQualityMutationSmokeAsync();
                LogService.Info("WALLPAPER_QUALITY_MUTATION_SMOKE_OK cases=18 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WALLPAPER_QUALITY_MUTATION_SMOKE_FAIL", ex); Environment.Exit(100); }
            return true;
        }

        if (HasArg(cli, "--store-autoupdate-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunStoreUpdatesMutationSmokeAsync();
                LogService.Info("STORE_AUTOUPDATE_MUTATION_SMOKE_OK cases=25 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STORE_AUTOUPDATE_MUTATION_SMOKE_FAIL", ex); Environment.Exit(98); }
            return true;
        }

        if (HasArg(cli, "--lockscreen-timeout-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunLockScreenTimeoutMutationSmokeAsync();
                LogService.Info("LOCKSCREEN_TIMEOUT_MUTATION_SMOKE_OK cases=18 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("LOCKSCREEN_TIMEOUT_MUTATION_SMOKE_FAIL", ex); Environment.Exit(99); }
            return true;
        }

        if (HasArg(cli, "--theme-mode-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunThemeModeMutationSmokeAsync();
                LogService.Info("THEME_MODE_MUTATION_SMOKE_OK cases=19 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("THEME_MODE_MUTATION_SMOKE_FAIL", ex); Environment.Exit(97); }
            return true;
        }

        if (HasArg(cli, "--exe-warnings-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunExeWarningsMutationSmokeAsync();
                LogService.Info("EXE_WARNINGS_MUTATION_SMOKE_OK cases=22 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("EXE_WARNINGS_MUTATION_SMOKE_FAIL", ex); Environment.Exit(96); }
            return true;
        }

        if (HasArg(cli, "--gallery-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunGalleryMutationSmokeAsync();
                LogService.Info("GALLERY_MUTATION_SMOKE_OK cases=20 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("GALLERY_MUTATION_SMOKE_FAIL", ex); Environment.Exit(95); }
            return true;
        }

        if (HasArg(cli, "--service-start-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunServiceStartMutationSmokeAsync();
                LogService.Info("SERVICE_START_MUTATION_SMOKE_OK cases=20 windowsServiceChanges=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SERVICE_START_MUTATION_SMOKE_FAIL", ex); Environment.Exit(94); }
            return true;
        }

        if (HasArg(cli, "--onedrive-nav-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunOneDriveNavMutationSmokeAsync();
                LogService.Info("ONEDRIVE_NAV_MUTATION_SMOKE_OK cases=19 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("ONEDRIVE_NAV_MUTATION_SMOKE_FAIL", ex); Environment.Exit(93); }
            return true;
        }

        if (HasArg(cli, "--explorer-home-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunExplorerHomeMutationSmokeAsync();
                LogService.Info("EXPLORER_HOME_MUTATION_SMOKE_OK cases=16 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("EXPLORER_HOME_MUTATION_SMOKE_FAIL", ex); Environment.Exit(92); }
            return true;
        }

        if (HasArg(cli, "--this-pc-folders-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunThisPcFolderMutationSmokeAsync();
                LogService.Info("THIS_PC_FOLDERS_MUTATION_SMOKE_OK cases=16 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("THIS_PC_FOLDERS_MUTATION_SMOKE_FAIL", ex); Environment.Exit(90); }
            return true;
        }

        if (HasArg(cli, "--desktop-icons-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunDesktopIconsMutationSmokeAsync();
                LogService.Info("DESKTOP_ICONS_MUTATION_SMOKE_OK cases=13 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DESKTOP_ICONS_MUTATION_SMOKE_FAIL", ex); Environment.Exit(89); }
            return true;
        }

        if (HasArg(cli, "--classic-menu-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunClassicMenuMutationSmokeAsync();
                LogService.Info("CLASSIC_MENU_MUTATION_SMOKE_OK cases=15 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("CLASSIC_MENU_MUTATION_SMOKE_FAIL", ex); Environment.Exit(88); }
            return true;
        }

        if (HasArg(cli, "--start-layout-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunStartLayoutMutationSmokeAsync();
                LogService.Info("START_LAYOUT_MUTATION_SMOKE_OK cases=14 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("START_LAYOUT_MUTATION_SMOKE_FAIL", ex); Environment.Exit(87); }
            return true;
        }

        if (HasArg(cli, "--multi-string-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunMultiStringMutationSmokeAsync();
                LogService.Info("MULTI_STRING_MUTATION_SMOKE_OK cases=14 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("MULTI_STRING_MUTATION_SMOKE_FAIL", ex); Environment.Exit(86); }
            return true;
        }

        if (HasArg(cli, "--string-tweak-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunStringTweakMutationSmokeAsync();
                LogService.Info("STRING_TWEAK_MUTATION_SMOKE_OK cases=13 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STRING_TWEAK_MUTATION_SMOKE_FAIL", ex); Environment.Exit(85); }
            return true;
        }

        if (HasArg(cli, "--filter-keys-shortcut-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunFilterKeysShortcutSmokeAsync();
                LogService.Info("FILTER_KEYS_SHORTCUT_SMOKE_OK cases=12 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("FILTER_KEYS_SHORTCUT_SMOKE_FAIL", ex); Environment.Exit(84); }
            return true;
        }

        if (HasArg(cli, "--cabc-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunCabcMutationSmokeAsync();
                LogService.Info("CABC_MUTATION_SMOKE_OK cases=12 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("CABC_MUTATION_SMOKE_FAIL", ex); Environment.Exit(83); }
            return true;
        }

        if (HasArg(cli, "--location-extras-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunLocationExtrasMutationSmokeAsync();
                LogService.Info("LOCATION_EXTRAS_MUTATION_SMOKE_OK cases=11 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("LOCATION_EXTRAS_MUTATION_SMOKE_FAIL", ex); Environment.Exit(82); }
            return true;
        }

        if (HasArg(cli, "--setting-sync-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunSettingSyncMutationSmokeAsync();
                LogService.Info("SETTING_SYNC_MUTATION_SMOKE_OK cases=11 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SETTING_SYNC_MUTATION_SMOKE_FAIL", ex); Environment.Exit(81); }
            return true;
        }

        if (HasArg(cli, "--realtek-idle-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunRealtekIdleMutationSmokeAsync();
                LogService.Info("REALTEK_IDLE_MUTATION_SMOKE_OK cases=11 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("REALTEK_IDLE_MUTATION_SMOKE_FAIL", ex); Environment.Exit(80); }
            return true;
        }

        if (HasArg(cli, "--pointer-precision-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunPointerPrecisionMutationSmokeAsync();
                LogService.Info("POINTER_PRECISION_MUTATION_SMOKE_OK cases=11 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("POINTER_PRECISION_MUTATION_SMOKE_FAIL", ex); Environment.Exit(79); }
            return true;
        }

        if (HasArg(cli, "--rotating-lock-screen-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunRotatingLockScreenMutationSmokeAsync();
                LogService.Info("ROTATING_LOCK_SCREEN_MUTATION_SMOKE_OK cases=10 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("ROTATING_LOCK_SCREEN_MUTATION_SMOKE_FAIL", ex); Environment.Exit(78); }
            return true;
        }

        if (HasArg(cli, "--type-overlay-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunTypeOverlayMutationSmokeAsync();
                LogService.Info("TYPE_OVERLAY_MUTATION_SMOKE_OK cases=10 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TYPE_OVERLAY_MUTATION_SMOKE_FAIL", ex); Environment.Exit(77); }
            return true;
        }

        if (HasArg(cli, "--desktop-shadow-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunDesktopLabelShadowMutationSmokeAsync();
                LogService.Info("DESKTOP_SHADOW_MUTATION_SMOKE_OK cases=10 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DESKTOP_SHADOW_MUTATION_SMOKE_FAIL", ex); Environment.Exit(76); }
            return true;
        }

        if (HasArg(cli, "--explorer-hubmode-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunExplorerHubModeMutationSmokeAsync();
                LogService.Info("EXPLORER_HUBMODE_MUTATION_SMOKE_OK cases=9 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("EXPLORER_HUBMODE_MUTATION_SMOKE_FAIL", ex); Environment.Exit(75); }
            return true;
        }

        if (HasArg(cli, "--tips-toast-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunTipsToastMutationSmokeAsync();
                LogService.Info("TIPS_TOAST_MUTATION_SMOKE_OK cases=8 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TIPS_TOAST_MUTATION_SMOKE_FAIL", ex); Environment.Exit(74); }
            return true;
        }

        if (HasArg(cli, "--security-notifications-mutation-smoke"))
        {
            try
            {
                await GTweakPortCatalog.RunSecurityNotificationsMutationSmokeAsync();
                LogService.Info("SECURITY_NOTIFICATIONS_MUTATION_SMOKE_OK cases=5 windowsWrites=0");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SECURITY_NOTIFICATIONS_MUTATION_SMOKE_FAIL", ex); Environment.Exit(73); }
            return true;
        }

        return false;
    }
}
