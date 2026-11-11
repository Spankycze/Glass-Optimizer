using System;

namespace SpankyhoOptimizer.Services;

// Publish an event watcher only after it has successfully started. A failed WMI
// subscription must not retain native handles or callback references.
internal static class WatcherStartupGuard
{
    internal static T Start<T>(Func<T> create, Action<T> subscribe,
        Action<T> start, Action<T> unsubscribe) where T : IDisposable
    {
        T watcher = create();
        try
        {
            subscribe(watcher);
            start(watcher);
            return watcher;
        }
        catch
        {
            try { unsubscribe(watcher); } catch { }
            try { watcher.Dispose(); } catch { }
            throw;
        }
    }
}
