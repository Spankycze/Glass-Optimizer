using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private readonly List<DesktopStatusNotificationWindow> _desktopStatusNotifications = new();

    private void ShowActionFeedback(string action, ActionFeedbackKind kind, string detail = "")
    {
        string area = string.IsNullOrWhiteSpace(_section) ? "app" : _section;
        ActionFeedbackService.Record(area, action, kind, detail);
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowActionFeedback(action, kind, detail));
            return;
        }

        try
        {
            // Feedback is a tray-style popup now; cap runaway bursts instead of
            // stacking an unbounded number of native windows.
            while (_desktopStatusNotifications.Count >= 3)
            {
                var oldest = _desktopStatusNotifications[0];
                _desktopStatusNotifications.RemoveAt(0);
                try { oldest.Close(); } catch { }
            }

            string body = string.IsNullOrWhiteSpace(detail) ? action : action + " \u2022 " + detail;
            var window = new DesktopStatusNotificationWindow(
                ActionFeedbackTitle(kind),
                body,
                kind,
                _desktopUpdateNotifications.Count + _desktopStatusNotifications.Count);
            _desktopStatusNotifications.Add(window);
            window.Closed += (_, _) => _desktopStatusNotifications.Remove(window);
            window.ShowWithoutActivation();
        }
        catch (Exception ex)
        {
            LogService.Error("ui", "Status popup could not be shown: " + action, ex);
        }
    }

    private static string ActionFeedbackTitle(ActionFeedbackKind kind) => kind switch
    {
        ActionFeedbackKind.Verified => "Hotovo \u2022 ov\u011b\u0159eno",
        ActionFeedbackKind.RestartRequired => "Pou\u017eito \u2022 nutn\u00fd restart",
        ActionFeedbackKind.Unsupported => "Nepodporov\u00e1no",
        ActionFeedbackKind.Failed => "Akce selhala",
        ActionFeedbackKind.Applied => "Pou\u017eito",
        _ => "Informace"
    };


    private UIElement BuildPageSummaryStrip(params (string Label, string Value, string Brush)[] items)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        for (int i = 0; i < items.Length; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < items.Length; i++)
        {
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(new TextBlock { Text = items[i].Label.ToUpperInvariant(), FontSize = 10.5, CharacterSpacing = 80, Foreground = TextSecondaryBrush(), FontWeight = FontWeights.SemiBold });
            stack.Children.Add(new TextBlock { Text = items[i].Value, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = UiBrush(items[i].Brush), TextWrapping = TextWrapping.Wrap });
            var card = BuildDashboardCard(stack, new Thickness(12, 10, 12, 10));
            Grid.SetColumn(card, i); grid.Children.Add(card);
        }
        return grid;
    }
}
