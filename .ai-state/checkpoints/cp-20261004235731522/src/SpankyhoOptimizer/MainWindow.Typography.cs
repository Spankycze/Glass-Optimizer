using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static double UiFontSize(string role) => (double)Application.Current.Resources[role];

    // Assert the actual rendered roles, including templates and theme rebuilds.
    // Deliberately independent upper limits prevent an enlarged token from passing unnoticed.
    private static void VerifyUiTypography(DependencyObject root, bool requireTweakRows)
    {
        int titles = 0, rows = 0, checkedText = 0;
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is TextBlock text && text.Tag is string role && role.StartsWith("Ui", StringComparison.Ordinal))
            {
                double limit = role switch
                {
                    "UiPageTitleSize" => 32, "UiGroupTitleSize" => 20,
                    "UiSubtitleSize" or "UiRowTitleSize" or "UiNavigationSize" => 14,
                    "UiDescriptionSize" => 12, _ => throw new InvalidOperationException("Unknown typography role: " + role)
                };
                if (text.FontSize <= 0 || text.FontSize > limit || text.FontSize != UiFontSize(role))
                    throw new InvalidOperationException($"Typography regression {role}: actual={text.FontSize} limit={limit}");
                if (role == "UiPageTitleSize") titles++;
                if (role == "UiRowTitleSize") rows++;
                checkedText++;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        if (requireTweakRows && (titles == 0 || rows == 0))
            throw new InvalidOperationException($"Typography regression: System template missing title={titles} rows={rows}");
        LogService.Info("visual-qa", $"UI_TYPOGRAPHY_OK count={checkedText} titles={titles} rows={rows}");
    }
}