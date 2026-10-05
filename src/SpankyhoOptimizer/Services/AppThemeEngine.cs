namespace SpankyhoOptimizer.Services;

public readonly record struct ThemeRgb(byte R, byte G, byte B);

public sealed record AppThemePalette(
    ThemeRgb TextPrimary,
    ThemeRgb TextSecondary,
    ThemeRgb Surface,
    ThemeRgb SurfaceElevated,
    ThemeRgb SurfaceAlt,
    ThemeRgb Border,
    ThemeRgb Accent,
    ThemeRgb AccentSoft,
    ThemeRgb HeroAccent,
    ThemeRgb SectionMarker,
    ThemeRgb PrimaryButton,
    ThemeRgb PrimaryButtonSoft,
    ThemeRgb PrimaryButtonBorder,
    ThemeRgb Success,
    ThemeRgb Warning,
    ThemeRgb Info,
    ThemeRgb SectionHeaderStart,
    ThemeRgb SectionHeaderMid,
    ThemeRgb SectionHeaderEnd);

public static class AppThemeEngine
{
    private static readonly AppThemePalette GraphiteDark = new(
        new(242,242,242), new(176,176,176),
        new(0,0,0), new(4,4,4), new(0,0,0), new(24,24,24),
        new(99,161,212), new(8,17,24), new(99,161,212), new(99,161,212),
        new(7,7,7), new(2,2,2), new(54,92,122),
        new(112,184,140), new(221,176,88), new(99,161,212),
        new(8,8,8), new(5,5,5), new(0,0,0));

    private static readonly AppThemePalette MidnightBlue = new(
        new(241,246,252), new(149,165,184),
        new(4,7,11), new(7,12,18), new(2,5,8), new(24,42,58),
        new(67,147,255), new(14,35,58), new(76,126,182), new(58,111,168),
        new(63,120,211), new(18,31,48), new(51,91,145),
        new(73,220,154), new(245,190,72), new(70,184,238),
        new(10,21,36), new(13,28,47), new(16,34,56));

    private static readonly AppThemePalette PurpleAccent = new(
        new(244,241,250), new(163,153,182),
        new(6,5,9), new(10,8,14), new(3,2,6), new(40,32,50),
        new(123,91,255), new(34,27,48), new(123,91,255), new(104,76,210),
        new(126,89,225), new(37,28,51), new(101,76,170),
        new(80,220,142), new(247,188,59), new(94,179,238),
        new(23,18,33), new(31,23,43), new(37,27,50));

    private static readonly AppThemePalette CyanTech = new(
        new(238,247,250), new(143,167,176),
        new(4,7,8), new(7,12,14), new(2,4,5), new(24,45,50),
        new(36,210,255), new(14,40,47), new(45,183,214), new(35,157,188),
        new(40,168,199), new(16,37,42), new(40,116,130),
        new(75,221,152), new(244,190,70), new(49,196,226),
        new(11,24,28), new(14,31,36), new(17,38,44));

    private static readonly AppThemePalette RedPerformance = new(
        new(247,241,241), new(177,151,151),
        new(7,4,5), new(12,7,8), new(4,2,3), new(48,29,33),
        new(232,80,86), new(47,24,28), new(196,86,94), new(170,66,76),
        new(202,66,73), new(46,24,28), new(145,54,59),
        new(78,219,140), new(246,186,66), new(232,120,96),
        new(29,17,20), new(39,22,25), new(47,26,30));

    private static readonly AppThemePalette WhiteLight = new(
        new(30,33,43), new(96,103,120),
        new(250,251,253), new(255,255,255), new(244,246,250), new(218,222,232),
        new(56,116,224), new(234,241,253), new(76,108,156), new(56,116,224),
        new(38,38,38), new(242,242,242), new(198,198,198),
        new(21,146,74), new(217,119,6), new(2,132,199),
        new(252,253,255), new(247,249,252), new(242,245,250));

    public static AppThemePalette Resolve(AppThemePreference preference, bool dark)
    {
        if (!dark || preference == AppThemePreference.Light)
            return WhiteLight;

        return preference switch
        {
            AppThemePreference.MidnightBlue => MidnightBlue,
            AppThemePreference.PurpleAccent => PurpleAccent,
            AppThemePreference.CyanTech => CyanTech,
            AppThemePreference.RedPerformance => RedPerformance,
            _ => GraphiteDark
        };
    }

    public static string DisplayName(AppThemePreference preference) => preference switch
    {
        AppThemePreference.System => "Podle systému",
        AppThemePreference.Light => "White / Light",
        AppThemePreference.Dark => "Graphite Dark",
        AppThemePreference.MidnightBlue => "Midnight Blue",
        AppThemePreference.PurpleAccent => "Purple Accent",
        AppThemePreference.CyanTech => "Cyan Tech",
        AppThemePreference.RedPerformance => "Red Performance",
        _ => preference.ToString()
    };
}
