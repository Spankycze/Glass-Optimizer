using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Only called by the explicit glass-stability test. No production polling or network access.
    private async Task RunGlassContinuationChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("native-card-detach-reload-lifetime", VerifyGlassCardDetachReloadAsync);
        await check("closed-popup-releases-native-card", VerifyPopupNativeDetachAsync);
        await check("native-card-retains-active-material", async () =>
        {
            var card = CreateNativeGlassElement(new CornerRadius(22));
            card.Width = 160; card.Height = 100;
            RootGrid.Children.Add(card);
            try { await Task.Delay(180); await VerifyElementActiveMaterialAsync(card); }
            finally { RootGrid.Children.Remove(card); }
        });
        await check("popup-inner-card-retains-active-material", async () =>
        {
            var popup = MakeContinuationPopup((_, _) => Task.FromResult(new DesktopUpdateCompletion("Ready", "Close")));
            try
            {
                popup.ShowWithoutActivation(); await Task.Delay(180);
                var elements = new List<SystemBackdropElement>();
                CollectBackdropElements((DependencyObject)popup.Content, elements);
                if (elements.Count != 1) throw new InvalidOperationException("Expected one inner backdrop, got " + elements.Count);
                await VerifyElementActiveMaterialAsync(elements[0]);
            }
            finally { popup.Close(); }
        });
        await check("popup-czech-download-labels", VerifyPopupCzechLabelsAsync);
        await check("popup-error-retry-and-double-click", VerifyPopupRetryAsync);
        await check("popup-respects-disabled-motion", VerifyPopupReducedMotionAsync);
        await check("popup-progress-buttons-inside-window", VerifyPopupProgressBoundsAsync);
        await check("popup-rounded-window-region", async () =>
        {
            var popup = MakeContinuationPopup((_, _) => Task.FromResult(new DesktopUpdateCompletion("Ready", "Close")));
            try
            {
                popup.ShowWithoutActivation(); await Task.Delay(100);
                if (!NativeTitleBarService.VerifyRoundedGlassRegion(WinRT.Interop.WindowNative.GetWindowHandle(popup), out string detail))
                    throw new InvalidOperationException("Popup still has rectangular native frame: " + detail);
            }
            finally { popup.Close(); }
        });
    }


    private static DesktopUpdateNotificationWindow MakeContinuationPopup(
        Func<IProgress<DesktopUpdateProgress>, System.Threading.CancellationToken, Task<DesktopUpdateCompletion>> action) =>
        new("TEST", "Spankyho Optimizer", "15.4.733", "Testovaci aktualizace. Zadny soubor se nestahuje.", action);

    private static T PopupField<T>(DesktopUpdateNotificationWindow popup, string name) =>
        (T)(typeof(DesktopUpdateNotificationWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(popup) ?? throw new InvalidOperationException("Missing popup field " + name));
    private static Task InvokePopupPrimaryAsync(DesktopUpdateNotificationWindow popup) =>
        (Task)typeof(DesktopUpdateNotificationWindow).GetMethod("OnPrimaryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(popup, null)!;

    private static void CollectBackdropElements(DependencyObject parent, List<SystemBackdropElement> result)
    {
        if (parent is SystemBackdropElement element) result.Add(element);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            CollectBackdropElements(VisualTreeHelper.GetChild(parent, i), result);
    }

    private static async Task VerifyElementActiveMaterialAsync(SystemBackdropElement element)
    {
        var backdrop = element.SystemBackdrop ?? throw new InvalidOperationException("Element has no backdrop.");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var config = backdrop.GetType().GetField("_defaultConfiguration", flags)?.GetValue(backdrop) as SystemBackdropConfiguration;
        var controller = backdrop.GetType().GetField("_controller", flags)?.GetValue(backdrop) as DesktopAcrylicController;
        if (config == null || controller == null)
            throw new InvalidOperationException("Element still uses default focus-sensitive material instead of the independent active policy: " + backdrop.GetType().Name);
        bool active = config.IsInputActive;
        try
        {
            config.IsInputActive = true; await Task.Delay(100);
            if (controller.State != SystemBackdropState.Active) throw new InvalidOperationException("No active acrylic baseline: " + controller.State);
            config.IsInputActive = false; await Task.Delay(100);
            if (controller.State != SystemBackdropState.Active) throw new InvalidOperationException("Element fell back on focus loss: " + controller.State);
        }
        finally { config.IsInputActive = active; }
    }

    private static async Task VerifyPopupCzechLabelsAsync()
    {
        var finish = new TaskCompletionSource<DesktopUpdateCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var popup = MakeContinuationPopup((_, _) => finish.Task);
        Task? action = null;
        try
        {
            popup.ShowWithoutActivation(); await Task.Delay(80);
            action = InvokePopupPrimaryAsync(popup);
            if (!Equals(PopupField<Button>(popup, "_secondary").Content, "Zru\u0161it"))
                throw new InvalidOperationException("Cancel label is corrupted: " + PopupField<Button>(popup, "_secondary").Content);
            if (PopupField<TextBlock>(popup, "_status").Text != "P\u0159ipravuji stahov\u00e1n\u00ed\u2026")
                throw new InvalidOperationException("Preparing-download status is corrupted.");
            finish.TrySetResult(new DesktopUpdateCompletion("Ready", "Install"));
            await action;
            if (!Equals(PopupField<Button>(popup, "_secondary").Content, "Zav\u0159\u00edt"))
                throw new InvalidOperationException("Close label is corrupted.");
        }
        finally { finish.TrySetResult(new DesktopUpdateCompletion("Ready", "Close")); if (action != null) await action; popup.Close(); }
    }

    private static async Task VerifyPopupRetryAsync()
    {
        int downloads = 0, completions = 0;
        var finish = new TaskCompletionSource<DesktopUpdateCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var popup = MakeContinuationPopup((_, _) => ++downloads == 1
            ? Task.FromException<DesktopUpdateCompletion>(new InvalidOperationException("test failure")) : finish.Task);
        Task? pending = null;
        try
        {
            popup.ShowWithoutActivation(); await Task.Delay(80);
            await InvokePopupPrimaryAsync(popup);
            if (PopupField<TextBlock>(popup, "_status").Text != "Aktualizaci se nepoda\u0159ilo st\u00e1hnout.")
                throw new InvalidOperationException("Error label is corrupted: " + PopupField<TextBlock>(popup, "_status").Text);
            // Actual error state is red, never the normal cyan status.
            var failed = ((SolidColorBrush)PopupField<TextBlock>(popup, "_percent").Foreground).Color;
            if (failed.R < 200 || failed.B > 190)
                throw new InvalidOperationException("Download failure still uses the blue success accent.");
            if (!PopupField<Button>(popup, "_primary").IsEnabled) throw new InvalidOperationException("Retry remains disabled.");
            pending = InvokePopupPrimaryAsync(popup);
            var retryAccent = ((SolidColorBrush)PopupField<TextBlock>(popup, "_percent").Foreground).Color;
            if (retryAccent.B < 230 || retryAccent.R > 110)
                throw new InvalidOperationException("Retry did not reset the normal blue progress tone.");
            await InvokePopupPrimaryAsync(popup);
            if (downloads != 2) throw new InvalidOperationException("Double click duplicated the download.");
            finish.SetResult(new DesktopUpdateCompletion("Ready", "Install", () => { completions++; return Task.CompletedTask; }));
            await pending; await InvokePopupPrimaryAsync(popup); await InvokePopupPrimaryAsync(popup);
            if (completions != 1) throw new InvalidOperationException("Completion action was not exactly once.");
        }
        finally { finish.TrySetResult(new DesktopUpdateCompletion("Ready", "Close")); if (pending != null) await pending; popup.Close(); }
    }

    private static Task VerifyPopupReducedMotionAsync()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var smokeField = typeof(App).GetField("<IsSmokeUi>k__BackingField", flags)!;
        var enabledField = typeof(UiMotionSettingsService).GetField("<Enabled>k__BackingField", flags)!;
        object? oldSmoke = smokeField.GetValue(null), oldEnabled = enabledField.GetValue(null);
        var popup = MakeContinuationPopup((_, _) => Task.FromResult(new DesktopUpdateCompletion("Ready", "Close")));
        var visual = ElementCompositionPreview.GetElementVisual((UIElement)popup.Content);
        try
        {
            // Only change in-memory test flags. Do not write the user's motion preference.
            smokeField.SetValue(null, false); enabledField.SetValue(null, false);
            visual.Opacity = 1f;
            typeof(DesktopUpdateNotificationWindow).GetMethod("RunEntranceAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(popup, null);
            if (visual.Opacity < .999f) throw new InvalidOperationException("Popup fades even though animations are disabled.");
        }
        finally
        {
            smokeField.SetValue(null, oldSmoke); enabledField.SetValue(null, oldEnabled);
            visual.StopAnimation("Opacity"); visual.Opacity = 1f; popup.Close();
        }
        return Task.CompletedTask;
    }

    private static async Task VerifyPopupProgressBoundsAsync()
    {
        IProgress<DesktopUpdateProgress>? progress = null;
        var finish = new TaskCompletionSource<DesktopUpdateCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var popup = new DesktopUpdateNotificationWindow("TEST", "Spankyho Optimizer", "15.4.733 - aktualizace",
            "Podrobnosti aktualizace na dvou radcich, aby se overilo, ze obsah a ovladaci prvky zustanou uvnitr okna.",
            (p, _) => { progress = p; return finish.Task; });
        Task? action = null;
        try
        {
            popup.ShowWithoutActivation(); await Task.Delay(100); action = InvokePopupPrimaryAsync(popup);
            progress!.Report(new DesktopUpdateProgress(42, "Stahovani", "Podrobny prubeh aktualizace a informace o velikosti souboru, ktere potrebuji dva radky v notifikaci."));
            await Task.Delay(200);
            var root = (FrameworkElement)popup.Content; root.UpdateLayout();
            foreach (string name in new[] { "_primary", "_secondary" })
            {
                var button = PopupField<Button>(popup, name);
                var point = button.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (button.ActualWidth <= 0 || button.ActualHeight <= 0 || point.Y < 0 || point.Y + button.ActualHeight > root.ActualHeight - 4)
                    throw new InvalidOperationException($"{name} clipped: top={point.Y:F1} height={button.ActualHeight:F1} window={root.ActualHeight:F1}");
            }
        }
        finally { finish.TrySetResult(new DesktopUpdateCompletion("Ready", "Close")); if (action != null) await action; popup.Close(); }
    }
}
