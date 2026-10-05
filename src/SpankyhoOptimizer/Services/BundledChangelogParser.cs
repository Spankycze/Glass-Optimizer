using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SpankyhoOptimizer.Services;

internal sealed record BundledChangelogSection(string Title, string Body, Version? ReleaseVersion, int Position);

internal static class BundledChangelogParser
{
    internal static IReadOnlyList<BundledChangelogSection> NewestFirst(string source)
    {
        var sections = new List<BundledChangelogSection>();
        string? title = null;
        var body = new StringBuilder();
        void Finish()
        {
            if (title == null) return;
            var match = Regex.Match(title, @"(?<!\d)(\d+\.\d+\.\d+)(?!\d)");
            Version? version = match.Success && Version.TryParse(match.Value, out var parsed) ? parsed : null;
            sections.Add(new(title, body.ToString().Trim(), version, sections.Count));
            body.Clear();
        }
        foreach (string line in source.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
            {
                Finish();
                title = line.TrimStart('#').Trim();
            }
            else if (title != null) body.AppendLine(line);
        }
        Finish();
        return sections.OrderByDescending(s => s.ReleaseVersion != null)
            .ThenByDescending(s => s.ReleaseVersion).ThenBy(s => s.Position).ToArray();
    }

    internal static string PreviewDetail(BundledChangelogSection section)
    {
        string[] lines = section.Body.Split('\n');
        string? bullet = lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("- ", StringComparison.Ordinal));
        if (bullet != null) return bullet[2..].Trim();
        return lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
    }
}
