using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static (string Text, string Resource) TweakUiState(TweakDefinition tweak)
    {
        if (tweak.IsBusy) return ("PRACUJI\u2026", "SpankyInfoBrush");
        if (tweak.ExecutionState == TweakExecutionState.RequiresElevation)
            return ("VY\u017dADUJE SPR\u00c1VCE", "SpankyWarningBrush");
        if (!string.IsNullOrWhiteSpace(tweak.LastError) || tweak.ExecutionState == TweakExecutionState.Failed)
            return ("CHYBA", "SpankyWarningBrush");
        if (!tweak.IsLoaded || tweak.ExecutionState == TweakExecutionState.Unsupported)
            return ("NEDOSTUPN\u00c9", "SpankyWarningBrush");
        if (tweak.ExecutionState == TweakExecutionState.RequiresRestart ||
            (tweak.RestartRequired && tweak.Status.Contains("restart", System.StringComparison.OrdinalIgnoreCase)))
            return ("VY\u017dADUJE RESTART", "SpankyWarningBrush");
        if (tweak.Pending != tweak.Current) return ("\u010cEK\u00c1 NA POU\u017dIT\u00cd", "SpankyWarningBrush");
        var state = tweak.Current ? "AKTIVN\u00cd" : "NEAKTIVN\u00cd";
        if (tweak.LastVerifiedUtc.HasValue) state += " \u2022 OV\u011a\u0158ENO";
        return (state, tweak.Current ? "SpankySuccessBrush" : "SpankyInfoBrush");
    }

    private static string TweakVerificationMeta(TweakDefinition tweak)
    {
        var meta = "Dopad: " + tweak.Impact + "   \u2022   " + tweak.Status;
        if (tweak.ExecutionState == TweakExecutionState.RequiresElevation)
            return meta + "   \u2022   Vy\u017eaduje opr\u00e1vn\u011bn\u00ed spr\u00e1vce";
        if (tweak.ExecutionState == TweakExecutionState.RequiresRestart)
            return meta + "   \u2022   Vy\u017eaduje restart";
        if (!string.IsNullOrWhiteSpace(tweak.LastError) || tweak.ExecutionState == TweakExecutionState.Failed)
            return meta + "   \u2022   Chyba ov\u011b\u0159en\u00ed";
        if (tweak.LastVerifiedUtc is { } verified)
            return meta + "   \u2022   Posledn\u00ed kontrola " + verified.ToLocalTime().ToString("HH:mm:ss");
        return meta + "   \u2022   Dosud neov\u011b\u0159eno";
    }
}
