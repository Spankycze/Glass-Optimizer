using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static readonly JsonSerializerOptions GlassReportOptions = new() { WriteIndented = true };
    // Explicit smoke only. No timers, reflection, or reports on the normal startup path.
    internal async Task RunGlassStabilitySmokeAsync()
    {
        var results = new List<object>();
        var failures = new List<string>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); results.Add(new { name, outcome = "PASS" }); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); results.Add(new { name, outcome = "FAIL", error = ex.ToString() }); }
        }

        await Check("native-glass-card-single-tint-layer", () =>
        {
            var payload = new TextBlock { Text = "smoke" };
            var card = BlackCard(payload);
            if (UseGlassChrome &&
                (card.Child is not Grid layers ||
                 layers.Children.Count != 2 ||
                 layers.Children[0] is not SystemBackdropElement ||
                 layers.Children[1] is not Border pad ||
                 !ReferenceEquals(pad.Child, payload) ||
                 pad.Background is not LinearGradientBrush ||
                 pad.Padding.Left != 18 ||
                 pad.CornerRadius != card.CornerRadius))
                throw new InvalidOperationException("Glass card layer merge changed backdrop/tint/content ordering.");
            var reference = ReferenceCard(new TextBlock { Text = "reference" });
            if (UseGlassChrome && (reference.Child is not Grid refLayers ||
                refLayers.Children.Count != 2 ||
                refLayers.Children[1] is not Border refPad ||
                refPad.Padding.Left != 0 ||
                refPad.Background is not LinearGradientBrush))
                throw new InvalidOperationException("Reference card tint or zero-padding regressed.");
            return Task.CompletedTask;
        });
        await Check("shared-card-shadow-resource", () =>
        {
            if (UseGlassChrome)
            {
                var first = BlackCard(new TextBlock { Text = "shadow-a" });
                var second = BlackCard(new TextBlock { Text = "shadow-b" });
                if (first.Shadow is not ThemeShadow ||
                    !ReferenceEquals(first.Shadow, second.Shadow) ||
                    !ReferenceEquals(first.Shadow, _sharedSurfaceShadow))
                    throw new InvalidOperationException("Glass cards did not share one native ThemeShadow resource.");
                if (first.Translation.Z != 8 || second.Translation.Z != 8)
                    throw new InvalidOperationException("Shared shadow altered native card elevation.");
                if (!UseCompactSectionChrome)
                {
                    var legacyA = BuildDashboardCard(new TextBlock { Text = "legacy-a" });
                    var legacyB = BuildDashboardCard(new TextBlock { Text = "legacy-b" });
                    if (!ReferenceEquals(legacyA.Shadow, first.Shadow) ||
                        !ReferenceEquals(legacyA.Shadow, legacyB.Shadow) ||
                        legacyA.Translation.Z != 7 || legacyB.Translation.Z != 7)
                        throw new InvalidOperationException("Legacy dashboard shadow sharing altered elevation.");
                }
            }
            return Task.CompletedTask;
        });
        await Check("native-gradient-resource-reuse", () =>
        {
            var a = Windows.UI.Color.FromArgb(25, 44, 83, 132);
            var b = Windows.UI.Color.FromArgb(36, 53, 95, 150);
            if (!ReferenceEquals(GlassGradient(a, b), GlassGradient(a, b)))
                throw new InvalidOperationException("Equal two-stop glass gradients were recreated.");
            if (ReferenceEquals(GlassGradient(a, b), GlassGradient(b, a)))
                throw new InvalidOperationException("Different gradient stops accidentally shared a brush.");
            if (!ReferenceEquals(ReferenceThinRim(), ReferenceThinRim()) ||
                !ReferenceEquals(ReferenceGlassSurface(), ReferenceGlassSurface()) ||
                !ReferenceEquals(ReferenceGlassSurface(ReferenceCpuAccent()), ReferenceGlassSurface(ReferenceCpuAccent())) ||
                !ReferenceEquals(ReferenceColor(21, 44, 67), ReferenceColor(21, 44, 67)))
                throw new InvalidOperationException("Identical native overview resources were recreated.");
            if (ReferenceEquals(ReferenceGlassSurface(ReferenceCpuAccent()), ReferenceGlassSurface(ReferenceRamAccent())))
                throw new InvalidOperationException("Distinct overview accents collided in gradient cache.");
            if (GlassGradientCache.Count > 128 || ReferenceSurfaceCache.Count > 64)
                throw new InvalidOperationException("Native gradient cache exceeded its size budget.");
            return Task.CompletedTask;
        });
        await Check("native-acrylic-surface-inventory", async () =>
        {
            // The first Overview template can still be connecting its native
            // backdrop targets when a release smoke starts immediately after
            // Window.Activate. Wait for actual Loaded states without sleeping
            // on the WinUI dispatcher, and keep the original >=6 requirement.
            // This is strictly an explicit smoke path, never production polling.
            var layers = new List<SystemBackdropElement>();
            var readiness = System.Diagnostics.Stopwatch.StartNew();
            int active;
            while (true)
            {
                layers.Clear();
                CollectBackdropElements(RootGrid, layers);
                active = 0;
                foreach (var element in layers)
                    if (element.IsLoaded && element.SystemBackdrop is OptimizerGlassBackdrop)
                        active++;
                if (!(UseGlassChrome && _section == "overview") || active >= 6 ||
                    readiness.Elapsed >= TimeSpan.FromSeconds(2))
                    break;
                await Task.Delay(60);
            }
            if (UseGlassChrome && _section == "overview" && active < 6)
                throw new InvalidOperationException(
                    $"Only {active} active native glass layers on Overview (expected at least six), " +
                    $"total={layers.Count}, rootLoaded={RootGrid.IsLoaded}, waitedMs={readiness.ElapsedMilliseconds}.");
            SpankyhoOptimizer.Services.LogService.Info("performance",
                $"GLASS_NATIVE_SURFACE_INVENTORY section={_section} active={active} total={layers.Count} waitMs={readiness.ElapsedMilliseconds}");
        });
        await Check("legacy-telemetry-clip-retained-across-resize", () =>
        {
            var card = BuildBlackTelemetryCard("CPU", "Test", ReferenceCpuAccent(), out var refs);
            card.Width = 280;
            card.Height = 240;
            RootGrid.Children.Add(card);
            try
            {
                RootGrid.UpdateLayout();
                var clip = refs.SparkHost.Clip as RectangleGeometry
                    ?? throw new InvalidOperationException("Telemetry card has no native clip geometry.");
                double firstWidth = clip.Rect.Width;
                card.Width = 330;
                RootGrid.UpdateLayout();
                if (!ReferenceEquals(clip, refs.SparkHost.Clip))
                    throw new InvalidOperationException("Canvas clip resource was replaced on resize.");
                if (clip.Rect.Width < firstWidth + 10)
                    throw new InvalidOperationException("Retained canvas clip did not adapt to width.");
            }
            finally { RootGrid.Children.Remove(card); }
            return Task.CompletedTask;
        });
        await Check("native-titlebar-colors-idempotent-across-focus", () =>
        {
            // Explicit-only native HWND test: unchanged focus/theme must not
            // send the same 12 WinRT property writes to DWM twice. A forced
            // palette-cache miss must still reapply everything correctly.
            ApplyWindowTitleBarTheme();
            int applied = _nativeCaptionColorApplyCount;
            int skipped = _nativeCaptionColorSkipCount;
            ApplyWindowTitleBarTheme();
            if (_nativeCaptionColorApplyCount != applied ||
                _nativeCaptionColorSkipCount != skipped + 1)
                throw new InvalidOperationException(
                    "Unchanged native caption palette was resent to DWM.");
            _appliedNativeCaptionColors = null;
            ApplyWindowTitleBarTheme();
            if (_nativeCaptionColorApplyCount != applied + 1 ||
                _cachedCaptionTitleBar?.ExtendsContentIntoTitleBar != true)
                throw new InvalidOperationException(
                    "A changed native caption palette did not reapply.");
            VerifyHeroDragRegions();
            int sourceAcquisitions = _heroDragPointerSourceAcquireCount;
            // Force a native region rewrite without changing the HWND. This
            // exercises the reuse path that matters during live resize/DPI.
            _heroDragSignature = null;
            UpdateHeroDragRegions();
            VerifyHeroDragRegions();
            UpdateHeroDragRegions();
            if (sourceAcquisitions != 1 ||
                _heroDragPointerSourceAcquireCount != sourceAcquisitions)
                throw new InvalidOperationException(
                    $"Stable HWND should acquire one native caption pointer source, observed {_heroDragPointerSourceAcquireCount}.");
            return Task.CompletedTask;
        });
        await Check("main-acrylic-retains-active-state", () => VerifyAcrylicFocusTransitionAsync(this));
        var popup = new DesktopUpdateNotificationWindow("Test", "Focus lifecycle", "Smoke", "No download occurs.",
            (_, _) => Task.FromResult(new DesktopUpdateCompletion("Ready", "Close")));
        try
        {
            popup.ShowWithoutActivation();
            await Task.Delay(250);
            await Check("popup-acrylic-retains-active-state", () => VerifyAcrylicFocusTransitionAsync(popup));
        }
        finally { popup.Close(); }
        await Check("closed-popup-ignores-late-completion", VerifyClosedPopupCompletionAsync);
        await RunGlassContinuationChecksAsync(Check);
        await Check("popup-retry-rejects-stale-progress", () => VerifyPopupRetryProgressAsync(false));
        await Check("popup-retry-resets-filled-track", () => VerifyPopupRetryProgressAsync(true));
        await Check("popup-has-no-native-frame-bits", () => VerifyPopupNativeFrame735Async(false));
        await Check("popup-fixed-size-has-no-resize-grip", () => VerifyPopupNativeFrame735Async(true));
        await Check("closed-popup-wrappers-survive-collection", async () =>
        {
            // Explicit test only. Leave XAML responsive while finalizers run.
            await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            await Task.Delay(150);
        });
        await RunVisibleGlassCaptureIfRequestedAsync();

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SpankyhoOptimizer", "glass-stability-smoke.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
            utc = DateTime.UtcNow, passed = failures.Count == 0, results
        }, GlassReportOptions));
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(" | ", failures));
    }

    private static async Task VerifyAcrylicFocusTransitionAsync(Window window)
    {
        var root = (FrameworkElement)window.Content;
        var backdrop = window.SystemBackdrop ?? throw new InvalidOperationException("No backdrop.");
        var controller = backdrop.GetType().GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(backdrop) as DesktopAcrylicController
            ?? throw new InvalidOperationException("No connected acrylic controller.");
        var config = backdrop.GetType().GetField("_defaultConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(backdrop) as SystemBackdropConfiguration
            ?? throw new InvalidOperationException("Default configuration was not captured from the connected target.");
        bool original = config.IsInputActive;
        try
        {
            config.IsInputActive = true;
            await Task.Delay(200);
            if (controller.State != SystemBackdropState.Active)
                throw new InvalidOperationException("Active acrylic baseline unavailable: " + controller.State);
            // Reproduce the actual default policy update made by WinUI on focus loss.
            config.IsInputActive = false;
            await Task.Delay(200);
            if (controller.State != SystemBackdropState.Active)
                throw new InvalidOperationException("Focus loss switched the existing controller to " + controller.State);
        }
        finally { config.IsInputActive = original; }
    }

    private static async Task VerifyClosedPopupCompletionAsync()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<DesktopUpdateCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var popup = new DesktopUpdateNotificationWindow("Test", "Late completion", "Smoke", "No network activity.",
            (_, token) => { captured = token; entered.TrySetResult(true); return finish.Task; });
        popup.ShowWithoutActivation();
        await Task.Delay(100);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var method = typeof(DesktopUpdateNotificationWindow).GetMethod("OnPrimaryAsync", flags)!;
        var status = (TextBlock)typeof(DesktopUpdateNotificationWindow).GetField("_status", flags)!.GetValue(popup)!;
        Task action = (Task)method.Invoke(popup, null)!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        string before = status.Text;
        popup.Close();
        if (!captured.IsCancellationRequested) throw new InvalidOperationException("Close did not cancel the operation.");
        finish.SetResult(new DesktopUpdateCompletion("LATE_RESULT_MUST_BE_IGNORED", "Install", () => Task.CompletedTask));
        await action.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
        if (!string.Equals(before, status.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("A completed download mutated a closed popup: " + status.Text);
    }
}
