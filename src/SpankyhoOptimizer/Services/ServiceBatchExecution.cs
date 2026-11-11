using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed record ServiceBatchOutcome(string[] Errors, string[] Skipped, int Applied);

internal static class ServiceBatchExecution
{
    // Same error distinction as WinUtil #5140: only ERROR_SERVICE_DOES_NOT_EXIST is optional.
    // Permission failures, unexpected errors and user cancellation retain their own semantics.
    internal static async Task<ServiceBatchOutcome> RunAsync(ServiceChangeRequest[] items,
        Func<ServiceChangeRequest, CancellationToken, Task> apply, bool skipMissing, CancellationToken token)
    {
        var errors = new List<string>(); var skipped = new List<string>(); int applied = 0;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try { await apply(item, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); applied++; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Win32Exception ex) when (skipMissing && ex.NativeErrorCode == 1060) { skipped.Add(item.Name); }
            catch (Exception ex) { errors.Add("Service " + item.Name + ": " + ex.Message); }
        }
        return new ServiceBatchOutcome(errors.ToArray(), skipped.ToArray(), applied);
    }
}
