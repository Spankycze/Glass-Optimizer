using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SpankyhoOptimizer.Services;

// Lightweight on-demand page index: retain titles/offsets, never 1,690
// historical bodies. The bundled Markdown resource is immutable in a release.
internal sealed record BundledChangelogIndexEntry(
    string Title, Version? ReleaseVersion, int Position, int BodyStart, int BodyEnd);

internal sealed class BundledChangelogIndex
{
    private readonly string _path;
    internal IReadOnlyList<BundledChangelogIndexEntry> Sections { get; }

    private BundledChangelogIndex(string path, IReadOnlyList<BundledChangelogIndexEntry> sections)
    {
        _path = path;
        Sections = sections;
    }

    internal static BundledChangelogIndex Empty(string path) => new(path, Array.Empty<BundledChangelogIndexEntry>());

    // Only the small title/offset index is retained. The 3+ MB changelog and
    // individual page bodies are still read from disk only on demand.
    // Reopening the popup should not repeatedly allocate multi-MB byte arrays.
    private static readonly object IndexCacheLock = new();
    private static BundledChangelogIndex? _lastIndex;
    private static string? _lastIndexPath;
    private static DateTime _lastIndexWriteUtc;
    private static long _lastIndexLength;

    internal static BundledChangelogIndex OpenCached(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return Empty(path);
        DateTime stamp = file.LastWriteTimeUtc;
        long bytes = file.Length;
        lock (IndexCacheLock)
        {
            if (_lastIndex != null &&
                string.Equals(_lastIndexPath, path, StringComparison.OrdinalIgnoreCase) &&
                stamp == _lastIndexWriteUtc && bytes == _lastIndexLength)
                return _lastIndex;
            BundledChangelogIndex index = Open(path);
            // If a release replaced the file during indexing, don't retain a
            // possibly torn index. The following open will retry from disk.
            file.Refresh();
            if (file.Exists && file.LastWriteTimeUtc == stamp && file.Length == bytes)
            {
                _lastIndex = index;
                _lastIndexPath = path;
                _lastIndexWriteUtc = stamp;
                _lastIndexLength = bytes;
            }
            return index;
        }
    }

    internal static BundledChangelogIndex Open(string path)
    {
        if (!File.Exists(path)) return Empty(path);
        byte[] data = File.ReadAllBytes(path);
        var result = new List<BundledChangelogIndexEntry>();
        string? title = null;
        Version? version = null;
        int bodyStart = 0;
        int position = 0;

        void Finish(int bodyEnd)
        {
            if (title == null) return;
            result.Add(new BundledChangelogIndexEntry(title, version, position++, bodyStart, bodyEnd));
        }

        for (int at = 0; at < data.Length;)
        {
            int lineStart = at;
            while (at < data.Length && data[at] != (byte)'\n') at++;
            int end = at;
            if (end > lineStart && data[end - 1] == (byte)'\r') end--;
            int following = at < data.Length ? at + 1 : at;
            int contentStart = lineStart == 0 && end - lineStart >= 3 &&
                               data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : lineStart;
            bool header = (end - contentStart >= 2 && data[contentStart] == (byte)'#' &&
                           data[contentStart + 1] == (byte)' ') ||
                          (end - contentStart >= 3 && data[contentStart] == (byte)'#' &&
                           data[contentStart + 1] == (byte)'#' && data[contentStart + 2] == (byte)' ');
            if (header)
            {
                Finish(lineStart);
                title = Encoding.UTF8.GetString(data, contentStart, end - contentStart).TrimStart('#').Trim();
                Match match = Regex.Match(title, @"(?<!\d)(\d+\.\d+\.\d+)(?!\d)");
                version = match.Success && Version.TryParse(match.Value, out Version? parsed)
                    ? parsed : null;
                bodyStart = following;
            }
            at = following;
        }
        Finish(data.Length);
        var sorted = result.OrderByDescending(s => s.ReleaseVersion != null)
            .ThenByDescending(s => s.ReleaseVersion)
            .ThenBy(s => s.Position).ToArray();
        return new BundledChangelogIndex(path, sorted);
    }

    internal string FormatPage(IReadOnlyList<BundledChangelogIndexEntry> sections, int page, int pageSize = 8)
    {
        if (sections.Count == 0) return "Historie změn není dostupná.";
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        int lastPage = (sections.Count - 1) / pageSize;
        int first = Math.Clamp(page, 0, lastPage) * pageSize;
        var output = new StringBuilder();
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        for (int i = first; i < Math.Min(sections.Count, first + pageSize); i++)
        {
            var entry = sections[i];
            int count = entry.BodyEnd - entry.BodyStart;
            byte[] body = new byte[count];
            stream.Seek(entry.BodyStart, SeekOrigin.Begin);
            stream.ReadExactly(body);
            string text = Encoding.UTF8.GetString(body)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", Environment.NewLine, StringComparison.Ordinal)
                .Trim();
            if (output.Length != 0) output.AppendLine().AppendLine();
            output.Append("## ").Append(entry.Title).AppendLine().Append(text);
        }
        return output.ToString();
    }
}
