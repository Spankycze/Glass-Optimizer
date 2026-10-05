using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static readonly ConditionalWeakTable<FrameworkElement, object> HoverMotionAttached = new();
    private static readonly object HoverMotionMarker = new();

    private static void AttachHoverMotion(FrameworkElement element, float hoverScale = 1.006f)
    {
        if (!SpankyhoOptimizer.Services.UiMotionSettingsService.Enabled) return;
        if (HoverMotionAttached.TryGetValue(element, out _)) return;
        HoverMotionAttached.Add(element, HoverMotionMarker);

        Visual? visual = null;
        Vector3KeyFrameAnimation? hoverScaleAnim = null;
        Vector3KeyFrameAnimation? normalScaleAnim = null;
        Vector3KeyFrameAnimation? pressedScaleAnim = null;
        ScalarKeyFrameAnimation? fullOpacityAnim = null;
        ScalarKeyFrameAnimation? restOpacityAnim = null;
        ScalarKeyFrameAnimation? pressedOpacityAnim = null;

        void AnimateTo(int state)
        {
            visual ??= ElementCompositionPreview.GetElementVisual(element);
            if (!SpankyhoOptimizer.Services.UiMotionSettingsService.Enabled)
            {
                visual.Scale = Vector3.One;
                visual.Opacity = 1f;
                return;
            }
            var compositor = visual.Compositor;
            visual.CenterPoint = new Vector3((float)(element.ActualWidth / 2), (float)(element.ActualHeight / 2), 0);

            Vector3KeyFrameAnimation scaleAnim;
            ScalarKeyFrameAnimation opacityAnim;
            switch (state)
            {
                case 0: // hover / released
                    scaleAnim = hoverScaleAnim ??= CreateHoverScaleAnimation(compositor, hoverScale);
                    opacityAnim = fullOpacityAnim ??= CreateHoverOpacityAnimation(compositor, 1f);
                    break;
                case 1: // pointer exited
                    scaleAnim = normalScaleAnim ??= CreateHoverScaleAnimation(compositor, 1f);
                    opacityAnim = restOpacityAnim ??= CreateHoverOpacityAnimation(compositor, 0.97f);
                    break;
                default: // pressed
                    scaleAnim = pressedScaleAnim ??= CreateHoverScaleAnimation(compositor, 0.985f);
                    opacityAnim = pressedOpacityAnim ??= CreateHoverOpacityAnimation(compositor, 0.92f);
                    break;
            }

            visual.StartAnimation("Scale", scaleAnim);
            visual.StartAnimation("Opacity", opacityAnim);
        }

        element.PointerEntered += (_, _) => AnimateTo(0);
        element.PointerExited += (_, _) => AnimateTo(1);
        element.PointerPressed += (_, _) => AnimateTo(2);
        element.PointerReleased += (_, _) => AnimateTo(0);
    }

    private static void AnimateDialogIn(FrameworkElement overlay, FrameworkElement card)
    {
        if (!SpankyhoOptimizer.Services.UiMotionSettingsService.Enabled) return;
        var overlayVisual = ElementCompositionPreview.GetElementVisual(overlay);
        var cardVisual = ElementCompositionPreview.GetElementVisual(card);
        var compositor = cardVisual.Compositor;
        overlayVisual.Opacity = 0f;
        cardVisual.Opacity = 0f;
        cardVisual.Scale = new Vector3(0.968f, 0.968f, 1);
        cardVisual.Offset = new Vector3(0, 9, 0);
        cardVisual.CenterPoint = new Vector3((float)(card.ActualWidth / 2), (float)(card.ActualHeight / 2), 0);

        var iosEase = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
        var overlayFade = compositor.CreateScalarKeyFrameAnimation();
        overlayFade.InsertKeyFrame(1f, 1f, iosEase);
        overlayFade.Duration = TimeSpan.FromMilliseconds(115);
        var cardFade = compositor.CreateScalarKeyFrameAnimation();
        cardFade.InsertKeyFrame(1f, 1f, iosEase);
        cardFade.Duration = TimeSpan.FromMilliseconds(145);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, Vector3.One, iosEase);
        scale.Duration = TimeSpan.FromMilliseconds(190);
        var lift = compositor.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(1f, Vector3.Zero, iosEase);
        lift.Duration = TimeSpan.FromMilliseconds(205);
        overlayVisual.StartAnimation("Opacity", overlayFade);
        cardVisual.StartAnimation("Opacity", cardFade);
        cardVisual.StartAnimation("Scale", scale);
        cardVisual.StartAnimation("Offset", lift);
    }

    private static void AnimateSectionIn(FrameworkElement element)
    {
        if (!SpankyhoOptimizer.Services.UiMotionSettingsService.Enabled || !element.IsLoaded) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.Opacity = 0.94f;
        visual.Offset = new Vector3(0, 7, 0);

        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(150);
        var lift = compositor.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(1f, Vector3.Zero, ease);
        lift.Duration = TimeSpan.FromMilliseconds(185);

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Offset", lift);
    }

    private static Vector3KeyFrameAnimation CreateHoverScaleAnimation(Compositor compositor, float scale)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1), ease);
        animation.Duration = TimeSpan.FromMilliseconds(135);
        return animation;
    }

    private static ScalarKeyFrameAnimation CreateHoverOpacityAnimation(Compositor compositor, float opacity)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
        animation.InsertKeyFrame(1f, opacity, ease);
        animation.Duration = TimeSpan.FromMilliseconds(120);
        return animation;
    }
}
