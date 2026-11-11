using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Serializes system-changing SSD/NTFS operations across the GUI and the
/// long-running RuntimeHost. Passive status snapshots never take this lock.
/// A named Windows semaphore is await-friendly (unlike a thread-owned mutex).
/// No service, process or permanent worker is created.
/// </summary>
internal static class StorageMutationGate
{
    private const string GateName = @"Local\SpankyhoOptimizer.StorageMaintenanceWrite.v1";
    private const int WaitTimeoutMs = 15000;

    internal static async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var semaphore = new Semaphore(1, 1, GateName);
        bool acquired = false;
        try
        {
            // The kernel semaphore is shared even if the user opens the GUI
            // while the separate FanAgent/RuntimeHost is updating SSD defaults.
            int result = await Task.Run(() =>
                WaitHandle.WaitAny(new WaitHandle[] { semaphore, token.WaitHandle }, WaitTimeoutMs))
                .ConfigureAwait(false);
            if (result == WaitHandle.WaitTimeout)
                throw new TimeoutException("Jiná operace právě mění nastavení úložiště; opakuj pokus později.");
            if (result != 0)
                throw new OperationCanceledException(token);
            acquired = true;
            token.ThrowIfCancellationRequested();
            return new Lease(semaphore);
        }
        catch
        {
            if (acquired) semaphore.Release();
            semaphore.Dispose();
            throw;
        }
    }

    private sealed class Lease(Semaphore semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { semaphore.Release(); }
            finally { semaphore.Dispose(); }
        }
    }

    // Tests the same production kernel gate without running fsutil or changing
    // any Windows setting. A second lease must wait for the first one.
    internal static async Task<string> RunOfflineSmokeAsync()
    {
        using var first = await EnterAsync(CancellationToken.None);
        Task<IDisposable> second = EnterAsync(CancellationToken.None);
        await Task.Delay(100).ConfigureAwait(false);
        if (second.IsCompleted)
            throw new InvalidOperationException("Storage mutation gate allowed overlapping writes.");
        first.Dispose();
        using (await second.ConfigureAwait(false)) { }

        using var holding = await EnterAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        Task<IDisposable> blocked = EnterAsync(cancelled.Token);
        cancelled.Cancel();
        try
        {
            using var unexpected = await blocked.ConfigureAwait(false);
            throw new InvalidOperationException("Storage mutation gate ignored cancellation.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        return "STORAGE_MUTATION_GATE_SMOKE_OK serial=true cancelled=true crossProcess=named-semaphore";
    }
}
