using System;

namespace SpankyhoOptimizer.Services;

internal static class AiDependentServiceProbe
{
    // ServiceController.DependentServices returns disposable controllers. Even a
    // short-circuiting Running result must release EVERY returned instance.
    internal static bool HasRunningOrUnknown<T>(T[] dependents, Func<T, bool> isRunning)
        where T : IDisposable
    {
        try
        {
            foreach (T dependent in dependents)
            {
                try
                {
                    if (isRunning(dependent)) return true;
                }
                catch
                {
                    // Fail closed: do not disable a service when a dependent's
                    // state cannot be inspected (including access denied).
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (T dependent in dependents)
            {
                try { dependent.Dispose(); }
                catch { /* Best effort: one failed Dispose must not leak the rest. */ }
            }
        }
    }
}
