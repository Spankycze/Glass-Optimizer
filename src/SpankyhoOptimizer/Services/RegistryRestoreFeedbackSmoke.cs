using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class RegistryRestoreFeedbackSmoke
{
    internal static Task RunAsync()
    {
        static void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("RESTORE_FEEDBACK_SMOKE_FAIL: " + name);
        }
        static Exception Item(string id, string cause = "Přístup odepřen") =>
            new InvalidOperationException("Obnova registry tweaku selhala: " + id,
                new UnauthorizedAccessException(cause));

        var failure = new AggregateException(
            "Registry obnova není úplná: úspěšně 3 z 5 unikátních položek, chyb 2. Neúspěšné položky zůstaly k ručnímu řešení.",
            Item("gt.sys.alpha"), Item("gt.ui.beta", "Chybí původní klíč"));
        string text = RegistryRestoreFeedbackService.FormatPartialRestore(failure);

        // 1. Surface the batch summary, not a misleading success claim.
        Check(text.Contains("úspěšně 3 z 5", StringComparison.Ordinal), "partial success summary");
        // 2. The first failed public ID is visible to the user.
        Check(text.Contains("gt.sys.alpha", StringComparison.Ordinal), "first failed ID");
        // 3. A second independent failed ID appears as well.
        Check(text.Contains("gt.ui.beta", StringComparison.Ordinal), "second failed ID");
        // 4. The underlying reason survives formatting.
        Check(text.Contains("Přístup odepřen", StringComparison.Ordinal), "root cause");
        // 5. A different root cause survives too.
        Check(text.Contains("Chybí původní klíč", StringComparison.Ordinal), "second cause");
        // 6. Explicitly explain partial restore is not a complete success.
        Check(text.Contains("neúplná obnova není úspěch", StringComparison.Ordinal),
              "non-success warning");
        // 7. Avoid dumping .NET AggregateException inner paragraphs twice.
        Check(text.Split("gt.sys.alpha", StringSplitOptions.None).Length == 2,
              "no duplicated inner exception");
        // 8. All successful entries are described as staying restored.
        Check(text.Contains("zůstala zachována", StringComparison.Ordinal),
              "successful entries stay restored");
        // 9. The output remains readable, without exception stack traces.
        Check(!text.Contains("at SpankyhoOptimizer", StringComparison.Ordinal) &&
              !text.Contains("System.AggregateException:", StringComparison.Ordinal),
              "no stack trace");

        // 10. Only first 2 of 5 entries are shown when maxEntries=2.
        var many = new AggregateException("Obnova nebyla úplná.",
            Enumerable.Range(0, 5).Select(i => Item("id-" + i)));
        string brief = RegistryRestoreFeedbackService.FormatPartialRestore(many, 2);
        Check(brief.Contains("id-0", StringComparison.Ordinal) &&
              brief.Contains("id-1", StringComparison.Ordinal) &&
              !brief.Contains("id-2", StringComparison.Ordinal),
              "bounded list");
        // 11. Number of hidden failures is explicit.
        Check(brief.Contains("dalších 3", StringComparison.Ordinal), "remaining count");
        // 12. Default cap is six entries.
        string cap = RegistryRestoreFeedbackService.FormatPartialRestore(
            new AggregateException("Neúplná obnova.",
                Enumerable.Range(0, 8).Select(i => Item("id-" + i))));
        Check(cap.Contains("id-5", StringComparison.Ordinal) &&
              !cap.Contains("id-6", StringComparison.Ordinal) &&
              cap.Contains("dalších 2", StringComparison.Ordinal), "default cap");
        // 13. Unknown, empty exception messages don't break the UI.
        string empty = RegistryRestoreFeedbackService.FormatPartialRestore(
            new AggregateException(" ", new InvalidOperationException(" ")));
        Check(empty.Contains("Obnova registru nebyla úplná.", StringComparison.Ordinal) &&
              empty.Contains("Neznámá chyba", StringComparison.Ordinal), "empty fields");
        // 14. Multiline data cannot create additional fake list entries.
        string injected = RegistryRestoreFeedbackService.FormatPartialRestore(
            new AggregateException("Chyba\nzáznamu",
                new InvalidOperationException("id-one\nid-two\tpříčina")));
        Check(!injected.Contains("\nid-two", StringComparison.Ordinal) &&
              !injected.Contains("Chyba\n", StringComparison.Ordinal), "multiline safe");
        // 15. Invalid maximum (zero) is refused.
        bool invalidZero = false;
        try { RegistryRestoreFeedbackService.FormatPartialRestore(failure, 0); }
        catch (ArgumentOutOfRangeException) { invalidZero = true; }
        Check(invalidZero, "zero cap invalid");
        // 16. Too large a maximum is refused to protect popup readability.
        bool invalidHuge = false;
        try { RegistryRestoreFeedbackService.FormatPartialRestore(failure, 31); }
        catch (ArgumentOutOfRangeException) { invalidHuge = true; }
        Check(invalidHuge, "huge cap invalid");
        // 17. Null failure must be refused rather than displayed as success.
        bool invalidNull = false;
        try { RegistryRestoreFeedbackService.FormatPartialRestore(null!); }
        catch (ArgumentNullException) { invalidNull = true; }
        Check(invalidNull, "null guard");
        // 18. Long ID and cause bounded independently.
        string longFormat = RegistryRestoreFeedbackService.FormatPartialRestore(
            new AggregateException(new string('x', 1000),
                new InvalidOperationException(new string('y', 700),
                    new System.IO.IOException(new string('z', 900)))));
        Check(longFormat.Length < 900 &&
              longFormat.Contains('…'), "bounded text");
        return Task.CompletedTask;
    }
}
