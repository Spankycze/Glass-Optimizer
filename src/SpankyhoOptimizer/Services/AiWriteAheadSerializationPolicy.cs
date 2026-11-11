using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Encode exactly the JSON snapshot already validated against the last durable
/// checkpoint. Do not run JsonSerializer a second time for the write-ahead
/// transaction; its UTF-8 bytes must represent the SAME version of the state.
/// </summary>
internal static class AiWriteAheadSerializationPolicy
{
    // This is a bounded local ownership ledger, never arbitrary user input.
    // An oversized or malicious file must not trigger an unbounded JSON
    // allocation in the 1-5s FanAgent / GUI refresh path.
    internal const int MaxJournalChars = 4 * 1024 * 1024;
    internal const int MaxJournalRows = 8192;
    // UTF-8 occupies at most four bytes per UTF-16 code unit. Reject an
    // implausibly large durable file BEFORE File.ReadAllText allocates its
    // string, even when JSON is invalid or deliberately padded.
    internal const long MaxJournalFileBytes = (long)MaxJournalChars * 4;

    internal static bool MayReadJournalFile(long byteLength) =>
        byteLength >= 2 && byteLength <= MaxJournalFileBytes;

    // Apply the SAME cap before durable writes and after reading a file.
    // Otherwise an oversized but successfully persisted AI state could be
    // rejected by this agent on its next launch.
    internal static bool FitsDurableJournal(int chars, int rows) =>
        chars >= 2 && chars <= MaxJournalChars &&
        rows >= 0 && rows <= MaxJournalRows;

    // Structural keys preserve the runtime's OrdinalIgnoreCase ownership
    // identity without creating a new Kind + "|" + Id string for every row.
    private sealed class OwnershipKeyComparer : IEqualityComparer<(string Kind, string Id)>
    {
        internal static readonly OwnershipKeyComparer Instance = new();
        private static readonly StringComparer Text = StringComparer.OrdinalIgnoreCase;

        public bool Equals((string Kind, string Id) x, (string Kind, string Id) y) =>
            Text.Equals(x.Kind, y.Kind) && Text.Equals(x.Id, y.Id);

        public int GetHashCode((string Kind, string Id) value) =>
            HashCode.Combine(Text.GetHashCode(value.Kind), Text.GetHashCode(value.Id));
    }

    internal enum CheckpointDecision
    {
        WriteRequired,
        AlreadyDurable,
        UnchangedNewMutation
    }

    // The journal contains immutable records only. If the current dictionary
    // yields exactly the previously durable values in the same order, sorting
    // and reserializing would produce the identical checkpoint. This check
    // allocates no per-row keys or JSON, and any uncertainty falls back to
    // the original full durable checkpoint path.
    internal static bool MatchesDurableValues(
        IReadOnlyList<AiSystemRuntimeItemState>? durable,
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection current)
    {
        if (durable is null || durable.Count != current.Count)
            return false;
        int index = 0;
        foreach (AiSystemRuntimeItemState state in current)
        {
            if (!Equals(durable[index], state))
                return false;
            index++;
        }
        return true;
    }

    // Recovery can accept an identical, already durable checkpoint.
    // An unchanged NEW Windows intervention is NOT a successful journal
    // write, even if a previous event happened to serialize identically.
    internal static CheckpointDecision DecideCheckpoint(
        string currentJson, string durableJson, bool recoveryOnly)
    {
        if (!string.Equals(currentJson, durableJson, StringComparison.Ordinal))
            return CheckpointDecision.WriteRequired;
        return recoveryOnly
            ? CheckpointDecision.AlreadyDurable
            : CheckpointDecision.UnchangedNewMutation;
    }

    // Never mistake malformed, duplicate or null ownership rows for an
    // empty first-install journal. A damaged journal is an admission veto:
    // otherwise a later routine save could overwrite the only recovery data.
    internal static bool TryDecodeJournal(
        string json, out AiSystemRuntimeItemState[] rows)
    {
        rows = Array.Empty<AiSystemRuntimeItemState>();
        if (json is null || !FitsDurableJournal(json.Length, 0))
            return false;
        try
        {
            AiSystemRuntimeItemState[]? loaded =
                JsonSerializer.Deserialize<AiSystemRuntimeItemState[]>(json);
            if (loaded is null || !FitsDurableJournal(json.Length, loaded.Length))
                return false;
            var seen = new HashSet<(string Kind, string Id)>(
                loaded.Length, OwnershipKeyComparer.Instance);
            foreach (AiSystemRuntimeItemState item in loaded)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Kind) ||
                    string.IsNullOrWhiteSpace(item.Id) ||
                    item.Kind.Contains('|') ||
                    !(item.Kind.Equals("service", StringComparison.OrdinalIgnoreCase) ||
                      item.Kind.Equals("task", StringComparison.OrdinalIgnoreCase) ||
                      item.Kind.Equals("driver", StringComparison.OrdinalIgnoreCase) ||
                      item.Kind.Equals("device", StringComparison.OrdinalIgnoreCase)) ||
                    // Kind may not contain the runtime's separator. For
                    // valid kinds, tuple identity exactly matches the live
                    // dictionary's OrdinalIgnoreCase Kind|Id key.
                    !seen.Add((item.Kind, item.Id)))
                    return false;
            }
            rows = loaded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static byte[] EncodeJournalJson(string serializedSnapshot) =>
        Encoding.UTF8.GetBytes(serializedSnapshot);
}
