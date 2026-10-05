using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private int _shellThemeApplyBusy;
    private Task BuildPowerShellTweakerPageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var buildPhase = Stopwatch.StartNew();

        FrameworkElement hero;
        if (_prewarmedPowerShellHero != null &&
            _prewarmedPowerShellThemePreference == _themePreference)
        {
            hero = _prewarmedPowerShellHero;
            _prewarmedPowerShellHero = null;
            _prewarmedPowerShellThemePreference = null;
        }
        else
        {
            var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heroActions.Children.Add(QuickButton("PowerShell 7", OpenPowerShell7Async));

            hero = BuildModernSectionHero(
                "powershell",
                "PowerShell / CMD",
                "Full-black shell themes + PowerShell 7, CMD a Windows Terminal v jednom.",
                "TERMINAL",
                "Black Red • Black Green • Pure Black",
                heroActions);
        }

        ContentStack.Children.Add(hero);
        long heroMs = buildPhase.ElapsedMilliseconds;

        string selectedTheme = "Black Red";

        var font = new Slider
        {
            Header = "Velikost písma",
            Minimum = 9,
            Maximum = 14,
            StepFrequency = 1,
            Value = 11,
            Width = 250
        };
        var bg = new Slider
        {
            Header = "Síla grafického pozadí",
            Minimum = 0,
            Maximum = 35,
            StepFrequency = 1,
            Value = 16,
            Width = 250
        };
        var acrylic = new ToggleSwitch { IsOn = false, OnContent = "Zapnuto", OffContent = "Vypnuto" };
        var banner = new ToggleSwitch { IsOn = true, OnContent = "Zapnuto", OffContent = "Vypnuto" };

        var cursor = new ComboBox { Header = "Kurzor", Width = 155, HorizontalAlignment = HorizontalAlignment.Left };
        cursor.Items.Add("Filled Box");
        cursor.Items.Add("Bar");
        cursor.Items.Add("Underscore");
        cursor.SelectedIndex = 0;

        var padding = new ComboBox { Header = "Padding", Width = 155, HorizontalAlignment = HorizontalAlignment.Left };
        padding.Items.Add("Compact");
        padding.Items.Add("Normal");
        padding.Items.Add("Wide");
        padding.SelectedIndex = 1;

        var density = new ComboBox { Header = "Prompt", Width = 155, HorizontalAlignment = HorizontalAlignment.Left };
        density.Items.Add("Detailed");
        density.Items.Add("Compact");
        density.Items.Add("Minimal");
        density.SelectedIndex = 0;

        var status = new TextBlock
        {
            Text = "Vyber vzhled a použij ho na PowerShell, CMD a Windows Terminal.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        long controlsMs = buildPhase.ElapsedMilliseconds - heroMs;

        async Task ApplyThemeAsync(string themeName)
        {
            if (Interlocked.Exchange(ref _shellThemeApplyBusy, 1) != 0)
            {
                status.Text = "Shell theme se už aplikuje…";
                return;
            }

            try
            {
                selectedTheme = themeName;
                status.Text = $"Aplikuji {themeName}…";

                string output = await PowerShellTweakerService.ApplyAsync(
                    "Bold",
                    themeName,
                    (int)Math.Round(font.Value),
                    (int)Math.Round(bg.Value),
                    acrylic.IsOn,
                    banner.IsOn,
                    cursor.SelectedItem?.ToString() ?? "Filled Box",
                    padding.SelectedItem?.ToString() ?? "Normal",
                    density.SelectedItem?.ToString() ?? "Detailed",
                    token);

                ShellToolkitService.ApplyCmdRecommended();
                ShellToolkitService.ApplyCmdTheme(themeName);

                status.Text = $"{themeName} • AKTIVNÍ • PowerShell + CMD + Terminal";
                ShowActionFeedback("Shell theme", ActionFeedbackKind.Verified,
                    string.IsNullOrWhiteSpace(output)
                        ? $"{themeName} aplikováno."
                        : $"{themeName} aplikováno • {output.Replace(Environment.NewLine, " • ")}");
            }
            finally
            {
                Interlocked.Exchange(ref _shellThemeApplyBusy, 0);
            }
        }

        var cards = new System.Collections.Generic.List<(string Name, Border Card, TextBlock Badge)>();

        Border MakeThemeCard(
            string name,
            string subtitle,
            string glyph,
            string accentHex,
            string mutedHex,
            string promptLine)
        {
            var accent = HexBrush(accentHex);
            var muted = HexBrush(mutedHex);

            var root = new StackPanel { Spacing = 11 };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconShell = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(12),
                Background = ThemeColor(255, 10, 10, 10),
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                Child = new FontIcon
                {
                    Glyph = glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 18,
                    Foreground = accent
                }
            };
            head.Children.Add(iconShell);

            var titles = new StackPanel { Spacing = 2, Margin = new Thickness(11, 1, 8, 0) };
            titles.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush()
            });
            titles.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);

            var badge = new TextBlock
            {
                Text = "",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(badge, 2);
            head.Children.Add(badge);
            root.Children.Add(head);

            var terminal = new Border
            {
                Background = ThemeColor(255, 0, 0, 0),
                BorderBrush = ThemeColor(255, 28, 28, 28),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(13, 11, 13, 11)
            };
            var terminalRoot = new StackPanel { Spacing = 5 };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            foreach (string dot in new[] { "●", "●", "●" })
                dots.Children.Add(new TextBlock { Text = dot, FontSize = 8, Foreground = muted });
            terminalRoot.Children.Add(dots);
            terminalRoot.Children.Add(new TextBlock
            {
                Text = "PS C:\\Users\\spnka> git status",
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 11,
                Foreground = accent
            });
            terminalRoot.Children.Add(new TextBlock
            {
                Text = "On branch main   ✓ clean",
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 10.5,
                Foreground = TextSecondaryBrush()
            });
            terminalRoot.Children.Add(new TextBlock
            {
                Text = promptLine,
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 11,
                Foreground = accent
            });
            terminal.Child = terminalRoot;
            root.Children.Add(terminal);

            var apply = PrimaryQuickButton("POUŽÍT NA VŠE", async () =>
            {
                SelectTheme(name);
                await ApplyThemeAsync(name);
            });
            root.Children.Add(apply);

            var card = new Border
            {
                Background = ThemeColor(255, 6, 7, 10),
                BorderBrush = ThemeColor(255, 34, 36, 44),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(15),
                Margin = new Thickness(4),
                Child = root
            };
            card.Translation = new System.Numerics.Vector3(0, 0, 12);
            cards.Add((name, card, badge));
            return card;
        }

        void SelectTheme(string name)
        {
            selectedTheme = name;
            foreach (var item in cards)
            {
                bool active = string.Equals(item.Name, name, StringComparison.Ordinal);
                item.Card.BorderThickness = new Thickness(active ? 2 : 1);
                item.Card.BorderBrush = active
                    ? name switch
                    {
                        "Black Green" => HexBrush("#46FF73"),
                        "Pure Black" => HexBrush("#AFAFAF"),
                        _ => HexBrush("#FF5555")
                    }
                    : ThemeColor(255, 34, 36, 44);
                item.Badge.Text = active ? "ACTIVE" : "";
            }
            status.Text = $"Vybráno: {name}.";
        }
        var themeGrid = new Grid { ColumnSpacing = 8 };
        themeGrid.ColumnDefinitions.Add(new ColumnDefinition());
        themeGrid.ColumnDefinitions.Add(new ColumnDefinition());
        themeGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var red = MakeThemeCard(
            "Black Red",
            "Full black • červený prompt • výrazný power styl",
            "\uE7C1",
            "#FF5555",
            "#7A1D1D",
            "16:42  OK   PATH ~\\Projects\\Optimizer   >");
        Grid.SetColumn(red, 0);
        themeGrid.Children.Add(red);

        var green = MakeThemeCard(
            "Black Green",
            "Full black • zelené písmo • moderní terminal look",
            "\uE756",
            "#46FF73",
            "#165B2A",
            "16:42  READY   PATH ~\\Projects\\Optimizer   >");
        Grid.SetColumn(green, 1);
        themeGrid.Children.Add(green);

        var pure = MakeThemeCard(
            "Pure Black",
            "Absolutní černá • bílé písmo • minimum rušivých barev",
            "\uE8A5",
            "#FFFFFF",
            "#555555",
            "PS ~\\Projects\\Optimizer  >");
        Grid.SetColumn(pure, 2);
        themeGrid.Children.Add(pure);
        long themeCardsMs = buildPhase.ElapsedMilliseconds - heroMs - controlsMs;

        var themeRoot = new StackPanel { Spacing = 12 };
        themeRoot.Children.Add(new TextBlock
        {
            Text = "Vyber jeden ze tří finálních presetů. Všechny používají skutečně černé pozadí #000000.",
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        themeRoot.Children.Add(themeGrid);

        var globalActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        globalActions.Children.Add(PrimaryQuickButton("APLIKOVAT VYBRANÉ NA VŠE", async () =>
            await ApplyThemeAsync(selectedTheme)));
        globalActions.Children.Add(QuickButton("TEST STARTUPU", async () =>
        {
            status.Text = "Testuji PowerShell 7 a profil…";
            var state = PowerShellTweakerService.ReadState();
            double normal = await PowerShellTweakerService.BenchmarkAsync(false, 3, token);
            double bare = await PowerShellTweakerService.BenchmarkAsync(true, 3, token);
            status.Text =
                $"PS7={(state.PowerShell7 ? "OK" : "MISSING")} • Terminal={(state.WindowsTerminal ? "OK" : "CHECK")} • " +
                $"startup={FmtMs(normal)} • -NoProfile={FmtMs(bare)}";
        }));
        globalActions.Children.Add(QuickButton("OBNOVIT PŮVODNÍ", async () =>
        {
            if (!await ShowStyledConfirmAsync(
                "Obnovit shell",
                "Obnovit poslední zálohu PowerShell profilů, Windows Terminal settings a CMD barev?",
                "Obnovit",
                "Zrušit",
                "PowerShell / CMD"))
                return;

            int restored = PowerShellTweakerService.RestoreAll();
            ShellToolkitService.RestoreCmdRecommended();
            status.Text = restored > 0
                ? $"Obnoveno souborů: {restored}. Otevři nové okno terminálu."
                : "Souborová záloha nebyla nalezena; CMD hodnoty byly vráceny, pokud existovala registry záloha.";
        }));
        themeRoot.Children.Add(globalActions);
        themeRoot.Children.Add(status);

        ContentStack.Children.Add(BuildSectionSurface(
            "powershell",
            "Shell Themes",
            "PowerShell 7 • CMD • Windows Terminal • full-black design",
            themeRoot));
        long themeSurfaceMs = buildPhase.ElapsedMilliseconds - heroMs - controlsMs - themeCardsMs;

        long advancedMs = 0;
        long extrasMs = 0;

        void BuildDeferredPowerShellTail()
        {
            var advanced = new StackPanel { Spacing = 10 };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            controls.Children.Add(font);
            controls.Children.Add(bg);
            controls.Children.Add(cursor);
            controls.Children.Add(padding);
            controls.Children.Add(density);
            advanced.Children.Add(controls);

            var switches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28 };
            var acrylicGroup = new StackPanel { Spacing = 3, Width = 210 };
            acrylicGroup.Children.Add(new TextBlock { Text = "Acrylic", FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
            acrylicGroup.Children.Add(new TextBlock { Text = "Průhlednost Windows Terminalu", FontSize = 11, Foreground = TextSecondaryBrush() });
            acrylicGroup.Children.Add(acrylic);
            switches.Children.Add(acrylicGroup);

            var bannerGroup = new StackPanel { Spacing = 3, Width = 210 };
            bannerGroup.Children.Add(new TextBlock { Text = "Startup banner", FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
            bannerGroup.Children.Add(new TextBlock { Text = "Grafický header při startu shellu", FontSize = 11, Foreground = TextSecondaryBrush() });
            bannerGroup.Children.Add(banner);
            switches.Children.Add(bannerGroup);
            advanced.Children.Add(switches);

            var deferred = System.Diagnostics.Stopwatch.StartNew();
            ContentStack.Children.Add(BuildSectionSurface(
                "powershell",
                "Theme Detaily",
                "Font • kurzor • padding • prompt • acrylic • banner",
                advanced));
            advancedMs = deferred.ElapsedMilliseconds;

            long extrasStart = deferred.ElapsedMilliseconds;
            BuildPowerShellCenterExtras(token);
            extrasMs = deferred.ElapsedMilliseconds - extrasStart;
            LogService.Info("performance",
                $"POWERSHELL_DEFERRED_TAIL advancedMs={advancedMs} extrasMs={extrasMs} totalMs={deferred.ElapsedMilliseconds}");
        }

        long selectStart = buildPhase.ElapsedMilliseconds;
        SelectTheme("Black Red");
        long selectMs = buildPhase.ElapsedMilliseconds - selectStart;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "powershell") return;
            BuildDeferredPowerShellTail();
        });

        LogService.Info("performance",
            $"POWERSHELL_BUILD_PHASE heroMs={heroMs} controlsMs={controlsMs} themeCardsMs={themeCardsMs} themeSurfaceMs={themeSurfaceMs} advancedMs=deferred extrasMs=deferred selectMs={selectMs} totalMs={buildPhase.ElapsedMilliseconds}");
        return Task.CompletedTask;
    }

    private Microsoft.UI.Xaml.Media.Brush HexBrush(string hex)
    {
        string s = hex.TrimStart('#');
        byte r = Convert.ToByte(s.Substring(0, 2), 16);
        byte g = Convert.ToByte(s.Substring(2, 2), 16);
        byte b = Convert.ToByte(s.Substring(4, 2), 16);
        return ThemeColor(255, r, g, b);
    }

    private static string FmtMs(double value) => value < 0 ? "n/a" : Math.Round(value) + " ms";

    private Task OpenPowerShell7Async()
    {
        string path = @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (!File.Exists(path))
            throw new FileNotFoundException("PowerShell 7 není nainstalovaný.", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
