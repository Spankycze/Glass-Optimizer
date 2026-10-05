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
        catch (Exception ex)
        {
            LogService.Error("overview", "Changelog load failed", ex);
            return "Changelog se nepodařilo načíst.";
        }
    }

    private IReadOnlyList<ChangelogPreviewEntry> LoadChangelogPreview(int count = 4)
    {
        return BundledChangelogParser.NewestFirst(LoadBundledChangelog())
            .Where(s => s.ReleaseVersion != null)
            .GroupBy(s => s.ReleaseVersion).Select(g => g.First())
            .Take(Math.Max(1, count))
            .Select(s => new ChangelogPreviewEntry(s.Title, BundledChangelogParser.PreviewDetail(s))).ToArray();
    }

    private static string FormatChangelogNewestFirst(string source)
    {
        var sections = BundledChangelogParser.NewestFirst(source);
        return sections.Count == 0 ? source : string.Join(Environment.NewLine + Environment.NewLine,
            sections.Select(s => "## " + s.Title + Environment.NewLine + s.Body));
    }

    private async Task ShowChangelogPopupAsync()
    {
        string changelog = FormatChangelogNewestFirst(LoadBundledChangelog());
        var text = new TextBlock
        {
            Text = changelog,
            FontSize = 11.5,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var host = new StackPanel { Spacing = 10 };
        host.Children.Add(new TextBlock
        {
            Text = "Kompletní historie změn aplikace",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush()
        });
        host.Children.Add(text);

        await ShowStyledContentAsync(
            "Changelog",
            "Historie verzí, oprav, optimalizací a UI změn.",
            host,
            "Zavřít");
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
                if (token.IsCancellationRequested || _section != "overview" || !ReferenceEquals(rows, _overviewHistoryRows) || !ReferenceEquals(caption, _overviewChangelogCaption)) return;

                if (caption != null)
                    caption.Text = items.Count == 0
                        ? "Changelog je prázdný"
                        : "Klikni pro kompletní historii";

                for (int i = 0; i < _overviewHistoryRows.Length; i++)
                {
                    if (i < items.Count)
                    {
                        string title = items[i].Title;
                        string version = title;
                        int dash = title.IndexOf('—');
                        if (dash > 0) version = title[..dash].Trim();
                        _overviewHistoryTimes[i].Text = version;
                        _overviewHistoryRows[i].Text = string.IsNullOrWhiteSpace(items[i].Detail)
                            ? title
                            : items[i].Detail;
                    }
                    else
                    {
                        _overviewHistoryTimes[i].Text = "—";
                        _overviewHistoryRows[i].Text = "Bez další historie";
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
}