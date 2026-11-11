using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SpankyhoOptimizer.Services;

internal sealed record BundledChangelogSection(string Title, string Body, Version? ReleaseVersion, int Position);

internal sealed record BundledChangelogPreviewEntry(string Title, string Detail, Version ReleaseVersion);

internal static class BundledChangelogParser
{
    // Streaming preview: only four latest unique versions are needed in the
    // always-visible Overview. Never split/parse the multi-MB release history.
    // Full NewestFirst remains available for the on-demand paged dialog.
    internal static IReadOnlyList<BundledChangelogPreviewEntry> NewestPreviewFromFile(
        string path, int maxEntries)
    {
        int limit = Math.Max(1, maxEntries);
        if (!System.IO.File.Exists(path))
            return Array.Empty<BundledChangelogPreviewEntry>();
        var best = new List<BundledChangelogPreviewEntry>(limit);
        string? title = null;
        Version? version = null;
        string? firstBullet = null;
        string? firstDetail = null;

        void Finish()
        {
            if (title == null || version == null ||
                best.Exists(x => x.ReleaseVersion.Equals(version)))
                return;
            int insertAt = 0;
            while (insertAt < best.Count &&
                   best[insertAt].ReleaseVersion.CompareTo(version) > 0)
                insertAt++;
            if (insertAt >= limit) return;
            // Retain the first section for each version, as GroupBy().First()
            // does in the old sorted parser. All later ties are discarded.
            var detail = firstBullet ?? firstDetail ?? string.Empty;
            best.Insert(insertAt, new BundledChangelogPreviewEntry(title, detail, version));
            if (best.Count > limit) best.RemoveAt(limit);
        }

        using var reader = new System.IO.StreamReader(path, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal) ||
                line.StartsWith("# ", StringComparison.Ordinal))
            {
                Finish();
                title = line.TrimStart('#').Trim();
                var match = Regex.Match(title, @"(?<!\d)(\d+\.\d+\.\d+)(?!\d)");
                version = match.Success && Version.TryParse(match.Value, out var parsed)
                    ? parsed : null;
                firstBullet = null;
                firstDetail = null;
                continue;
            }
            if (version == null) continue;
            string trimmed = line.Trim();
            if (firstBullet == null && trimmed.StartsWith("- ", StringComparison.Ordinal))
                firstBullet = trimmed[2..].Trim();
            if (firstDetail == null && trimmed.Length > 0 && !trimmed.StartsWith('#'))
                firstDetail = trimmed;
        }
        Finish();
        return best;
    }
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
