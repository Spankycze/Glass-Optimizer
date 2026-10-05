using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static void AttachHoverMotion(FrameworkElement element, float hoverScale = 1.006f)
    {
        if (!SpankyhoOptimizer.Services.UiMotionSettingsService.Enabled) return;

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

        var overlayFade = compositor.CreateScalarKeyFrameAnimation();
        overlayFade.InsertKeyFrame(1f, 1f);
        overlayFade.Duration = TimeSpan.FromMilliseconds(120);
        var cardFade = compositor.CreateScalarKeyFrameAnimation();
        cardFade.InsertKeyFrame(1f, 1f);
        cardFade.Duration = TimeSpan.FromMilliseconds(135);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, Vector3.One);
        scale.Duration = TimeSpan.FromMilliseconds(165);
        var lift = compositor.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(1f, Vector3.Zero);
        lift.Duration = TimeSpan.FromMilliseconds(175);
        overlayVisual.StartAnimation("Opacity", overlayFade);
        cardVisual.StartAnimation("Opacity", cardFade);
        cardVisual.StartAnimation("Scale", scale);
        cardVisual.StartAnimation("Offset", lift);
    }

    private static Vector3KeyFrameAnimation CreateHoverScaleAnimation(Compositor compositor, float scale)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1));
        animation.Duration = TimeSpan.FromMilliseconds(105);
        return animation;
    }

    private static ScalarKeyFrameAnimation CreateHoverOpacityAnimation(Compositor compositor, float opacity)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, opacity);
        animation.Duration = TimeSpan.FromMilliseconds(90);
        return animation;
    }
}
