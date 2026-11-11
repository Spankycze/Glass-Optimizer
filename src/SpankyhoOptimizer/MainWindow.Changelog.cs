using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private sealed record ChangelogPreviewEntry(string Title, string Detail);

    private string ChangelogPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Changelog.md");

    private string LoadBundledChangelog()
    {
        try
        {
            return File.Exists(ChangelogPath)
                ? File.ReadAllText(ChangelogPath, Encoding.UTF8)
                : "Changelog není v této sestavě dostupný.";
        }
        catch (IOException ex)
        {
            LogService.Error("overview", "Changelog load failed", ex);
            return "Changelog se nepodařilo načíst.";
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.Error("overview", "Changelog load failed", ex);
            return "Changelog se nepodařilo načíst.";
        }
        catch (System.Security.SecurityException ex)
        {
            LogService.Error("overview", "Changelog load failed", ex);
            return "Changelog se nepodařilo načíst.";
        }
    }

    // Keep only the tiny four-row Overview preview, never the ~1.4 MB history.
    // A file timestamp + size guard makes a published changelog update visible
    // on the next navigation; concurrent Overview refreshes share one parse.
    private readonly object _changelogPreviewLock = new();
    private ChangelogPreviewEntry[]? _changelogPreviewCache;
    private DateTime _changelogPreviewStampUtc;
    private long _changelogPreviewBytes = -2;
    private int _changelogPreviewCount;
    private int _changelogPreviewCacheMisses;

    private IReadOnlyList<ChangelogPreviewEntry> LoadChangelogPreview(int count = 4)
    {
        int limit = Math.Max(1, count);
        lock (_changelogPreviewLock)
        {
            var file = new FileInfo(ChangelogPath);
            bool exists = file.Exists;
            DateTime stamp = exists ? file.LastWriteTimeUtc : DateTime.MinValue;
            long bytes = exists ? file.Length : -1;
            if (_changelogPreviewCache is { } cached &&
                _changelogPreviewStampUtc == stamp &&
                _changelogPreviewBytes == bytes &&
                _changelogPreviewCount == limit)
                return cached;

            var result = BundledChangelogParser.NewestPreviewFromFile(ChangelogPath, limit)
                .Select(s => new ChangelogPreviewEntry(s.Title, s.Detail))
                .ToArray();
            _changelogPreviewCache = result;
            _changelogPreviewStampUtc = stamp;
            _changelogPreviewBytes = bytes;
            _changelogPreviewCount = limit;
            _changelogPreviewCacheMisses++;
            return result;
        }
    }

    private const int ChangelogPageSize = 8;

    // Render at most one page into WinUI rather than feeding the entire
    // multi-megabyte developer history into one TextBlock.
    private static string FormatChangelogPage(
        IReadOnlyList<BundledChangelogSection> sections, int page,
        int pageSize = ChangelogPageSize)
    {
        if (sections.Count == 0) return "Historie změn není dostupná.";
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        int lastPage = (sections.Count - 1) / pageSize;
        int first = Math.Clamp(page, 0, lastPage) * pageSize;
        return string.Join(Environment.NewLine + Environment.NewLine,
            sections.Skip(first).Take(pageSize).Select(
                section => "## " + section.Title + Environment.NewLine + section.Body));
    }

    internal string RunChangelogPagingPolicySmoke()
    {
        var sections = BundledChangelogParser.NewestFirst(LoadBundledChangelog());
        if (sections.Count == 0 || sections[0].ReleaseVersion == null)
            throw new InvalidOperationException("Changelog has no released versions.");
        Version assemblyVersion = typeof(MainWindow).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Assembly has no version.");
        if (sections[0].ReleaseVersion > assemblyVersion)
            throw new InvalidOperationException("Changelog is newer than the current assembly.");
        int pages = (sections.Count + ChangelogPageSize - 1) / ChangelogPageSize;
        int visible = 0;
        for (int index = 0; index < pages; index++)
            visible += Math.Min(ChangelogPageSize, sections.Count - index * ChangelogPageSize);
        if (visible != sections.Count || pages < 1)
            throw new InvalidOperationException("Changelog paging loses historical releases.");
        string newest = FormatChangelogPage(sections, 0);
        string oldest = FormatChangelogPage(sections, pages - 1);
        if (!newest.Contains(sections[0].Title, StringComparison.Ordinal) ||
            !oldest.Contains(sections[^1].Title, StringComparison.Ordinal) ||
            (pages > 1 && newest.Contains(sections[^1].Title, StringComparison.Ordinal)))
            throw new InvalidOperationException("Changelog page boundary or order is incorrect.");
        // Release gate: the indexed popup must preserve every visible page,
        // not just the newest one. Run against the authoritative legacy
        // parser without introducing a second production history cache.
        var indexedHistory = BundledChangelogIndex.Open(ChangelogPath);
        if (indexedHistory.Sections.Count != sections.Count)
            throw new InvalidOperationException("Indexed changelog lost historical sections.");
        for (int p = 0; p < pages; p++)
        {
            string expectedPage = FormatChangelogPage(sections, p);
            string actualPage = indexedHistory.FormatPage(indexedHistory.Sections, p, ChangelogPageSize);
            if (!string.Equals(expectedPage, actualPage, StringComparison.Ordinal))
                throw new InvalidOperationException($"Indexed changelog mismatch at page {p + 1}.");
        }
        foreach (string term in new[] { "15.4.", "Windows", "GPU", "privacy", "nonexistent-zzzzz" })
        {
            var oldFiltered = sections.Where(s =>
                s.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
            var newFiltered = indexedHistory.Sections.Where(s =>
                s.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (oldFiltered.Length != newFiltered.Length)
                throw new InvalidOperationException($"Indexed changelog search count mismatch: {term}");
            int filteredPages = (oldFiltered.Length + ChangelogPageSize - 1) / ChangelogPageSize;
            for (int p = 0; p < Math.Max(1, filteredPages); p++)
                if (!string.Equals(FormatChangelogPage(oldFiltered, p),
                        indexedHistory.FormatPage(newFiltered, p, ChangelogPageSize),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException($"Indexed changelog search mismatch: {term} page {p + 1}");
        }
        LogService.Info("performance", $"CHANGELOG_INDEX_PARITY_SMOKE_OK sections={sections.Count} pages={pages} searchQueries=5");
        // Compare the bounded streaming algorithm against the authoritative
        // full-file semantics (version order, first duplicate, first bullet).
        var expectedPreview = sections.Where(s => s.ReleaseVersion != null)
            .GroupBy(s => s.ReleaseVersion).Select(g => g.First()).Take(4)
            .Select(s => new ChangelogPreviewEntry(s.Title, BundledChangelogParser.PreviewDetail(s)))
            .ToArray();
        var actualPreview = BundledChangelogParser.NewestPreviewFromFile(ChangelogPath, 4)
            .Select(s => new ChangelogPreviewEntry(s.Title, s.Detail)).ToArray();
        if (!expectedPreview.SequenceEqual(actualPreview))
            throw new InvalidOperationException("Streaming changelog preview differs from full parser.");
        LogService.Info("performance", $"CHANGELOG_STREAM_PARITY_SMOKE_OK entries={actualPreview.Length}");
        LogService.Info("performance", RunChangelogPreviewCachePolicySmoke());
        LogService.Info("performance", RunChangelogPopupIndexCachePolicySmoke());
        return $"CHANGELOG_PAGING_SMOKE_OK sections={sections.Count} pages={pages} " +
               $"firstPageChars={newest.Length} completeHistory=true";
    }

    private async Task ShowChangelogPopupAsync()
    {
        // Only an uncached changelog index needs parsing off the UI dispatcher.
        BundledChangelogIndex indexed;
        try { indexed = await Task.Run(() => BundledChangelogIndex.OpenCached(ChangelogPath)); }
        catch (IOException ex)
        {
            LogService.Error("overview", "Indexed changelog load failed", ex);
            indexed = BundledChangelogIndex.Empty(ChangelogPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.Error("overview", "Indexed changelog load failed", ex);
            indexed = BundledChangelogIndex.Empty(ChangelogPath);
        }
        catch (System.Security.SecurityException ex)
        {
            LogService.Error("overview", "Indexed changelog load failed", ex);
            indexed = BundledChangelogIndex.Empty(ChangelogPath);
        }
        IReadOnlyList<BundledChangelogIndexEntry> all = indexed.Sections;
        IReadOnlyList<BundledChangelogIndexEntry> filtered = all;
        int page = 0;
        var text = new TextBlock
        {
            FontSize = 11.5,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var heading = new TextBlock
        {
            Text = "Kompletní historie změn aplikace",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush()
        };
        var search = new TextBox
        {
            PlaceholderText = "Hledat verzi nebo název změny…",
            MaxWidth = 510
        };
        var previous = new Button { Content = "← Novější" };
        var next = new Button { Content = "Starší →" };
        var position = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSecondaryBrush(),
            FontSize = 11
        };
        void Refresh()
        {
            int pages = Math.Max(1, (filtered.Count + ChangelogPageSize - 1) / ChangelogPageSize);
            page = Math.Clamp(page, 0, pages - 1);
            text.Text = indexed.FormatPage(filtered, page, ChangelogPageSize);
            position.Text = $"Strana {page + 1}/{pages} · {filtered.Count} verzí";
            previous.IsEnabled = page > 0;
            next.IsEnabled = page + 1 < pages;
        }
        previous.Click += (_, _) => { page--; Refresh(); };
        next.Click += (_, _) => { page++; Refresh(); };
        search.TextChanged += (_, _) =>
        {
            string term = search.Text.Trim();
            filtered = term.Length == 0 ? all :
                all.Where(s => s.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
            page = 0;
            Refresh();
        };
        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { previous, position, next }
        };
        var host = new StackPanel { Spacing = 10 };
        host.Children.Add(heading);
        host.Children.Add(search);
        host.Children.Add(navigation);
        host.Children.Add(text);
        Refresh();
        await ShowStyledContentAsync(
            "Changelog", "Historie verzí, oprav, optimalizací a UI změn.",
            host, "Zavřít");
    }

    private async Task RefreshOverviewChangelogRowsAsync(CancellationToken token)
    {
        try
        {
            var rows = _overviewHistoryRows;
            var versions = _overviewHistoryTimes;
            var caption = _overviewChangelogCaption;
            if (rows == null || versions == null || caption == null) return;
            var items = await Task.Run(() => LoadChangelogPreview(4), token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _section != "overview" || !ReferenceEquals(rows, _overviewHistoryRows) || !ReferenceEquals(versions, _overviewHistoryTimes) || !ReferenceEquals(caption, _overviewChangelogCaption)) return;

                SetOverviewTextIfChanged(caption,
                    items.Count == 0 ? "Changelog je prázdný" : "Klikni pro kompletní historii");

                for (int i = 0; i < Math.Min(rows.Length, versions.Length); i++)
                {
                    if (i < items.Count)
                    {
                        string title = items[i].Title;
                        string version = title;
                        int dash = title.IndexOf('—');
                        if (dash > 0) version = title[..dash].Trim();
                        SetOverviewTextIfChanged(versions[i], version);
                        SetOverviewTextIfChanged(rows[i],
                            FormatOverviewInlineChangelogRow(title, items[i].Detail));
                    }
                    else
                    {
                        SetOverviewTextIfChanged(versions[i], "—");
                        SetOverviewTextIfChanged(rows[i], "Bez další historie");
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("overview", "Changelog preview failed", ex);
        }
    }

    private static string FormatOverviewInlineChangelogRow(string title, string? detail)
    {
        string raw = string.IsNullOrWhiteSpace(detail) ? title : detail;
        string cleaned = raw
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("- ACTIVE WORK_PROGRESS -", "•", StringComparison.OrdinalIgnoreCase)
            .Replace("ACTIVE WORK_PROGRESS", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("STRICT VERIFIED RELEASE", "•", StringComparison.OrdinalIgnoreCase)
            .Replace("Real production defect:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(cleaned) ? title : cleaned;
    }
}
