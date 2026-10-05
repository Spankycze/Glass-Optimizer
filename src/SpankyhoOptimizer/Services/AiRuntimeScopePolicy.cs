namespace SpankyhoOptimizer.Services;

public static class AiRuntimeScopePolicy
{
    public static AiSystemRuntimeConfig Sanitize(AiSystemRuntimeConfig config) =>
        config with
        {
            TweaksEnabled = false,
            SecurityEnabled = false
        };
}
