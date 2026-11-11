using System;
using System.Globalization;

namespace SpankyhoOptimizer.Services;

public enum UiLanguage
{
    Czech,
    English
}

public static class LanguageService
{
    private static readonly object Gate = new();
    private static UiLanguage _current = Load();

    public static UiLanguage Current
    {
        get { lock (Gate) return _current; }
    }

    public static bool IsEnglish => Current == UiLanguage.English;

    public static string[] Options => new[] { "Čeština", "English" };

    public static void Set(UiLanguage language)
    {
        lock (Gate) _current = language;
        UiStateService.Save(UiStateService.Load() with { Language = language.ToString() });
    }

    public static string Text(string czech, string english) => IsEnglish ? english : czech;

    private static UiLanguage Load()
    {
        string? value = UiStateService.Load().Language;
        return Enum.TryParse(value, ignoreCase: true, out UiLanguage language)
            ? language
            : DetectSystemLanguage();
    }

    private static UiLanguage DetectSystemLanguage()
    {
        string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.Equals(language, "cs", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Czech
            : UiLanguage.English;
    }
}
