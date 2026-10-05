using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SpankyhoOptimizer.Services;

public enum SpankyVoiceIntent
{
    Unknown,
    FanNight,
    FanQuiet,
    FanAuto,
    FanBalanced,
    FanPerformance,
    OpenFans,
    Greeting,
    FanStatus
}

public sealed record SpankyVoiceDecision(SpankyVoiceIntent Intent, double Confidence, string Reply);

public static class SpankyVoiceAiService
{
    public static bool HasWakeWord(string utterance)
    {
        string text = Normalize(utterance);
        return Has(text, "spanky", "spanki", "spenky");
    }

    public static SpankyVoiceDecision Understand(string utterance)
    {
        string text = Normalize(utterance);
        if (text.Length == 0) return new(SpankyVoiceIntent.Unknown, 0, "Neslyšel jsem příkaz.");

        string withoutWake = text
            .Replace("spanky", string.Empty, StringComparison.Ordinal)
            .Replace("spanki", string.Empty, StringComparison.Ordinal)
            .Replace("spenky", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (withoutWake.Length == 0 || Has(withoutWake, "ahoj", "cus", "cau", "nazdar", "zdravim"))
            return new(SpankyVoiceIntent.Greeting, .99, "Ahoj. Jsem tady a poslouchám.");

        if (Has(withoutWake, "jsi tam", "poslouchas", "slysis me", "slysis", "fungujes"))
            return new(SpankyVoiceIntent.Greeting, .98, "Jo. Jsem tady a poslouchám tě.");

        bool fanContext = Has(text, "vetr", "ventilator", "fan", "chlaz", "otack", "rpm", "hluk", "hluc", "tich");
        if (Has(text, "otevri", "ukaz", "zobraz") && Has(text, "vetr", "fan", "chlaz"))
            return new(SpankyVoiceIntent.OpenFans, .98, "Otevírám ventilátory.");

        if ((fanContext && Has(withoutWake, "jaky rezim", "jaky profil")) ||
            Has(withoutWake, "kolik toci", "kolik otacek", "stav vetraku", "stav ventilatoru"))
        {
            FanProfile profile = FanProfileService.Load();
            string mode = profile.CpuTempTargetEnabled ? "automatický režim" :
                profile.TargetRpm <= 800 ? "noční režim" :
                profile.TargetRpm <= 1200 ? "tichý režim" :
                profile.TargetRpm >= 2000 ? "výkonný režim" :
                "normální režim";
            return new(SpankyVoiceIntent.FanStatus, .99, $"Běží {mode}, cíl je {profile.TargetRpm} otáček za minutu.");
        }

        if (fanContext && Has(text, "700 rpm", "700 otacek", "sedm set otacek"))
            return new(SpankyVoiceIntent.FanNight, .99, "Nastavuju sedm set otáček.");

        if (fanContext && Has(text, "1100 rpm", "1100 otacek", "jedenact set otacek"))
            return new(SpankyVoiceIntent.FanQuiet, .99, "Nastavuju jedenáct set otáček.");

        if (fanContext && Has(text, "1620 rpm", "1620 otacek"))
            return new(SpankyVoiceIntent.FanBalanced, .99, "Nastavuju šestnáct set dvacet otáček.");

        if (fanContext && Has(text, "2100 rpm", "2100 otacek", "dva tisice sto otacek"))
            return new(SpankyVoiceIntent.FanPerformance, .99, "Nastavuju dva tisíce sto otáček.");

        if (fanContext && Has(text, "noc", "nocni", "spani", "spat", "nejtissi", "co nejmin"))
            return new(SpankyVoiceIntent.FanNight, .97, "Zapínám noční režim ventilátorů.");

        if (fanContext && Has(text, "ticho", "tichy", "ztisit", "hlucne", "hluk", "potichu"))
            return new(SpankyVoiceIntent.FanQuiet, .94, "Ztišuji ventilátory.");

        if (fanContext && Has(text, "auto", "automat", "teplot", "sam", "podle cpu"))
            return new(SpankyVoiceIntent.FanAuto, .95, "Zapínám automatické řízení podle teploty.");

        if (fanContext && Has(text, "vykon", "performance", "gaming", "herni", "naplno", "maximum", "max", "chladit", "rychleji"))
            return new(SpankyVoiceIntent.FanPerformance, .96, "Zapínám výkonný režim ventilátorů.");

        if (fanContext && Has(text, "normal", "normalni", "balanced", "vyvazen"))
            return new(SpankyVoiceIntent.FanBalanced, .91, "Zapínám normální režim ventilátorů.");

        return new(SpankyVoiceIntent.Unknown, .20, "Tomuhle zatím nerozumím.");
    }

    public static bool Execute(SpankyVoiceDecision decision)
    {
        return decision.Intent switch
        {
            SpankyVoiceIntent.FanNight => FanProfileService.ApplyVoicePreset("night"),
            SpankyVoiceIntent.FanQuiet => FanProfileService.ApplyVoicePreset("quiet"),
            SpankyVoiceIntent.FanAuto => FanProfileService.ApplyVoicePreset("auto"),
            SpankyVoiceIntent.FanBalanced => FanProfileService.ApplyVoicePreset("balanced"),
            SpankyVoiceIntent.FanPerformance => FanProfileService.ApplyVoicePreset("performance"),
            SpankyVoiceIntent.OpenFans => FanProfileService.OpenFansFromVoice(),
            SpankyVoiceIntent.Greeting => true,
            SpankyVoiceIntent.FanStatus => true,
            _ => false
        };
    }

    private static bool Has(string text, params string[] words) => words.Any(text.Contains);

    private static string Normalize(string value)
    {
        string formD = (value ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (char c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
