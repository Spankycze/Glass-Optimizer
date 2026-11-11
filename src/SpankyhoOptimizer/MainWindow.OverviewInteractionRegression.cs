using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Explicit smoke only: validates actual rendered controls, never applies a tweak.
    private void VerifyOverviewInteractionSmoke()
    {
        var results = new List<object>();
        var failures = new List<string>();
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, outcome = "PASS" }); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); results.Add(new { name, outcome = "FAIL", error = ex.Message }); }
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        Check("changelog-native-keyboard-action", () =>
        {
            // Changelog is always visible inline and opens full history on demand.
            var button = _overviewChangelogAction;
            if (button == null || !button.IsTabStop || !button.IsEnabled ||
                AutomationProperties.GetName(button) != "Changelog" ||
                new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) == null)
                throw new InvalidOperationException("Changelog has no named, tab-focusable native Invoke action.");
        });
        Check("system-inline-three-cards-no-popup-no-duplicate-network", () =>
        {
            if (_overviewInfoFlyout != null || _overviewInfoButton != null ||
                _overviewInfoCardsGrid is not { Children.Count: 3 } ||
                _overviewLiveHost is not { Children.Count: 1 })
                throw new InvalidOperationException("System details aren't always inline.");
            if (_overviewInfoCardsGrid.Children.OfType<FrameworkElement>().Any(
                x => (x.Tag as string)?.Contains("Network", StringComparison.OrdinalIgnoreCase) == true))
                throw new InvalidOperationException("Network duplicated below the existing gauge.");
            if (_overviewInfoCardsGrid.MaxHeight > 90 &&
                (_referenceCpuPlotHost == null ||
                 _referenceCpuPlotHost.Height <= ReferenceBasePlotHeight))
                throw new InvalidOperationException("Inline system row expanded without viewport fit.");
        });
        Check("three-inline-card-borders-share-the-same-row", () =>
        {
            if (_overviewInfoCardsGrid is not { Children.Count: 3 } row ||
                row.Children[0] is not Border system ||
                row.Children[1] is not Border optimizer ||
                row.Children[2] is not Button action ||
                action.Content is not Border changelog)
                throw new InvalidOperationException("Inline card hierarchy changed.");

            row.UpdateLayout();
            if (system.ActualHeight < ReferenceBaseInfoHeight - 1 ||
                optimizer.ActualHeight < ReferenceBaseInfoHeight - 1 ||
                action.ActualHeight < ReferenceBaseInfoHeight - 1 ||
                changelog.ActualHeight < ReferenceBaseInfoHeight - 1 ||
                Math.Abs(system.ActualHeight - optimizer.ActualHeight) > 1.5 ||
                Math.Abs(system.ActualHeight - action.ActualHeight) > 1.5 ||
                Math.Abs(system.ActualHeight - changelog.ActualHeight) > 1.5)
                throw new InvalidOperationException(
                    $"Unequal inline-card heights: system={system.ActualHeight:0.#} " +
                    $"optimizer={optimizer.ActualHeight:0.#} button={action.ActualHeight:0.#} " +
                    $"changelog={changelog.ActualHeight:0.#}.");

            double systemTop = system.TransformToVisual(row).TransformPoint(new Windows.Foundation.Point()).Y;
            double optimizerTop = optimizer.TransformToVisual(row).TransformPoint(new Windows.Foundation.Point()).Y;
            double buttonTop = action.TransformToVisual(row).TransformPoint(new Windows.Foundation.Point()).Y;
            double changelogTop = changelog.TransformToVisual(row).TransformPoint(new Windows.Foundation.Point()).Y;
            if (Math.Abs(systemTop - optimizerTop) > 1.5 ||
                Math.Abs(systemTop - buttonTop) > 1.5 ||
                Math.Abs(systemTop - changelogTop) > 1.5)
                throw new InvalidOperationException(
                    $"Unequal inline-card top edges: system={systemTop:0.#} " +
                    $"optimizer={optimizerTop:0.#} button={buttonTop:0.#} " +
                    $"changelog={changelogTop:0.#}.");
        });
        Check("changelog-current-release-is-first", () =>
        {
            string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
            var previews = LoadChangelogPreview(1);
            var newest = previews.Count == 0 ? null : previews[0];
            if (newest == null || !newest.Title.StartsWith(version, StringComparison.Ordinal))
                throw new InvalidOperationException("Expected " + version + ", got " + newest?.Title);
        });
        // The visible network plot now belongs to the sixth-card layout.
        // Do not assert layout coordinates of a legacy chart collapsed behind
        // "Další informace" (ActualWidth=0 by design).
        var cached=_referenceNetworkHistory.ToArray();
        var activeChart=_referenceNetworkSpark?.Parent as Canvas;
        double originalWidth=activeChart?.Width ?? double.NaN;
        try
        {
            foreach(double width in new[]{96d,210d,460d})
                Check("network-plot-bounds-"+width,()=>{
                    if(activeChart==null || _referenceNetworkSpark==null)
                        throw new InvalidOperationException("Visible network chart missing.");
                    activeChart.Width=width;
                    activeChart.UpdateLayout();
                    _referenceNetworkHistory.Clear();
                    _referenceNetworkHistory.Enqueue(2);
                    _referenceNetworkHistory.Enqueue(8);
                    RenderReferenceNetworkSpark(activeChart);
                    foreach(var point in _referenceNetworkSpark.Points)
                        if(!double.IsFinite(point.X)||!double.IsFinite(point.Y)||
                            point.X<0||point.X>width||point.Y<0||point.Y>activeChart.Height)
                            throw new InvalidOperationException(
                                $"Point {point} escapes {width}x{activeChart.Height}.");
                    if(_referenceNetworkSpark.Points.Count==0 ||
                        Math.Abs(_referenceNetworkSpark.Points.Last().X-(width-2))>.1)
                        throw new InvalidOperationException(
                            "Visible network plot does not match viewport width.");
                    if(activeChart.Clip is not RectangleGeometry clip ||
                        Math.Abs(clip.Rect.Width-width)>.1)
                        throw new InvalidOperationException("Visible plot not clipped.");
                });
        }
        finally
        {
            if(activeChart!=null){activeChart.Width=originalWidth;activeChart.UpdateLayout();}
            _referenceNetworkHistory.Clear();
            foreach(double v in cached)_referenceNetworkHistory.Enqueue(v);
        }
        string report = Path.Combine(LogService.Root, "overview-interaction-smoke.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), utc = DateTime.UtcNow, passed = failures.Count == 0, results }));
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(" | ", failures));
        LogService.Info("visual-qa", $"OVERVIEW_INTERACTION_SMOKE_OK tests={results.Count}");
    }
}
