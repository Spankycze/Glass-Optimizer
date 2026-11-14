using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class AudioEnhancementsTweakService
{
    // Synthetic test only: no production audio devices or journal touched.
    private static string RunAudioMutexCancellationSmoke()
    {
        using var mutex = new Mutex(false);
        if (!mutex.WaitOne(0))
            throw new InvalidOperationException("Audio mutex synthetic owner unavailable.");
        long cancelMs;
        try
        {
            using var cancel = new CancellationTokenSource();
            using var started = new ManualResetEventSlim(false);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Task<bool> waiting = Task.Factory.StartNew(() =>
            {
                started.Set();
                try
                {
                    bool acquired = WaitForAudioMutex(mutex, cancel.Token,
                        TimeSpan.FromSeconds(5));
                    if (acquired) mutex.ReleaseMutex();
                    return false;
                }
                catch (OperationCanceledException) { return true; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!started.Wait(TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Audio mutex waiter never started.");
            cancel.CancelAfter(80);
            if (!waiting.Wait(TimeSpan.FromSeconds(2)) || !waiting.Result)
                throw new InvalidOperationException("Audio mutex cancel request was ignored.");
            cancelMs = stopwatch.ElapsedMilliseconds;
            if (cancelMs > 1600)
                throw new InvalidOperationException("Audio mutex cancel took too long.");

            Task<bool> timeout = Task.Factory.StartNew(() =>
            {
                bool acquired = WaitForAudioMutex(mutex, CancellationToken.None,
                    TimeSpan.FromMilliseconds(60));
                if (acquired) mutex.ReleaseMutex();
                return !acquired;
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!timeout.Wait(TimeSpan.FromSeconds(3)) || !timeout.Result)
                throw new InvalidOperationException("Busy audio mutex timeout was ignored.");

            using var cancelledToken = new CancellationTokenSource();
            cancelledToken.Cancel();
            bool rejected = false;
            try { _ = WaitForAudioMutex(mutex, cancelledToken.Token, TimeSpan.Zero); }
            catch (OperationCanceledException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Pre-cancelled audio mutex was acquired.");
        }
        finally { mutex.ReleaseMutex(); }

        if (!WaitForAudioMutex(mutex, CancellationToken.None, TimeSpan.Zero))
            throw new InvalidOperationException("Audio mutex did not become available.");
        mutex.ReleaseMutex();

        // AbandonedMutexException means ownership WAS transferred to this
        // thread. Never treat it as a failed acquisition without releasing.
        using var abandoned = new Mutex(false);
        using var ownerReady = new ManualResetEventSlim(false);
        var orphanOwner = new Thread(() =>
        {
            abandoned.WaitOne();
            ownerReady.Set();
            // Deliberately terminate without releasing this synthetic mutex.
        }) { IsBackground = true };
        orphanOwner.Start();
        if (!ownerReady.Wait(TimeSpan.FromSeconds(3)) ||
            !orphanOwner.Join(TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException("Synthetic abandoned audio mutex owner stalled.");
        if (!WaitForAudioMutex(abandoned, CancellationToken.None, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("Audio mutex abandonment was not recovered.");
        abandoned.ReleaseMutex();
        return $"audioMutexCancelMs={cancelMs} audioMutexTimeoutHonored=True " +
            "audioMutexReacquired=True audioMutexAbandonedRecovered=True";
    }

    internal static string RunPermissionFlowSmoke()
    {
        string mutexEvidence = RunAudioMutexCancellationSmoke();
        // A fresh capture must not treat an outside 0 -> 1 transition as
        // our own successful write, or a later rollback might revert it.
        // An older durable journal, however, can legitimately reflect our
        // already-applied 1 from an interrupted previous attempt.
        if (AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                true, 0, true, 1) != AudioOwnedWriteDecision.Conflict ||
            AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                false, 0, true, 1) != AudioOwnedWriteDecision.Conflict ||
            AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                true, 7, true, 1) != AudioOwnedWriteDecision.Conflict ||
            AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                true, 0, true, 0) != AudioOwnedWriteDecision.Apply ||
            AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                false, 0, false, 0) != AudioOwnedWriteDecision.Apply ||
            AudioEnhancementsOwnershipPolicy.SelectApply(
                true, 0, true, 1) != AudioOwnedWriteDecision.AlreadyApplied)
            throw new InvalidOperationException(
                "Audio fresh-capture preflight can misattribute an external change.");
        // Test partial failure and cancellation without touching HKLM or the user's journal.
        int restoredOk = 0;
        (List<int> unresolved, Exception? deniedReason, bool wasCancelled) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2 }, CancellationToken.None, index =>
            {
                if (index == 1) throw new UnauthorizedAccessException("synthetic protected endpoint");
                restoredOk++;
            });
        if (restoredOk != 2 || unresolved.Count != 1 || unresolved[0] != 1 ||
            deniedReason is not UnauthorizedAccessException || wasCancelled)
            throw new InvalidOperationException("Audio partial restore journal failed to retain only blocked endpoints.");
        int continued = 0;
        (List<int> volatilePending, Exception? transientFailure, bool volatileCancelled) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2, 3 }, CancellationToken.None, index =>
            {
                if (index == 1) throw new ArgumentException("synthetic changed endpoint type");
                if (index == 2) throw new System.ComponentModel.Win32Exception(5,
                    "synthetic vendor registry read failed");
                continued++;
            });
        if (continued != 2 || volatilePending.Count != 2 || volatilePending[0] != 1 ||
            volatilePending[1] != 2 || transientFailure is not ArgumentException ||
            volatileCancelled)
            throw new InvalidOperationException("Audio rollback stopped after independent vendor failures.");
        (List<int> numericPending, Exception? numericFailure, bool numericCancelled) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2, 3 }, CancellationToken.None, index =>
            {
                if (index == 1) throw new FormatException("synthetic malformed DWORD");
                if (index == 2) throw new OverflowException("synthetic DWORD conversion overflow");
            });
        if (numericCancelled || numericPending.Count != 2 ||
            numericPending[0] != 1 || numericPending[1] != 2 ||
            numericFailure is not FormatException)
            throw new InvalidOperationException("Audio numeric read lost rollback endpoints.");
        using var interrupted = new CancellationTokenSource();
        (List<int> interruptedPending, _, bool interruptedCancelled) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2 }, interrupted.Token, index =>
            {
                if (index != 0) throw new InvalidOperationException("Later endpoint was reached.");
                interrupted.Cancel();
                throw new OperationCanceledException(interrupted.Token);
            });
        if (!interruptedCancelled || interruptedPending.Count != 3 ||
            interruptedPending[0] != 0 || interruptedPending[1] != 1 ||
            interruptedPending[2] != 2)
            throw new InvalidOperationException("Audio in-flight cancel dropped current endpoint journal.");
        using var partialCancel = new CancellationTokenSource();
        (List<int> cancelledPending, _, bool cancelledPartway) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2 }, partialCancel.Token,
                index => { if (index == 0) partialCancel.Cancel(); });
        if (!cancelledPartway || cancelledPending.Count != 2 ||
            cancelledPending[0] != 1 || cancelledPending[1] != 2)
            throw new InvalidOperationException("Audio cancel did not retain unattempted journal entries.");
        string firstEndpoint = Render + @"\{11111111-1111-1111-1111-111111111111}\FxProperties";
        string inputEndpoint = Capture + @"\{33333333-3333-3333-3333-333333333333}\FxProperties";
        if (!DescribeEndpoint(firstEndpoint).StartsWith("výstup {11111111", StringComparison.Ordinal) ||
            !DescribeEndpoint(inputEndpoint).StartsWith("vstup {33333333", StringComparison.Ordinal) ||
            DescribeEndpoint(@"SOFTWARE\Unrelated\Key") != "neznámé zvukové zařízení")
            throw new InvalidOperationException("Audio endpoint permission diagnostics regressed.");
        string secondEndpoint = Render + @"\{22222222-2222-2222-2222-222222222222}\FxProperties";
        string partialTestFile = Path.Combine(Path.GetTempPath(),
            "SpankyAudioPartial-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var originals = new[]
            {
                new OriginalValue(firstEndpoint, true, 0),
                new OriginalValue(secondEndpoint, false, 0)
            };
            File.WriteAllText(partialTestFile, JsonSerializer.Serialize(originals),
                new UTF8Encoding(false));
            AtomicFileService.WriteText(partialTestFile,
                JsonSerializer.Serialize(new[] { originals[1] }));
            OriginalValue[] compacted = ReadJournalFile(partialTestFile);
            if (compacted.Length != 1 || compacted[0] != originals[1])
                throw new InvalidOperationException("Audio partial journal atomic compaction failed.");
        }
        finally { if (File.Exists(partialTestFile)) File.Delete(partialTestFile); }
        int attempted = 0, restored = 0;
        (int blocked, Exception? first) = ExecuteBestEffortRestore(
            new[] { 0, 1, 2 }, CancellationToken.None, index =>
            {
                attempted++;
                if (index == 0) throw new UnauthorizedAccessException("synthetic denied");
                restored++;
            });
        if (blocked != 1 || attempted != 3 || restored != 2 ||
            first is not UnauthorizedAccessException)
            throw new InvalidOperationException(
                "Audio Enhancements denied endpoint interrupted remaining rollbacks.");
        (int disconnected, Exception? disconnectedFailure) = ExecuteBestEffortRestore(
            new[] { 0, 1 }, CancellationToken.None, index =>
            {
                if (index == 0)
                    throw new InvalidOperationException("synthetic unplugged endpoint");
            });
        if (disconnected != 1 || disconnectedFailure is not InvalidOperationException)
            throw new InvalidOperationException(
                "Audio Enhancements disconnected endpoint recovery regressed.");
        (int clean, Exception? failure) = ExecuteBestEffortRestore(
            new[] { 0, 1 }, CancellationToken.None, _ => { });
        if (clean != 0 || failure != null ||
            AudioEnhancementsOwnershipPolicy.SelectRestore(true, true, 0) !=
                AudioOwnedRestoreDecision.LeaveExternalChange ||
            AudioEnhancementsOwnershipPolicy.SelectRestore(true, true, 1) !=
                AudioOwnedRestoreDecision.RestoreOriginal ||
            AudioEnhancementsOwnershipPolicy.SelectApply(true, 0, true, 1) !=
                AudioOwnedWriteDecision.AlreadyApplied ||
            AudioEnhancementsOwnershipPolicy.SelectApply(true, 0, true, 0) !=
                AudioOwnedWriteDecision.Apply)
            throw new InvalidOperationException("Audio Enhancements ownership policy regressed.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool cancellationHonored = false;
        try
        {
            _ = ExecuteBestEffortRestore(new[] { 0 }, canceled.Token, _ =>
                throw new InvalidOperationException("Canceled rollback attempted a write."));
        }
        catch (OperationCanceledException) { cancellationHonored = true; }
        if (!cancellationHonored)
            throw new InvalidOperationException("Audio Enhancements rollback ignored cancellation.");
        // Synthetic safety cases only: malformed rollback records cannot
        // silently default original Existed/Value and remove an unknown key.
        string endpoint = Render + @"\{11111111-1111-1111-1111-111111111111}\FxProperties";
        var original = new OriginalValue(endpoint, false, 0);
        string valid = JsonSerializer.Serialize(new[] { original });
        OriginalValue[] roundTrip = ParseJournal(valid);
        if (roundTrip.Length != 1 || roundTrip[0] != original ||
            ParseJournal(JsonSerializer.Serialize(new[] {
                new OriginalValue(endpoint, true, 9) }))[0].Value != 9)
            throw new InvalidOperationException("Audio Enhancements strict journal rejected a valid original.");
        string[] rejected = {
            "[]",
            "null",
            "[{]",
            JsonSerializer.Serialize(new { Path = endpoint }),
            JsonSerializer.Serialize(new { Path = endpoint, Existed = false }),
            valid.Replace("\"Existed\":false", "\"Existed\":false,\"Existed\":true",
                StringComparison.Ordinal),
            valid.Replace("\"Value\":0", "\"Value\":0,\"Unknown\":true",
                StringComparison.Ordinal),
            JsonSerializer.Serialize(new[] { original, original }),
            JsonSerializer.Serialize(new[] { new OriginalValue(endpoint, false, 7) }),
            JsonSerializer.Serialize(new[] { new OriginalValue(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\other",
                false, 0) })
        };
        int blockedJournals = 0;
        foreach (string json in rejected)
        {
            try { _ = ParseJournal(json); }
            catch (InvalidDataException) { blockedJournals++; }
        }
        if (blockedJournals != rejected.Length)
            throw new InvalidOperationException(
                $"Audio Enhancements unsafe journals rejected {blockedJournals}/{rejected.Length}.");
        // File-only smoke; no access to ProgramData or audio registry.
        string testFile = Path.Combine(Path.GetTempPath(),
            "SpankyAudioJournalSmoke-" + Guid.NewGuid().ToString("N") + ".json");
        bool oversizedRejected = false, truncatedRejected = false;
        bool invalidEncodingRejected = false, utf16LeRejected = false;
        bool utf16BeRejected = false, utf8BomAccepted = false;
        try
        {
            File.WriteAllText(testFile, valid, new UTF8Encoding(false, true));
            if (ReadJournalFile(testFile).Length != 1)
                throw new InvalidOperationException("Bounded audio journal rejected valid JSON.");
            // Optional UTF-8 BOM is accepted; other BOM encodings must not
            // silently bypass the strict UTF-8 journal requirement.
            File.WriteAllBytes(testFile,
                new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(valid)).ToArray());
            utf8BomAccepted = ReadJournalFile(testFile).Length == 1;
            File.WriteAllBytes(testFile,
                Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(valid)).ToArray());
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { utf16LeRejected = true; }
            File.WriteAllBytes(testFile,
                Encoding.BigEndianUnicode.GetPreamble().Concat(
                    Encoding.BigEndianUnicode.GetBytes(valid)).ToArray());
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { utf16BeRejected = true; }
            File.WriteAllText(testFile, valid[..^1], new UTF8Encoding(false, true));
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { truncatedRejected = true; }
            // Exactly 512 KiB is legal for complete JSON.
            int padding = MaxJournalBytes - Encoding.UTF8.GetByteCount(valid);
            File.WriteAllText(testFile, valid + new string(' ', padding),
                new UTF8Encoding(false, true));
            if (ReadJournalFile(testFile).Length != 1)
                throw new InvalidOperationException("Exact-limit audio journal was rejected.");
            using (var file = new FileStream(testFile, FileMode.Open,
                FileAccess.Write, FileShare.None))
                file.SetLength(MaxJournalBytes + 1);
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { oversizedRejected = true; }
            // Locked readers must prevent a concurrent writer from replacing
            // a validated JSON body while the restore owns its file handle.
            using (var readerLock = new FileStream(testFile, FileMode.Open,
                FileAccess.Read, FileShare.Read))
            {
                bool writeDenied = false;
                try
                {
                    using var writer = new FileStream(testFile, FileMode.Open,
                        FileAccess.Write, FileShare.ReadWrite);
                }
                catch (IOException) { writeDenied = true; }
                if (!writeDenied)
                    throw new InvalidOperationException("Audio journal read allowed concurrent mutation.");
            }
            File.WriteAllBytes(testFile, new byte[] { (byte)'[', 0xFF, (byte)']' });
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { invalidEncodingRejected = true; }
        }
        finally { if (File.Exists(testFile)) File.Delete(testFile); }
        if (!oversizedRejected || !truncatedRejected || !invalidEncodingRejected ||
            !utf8BomAccepted || !utf16LeRejected || !utf16BeRejected)
            throw new InvalidOperationException("UTF-8-only bounded audio journal file safety regressed.");
        return $"AUDIO_PERMISSION_ROLLBACK_SMOKE_OK attempted={attempted} restored={restored} " +
            $"blocked={blocked} disconnectedRetained={disconnected} cancellationHonored={cancellationHonored} " +
            $"validJournal=true malformedRejected={blockedJournals} " +
            $"boundedFile=true truncatedRejected={truncatedRejected} oversizedRejected={oversizedRejected} " +
            $"invalidEncodingRejected={invalidEncodingRejected} utf8BomAccepted={utf8BomAccepted} " +
            $"utf16LeRejected={utf16LeRejected} utf16BeRejected={utf16BeRejected} {mutexEvidence}";
    }

    private const int MaxJournalBytes = 512 * 1024;

    private static OriginalValue[] ReadJournal() => ReadJournalFile(JournalPath);

    // Read and validate the SAME opened file handle. A separate FileInfo
    // length check followed by File.ReadAllText could inspect different
    // files across an atomic rename. Share only reads while validating.
    private static OriginalValue[] ReadJournalFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length > MaxJournalBytes)
            throw new InvalidDataException("Audio Enhancements ownership journal is oversized.");
        // Do not auto-detect UTF-16/UTF-32 via BOM: ownership journals are
        // UTF-8 only. Read the already bounded bytes through this same locked
        // handle, then accept at most an optional UTF-8 BOM.
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        int offset = bytes.Length >= 3 && bytes[0] == 0xEF &&
            bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            string json = new UTF8Encoding(false, true).GetString(
                bytes, offset, bytes.Length - offset);
            return ParseJournal(json);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "Audio Enhancements ownership journal must use valid UTF-8 text.", ex);
        }
    }

    // Deserialize strictly: record positional defaults could otherwise turn
    // a missing Existed/Value field into false/0 and later DELETE an audio
    // property whose true original value is unknown.
    private static OriginalValue[] ParseJournal(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Audio Enhancements ownership journal contains invalid JSON.", ex);
        }
        using (document)
        {
            JsonElement array = document.RootElement;
            if (array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() is 0 or > 1024)
                throw new InvalidDataException("Audio Enhancements ownership journal has invalid count.");

            var entries = new List<OriginalValue>(array.GetArrayLength());
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Audio Enhancements journal entry must be an object.");
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty field in element.EnumerateObject())
                {
                    if (!fields.Add(field.Name) ||
                        field.Name is not ("Path" or "Existed" or "Value"))
                        throw new InvalidDataException("Audio Enhancements journal has duplicate or unexpected fields.");
                }
                if (fields.Count != 3 ||
                    !element.TryGetProperty("Path", out JsonElement pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String ||
                    !element.TryGetProperty("Existed", out JsonElement existedElement) ||
                    existedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !element.TryGetProperty("Value", out JsonElement valueElement) ||
                    valueElement.ValueKind != JsonValueKind.Number ||
                    !valueElement.TryGetInt32(out int originalValue))
                    throw new InvalidDataException("Audio Enhancements journal has missing or invalid original values.");

                if (!existedElement.GetBoolean() && originalValue != 0)
                    throw new InvalidDataException(
                        "Audio Enhancements journal has an inconsistent missing original.");
                string? path = pathElement.GetString();
                if (!AudioEnhancementsOwnershipPolicy.IsTrustedEndpointPath(path) ||
                    !seenPaths.Add(path!))
                    throw new InvalidDataException("Audio Enhancements journal contains an unsafe or duplicate endpoint.");
                entries.Add(new OriginalValue(path!, existedElement.GetBoolean(), originalValue));
            }
            return entries.ToArray();
        }
    }

    private static void WriteJournal(List<OriginalValue> originals)
    {
        WriteAheadOwnershipJournal.CreateNew(
            JournalPath, JsonSerializer.SerializeToUtf8Bytes(originals));
    }
}
