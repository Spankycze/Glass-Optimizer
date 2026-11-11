using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SpankyhoOptimizer.Services;

// Presentation-only formatting. No registry access or changes.
internal static class RegistryRestoreFeedbackService
{
    internal static string FormatPartialRestore(AggregateException failure, int maxEntries = 6)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (maxEntries < 1 || maxEntries > 30)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));

        IReadOnlyList<Exception> errors = failure.InnerExceptions;
        var sb = new StringBuilder();
        // The producer already provides successful/total counts in its
        // introductory message; exclude appended inner-exception paragraphs.
        string intro = failure.Message;
        int details = intro.IndexOf(" (", StringComparison.Ordinal);
        if (details >= 0) intro = intro[..details];
        intro = Normalize(intro, 290);
        sb.AppendLine(string.IsNullOrWhiteSpace(intro)
            ? "Obnova registru nebyla úplná."
            : intro);
        sb.AppendLine();
        sb.AppendLine("Neobnovené položky:");
        foreach (Exception error in errors.Take(maxEntries))
        {
            string label = Normalize(error.Message, 180);
            if (string.IsNullOrWhiteSpace(label))
                label = "Neznámá chyba při obnově.";
            string cause = error.InnerException is null
                ? string.Empty
                : Normalize(error.InnerException.Message, 140);
            sb.Append("• ").Append(label);
            if (!string.IsNullOrWhiteSpace(cause))
                sb.Append(" — ").Append(cause);
            sb.AppendLine();
        }
        if (errors.Count > maxEntries)
            sb.Append("… a dalších ").Append(errors.Count - maxEntries)
                .AppendLine(" neúspěšných položek.");
        sb.AppendLine();
        sb.Append("Ostatní úspěšně obnovená nastavení zůstala zachována. ")
          .Append("Zkontrolujte uvedené položky; neúplná obnova není úspěch.");
        return sb.ToString().TrimEnd();
    }

    private static string Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string cleaned = string.Join(" ", value.Split(
            new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..(maxLength - 1)] + "…";
        return cleaned;
    }
}
