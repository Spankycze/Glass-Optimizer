using System;
using System.IO;
using System.Text;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Read a bounded ownership snapshot only when file metadata is stable
/// across the read. Atomic replacements are allowed one retry, never a
/// partially committed ownership record. Caller decides fail-closed action.
/// </summary>
internal static class AiJournalSnapshotReadPolicy
{
    // Invalid UTF bytes must NEVER be replaced with U+FFFD in an ownership
    // journal: that could silently alter a service, task or driver identity.
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(false, true, true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, true, true);
    private static readonly Encoding StrictUtf32Le = new UTF32Encoding(false, true, true);
    private static readonly Encoding StrictUtf32Be = new UTF32Encoding(true, true, true);

    internal const int MaximumReadAttempts = 2;
    internal static readonly TimeSpan RejectedCacheTtl = TimeSpan.FromSeconds(15);

    internal static bool CanReuseRejected(
        FileMetadataStamp rejectedStamp, FileMetadataStamp currentStamp,
        DateTime lastAttemptUtc, DateTime nowUtc) =>
        rejectedStamp == currentStamp &&
        nowUtc >= lastAttemptUtc &&
        nowUtc - lastAttemptUtc < RejectedCacheTtl;

    internal static bool TryReadStable(
        string path, ReusableFileMetadata metadata,
        FileMetadataStamp firstStamp,
        out AiSystemRuntimeItemState[] result,
        out FileMetadataStamp acceptedStamp)
    {
        result = Array.Empty<AiSystemRuntimeItemState>();
        acceptedStamp = firstStamp;

        for (int attempt = 0; attempt < MaximumReadAttempts; attempt++)
        {
            if (!acceptedStamp.Exists ||
                !AiWriteAheadSerializationPolicy.MayReadJournalFile(acceptedStamp.Length))
                return false;

            string text;
            try
            {
                text = ReadBoundedText(path);
            }
            catch (IOException)
            {
                // If a rename happened during open, the second attempt
                // observes the new metadata. Do not spin indefinitely.
                acceptedStamp = metadata.Read();
                continue;
            }

            FileMetadataStamp after = metadata.Read();
            if (after != acceptedStamp)
            {
                acceptedStamp = after;
                continue;
            }

            if (!AiWriteAheadSerializationPolicy.TryDecodeJournal(text, out result))
                return false;

            acceptedStamp = after;
            return true;
        }
        return false;
    }

    // No first-install lost-update race: create-only atomic move refuses
    // to replace an ownership file another process created meanwhile.
    // Subsequent updates retain the established atomic replacement contract.
    internal static void CommitSnapshot(
        string preparedTemp, string durablePath, bool initiallyAbsent) =>
        File.Move(preparedTemp, durablePath, overwrite: !initiallyAbsent);

    internal static string ReadBoundedText(string path)
    {
        // File size may grow AFTER the external metadata check. Read exactly
        // the length of the opened file handle instead of unbounded
        // File.ReadAllText, and keep rename compatibility with the writer.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        long size = file.Length;
        if (!AiWriteAheadSerializationPolicy.MayReadJournalFile(size))
            throw new IOException("AI ownership snapshot changed to an out-of-bounds file.");

        var bytes = new byte[(int)size];
        file.ReadExactly(bytes);
        return DecodeJournalText(bytes);
    }

    // Legacy UTF-16/UTF-32 BOM files remain readable, but no encoding is
    // allowed to silently substitute malformed bytes or Unicode surrogates.
    internal static string RunStrictJournalEncodingSmoke()
    {
        const string fixture = "[\"služba\"]";
        static byte[] WithBom(byte[] bom, byte[] content)
        {
            var bytes = new byte[bom.Length + content.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
            Buffer.BlockCopy(content, 0, bytes, bom.Length, content.Length);
            return bytes;
        }
        var valid = new (string Name, byte[] Bytes)[]
        {
            ("utf8", StrictUtf8.GetBytes(fixture)),
            ("utf8-bom", WithBom([0xEF, 0xBB, 0xBF], StrictUtf8.GetBytes(fixture))),
            ("utf16-le", WithBom([0xFF, 0xFE], StrictUtf16Le.GetBytes(fixture))),
            ("utf16-be", WithBom([0xFE, 0xFF], StrictUtf16Be.GetBytes(fixture))),
            ("utf32-le", WithBom([0xFF, 0xFE, 0, 0], StrictUtf32Le.GetBytes(fixture))),
            ("utf32-be", WithBom([0, 0, 0xFE, 0xFF], StrictUtf32Be.GetBytes(fixture)))
        };
        foreach (var test in valid)
            if (DecodeJournalText(test.Bytes) != fixture)
                throw new InvalidOperationException("Valid AI journal encoding rejected: " + test.Name);

        var malformed = new (string Name, byte[] Bytes)[]
        {
            ("utf8-invalid-continuation", [0x5B, 0x22, 0xC3, 0x28, 0x22, 0x5D]),
            ("utf8-truncated", [0x5B, 0xE2, 0x82]),
            ("utf16-le-orphan-surrogate", [0xFF, 0xFE, 0x00, 0xD8]),
            ("utf16-be-orphan-surrogate", [0xFE, 0xFF, 0xD8, 0x00]),
            ("utf32-le-invalid-codepoint", [0xFF, 0xFE, 0, 0, 0, 0, 0x11, 0]),
            ("utf32-be-invalid-codepoint", [0, 0, 0xFE, 0xFF, 0, 0x11, 0, 0]),
            ("utf32-truncated", [0xFF, 0xFE, 0, 0, 0x5B])
        };
        foreach (var test in malformed)
        {
            bool refused = false;
            try { _ = DecodeJournalText(test.Bytes); }
            catch (IOException) { refused = true; }
            if (!refused)
                throw new InvalidOperationException("Damaged AI journal bytes accepted: " + test.Name);
        }

        // Exercise the actual bounded FileStream path on a DISPOSABLE file;
        // never read or write the current user's durable AI ownership state.
        string testPath = Path.Combine(Path.GetTempPath(),
            "spanky-ai-encoding-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(testPath, valid[0].Bytes);
            if (ReadBoundedText(testPath) != fixture)
                throw new InvalidOperationException("Bounded AI journal reader changed valid bytes.");
            File.WriteAllBytes(testPath, malformed[0].Bytes);
            bool rejected = false;
            try { _ = ReadBoundedText(testPath); }
            catch (IOException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Bounded AI journal reader accepted invalid UTF-8.");
        }
        finally { try { File.Delete(testPath); } catch { } }
        if (File.Exists(testPath))
            throw new InvalidOperationException("Journal encoding smoke leaked disposable file.");
        return "AI_JOURNAL_STRICT_ENCODING_SMOKE_OK utf8=true legacyUtf16=true " +
            "legacyUtf32=true malformedRejected=true boundedRead=true systemWrites=0";
    }

    internal static string DecodeJournalText(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding = StrictUtf8;
        int offset = 0;
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE &&
            bytes[2] == 0 && bytes[3] == 0)
            (encoding, offset) = (StrictUtf32Le, 4);
        else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 &&
                 bytes[2] == 0xFE && bytes[3] == 0xFF)
            (encoding, offset) = (StrictUtf32Be, 4);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            (encoding, offset) = (StrictUtf16Le, 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            (encoding, offset) = (StrictUtf16Be, 2);
        else if (bytes.Length >= 3 && bytes[0] == 0xEF &&
                 bytes[1] == 0xBB && bytes[2] == 0xBF)
            offset = 3;
        try
        {
            return encoding.GetString(bytes[offset..]);
        }
        catch (DecoderFallbackException ex)
        {
            throw new IOException("AI ownership journal contains malformed Unicode; refusing recovery snapshot.", ex);
        }
    }
}
