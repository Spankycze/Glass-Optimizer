using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Border? _componentsContextCard;

    private void BuildComponentsContextActions()
    {
        if (_componentsContextCard != null)
        {
            AttachCachedElement(_componentsContextCard);
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(PrimaryQuickButton("Doporučené podle toho, co používám", ShowComponentsContextDialogAsync));
        row.Children.Add(new TextBlock
        {
            Text = "Recommended respektuje funkce, které označíš jako používané.",
            Opacity = .65,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        _componentsContextCard = BuildDashboardExpander(
            "Recommended podle používání",
            "Služby a komponenty podle reálného použití PC",
            row);
        ContentStack.Children.Add(_componentsContextCard);
    }

    private async Task ShowComponentsContextDialogAsync()
    {
        await _engine.DetectAsync(_tweaks);
        var checks = new List<CheckBox>();
        var list = new StackPanel { Spacing = 10 };
        var intro = new Border { Background = AccentSoftBrush(), CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 8) };
        intro.Child = new TextBlock { Text = "Zaškrtni funkce, které opravdu používáš. Recommended je zachová a optimalizuje pouze zbytek.", TextWrapping = TextWrapping.Wrap, Foreground = TextPrimaryBrush(), FontSize = 14 };
        list.Children.Add(intro);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(QuickButton("Vybrat vše", () => { foreach (var c in checks) c.IsChecked = true; return Task.CompletedTask; }));
        buttons.Children.Add(QuickButton("Zrušit vše", () => { foreach (var c in checks) c.IsChecked = false; return Task.CompletedTask; }));
        list.Children.Add(buttons);
        var savedProfile = UserFeatureProfileService.Load();
        foreach (var item in ContextOptionsForCurrentSub())
        {
            var cb = new CheckBox { Content = item.Label, Tag = item.Key, IsChecked = savedProfile.Keeps(item.Key), FontSize = 14, MinHeight = 34 };
            checks.Add(cb); list.Children.Add(cb);
        }
        var frame = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.2 : 1), CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Child = new ScrollViewer { Content = list, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden } };
        if (!await ShowStyledContentAsync((_sub ?? "Komponenty") + " — co používám", "Vyber funkce a hardware, které na tomto PC opravdu používáš. Recommended je ochrání.", frame, "Použít Recommended")) return;
        var keep = checks.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        UserFeatureProfileService.Save(keep);
        var plan = await RecommendedEngine.BuildAsync(_tweaks);
        var decisions = plan.Decisions.ToDictionary(x => x.TweakId, StringComparer.OrdinalIgnoreCase);
        foreach (var t in _tweaks) t.Pending = decisions.TryGetValue(t.Id, out var d) && d.Eligible ? d.Enable : t.Current;
        UpdateDirtyState();
        if (string.Equals(_sub, "Ovladače", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshDriverTweakRowsAsync(System.Threading.CancellationToken.None);
            if (_componentsPageCache.ContainsKey("Ovladače"))
                _componentsPageRevisionStamp["Ovladače"] = CurrentDriverRevisionStamp();
        }
        else
        {
            RefreshCards();
        }
    }

    private readonly record struct ContextOption(string Key, string Label);

    private static readonly ContextOption[] ServiceContextOptions =
    {
        new("printer","Tiskárna / Print Spooler"), new("bluetooth","Bluetooth / BT audio"),
        new("xbox","Xbox / Game Pass"), new("bio","Windows Hello / biometrie"), new("phone","Phone Link"),
        new("smartcard","Smart Card"), new("sensors","Senzory"), new("camera","Kamera / skener"),
        new("hotspot","Mobile Hotspot / sdílení internetu"), new("rdp","Remote Desktop"), new("vpn","VPN / Remote Access"),
        new("discovery","Network Discovery / UPnP"), new("webdav","WebDAV"), new("touch","Dotyk / pero"), new("winrm","WinRM / Event Collector")
    };

    private static readonly ContextOption[] DriverContextOptions =
    {
        new("bluetooth","Bluetooth zařízení / BT audio / gamepad"), new("cdrom","CD/DVD nebo ISO mount"),
        new("serial","COM / sériová zařízení"), new("parallel","LPT / paralelní zařízení"),
        new("vpn","VPN / WAN / PPP"), new("iscsi","iSCSI / síťové storage"),
        new("discovery","Starší LAN discovery / NetBIOS"), new("hotspot","Mobile Hotspot / Miracast / Virtual Wi-Fi")
    };

    private static readonly ContextOption[] TaskContextOptions =
    {
        new("Telemetry","Telemetry"), new("Feedback","Feedback"), new("Maps","Offline mapy"),
        new("Office","Microsoft Office"), new("Family","Family Safety"), new("Location","Poloha"),
        new("Updates","Automatické aktualizace aplikací / browserů"), new("Launchers","Herní launchery")
    };

    private ContextOption[] ContextOptionsForCurrentSub() =>
        _sub == "Služby" ? ServiceContextOptions :
        _sub == "Ovladače" ? DriverContextOptions :
        TaskContextOptions;

}
