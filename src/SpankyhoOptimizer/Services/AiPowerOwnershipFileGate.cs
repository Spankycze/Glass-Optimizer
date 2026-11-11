using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// An async, cross-process lease protecting the whole power ownership
/// read/modify/write transaction shared by the UI and RuntimeHost.
/// The exclusive file handle is automatically released after process exit.
/// Never delete the persistent zero-byte lock file while another process
/// might be opening it.
/// </summary>
internal static class AiPowerOwnershipFileGate
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);

    internal static async Task<FileStream> AcquireAsync(
        string ownershipPath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipPath);
        string lockPath = ownershipPath + ".lock";
        string? directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsSharingConflict(ex) && timer.Elapsed < Timeout)
            {
                // Only cross-process sharing conflicts are retried. Disk
                // failure, missing permissions and other IO errors fail fast.
                await Task.Delay(RetryInterval, token).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingConflict(IOException ex) =>
        ex.HResult == unchecked((int)0x80070020) || // ERROR_SHARING_VIOLATION
        ex.HResult == unchecked((int)0x80070021);   // ERROR_LOCK_VIOLATION
}
