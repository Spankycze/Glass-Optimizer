namespace SpankyhoOptimizer.Services;

/// <summary>
/// Sidebars use two discrete content padding modes. Avoid setting the WinUI
/// Padding dependency property on every SizeChanged when the mode is unchanged,
/// as even equal Thickness values can enqueue unnecessary layout invalidation.
/// </summary>
internal static class ShellViewportPaddingPolicy
{
    internal static bool IsCompact(double viewportWidth) =>
        double.IsFinite(viewportWidth) && viewportWidth < 1180d;

    internal static bool NeedsPaddingUpdate(bool currentCompact, double viewportWidth) =>
        IsCompact(viewportWidth) != currentCompact;
}
