using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Border? _winhanceUiToolsCard;

    private void BuildWinhanceUiActions()
    {
        if (_winhanceUiToolsCard != null)
        {
            AttachCachedElement(_winhanceUiToolsCard);
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(QuickButton("Regionální formáty", ShowRegionalFormatsAsync));
        _winhanceUiToolsCard = BuildDashboardExpander(
            "UI nástroje",
            "Regionální formáty Windows",
            row);
        ContentStack.Children.Add(_winhanceUiToolsCard);
    }

    private async Task ShowRegionalFormatsAsync()
    {
        var current = WinhanceUiService.ReadRegional();
        var root = new StackPanel { Spacing = 8, MinWidth = 470 };
        var shortDate = AddRegionalCombo(root, "Krátké datum", new[] { "M/d/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "yyyy/MM/dd", "dd MMM yyyy" }, current["sShortDate"]);
        var firstDay = AddRegionalCombo(root, "První den týdne", new[] { "Neděle", "Pondělí", "Úterý", "Středa", "Čtvrtek", "Pátek", "Sobota" }, FirstDayLabel(current["iFirstDayOfWeek"]));
        var numberDecimal = AddRegionalCombo(root, "Desetinný oddělovač", new[] { ".", ",", "mezera", "'" }, DisplaySymbol(current["sDecimal"]));
        var listSep = AddRegionalCombo(root, "Oddělovač seznamu", new[] { ",", ";" }, current["sList"]);
        var measure = AddRegionalCombo(root, "Měrná soustava", new[] { "Metrická", "US / Imperial" }, current["iMeasure"] == "1" ? "US / Imperial" : "Metrická");
        var currencyDecimal = AddRegionalCombo(root, "Desetinný oddělovač měny", new[] { ".", ",", "mezera", "'" }, DisplaySymbol(current["sMonDecimalSep"]));

        if (!await ShowStyledContentAsync("Regionální formáty", "Volitelné uživatelské formáty Windows. Nejsou součástí Recommended.", root, "Použít")) return;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sShortDate"] = shortDate.SelectedItem?.ToString() ?? current["sShortDate"],
            ["iFirstDayOfWeek"] = FirstDayValue(firstDay.SelectedItem?.ToString()),
            ["sDecimal"] = RawSymbol(numberDecimal.SelectedItem?.ToString()),
            ["sList"] = listSep.SelectedItem?.ToString() ?? current["sList"],
            ["iMeasure"] = measure.SelectedIndex == 1 ? "1" : "0",
            ["sMonDecimalSep"] = RawSymbol(currencyDecimal.SelectedItem?.ToString())
        };
        WinhanceUiService.WriteRegional(values);
        await ShowMessageAsync("Regionální formáty", "Nastavení bylo zapsáno. Některé aplikace mohou vyžadovat nové přihlášení.");
    }
    private static ComboBox AddRegionalCombo(StackPanel root, string label, string[] values, string selected)
    {
        root.Children.Add(new TextBlock { Text = label });
        var combo = new ComboBox { MinWidth = 320 };
        foreach (string value in values) combo.Items.Add(value);
        int index = Array.FindIndex(values, x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;
        root.Children.Add(combo);
        return combo;
    }

    private static string DisplaySymbol(string value) => value == " " ? "mezera" : value;
    private static string RawSymbol(string? value) => value == "mezera" ? " " : value ?? string.Empty;
    private static string FirstDayLabel(string value) => value switch { "6" => "Neděle", "0" => "Pondělí", "1" => "Úterý", "2" => "Středa", "3" => "Čtvrtek", "4" => "Pátek", "5" => "Sobota", _ => "Pondělí" };
    private static string FirstDayValue(string? value) => value switch { "Neděle" => "6", "Pondělí" => "0", "Úterý" => "1", "Středa" => "2", "Čtvrtek" => "3", "Pátek" => "4", "Sobota" => "5", _ => "0" };
}
