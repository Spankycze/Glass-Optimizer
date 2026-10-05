using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static readonly IReadOnlyDictionary<string, string> HardwareIconMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["overview"] = "overview",
            ["power"] = "power",
            ["gpu"] = "gpu",
            ["memory"] = "ram",
            ["ram"] = "ram",
            ["network"] = "network",
            ["storage"] = "storage",
            ["system"] = "system",
            ["components"] = "components",
            ["usb"] = "usb",
            ["lianli"] = "lianli",
            ["startup"] = "startup",
            ["ui"] = "ui",
            ["update"] = "update",
            ["browsers"] = "browsers",
            ["spicetify"] = "spicetify",
            ["apps"] = "apps",
            ["privacy"] = "privacy",
            ["ai"] = "ai",
            ["ai-runtime"] = "ai",
            ["defender"] = "defender",
            ["debloat"] = "debloat",
            ["fixer"] = "fixer",
            // These utility tags intentionally reuse existing packaged SVGs instead of
            // probing non-existent assets and falling back after an ImageFailed event.
            ["logs"] = "fixer",
            ["activity"] = "overview",
            ["services"] = "components",
            ["drivers"] = "components",
            ["tasks"] = "components",
            ["restore"] = "update",
            ["cursor"] = "cursor",
            ["settings"] = "settings"
        };

    private static string SectionIconName(string tag) =>
        HardwareIconMap.TryGetValue(tag ?? string.Empty, out string? name) ? name : "overview";

    private static readonly object HardwareIconSourceCacheGate = new();
    private static readonly Dictionary<string, ImageSource> HardwareIconSourceCache = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource SectionIconSource(string tag)
    {
        string name = SectionIconName(tag);
        lock (HardwareIconSourceCacheGate)
        {
            if (HardwareIconSourceCache.TryGetValue(name, out ImageSource? cached))
                return cached;

            var source = new SvgImageSource(new Uri($"ms-appx:///Assets/SidebarIcons/{name}.svg", UriKind.Absolute));
            HardwareIconSourceCache[name] = source;
            return source;
        }
    }

    private static string FallbackGlyph(string tag) => tag switch
    {
        "overview" => "\uE80F",
        "power" => "\uE945",
        "gpu" => "\uE7F8",
        "memory" => "\uE950",
        "network" => "\uE968",
        "storage" => "\uEDA2",
        "system" => "\uE770",
        "components" => "\uE713",
        "usb" => "\uE88E",
        "lianli" => "\uE9CA",
        "startup" => "\uE768",
        "ui" => "\uE8A7",
        "update" => "\uE895",
        "browsers" => "\uE774",
        "spicetify" => "\uE8D6",
        "apps" => "\uE71D",
        "privacy" => "\uE72E",
        "ai" => "\uE9D9",
        "ai-runtime" => "\uE9D9",
        "defender" => "\uE83D",
        "debloat" => "\uE74D",
        "fixer" => "\uE90F",
        "powershell" => "\uE756",
        "settings" => "\uE713",
        _ => "\uE80F"
    };

    private static readonly IReadOnlyDictionary<string, string> SidebarIconMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["overview"] = "overview",
            ["power"] = "power",
            ["gpu"] = "gpu",
            ["network"] = "network",
            ["storage"] = "storage",
            ["system"] = "system",
            ["components"] = "components",
            ["usb"] = "usb",
            ["lianli"] = "lianli",
            ["startup"] = "startup",
            ["ui"] = "ui",
            ["update"] = "update",
            ["browsers"] = "browsers",
            ["spicetify"] = "spicetify",
            ["apps"] = "apps",
            ["privacy"] = "privacy",
            ["ai"] = "ai",
            ["ai-runtime"] = "ai",
            ["defender"] = "defender",
            ["debloat"] = "debloat",
            ["fixer"] = "fixer",
            ["powershell"] = "powershell",
            ["cursor"] = "cursor",
            ["settings"] = "settings"
        };

    private static string SidebarIconName(string section) =>
        SidebarIconMap.TryGetValue(section ?? string.Empty, out string? name) ? name : "overview";

    private static readonly object SidebarIconSourceCacheGate = new();
    private static readonly Dictionary<string, ImageSource> SidebarIconSourceCache = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource SidebarIconSource(string name)
    {
        lock (SidebarIconSourceCacheGate)
        {
            if (SidebarIconSourceCache.TryGetValue(name, out ImageSource? cached))
                return cached;

            var source = new SvgImageSource(new Uri($"ms-appx:///Assets/SidebarIcons/{name}.svg", UriKind.Absolute));
            SidebarIconSourceCache[name] = source;
            return source;
        }
    }

    private static FrameworkElement SidebarIcon(string section, double size)
    {
        string name = SidebarIconName(section);
        var fallback = new FontIcon
        {
            Glyph = FallbackGlyph(section),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = Math.Max(16, size * 0.82),
            Width = 32,
            Height = 32,
            Foreground = SidebarIconAccent(section),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.95
        };

        var image = new Image
        {
            Width = size,
            Height = size,
            Opacity = 1,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        void AssignFreshSource()
        {
            image.Source = SidebarIconSource(name);
        }

        image.ImageOpened += (_, _) => fallback.Visibility = Visibility.Collapsed;
        image.ImageFailed += (_, e) =>
        {
            fallback.Visibility = Visibility.Visible;
            image.Visibility = Visibility.Collapsed;
            LogService.Error("ui", $"SIDEBAR_ICON_LOAD_FAILED section={section} asset={name}.svg error={e.ErrorMessage}");
        };
        image.Loaded += (_, _) =>
        {
            if (image.Source == null)
            {
                image.Visibility = Visibility.Visible;
                AssignFreshSource();
            }
        };

        AssignFreshSource();

        var host = new Grid
        {
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        image.Width = Math.Max(size, 29);
        image.Height = Math.Max(size, 29);
        image.Opacity = 1;
        host.Children.Add(fallback);
        host.Children.Add(image);
        return host;
    }

    private static Brush SidebarIconAccent(string section) => section switch
    {
        "overview" => ThemeColor(154, 214, 255),
        "system" => ThemeColor(88, 184, 255),
        "power" => ThemeColor(92, 231, 126),
        "network" => ThemeColor(75, 202, 255),
        "gpu" => ThemeColor(196, 174, 255),
        "storage" => ThemeColor(255, 187, 92),
        "settings" => ThemeColor(220, 224, 232),
        "usb" => ThemeColor(99, 210, 255),
        "lianli" => ThemeColor(255, 112, 168),
        "startup" => ThemeColor(111, 230, 176),
        "ui" => ThemeColor(255, 199, 96),
        "update" => ThemeColor(88, 181, 255),
        "spicetify" => ThemeColor(82, 220, 113),
        "apps" => ThemeColor(123, 188, 255),
        "privacy" => ThemeColor(95, 201, 255),
        "ai" or "ai-runtime" => ThemeColor(104, 226, 231),
        "defender" => ThemeColor(98, 221, 151),
        "debloat" => ThemeColor(255, 137, 111),
        "fixer" => ThemeColor(255, 199, 92),
        "powershell" => ThemeColor(87, 174, 255),
        "cursor" => ThemeColor(204, 156, 255),
        _ => ThemeColor(146, 199, 255)
    };

    private static FrameworkElement SectionIcon(string tag, double size)
    {
        string name = SectionIconName(tag);
        var fallback = new FontIcon
        {
            Glyph = FallbackGlyph(tag),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = Math.Max(14, size * 0.72),
            Width = size,
            Height = size,
            Foreground = TextSecondaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.82
        };

        var image = new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        void AssignFreshSource()
        {
            image.Source = SectionIconSource(tag);
        }

        image.ImageOpened += (_, _) =>
        {
            fallback.Visibility = Visibility.Collapsed;
        };
        image.ImageFailed += (_, e) =>
        {
            fallback.Visibility = Visibility.Visible;
            image.Visibility = Visibility.Collapsed;
            LogService.Error("ui", $"ICON_LOAD_FAILED tag={tag} asset={name}.svg error={e.ErrorMessage}");
        };
        image.Loaded += (_, _) =>
        {
            if (image.Source == null)
            {
                image.Visibility = Visibility.Visible;
                AssignFreshSource();
            }
        };

        AssignFreshSource();

        var host = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        host.Children.Add(fallback);
        host.Children.Add(image);
        return host;
    }
}
