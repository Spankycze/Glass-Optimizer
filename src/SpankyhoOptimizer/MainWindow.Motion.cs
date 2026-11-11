using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static class Ios27Motion
    {
        public const int MicroMs = 110;
        public const int NormalMs = 170;
        public const int SectionMs = 185;
        public const int PopupMs = 220;
        public const int PopupExitMs = 155;
        public const float PressScale = 0.982f;
        public const float RestOpacity = 1f;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, object> HoverMotionAttached = new();
    private static readonly ConditionalWeakTable<ToggleSwitch, object> ToggleMotionAttached = new();
    private static readonly ConditionalWeakTable<Slider, object> SliderMotionAttached = new();
    private static readonly object MotionMarker = new();

    private static bool MotionEnabled => UiMotionSettingsService.EffectiveEnabled;

    private static CubicBezierEasingFunction CreateIosEase(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

    private static CubicBezierEasingFunction CreateIosSpringEase(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.18f), new Vector2(0.30f, 1f));

    private static void ResetMotionVisual(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        // Never assign Visual.Offset on XAML-owned visuals: XAML layout owns that value.
        // Only stop any handoff animation so layout immediately regains control.
        visual.StopAnimation("Offset");
        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");
        // Native Composition property writes are comparatively expensive. Most
        // persistent navigation elements are already in their resting state.
        if (visual.Scale != Vector3.One) visual.Scale = Vector3.One;
        if (visual.Opacity != 1f) visual.Opacity = 1f;
    }

    private static void AttachHoverMotion(FrameworkElement element, float hoverScale = 1.006f)
    {
        // Persistent controls/cards must never own compositor opacity/offset/scale animations.
        // They caused intermittent invisible Overview/sidebar content after rapid navigation.
        if (element.IsLoaded)
            ResetMotionVisual(element);
    }

    private static void AttachToggleMotion(ToggleSwitch toggle)
    {
        if (toggle.IsLoaded)
            ResetMotionVisual(toggle);
    }

    private static void AttachSliderMotion(Slider slider)
    {
        if (slider.IsLoaded)
            ResetMotionVisual(slider);
    }

    private static void AttachIosMotionTree(DependencyObject root)
    {
        // Disabled for persistent page trees. Recursive compositor event attachment made
        // reused/cached WinUI elements capable of keeping stale visual state after navigation.
        // Dialog enter/exit motion remains isolated below.
    }

    private static void AnimateDialogIn(FrameworkElement overlay, FrameworkElement card)
    {
        if (!MotionEnabled)
        {
            ResetMotionVisual(overlay);
            ResetMotionVisual(card);
            return;
        }

        var overlayVisual = ElementCompositionPreview.GetElementVisual(overlay);
        var cardVisual = ElementCompositionPreview.GetElementVisual(card);
        var compositor = cardVisual.Compositor;
        var ease = CreateIosEase(compositor);
        var spring = CreateIosSpringEase(compositor);

        overlayVisual.Opacity = 0f;
        cardVisual.Opacity = 0f;
        cardVisual.Scale = new Vector3(0.985f, 0.985f, 1);
        cardVisual.CenterPoint = new Vector3((float)(card.ActualWidth / 2), (float)(card.ActualHeight / 2), 0);

        var overlayFade = compositor.CreateScalarKeyFrameAnimation();
        overlayFade.InsertKeyFrame(1f, 1f, ease);
        overlayFade.Duration = TimeSpan.FromMilliseconds(Ios27Motion.MicroMs + 20);

        var cardFade = compositor.CreateScalarKeyFrameAnimation();
        cardFade.InsertKeyFrame(1f, 1f, ease);
        cardFade.Duration = TimeSpan.FromMilliseconds(Ios27Motion.NormalMs);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0.72f, new Vector3(1.002f, 1.002f, 1), spring);
        scale.InsertKeyFrame(1f, Vector3.One, spring);
        scale.Duration = TimeSpan.FromMilliseconds(180);

        overlayVisual.StartAnimation("Opacity", overlayFade);
        cardVisual.StartAnimation("Opacity", cardFade);
        cardVisual.StartAnimation("Scale", scale);
    }

    private static void AnimateDialogOut(FrameworkElement overlay, FrameworkElement card, Action completed)
    {
        if (!MotionEnabled || !overlay.IsLoaded)
        {
            completed();
            return;
        }

        var overlayVisual = ElementCompositionPreview.GetElementVisual(overlay);
        var cardVisual = ElementCompositionPreview.GetElementVisual(card);
        var compositor = cardVisual.Compositor;
        var ease = CreateIosEase(compositor);
        cardVisual.CenterPoint = new Vector3((float)(card.ActualWidth / 2), (float)(card.ActualHeight / 2), 0);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var overlayFade = compositor.CreateScalarKeyFrameAnimation();
        overlayFade.InsertKeyFrame(1f, 0f, ease);
        overlayFade.Duration = TimeSpan.FromMilliseconds(Ios27Motion.PopupExitMs - 25);
        overlayVisual.StartAnimation("Opacity", overlayFade);

        var cardFade = compositor.CreateScalarKeyFrameAnimation();
        cardFade.InsertKeyFrame(1f, 0f, ease);
        cardFade.Duration = TimeSpan.FromMilliseconds(Ios27Motion.PopupExitMs);
        cardVisual.StartAnimation("Opacity", cardFade);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, new Vector3(0.990f, 0.990f, 1), ease);
        scale.Duration = TimeSpan.FromMilliseconds(Ios27Motion.PopupExitMs);
        cardVisual.StartAnimation("Scale", scale);

        batch.End();
        batch.Completed += (_, _) =>
            overlay.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                () => completed());
    }

    private static void AnimateSectionOut(FrameworkElement element)
    {
        // ContentStack is a persistent host. Animating the host itself can leave the next page
        // inheriting a stale compositor opacity/offset when navigation is superseded.
        // Keep page containers deterministic; motion belongs inside transient controls/dialogs.
        if (element.IsLoaded) ResetMotionVisual(element);
    }

    private static void AnimateSectionIn(FrameworkElement element)
    {
        // Never animate the persistent ContentStack host. This guarantees every page starts
        // at full opacity and zero composition offset even after rapid/re-entrant navigation.
        if (element.IsLoaded) ResetMotionVisual(element);
    }

    private static Vector3KeyFrameAnimation CreateScaleAnimation(Compositor compositor, float scale, int durationMs, bool spring)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        var ease = spring ? CreateIosSpringEase(compositor) : CreateIosEase(compositor);
        if (spring && scale >= 1f)
            animation.InsertKeyFrame(0.62f, new Vector3(scale + 0.004f, scale + 0.004f, 1), ease);
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1), ease);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        return animation;
    }

    private static Vector3KeyFrameAnimation CreateOffsetAnimation(Compositor compositor, float y, int durationMs, bool spring)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        var ease = spring ? CreateIosSpringEase(compositor) : CreateIosEase(compositor);
        if (spring && y <= 0f)
            animation.InsertKeyFrame(0.62f, new Vector3(0, y - 0.35f, 0), ease);
        animation.InsertKeyFrame(1f, new Vector3(0, y, 0), ease);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        return animation;
    }

    private static ScalarKeyFrameAnimation CreateOpacityAnimation(Compositor compositor, float opacity, int durationMs)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, opacity, CreateIosEase(compositor));
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        return animation;
    }
}
