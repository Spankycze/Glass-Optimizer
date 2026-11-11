namespace SpankyhoOptimizer.Services;

public static class AiRuntimeScopePolicy
{
    public static AiSystemRuntimeConfig Sanitize(AiSystemRuntimeConfig config) =>
        !config.TweaksEnabled && !config.SecurityEnabled
            ? config // Already normalized: avoid cloning a record on every runtime tick.
            : config with
            {
                TweaksEnabled = false,
                SecurityEnabled = false
            };
}
