using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark,
    SolidDark,
    MidnightBlue,
    PurpleAccent,
    CyanTech,
    RedPerformance
}

public static class AppThemeService
{
    private const string SettingsKey = @"Software\SpankyhoOptimizer\UI";

    public static AppThemePreference Normalize(AppThemePreference mode) =>
        System.Enum.IsDefined(mode) ? mode : AppThemePreference.Dark;

    public static AppThemePreference LoadPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            var raw = key?.GetValue("Theme")?.ToString();
            if (System.Enum.TryParse<AppThemePreference>(raw, true, out var mode)) return Normalize(mode);
        }
        catch (System.Exception ex)
        {
            LogService.Error("theme", "Theme preference load failed", ex);
        }
        return AppThemePreference.Dark;
    }

    public static void SavePreference(AppThemePreference mode)
    {
        try
        {
            mode = Normalize(mode);
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey, true);
            key?.SetValue("Theme", mode.ToString(), RegistryValueKind.String);
        }
        catch (System.Exception ex)
        {
            LogService.Error("theme", "Theme preference save failed", ex);
        }
    }

    public static bool ResolveDark(AppThemePreference mode)
    {
        return mode switch
        {
            AppThemePreference.Light => false,
            AppThemePreference.System => IsWindowsDarkMode(),
            _ => true
        };
    }

    public static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch { return false; }
    }

    public static bool IsWindowsTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("EnableTransparency");
            return value is not int i || i != 0;
        }
        catch { return true; }
    }
}
