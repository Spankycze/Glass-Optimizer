using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task BuildSpicetifyTweaksAsync(StackPanel root, CancellationToken token)
    {
        if (root.Children.Count > 0) return;
        SpicetifyTweakState state = await SpicetifyService.GetTweakStateAsync(token);
        root.Children.Add(new TextBlock
        {
            Text = "Bezpečné Spicetify tweaky. Recommended vypne Sentry/UI logging, zachová API pro extensions a nechá bezpečné theme volby.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondaryBrush()
        });

        ToggleSwitch sentry = TweakSwitch("Vypnout Sentry / error reporting", state.DisableSentry);
        ToggleSwitch uiLog = TweakSwitch("Vypnout Spotify UI logging", state.DisableUiLogging);
        ToggleSwitch apis = TweakSwitch("Expose APIs pro extensions", state.ExposeApis);
        ToggleSwitch upgrades = TweakSwitch("Kontrolovat aktualizace Spicetify", state.CheckUpgrade);
        ToggleSwitch css = TweakSwitch("Theme CSS", state.InjectCss);
        ToggleSwitch js = TweakSwitch("Theme JavaScript", state.InjectThemeJs);
        ToggleSwitch colors = TweakSwitch("Theme colors", state.ReplaceColors);
        ToggleSwitch assets = TweakSwitch("Přepisovat Spotify assets", state.OverwriteAssets);

        foreach (ToggleSwitch item in new[] { sentry, uiLog, apis, upgrades, css, js, colors, assets })
            root.Children.Add(item);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Recommended", () =>
            RunSpicetifyActionAsync("Recommended Spotify / Spicetify", SpicetifyService.ApplyRecommendedTweaksAsync, token)));
        actions.Children.Add(QuickButton("Použít vybrané", () =>
        {
            var selected = new SpicetifyTweakState(
                sentry.IsOn, uiLog.IsOn, apis.IsOn, upgrades.IsOn,
                css.IsOn, js.IsOn, colors.IsOn, assets.IsOn);
            return RunSpicetifyActionAsync("Spotify / Spicetify tweaky", ct => SpicetifyService.ApplyTweaksAsync(selected, ct), token);
        }));
        root.Children.Add(actions);
    }

    private static ToggleSwitch TweakSwitch(string header, bool isOn) => new()
    {
        IsOn = isOn,
        OnContent = header + "  •  ON",
        OffContent = header + "  •  OFF",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private void BuildSpicetifyExtensionManager(
        SpicetifyStatus status,
        StackPanel root,
        CancellationToken token,
        IReadOnlySet<string>? enabledSnapshot = null)
    {
        if (root.Children.Count > 0) return;
        IReadOnlySet<string> enabled = enabledSnapshot ?? SpicetifyService.GetEnabledExtensions();
        var switches = new List<ToggleSwitch>();

        if (status.Extensions.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Žádné lokální extensions zatím nejsou v adresáři Spicetify. Nainstaluj je přes Marketplace a potom dej Obnovit.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush()
            });
        }
        foreach (string extension in status.Extensions)
        {
            var toggle = TweakSwitch(extension, enabled.Contains(extension));
            toggle.Tag = extension;
            switches.Add(toggle);
            root.Children.Add(toggle);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(QuickButton("Použít extensions", () =>
        {
            string[] desired = switches.Where(x => x.IsOn).Select(x => x.Tag as string)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
            return RunSpicetifyActionAsync(
                "Spicetify extensions",
                ct => SpicetifyService.ApplyExtensionsAsync(status.Extensions, desired, ct), token);
        }));
        actions.Children.Add(QuickButton("Marketplace složka", () =>
        {
            SpicetifyService.OpenMarketplaceRoot();
            return Task.CompletedTask;
        }));
        root.Children.Add(actions);
    }
}
