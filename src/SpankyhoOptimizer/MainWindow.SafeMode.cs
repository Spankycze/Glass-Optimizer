using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private bool IsSafeModeAllowedSection(string key) =>
        key is "overview" or "settings" or "restore";

    private void BuildCrashSafeModePage()
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = "Aplikace zjistila několik nedokončených startů za sebou.",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = "Pro tento běh jsou nízkoúrovňové moduly dočasně blokované, aby šlo bezpečně obnovit nastavení.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        }); body.Children.Add(new TextBlock
        {
            Text = "Dostupné zůstávají: Přehled, Nastavení a Obnovení.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        ContentStack.Children.Add(VisualPanel("\uE7BA", "Safe mode", "Ochrana proti opakovanému startup pádu", body));
    }
}
