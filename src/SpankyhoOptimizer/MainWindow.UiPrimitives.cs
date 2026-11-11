using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Button PrimaryQuickButton(string text, Func<Task> action, Func<bool>? canExecute = null)
    {
        var button = QuickButton(text, action, canExecute);
        ApplyMaterialButtonAppearance(button, primary: true);
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(UseGlassChrome ? 14 : 7);
        button.MinHeight = UseGlassChrome ? 36 : 34;
        button.Padding = new Thickness(UseGlassChrome ? 16 : 14, 6, UseGlassChrome ? 16 : 14, 6);
        button.FontSize = 12.5;
        button.FontWeight = FontWeights.SemiBold;
        EnsureAccessibleButton(button);
        return button;
    }

    private static void EnsureAccessibleButton(Button button, string? fallbackName = null)
    {
        string current = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button);
        if (!string.IsNullOrWhiteSpace(current)) return;

        string? name = ExtractAccessibleText(button.Content);
        if (string.IsNullOrWhiteSpace(name))
        {
            object? tip = ToolTipService.GetToolTip(button);
            name = tip switch
            {
                string s => s,
                TextBlock tb => tb.Text,
                _ => fallbackName
            };
        }

        if (!string.IsNullOrWhiteSpace(name))
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name.Trim());
    }

    private static string? ExtractAccessibleText(object? content)
    {
        if (content is string s) return s;
        if (content is TextBlock text) return text.Text;
        if (content is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                string? found = ExtractAccessibleText(child);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }
        if (content is ContentControl cc) return ExtractAccessibleText(cc.Content);
        return null;
    }

}

