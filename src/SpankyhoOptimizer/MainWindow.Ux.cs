using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private InfoBar? _actionFeedbackBar;

    private void ShowActionFeedback(string action, ActionFeedbackKind kind, string detail = "")
    {
        string area = string.IsNullOrWhiteSpace(_section) ? "app" : _section;
        ActionFeedbackService.Record(area, action, kind, detail);
        var bar = new InfoBar
        {
            IsOpen = true,
            IsClosable = true,
            Title = kind switch
            {
                ActionFeedbackKind.Verified => "Applied • Verified",
                ActionFeedbackKind.RestartRequired => "Applied • Restart required",
                ActionFeedbackKind.Unsupported => "Unsupported",
                ActionFeedbackKind.Failed => "Failed",
                ActionFeedbackKind.Applied => "Applied",
                _ => "Status"
            },
            Message = string.IsNullOrWhiteSpace(detail) ? action : action + " • " + detail,
            Severity = kind switch
            {
                ActionFeedbackKind.Verified => InfoBarSeverity.Success,
                ActionFeedbackKind.RestartRequired => InfoBarSeverity.Warning,
                ActionFeedbackKind.Unsupported => InfoBarSeverity.Warning,
                ActionFeedbackKind.Failed => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            },
            Margin = new Thickness(0, 0, 0, 10)
        };
        if (_actionFeedbackBar != null && ContentStack.Children.Contains(_actionFeedbackBar))
            ContentStack.Children.Remove(_actionFeedbackBar);
        _actionFeedbackBar = bar;
        ContentStack.Children.Insert(0, bar);
    }


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
