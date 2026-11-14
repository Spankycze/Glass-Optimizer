using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed partial class AiThirdPartyIdleAutomationService
{
    internal static bool RunFailedJournalWriteSmoke()
    {
        var worker = new AiThirdPartyIdleAutomationService();
        DateTime at = new(2026, 10, 22, 11, 0, 0, DateTimeKind.Utc);
        var owner = new Owner("service", "Steam Client Service", "steam",
            at.AddMinutes(-5), true, true, OriginalServiceStartMode: 3);
        worker._owned[Key(owner.Kind, owner.Id)] = owner;
        worker._lastScanUtc = at.AddMinutes(-1);
        worker._lastFailedOffRestoreUtc = at.AddSeconds(-10);
        worker.InvalidateJournalAfterFailedWrite(at);
        return !worker._journalHealthy &&
            worker._lastJournalErrorUtc == at &&
            worker._lastScanUtc == DateTime.MinValue &&
            worker._lastFailedOffRestoreUtc == default &&
            worker._owned.Count == 1 &&
            ReferenceEquals(worker._owned[Key(owner.Kind, owner.Id)], owner) &&
            !AiThirdPartyIdlePolicy.MayRetryJournalRead(at, at.AddSeconds(59)) &&
            AiThirdPartyIdlePolicy.MayRetryJournalRead(at, at.AddSeconds(60));
    }

    private async Task PersistAsync(CancellationToken token)
    {
        string temp = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PruneFeedback(DateTime.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            byte[] journalBytes = SerializeBoundedJournalBytes(
                new Journal(_owned, _feedback, _references));
            await File.WriteAllBytesAsync(temp, journalBytes, token)
                .ConfigureAwait(false);
            // Cross-Host lease is authoritative for compliant RuntimeHosts.
            // Detect even noncompliant external changes before replacement;
            // never silently clobber a newer ownership/restore journal.
            if (!JournalUnchangedSinceLeaseRead())
                throw new IOException(
                    "AI journal changed since validated load; refusing stale overwrite");
            File.Move(temp, JournalPath, overwrite: true);
            CaptureJournalStamp(SHA256.HashData(journalBytes));
            // Our own atomic swap is not an external change.
        }
        catch
        {
            // A failed prewrite, commit, or external stamp conflict must
            // NEVER leave a healthy journal flag pointing at speculative
            // in-memory state. Existing one-minute retry revalidates disk
            // under the same cross-host exclusive lease.
            InvalidateJournalAfterFailedWrite(DateTime.UtcNow);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // Cleanup must not conceal the original durable write result.
            }
            catch (UnauthorizedAccessException)
            {
                // A leftover uniquely-named temp is never a valid journal.
            }
        }
    }
}
