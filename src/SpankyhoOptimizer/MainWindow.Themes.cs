using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void BuildThemesPage()
    {
        var themesNavWatch = Stopwatch.StartNew();
        ContentStack.Children.Add(BuildSectionHero(
            "themes",
            "PERSONALIZACE",
            "Personalizace",
            "Glass Windows, tapety, kurzory, PowerShell a Explorer na jednom místě.",
            null));
        long themesHeroMs = themesNavWatch.ElapsedMilliseconds;

        var status = new StackPanel { Spacing = 4 };
        // Use one consistent wallpaper snapshot for header and both action cards.
        // Previously every re-navigation opened the wallpaper registry key 3 times.
        var currentWallpaperTheme = WallpaperThemeService.CurrentTheme;
        string wallpaper = currentWallpaperTheme switch
        {
            SpankyWallpaperTheme.NeonWave => "Windows Neon Wave",
            SpankyWallpaperTheme.PurpleValley => "Windows Purple Valley",
            _ => "Vlastní / Windows"
        };
        status.Children.Add(new TextBlock
        {
            Text = "Aktivní vzhled",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        status.Children.Add(new TextBlock
        {
            Text = $"Dark glass • Pozadí: {wallpaper}",
            FontSize = 12,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        ContentStack.Children.Add(BuildSectionSurface(
            "themes",
            "Stav",
            "Rychlý přehled aktivního vzhledu Windows",
            status));
        long themesStatusMs = themesNavWatch.ElapsedMilliseconds;

        var tools = new Grid { Name = "ThemeHubCards", ColumnSpacing = 10, RowSpacing = 10, MinHeight = 290 };
        tools.ColumnDefinitions.Add(new ColumnDefinition());
        tools.ColumnDefinitions.Add(new ColumnDefinition());
        // The four action-card subtrees take around 14 ms to construct on a
        // cold Themes navigation. Reserve their geometry, then materialize on
        // the low-priority dispatcher after the header's first paint. Loaded
        // retries when a cached host was detached before that callback ran.
        tools.Loaded += (_, _) =>
        {
            if (tools.Children.Count != 0) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_windowLifetimeCts.IsCancellationRequested || !tools.IsLoaded || tools.Children.Count != 0) return;
                AddThemeHubCard(tools, 0, 0, "ui", "Windows Glass",
                    "Průhlednost, blur, taskbar, Start, dialogy a systémové glass povrchy.",
                    "Otevřít", () => ShowSectionAsync("ui"));
                AddThemeHubCard(tools, 0, 1, "ui", "Kontextové menu",
                    "Klasické pravé menu, hover submenu a vlastní příkazy.",
                    "Nastavit", () => ShowSectionAsync("ui"));
                AddThemeHubCard(tools, 1, 0, "cursor", "Cursor",
                    "Systémové kurzory, community knihovna, náhled a aplikace motivu.",
                    "Otevřít", () => ShowSectionAsync("cursor"));
                AddThemeHubCard(tools, 1, 1, "powershell", "PowerShell",
                    "PowerShell 7, Terminal vzhled a související shell nastavení.",
                    "Otevřít", () => ShowSectionAsync("powershell"));
            });
        };

        ContentStack.Children.Add(BuildSectionSurface(
            "themes",
            "Vzhled Windows",
            "Glass, kontextové menu, kurzory a PowerShell jsou pohromadě; UI / Explorer a Nastavení zůstávají samostatně vlevo.",
            tools));
        long themesHubMs = themesNavWatch.ElapsedMilliseconds;

        var wallpapers = new Grid { Name = "ThemeWallpaperCards", ColumnSpacing = 10, RowSpacing = 10, MinHeight = 285 };
        wallpapers.ColumnDefinitions.Add(new ColumnDefinition());
        wallpapers.ColumnDefinitions.Add(new ColumnDefinition());

        // The previews are below the first viewport on compact windows. Keep
        // their layout space, but construct both WinUI card subtrees only after
        // the first page frame. Loaded re-arms when a cached page was detached
        // before the low-priority callback ran; never populate a detached tree.
        wallpapers.Loaded += (_, _) =>
        {
            if (wallpapers.Children.Count != 0) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_windowLifetimeCts.IsCancellationRequested || !wallpapers.IsLoaded ||
                    wallpapers.Children.Count != 0) return;
                AddWallpaperCard(wallpapers, 0, 0,
                    "Windows Neon Wave",
                    "Tmavý modro-fialový wave wallpaper s Windows logem — přímo z tvého nahraného obrázku.",
                    SpankyWallpaperTheme.NeonWave,
                    currentWallpaperTheme);
                AddWallpaperCard(wallpapers, 0, 1,
                    "Windows Purple Valley",
                    "Fialovo-modré údolí s Windows logem — druhý obrázek, který jsi poslal.",
                    SpankyWallpaperTheme.PurpleValley,
                    currentWallpaperTheme);
            });
        };

        var restore = ActionCard(
            "Původní pozadí",
            "Vrátí tapetu, která byla nastavená před prvním použitím tapet v Optimizeru.",
            "Vrátit",
            async () =>
            {
                bool ok = await Task.Run(WallpaperThemeService.RestoreOriginal);
                InvalidateThemesPageCache();
                await ShowMessageAsync("Personalizace", ok
                    ? "Původní Windows pozadí bylo obnoveno."
                    : "Původní pozadí není dostupné.");
            });

        var wallpaperHost = new StackPanel { Spacing = 0 };
        wallpaperHost.Children.Add(wallpapers);
        wallpaperHost.Children.Add(restore);
        ContentStack.Children.Add(BuildSectionSurface(
            "themes",
            "Pozadí",
            "Dva Windows wallpapery z tvých nahraných obrázků, zabalené přímo s Optimizerem.",
            wallpaperHost));
        long themesTotalMs = themesNavWatch.ElapsedMilliseconds;
        if (themesTotalMs >= 20 && LogService.PerformanceDiagnosticsEnabled)
            LogService.Info("performance", $"THEMES_BUILD_PHASE heroMs={themesHeroMs} statusMs={themesStatusMs - themesHeroMs} hubMs={themesHubMs - themesStatusMs} footerMs={themesTotalMs - themesHubMs} totalMs={themesTotalMs}");
    }

    private void AddThemeHubCard(
        Grid host,
        int row,
        int column,
        string iconTag,
        string title,
        string description,
        string buttonText,
        Func<Task> action)
    {
        while (host.RowDefinitions.Count <= row)
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(14),
            Background = GlassSurfaceLowBrush(),
            BorderBrush = GlassEdgeBrush(),
            BorderThickness = new Thickness(1),
            Child = SectionIcon(iconTag, 30),
            VerticalAlignment = VerticalAlignment.Center
        });

        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3
        });
        Grid.SetColumn(text, 1);
        body.Children.Add(text);

        var button = QuickButton(buttonText, action);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.Margin = new Thickness(0, 12, 0, 0);
        text.Children.Add(button);

        var card = new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1.1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Margin = new Thickness(0),
            Child = body
        };
        AttachHoverMotion(card, 1.006f);
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        host.Children.Add(card);
    }

    private void AddWallpaperCard(
        Grid host,
        int row,
        int column,
        string title,
        string description,
        SpankyWallpaperTheme theme,
        SpankyWallpaperTheme? currentWallpaperTheme)
    {
        while (host.RowDefinitions.Count <= row)
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        string previewPath = WallpaperThemeService.GetPreviewSource(theme);
        var previewImage = new Image
        {
            Name = "ThemeWallpaperPreview_" + theme,
            Height = 150,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        // The source photos are 2048x1152, but their cards are only 150 px tall.
        // Decode a bounded preview after the page has loaded, rather than
        // constructing full-size image sources on the navigation hot path.
        // A fresh Loaded event retries correctly if navigation detached the
        // cached tree before the low-priority callback had a chance to run.
        previewImage.Loaded += (_, _) =>
        {
            if (previewImage.Source != null) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_windowLifetimeCts.IsCancellationRequested ||
                    !previewImage.IsLoaded || previewImage.Source != null) return;
                if (!System.IO.File.Exists(previewPath)) return;
                previewImage.Source = new BitmapImage
                {
                    DecodePixelWidth = 1024,
                    UriSource = new Uri(previewPath)
                };
            });
        };

        var preview = new Border
        {
            Height = 150,
            CornerRadius = new CornerRadius(14),
            BorderBrush = GlassEdgeBrush(),
            BorderThickness = new Thickness(1),
            Child = previewImage
        };

        var body = new StackPanel { Spacing = 9 };
        body.Children.Add(preview);
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        body.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        bool active = currentWallpaperTheme == theme;
        var button = QuickButton(active ? "Aktivní" : "Použít", async () =>
        {
            await Task.Run(() => WallpaperThemeService.Apply(theme));
            InvalidateThemesPageCache();
            await ShowMessageAsync("Personalizace", $"Pozadí {title} bylo použito.");
            await ShowSectionAsync("themes");
        });
        button.IsEnabled = !active;
        body.Children.Add(button);

        var card = new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = active ? GlassBlueEdgeBrush() : SectionBorderBrush(),
            BorderThickness = new Thickness(active ? 1.5 : 1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Child = body
        };
        AttachHoverMotion(card, 1.006f);
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        host.Children.Add(card);
    }
}
