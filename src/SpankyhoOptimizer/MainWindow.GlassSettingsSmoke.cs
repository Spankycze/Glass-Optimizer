using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // RELEASE UI SMOKE ONLY: presses the same button factory used in Settings,
    // but injects an in-memory backing store. Never touches the user's motion
    // preference, Registry, network or Windows configuration.
    internal async Task RunGlassMotionResetUiSmokeAsync()
    {
        if (_themePreference != AppThemePreference.Dark)
            throw new InvalidOperationException("Single Dark Glass default was not active.");

        await ShowSectionAsync("settings");
        // Requests arriving while a low-priority stage is still running must
        // coalesce, not create a second set of controls or duplicate handlers.
        for (int attempt = 0; attempt < 12; attempt++) ScheduleDeferredSettingsBody();
        for (int attempt = 0; attempt < 150 &&
             (_settingsDeferredStage < 3 || _settingsDeferredQueued || _settingsDeferredRunning); attempt++)
            await Task.Delay(20);
        StackPanel? settingsBody = _settingsDeferredBody;
        if (_settingsDeferredStage != 3 || settingsBody == null || settingsBody.Children.Count != 3 ||
            _settingsDeferredQueued || _settingsDeferredRunning)
            throw new InvalidOperationException("Deferred Glass Settings built duplicate/incomplete stages or remained busy.");
        for (int attempt = 0; attempt < 12; attempt++) ScheduleDeferredSettingsBody();
        await Task.Delay(20);
        if (settingsBody.Children.Count != 3)
            throw new InvalidOperationException("Completed Glass settings were rendered more than once.");
        LogService.Info("release-ui-smoke",
            "GLASS_SETTINGS_SINGLE_FLIGHT_SMOKE_OK stages=3 duplicatePanels=0 queued=false running=false");

        var fixture = new StackPanel { Spacing = 4 };
        var toggle = new ToggleSwitch { IsOn = false };
        bool store = false;
        bool suppress = false;
        int writes = 0;
        int unexpectedToggles = 0;
        int uiUpdates = 0;
        toggle.Toggled += (_, _) =>
        {
            if (!suppress) unexpectedToggles++;
        };
        void ShowState(bool value)
        {
            suppress = true;
            try { if (toggle.IsOn != value) toggle.IsOn = value; }
            finally { suppress = false; }
            uiUpdates++;
        }

        var reset = BuildVerifiedMotionResetButton(
            toggle,
            () => store,
            value => { writes++; store = value; },
            ShowState,
            ex => throw new InvalidOperationException("Unexpected reset error in success test.", ex));
        fixture.Children.Add(toggle);
        fixture.Children.Add(reset);
        if (!reset.IsTabStop ||
            AutomationProperties.GetName(reset) != "Obnovit výchozí animace" ||
            string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(reset)) ||
            new ButtonAutomationPeer(reset).GetPattern(PatternInterface.Invoke) == null)
            throw new InvalidOperationException("Glass motion reset lacks keyboard/screen-reader Invoke accessibility.");

        var failingToggle = new ToggleSwitch { IsOn = false };
        int errors = 0;
        int attemptedWrites = 0;
        int failedUiUpdates = 0;
        var failureButton = BuildVerifiedMotionResetButton(
            failingToggle,
            () => false,
            _ => { attemptedWrites++; throw new IOException("Isolated simulated write failure."); },
            _ => failedUiUpdates++,
            _ => { errors++; return Task.CompletedTask; });
        fixture.Children.Add(failingToggle);
        fixture.Children.Add(failureButton);

        ContentStack.Children.Add(fixture);
        try
        {
            var resetPeer = new ButtonAutomationPeer(reset);
            resetPeer.Invoke();
            for (int i = 0; i < 40 && (uiUpdates != 1 || !Equals(reset.Content, "Obnovit výchozí animace")); i++) await Task.Delay(15);
            if (!store || !toggle.IsOn || writes != 1 || uiUpdates != 1 ||
                unexpectedToggles != 0)
                throw new InvalidOperationException(
                    "Motion reset failed to persist once and update only the verified UI.");

            resetPeer.Invoke();
            for (int i = 0; i < 40 && (uiUpdates != 2 || !Equals(reset.Content, "Obnovit výchozí animace")); i++) await Task.Delay(15);
            if (writes != 1 || uiUpdates != 2 || unexpectedToggles != 0)
                throw new InvalidOperationException(
                    "Repeated motion reset wrote unnecessarily or fired an unguarded toggle.");

            toggle.IsEnabled = false;
            resetPeer.Invoke();
            await Task.Delay(20);
            if (writes != 1 || uiUpdates != 2)
                throw new InvalidOperationException("Busy motion switch allowed a reset write.");

            var failedPeer = new ButtonAutomationPeer(failureButton);
            failedPeer.Invoke();
            for (int i = 0; i < 40 && (errors != 1 || !Equals(failureButton.Content, "Obnovit výchozí animace")); i++) await Task.Delay(15);
            if (errors != 1 || attemptedWrites != 1 || failedUiUpdates != 0 ||
                failingToggle.IsOn)
                throw new InvalidOperationException(
                    "Failed motion write did not report error or changed the displayed value.");

            if (AutomationProperties.GetName(reset) != "Obnovit výchozí animace")
                throw new InvalidOperationException("Motion reset lost its screen-reader name after Invoke.");
            LogService.Info("release-ui-smoke",
                "GLASS_MOTION_RESET_UI_SMOKE_OK actualButtonInvoke=true accessibleNameStable=true writes=1 duplicateWrites=0 " +
                "unguardedToggles=0 busyBlocked=true failingWriteReported=true " +
                "systemSettingsTouched=false");
        }
        finally
        {
            ContentStack.Children.Remove(fixture);
        }
    }
}
