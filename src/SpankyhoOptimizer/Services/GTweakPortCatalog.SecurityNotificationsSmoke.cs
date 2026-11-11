using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Exact production transaction exercised exclusively with fake delegates.
// No actual HKCU/HKLM writes, application state or backups are changed.
internal static partial class GTweakPortCatalog
{
    internal static async Task RunSecurityNotificationsMutationSmokeAsync()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Security notifications smoke: " + message);
        }

        var restored = new List<string>();
        int captured = 0, applied = 0;
        Func<CancellationToken, Task> capture = _ =>
        {
            captured++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = _ =>
        {
            applied++;
            return Task.CompletedTask;
        };
        Func<string, CancellationToken, Task<bool>> restore = (id, _) =>
        {
            restored.Add(id);
            return Task.FromResult(true);
        };

        // 1. Successful enable: capture and apply once; do not restore.
        await ExecuteSecurityNotificationsMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Require(captured == 1 && applied == 1 && restored.Count == 0, "enable path");

        // 2. Disable: all three restores with no new backup or write.
        await ExecuteSecurityNotificationsMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Require(captured == 1 && applied == 1 &&
                restored.SequenceEqual(SecurityNotificationBackupIds), "disable path");
        restored.Clear();

        // 3. Partial Windows mutation failure must restore every original.
        bool writeFailed = false;
        try
        {
            await ExecuteSecurityNotificationsMutationAsync(true, capture,
                _ => throw new IOException("injected registry write failure"), restore, CancellationToken.None);
        }
        catch (IOException) { writeFailed = true; }
        Require(writeFailed && restored.SequenceEqual(SecurityNotificationBackupIds),
            "partial mutation rollback");
        restored.Clear();

        // 4. Restore must attempt independent keys despite an error or missing backup.
        bool partialRestoreReported = false;
        try
        {
            await ExecuteSecurityNotificationsMutationAsync(false, capture, apply, (id, _) =>
            {
                restored.Add(id);
                if (id == SecurityNotificationBackupIds[0]) throw new IOException("injected restore fault");
                return Task.FromResult(id != SecurityNotificationBackupIds[1]);
            }, CancellationToken.None);
        }
        catch (AggregateException ex) { partialRestoreReported = ex.InnerExceptions.Count == 2; }
        Require(partialRestoreReported && restored.SequenceEqual(SecurityNotificationBackupIds),
            "independent restore errors");
        restored.Clear();

        // 5. Cancellation after a simulated write still rolls back all originals.
        using var cancellation = new CancellationTokenSource();
        bool cancellationReported = false;
        try
        {
            await ExecuteSecurityNotificationsMutationAsync(true, capture, _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, restore, cancellation.Token);
        }
        catch (OperationCanceledException) { cancellationReported = true; }
        Require(cancellationReported && restored.SequenceEqual(SecurityNotificationBackupIds),
            "cancellation recovery");

        // 6. Report both the original write fault and any rollback fault.
        bool compoundFailureReported = false;
        try
        {
            await ExecuteSecurityNotificationsMutationAsync(true, capture,
                _ => throw new IOException("injected apply fault"), (id, _) =>
                    Task.FromResult(id != SecurityNotificationBackupIds[1]), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compoundFailureReported = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Require(compoundFailureReported, "compound apply and rollback failure");
    }
}
