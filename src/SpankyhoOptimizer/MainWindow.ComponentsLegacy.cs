using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        string componentSub = _sub ?? "Služby";
        // Capture the selection scope before the awaited detection. Navigating
        // while it runs must not silently change the keys presented to the user.
        ContextOption[] shownOptions = componentSub == "Služby"
            ? ServiceContextOptions : componentSub == "Ovladače"
                ? DriverContextOptions : TaskContextOptions;
        string componentCacheKey = "components|" + componentSub;
        if (!_tweakCache.TryGetValue(componentCacheKey, out var contextTweaks))
            _tweakCache[componentCacheKey] = contextTweaks = CatalogService.GetTweaks("components", componentSub);
        int navigationGeneration = _navigationGeneration;
        CancellationToken navigationToken = _loadCts?.Token ?? _windowLifetimeCts.Token;
        // Detection can finish after another page is selected. Work against
        // the captured catalog; never hand a mutable global _tweaks to the
        // awaited operation and then render controls for a different page.
        await _engine.DetectAsync(contextTweaks, navigationToken);
        if (navigationToken.IsCancellationRequested ||
            navigationGeneration != _navigationGeneration ||
            _section != "components" ||
            !string.Equals(_sub ?? "Služby", componentSub, StringComparison.OrdinalIgnoreCase))
            return;
        _tweaks = contextTweaks;
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
        if (savedProfile.IsUntrusted)
        {
            await ShowStyledInfoAsync("Používané funkce — profil nedostupný",
                "Uložený profil není bezpečně čitelný. Aby se neztratily vybrané funkce, " +
                "nelze nyní potvrdit nové nastavení ani spustit Recommended. " +
                "Po opravě souboru user-feature-profile.json to lze zkusit znovu.");
            return;
        }
        // Bind this dialog to its original subsection. Only its displayed
        // keys may change; a later Save must preserve all other components
        // and the background apps' 'Ponechat' choices.
        foreach (var item in shownOptions)
        {
            var cb = new CheckBox { Content = item.Label, Tag = item.Key, IsChecked = savedProfile.Keeps(item.Key), FontSize = 14, MinHeight = 34 };
            checks.Add(cb); list.Children.Add(cb);
        }
        var frame = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.2 : 1), CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Child = new ScrollViewer { Content = list, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden } };
        if (!await ShowStyledContentAsync((_sub ?? "Komponenty") + " — co používám", "Vyber funkce a hardware, které na tomto PC opravdu používáš. Recommended je ochrání.", frame, "Použít Recommended")) return;
        // Do not apply a stale dialog to a different section if navigation
        // changed while the modal was open.
        if (navigationToken.IsCancellationRequested ||
            navigationGeneration != _navigationGeneration ||
            !string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_sub ?? "Služby", componentSub, StringComparison.OrdinalIgnoreCase))
            return;
        var keep = checks.Where(x => x.IsChecked == true)
            .Select(x => (string)x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayedKeys = shownOptions.Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Merge under the cross-process file lease with a FRESH profile:
        // clearing a checkbox must not clear user preferences from other
        // tabs, or an unrelated background-app Keep choice.
        UserFeatureProfile updatedProfile;
        try
        {
            updatedProfile = UserFeatureProfileService.Update(current =>
            {
                // Another window may have edited one of these SAME visible
                // choices while the dialog was open. Do not silently revert
                // its change with stale checkboxes; ask the user to reopen.
                if (shownOptions.Any(option =>
                    current.Keeps(option.Key) != savedProfile.Keeps(option.Key)))
                    throw new InvalidOperationException(
                        "Některé volby v této sekci se mezitím změnily. Otevři výběr znovu.");
                return current.KeepFeatures
                    .Where(key => !displayedKeys.Contains(key))
                    .Concat(keep).ToArray();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.IO.IOException or UnauthorizedAccessException)
        {
            LogService.Error("profile", "Component preferences were not saved", ex);
            await ShowStyledInfoAsync("Používané funkce – neuloženo",
                "Jiná instance mohla právě změnit profil. Žádná tvoje volba nebyla přepsána. " +
                "Zkus otevřít výběr znovu.\n" + ex.Message);
            return;
        }
        // Update only the existing advisor counter; rebuilding the page
        // would discard the user's current scroll position and toggles.
        RefreshComponentAdvisorCount(componentSub, updatedProfile);
        // The user can edit toggles while the hardware recommendation is
        // computing. Never apply a delayed plan to a newer row revision.
        var recommendationRows = contextTweaks
            .Select(t => (Tweak: t, Revision: t.Revision)).ToArray();
        var plan = await RecommendedEngine.BuildAsync(contextTweaks, navigationToken);
        // Preferences are already persisted, but a late plan must not mutate
        // tweak Pending values or redraw the newly navigated page.
        if (navigationToken.IsCancellationRequested ||
            navigationGeneration != _navigationGeneration ||
            !string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_sub ?? "Služby", componentSub, StringComparison.OrdinalIgnoreCase))
            return;
        if (plan.HardwareSummary.StartsWith("Recommended blocked", StringComparison.Ordinal))
        {
            ShowActionFeedback("Recommended", ActionFeedbackKind.Failed,
                "Bezpečný profil doporučení není dostupný; ručně vybrané tweaky zůstaly beze změny.");
            return;
        }
        var decisions = plan.Decisions.ToDictionary(x => x.TweakId, StringComparer.OrdinalIgnoreCase);
        int skippedNewer = 0;
        foreach (var row in recommendationRows)
        {
            decisions.TryGetValue(row.Tweak.Id, out RecommendationDecision? decision);
            if (!TryStageComponentRecommendation(row.Tweak, row.Revision, decision))
                skippedNewer++;
        }
        if (skippedNewer > 0)
            ShowActionFeedback("Recommended", ActionFeedbackKind.Info,
                $"Zachováno {skippedNewer} novějších nebo právě načítaných voleb; změň je případně ručně.");
        UpdateDirtyState();
        if (string.Equals(_sub, "Ovladače", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshDriverTweakRowsAsync(_windowLifetimeCts.Token);
            if (_componentsPageCache.ContainsKey("Ovladače"))
                _componentsPageRevisionStamp["Ovladače"] = CurrentDriverRevisionStamp();
        }
        else
        {
            RefreshCards();
        }
    }

    // A queued plan is advisory, not permission to overwrite a row that the
    // user or a background Windows refresh changed after confirmation.
    internal static bool TryStageComponentRecommendation(
        TweakDefinition tweak, int expectedRevision, RecommendationDecision? decision)
    {
        if (!tweak.IsLoaded || tweak.IsBusy || tweak.Revision != expectedRevision)
            return false;
        tweak.Pending = decision is { Eligible: true } ? decision.Enable : tweak.Current;
        return true;
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
        new("Updates","Automatické aktualizace aplikací"), new("Launchers","Herní launchery")
    };

    private ContextOption[] ContextOptionsForCurrentSub() =>
        _sub == "Služby" ? ServiceContextOptions :
        _sub == "Ovladače" ? DriverContextOptions :
        TaskContextOptions;

}
