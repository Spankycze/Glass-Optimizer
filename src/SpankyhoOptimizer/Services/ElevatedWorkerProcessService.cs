using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class ElevatedWorkerProcessService
{
    public static async Task<ProcessResult> RunAsync(
        string file,
        string arguments,
        TimeSpan timeout,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(timeout);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            },
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
                return new ProcessResult(-1, string.Empty, "Elevated worker se nepodařilo spustit.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("UAC potvrzení bylo zrušeno.", ex, token);
        }

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Empty, string.Empty);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            TryTerminate(process);
            throw new TimeoutException($"Elevated worker překročil timeout {timeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
