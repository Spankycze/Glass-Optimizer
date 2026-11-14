using System;
using System.Threading;

// One active setup workflow per Windows session, including download and WPF
// child process lifetime. Ownership remains on the invoking main thread.
internal sealed class BootstrapSessionGuard : IDisposable
{
    private const string Name = @"Local\SpankyhoOptimizerNativeSetup_v15";
    private readonly Mutex mutex;
    private readonly bool acquired;
    private bool disposed;

    internal bool Acquired => acquired;

    internal BootstrapSessionGuard()
    {
        mutex = new Mutex(initiallyOwned: false, name: Name);
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // Windows transfers ownership after previous crash
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (acquired) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
