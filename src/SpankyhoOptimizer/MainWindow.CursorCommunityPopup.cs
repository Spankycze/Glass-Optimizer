using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private ContentDialog? _communityCursorDialog;

    private static readonly string[] CommunityCursorCategories =
    {
        "All", "Minimal", "macOS", "Retro", "Pixel & Gaming",
        "Neon", "Glass", "Cute", "Animated"
    };

    internal string RunCommunityCursorPopupLifecycleSmoke()
    {
        var probe = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Community Cursor Library",
            PrimaryButtonText = string.Empty,
            SecondaryButtonText = string.Empty,
            CloseButtonText = string.Empty
        };

        var close = QuickButton("Zavřít", () => Task.CompletedTask);
        var apply = PrimaryQuickButton("Použít vybraný", () => Task.CompletedTask);

        if (close.Content?.ToString() != "Zavřít" || apply.Content?.ToString() != "Použít vybraný")
            throw new InvalidOperationException("Community Cursor vlastní footer tlačítka nejsou dostupná.");

        return "COMMUNITY_CURSOR_POPUP_LIFECYCLE_OK nativeContentDialog=yes customClose=yes customApply=yes";
    }

    private async Task ShowCommunityCursorPopupAsync()
    {
        if (_communityCursorDialog != null)
        {
            try { _communityCursorDialog.Hide(); } catch { }
            _communityCursorDialog = null;
        }

        var packs = CursorCommunityPackService.LoadBundledPacks();
        string currentPackId = CursorCommunityPackService.CurrentCommunityPackId;
        CommunityCursorPack? selectedPack = packs.FirstOrDefault(p =>
            string.Equals(p.Id, currentPackId, StringComparison.OrdinalIgnoreCase));
        Button? selectedCard = null;
        string selectedCategory = "All";
        int page = 0;
        const int PageSize = 1000;
        bool applied = false;

        var root = new Grid
        {
            RowSpacing = 10,
            Width = 500,
            Height = 650,
            Background = SurfaceBrush()
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var search = new TextBox
        {
            Width = 255,
            PlaceholderText = "Hledat pack nebo autora..."
        };
        tools.Children.Add(search);

        var countText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSecondaryBrush(),
            FontSize = 10.5,
            MinWidth = 90
        };
        tools.Children.Add(countText);

        Button? prevButton = null;
        Button? nextButton = null;

        var browser = new Grid { RowSpacing = 10 };
        browser.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        browser.RowDefinitions.Add(new RowDefinition());

        var categoryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var categoryScroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = categoryRow
        };
        browser.Children.Add(categoryScroll);

        var cardsHost = new StackPanel { Spacing = 10 };
        var cardsScroll = new ScrollViewer
        {
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = cardsHost
        };
        Grid.SetRow(cardsScroll, 1);
        browser.Children.Add(cardsScroll);
        Grid.SetRow(browser, 2);

        var status = new TextBlock
        {
            Text = selectedPack == null ? "Vyber cursor pack." : "Vybráno • " + selectedPack.Name,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 0)
        };
        var actionBar = new Grid
        {
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Background = DialogInnerSurfaceBrush(),
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 54
        };
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        actionBar.Children.Add(status);
        Grid.SetRow(actionBar, 1);

        root.Children.Add(tools);
        root.Children.Add(actionBar);
        root.Children.Add(browser);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Background = _darkTheme ? GlassPanelBrush() : SurfaceBrush(),
            Foreground = TextPrimaryBrush(),
            Title = "Community Cursor Library",
            Content = root,
            PrimaryButtonText = string.Empty,
            SecondaryButtonText = string.Empty,
            CloseButtonText = string.Empty,
            DefaultButton = ContentDialogButton.None,
            MinWidth = 540,
            MaxWidth = 580
        };
        _communityCursorDialog = dialog;

        var headerCloseButton = new Button
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Background = SurfaceElevatedBrush(),
            Foreground = TextPrimaryBrush(),
            BorderBrush = BorderThemeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 11
            }
        };
        ToolTipService.SetToolTip(headerCloseButton, "Zavřít");
        EnsureAccessibleButton(headerCloseButton, "Zavřít Community Cursor Library");
        headerCloseButton.Click += (_, _) =>
        {
            LogService.Info("cursor", "COMMUNITY_HEADER_CLOSE_CLICK");
            try { dialog.Hide(); }
            catch (Exception ex) { LogService.Error("cursor", "COMMUNITY_HEADER_CLOSE_FAIL", ex); }
        };
        tools.Children.Add(headerCloseButton);

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            e.Handled = true;
            LogService.Info("cursor", "COMMUNITY_ESCAPE_CLOSE");
            try { dialog.Hide(); }
            catch (Exception ex) { LogService.Error("cursor", "COMMUNITY_ESCAPE_CLOSE_FAIL", ex); }
        };

        Button? applyActionButton = null;

        void Rebuild()
        {
            categoryRow.Children.Clear();
            foreach (string category in CommunityCursorCategories)
            {
                var chip = QuickButton(category, () =>
                {
                    selectedCategory = category;
                    page = 0;
                    Rebuild();
                    return Task.CompletedTask;
                });
                if (selectedCategory == category)
                {
                    chip.Background = AccentSoftBrush();
                    chip.BorderBrush = AccentBrush();
                }
                categoryRow.Children.Add(chip);
            }

            string query = search.Text?.Trim() ?? string.Empty;
            var filtered = packs.Where(p =>
                    (selectedCategory == "All" || p.Category.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase)) &&
                    (query.Length == 0 ||
                     p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     p.Author.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            int pageCount = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);
            page = Math.Clamp(page, 0, pageCount - 1);
            var visible = filtered.Skip(page * PageSize).Take(PageSize).ToArray();

            countText.Text = $"{filtered.Length} packů";
            if (prevButton != null) prevButton.IsEnabled = page > 0;
            if (nextButton != null) nextButton.IsEnabled = page < pageCount - 1;

            cardsHost.Children.Clear();
            selectedCard = null;
            StackPanel row = NewCursorSkinRow();
            int index = 0;

            foreach (CommunityCursorPack pack in visible)
            {
                var cardButton = BuildCommunityCursorCard(pack, countText, (picked, pickedButton) =>
                {
                    selectedPack = picked;

                    if (selectedCard != null)
                    {
                        selectedCard.BorderBrush = DialogNeutralBorderBrush();
                        selectedCard.Background = DialogInnerSurfaceBrush();
                        selectedCard.BorderThickness = new Thickness(1);
                    }

                    selectedCard = pickedButton;
                    selectedCard.BorderBrush = AccentBrush();
                    selectedCard.Background = AccentSoftBrush();
                    selectedCard.BorderThickness = new Thickness(1.6);

                    status.Text = "Vybráno • " + picked.Name;
                    if (applyActionButton != null)
                        applyActionButton.IsEnabled = true;
                });

                if (selectedPack != null &&
                    string.Equals(selectedPack.Id, pack.Id, StringComparison.OrdinalIgnoreCase))
                {
                    selectedCard = cardButton;
                    cardButton.BorderBrush = AccentBrush();
                    cardButton.Background = AccentSoftBrush();
                    cardButton.BorderThickness = new Thickness(1.6);
                }

                row.Children.Add(cardButton);
                index++;
                if (index % 2 == 0)
                {
                    cardsHost.Children.Add(row);
                    row = NewCursorSkinRow();
                }
            }

            if (row.Children.Count > 0)
                cardsHost.Children.Add(row);

            if (filtered.Length == 0)
            {
                cardsHost.Children.Add(new TextBlock
                {
                    Text = "Nic nenalezeno.",
                    Foreground = TextSecondaryBrush(),
                    Margin = new Thickness(4, 14, 0, 0)
                });
            }

            if (applyActionButton != null)
                applyActionButton.IsEnabled = selectedPack != null;
        }

        // Všechny packy jsou v jednom vertikálním scrollu; žádné stránkování,
        // které by zabíralo místo a schovávalo část knihovny.
        prevButton = null;
        nextButton = null;

        search.TextChanged += (_, _) =>
        {
            page = 0;
            Rebuild();
        };

        var closeActionButton = new Button
        {
            Content = "Zavřít",
            MinWidth = 118,
            Height = 38,
            Padding = new Thickness(18, 0, 18, 0),
            Background = SurfaceElevatedBrush(),
            Foreground = TextPrimaryBrush(),
            BorderBrush = BorderThemeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeActionButton.Click += (_, _) =>
        {
            LogService.Info("cursor", "COMMUNITY_CLOSE_CLICK");
            try
            {
                dialog.Hide();
                LogService.Info("cursor", "COMMUNITY_CLOSE_OK");
            }
            catch (Exception ex)
            {
                LogService.Error("cursor", "COMMUNITY_CLOSE_HIDE_FAIL", ex);
            }
        };
        Grid.SetColumn(closeActionButton, 2);
        actionBar.Children.Add(closeActionButton);

        applyActionButton = new Button
        {
            Content = "Použít vybraný",
            MinWidth = 170,
            Height = 38,
            Padding = new Thickness(18, 0, 18, 0),
            Background = AccentBrush(),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = selectedPack != null
        };
        async Task ApplySelectedCursorAsync()
        {
            if (selectedPack == null)
            {
                status.Text = "Nejdřív vyber cursor pack.";
                return;
            }

            CommunityCursorPack packToApply = selectedPack;
            applyActionButton.IsEnabled = false;
            closeActionButton.IsEnabled = false;
            status.Text = "Aplikuji • " + packToApply.Name + "…";

            try
            {
                LogService.Info("cursor", $"COMMUNITY_APPLY_START id={packToApply.Id} name={packToApply.Name}");
                await Task.Run(() => CursorCommunityPackService.Apply(packToApply));

                if (!CursorCommunityPackService.IsApplied(packToApply))
                    throw new InvalidOperationException("Read-back nepotvrdil aplikovaný cursor pack.");

                LogService.Info("cursor", $"COMMUNITY_APPLY_OK id={packToApply.Id} name={packToApply.Name}");
                _cursorStatusText = "Aktivováno: " + packToApply.Name;
                status.Text = "Aktivováno • " + packToApply.Name;
                applied = true;
                dialog.Hide();
            }
            catch (Exception ex)
            {
                LogService.Error("cursor", $"COMMUNITY_APPLY_FAIL id={packToApply.Id} name={packToApply.Name}", ex);
                status.Text = "Apply selhal • " + ex.Message;
                applyActionButton.IsEnabled = true;
                closeActionButton.IsEnabled = true;
            }
        }
        applyActionButton.Click += (_, _) => _ = ApplySelectedCursorAsync();
        Grid.SetColumn(applyActionButton, 1);
        actionBar.Children.Add(applyActionButton);

        Rebuild();

        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_communityCursorDialog, dialog))
                _communityCursorDialog = null;
        }

        if (applied)
            BuildCursorPageRefresh();
    }
    private Button BuildCommunityCursorCard(CommunityCursorPack pack, TextBlock status, Action<CommunityCursorPack, Button> onSelected)
    {
        var body = new StackPanel { Spacing = 7, Width = 205 };
        body.Children.Add(new TextBlock
        {
            Text = pack.Name,
            FontSize = 12.8,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 38
        });

        var previews = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (string role in new[] { "Arrow", "Hand", "IBeam", "Wait" })
        {
            string path = System.IO.Path.Combine(pack.Folder, "_preview", role + ".png");
            previews.Children.Add(BuildCommunityPreview(path));
        }
        body.Children.Add(previews);

        body.Children.Add(new TextBlock
        {
            Text = pack.Category + " • " + pack.Author,
            FontSize = 9.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 32
        });
        body.Children.Add(new TextBlock
        {
            Text = pack.License,
            FontSize = 8.5,
            Foreground = AccentBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 28
        });

        var button = new Button
        {
            Content = body,
            Width = 230,
            MinHeight = 142,
            Padding = new Thickness(11),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13)
        };

        AttachHoverMotion(button, 1.01f);
        button.Click += (_, _) => onSelected(pack, button);
        return button;
    }

    private Border BuildCommunityPreview(string path)
    {
        var box = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1)
        };

        if (System.IO.File.Exists(path))
        {
            box.Child = new Image
            {
                Width = 30,
                Height = 30,
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri(path))
            };
        }
        else
        {
            box.Child = new TextBlock
            {
                Text = "•",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextSecondaryBrush()
            };
        }
        return box;
    }
}
