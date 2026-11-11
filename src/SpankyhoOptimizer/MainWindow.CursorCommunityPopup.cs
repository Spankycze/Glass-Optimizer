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
    private Grid? _communityCursorDialog;
    private Action? _closeCommunityCursorDialog;

    private static readonly string[] CommunityCursorCategories =
    {
        "All", "Minimal", "macOS", "Retro", "Pixel & Gaming",
        "Neon", "Glass", "Cute", "Animated"
    };

    internal string RunCommunityCursorPopupLifecycleSmoke()
    {
        var overlay = CreateDialogOverlay();
        if (Grid.GetColumnSpan(overlay) != Math.Max(1, RootGrid.ColumnDefinitions.Count))
            throw new InvalidOperationException("Community Cursor overlay nezakrývá sidebar.");
        var close = QuickButton("Zavřít", () => Task.CompletedTask);
        var apply = PrimaryQuickButton("Použít vybraný", () => Task.CompletedTask);

        if (close.Content?.ToString() != "Zavřít" || apply.Content?.ToString() != "Použít vybraný")
            throw new InvalidOperationException("Community Cursor vlastní footer tlačítka nejsou dostupná.");

        return "COMMUNITY_CURSOR_POPUP_LIFECYCLE_OK sharedModal=yes customClose=yes customApply=yes";
    }

    private async Task ShowCommunityCursorPopupAsync()
    {
        if (_communityCursorDialog != null)
        {
            // Keep one owner while its backend mutation is still running.
            return;
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

        var overlay = CreateDialogOverlay();
        overlay.Tag = "CommunityCursorModal";
        // Use the shared glass scrim so this modal cannot drift back to a black wall.
        overlay.Background = GlassOverlayBrush(78);
        var card = CreateDialogCard(560, 760);
        card.Padding = new Thickness(24);
        var root = new Grid
        {
            RowSpacing = 12,
            Height = Math.Max(240, card.MaxHeight - 50)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Name = "CursorHeader", Spacing = 4, Margin = new Thickness(0, 0, 36, 0) };
        header.Children.Add(new TextBlock
        {
            Text = "Community Cursor Library", FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(new TextBlock
        {
            Text = "Vyber pack a použij ho ve Windows.", FontSize = 12,
            Foreground = DialogSecondaryTextBrush(), TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(header);
        var tools = new Grid { Name = "CursorSearch", ColumnSpacing = 12 };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new TextBox
        {
            Name = "CursorQuery", MinWidth = 160,
            PlaceholderText = "Hledat pack nebo autora...",
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 7, 14, 7),
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush()
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Hledat cursor pack nebo autora");
        tools.Children.Add(search);
        var countText = new TextBlock
        {
            Name = "CursorCount",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSecondaryBrush(),
            FontSize = 10.5,
            MinWidth = 90
        };
        Grid.SetColumn(countText, 1);
        tools.Children.Add(countText);

        Button? prevButton = null;
        Button? nextButton = null;

        var categoryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var categoryScroll = new ScrollViewer
        {
            Name = "CursorCategories",
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = categoryRow
        };
        Grid.SetRow(categoryScroll, 2);
        root.Children.Add(categoryScroll);

        var cardsHost = new StackPanel { Spacing = 10 };
        var cardsScroll = new ScrollViewer
        {
            Name = "CursorCards",
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = cardsHost
        };
        Grid.SetRow(cardsScroll, 3);
        root.Children.Add(cardsScroll);

        var status = new TextBlock
        {
            Text = selectedPack == null ? "Vyber cursor pack." : "Vybráno • " + selectedPack.Name,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 0)
        };
        var actionBar = new Grid
        {
            Name = "CursorActions",
            RowSpacing = 10,
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Background = DialogBodyBrush(),
            CornerRadius = new CornerRadius(22),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            MinHeight = 54
        };
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        actionBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        actionBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(status, 3);
        actionBar.Children.Add(status);
        Grid.SetRow(actionBar, 4);

        Grid.SetRow(tools, 1);
        root.Children.Add(tools);
        root.Children.Add(actionBar);


        card.Child = root;
        var completion = new TaskCompletionSource<bool>();
        bool closed = false;
        bool applying = false;
        System.Threading.CancellationTokenSource? applyCts = null;
        // Apply is synchronous registry work; cancel/timeout only stops waiting, so block overlap until it ends.
        Task? pendingApplyTask = null;
        Button? popupCloseButton = null;
        SizeChangedEventHandler resizeHandler = (_, _) =>
        {
            // Resize can fire several times for the same effective modal
            // geometry. Avoid redundant dependency-property writes that force
            // another WinUI measure/arrange pass while the user drags a window.
            double maxHeight = DialogMaxHeight(760);
            if (Math.Abs(card.MaxHeight - maxHeight) > .1)
                card.MaxHeight = maxHeight;
            double contentHeight = Math.Max(240, maxHeight - 50);
            if (double.IsNaN(root.Height) || Math.Abs(root.Height - contentHeight) > .1)
                root.Height = contentHeight;
            if (overlay.Children.FirstOrDefault() is Grid host)
            {
                double hostWidth = Math.Min(560, Math.Max(320, RootGrid.ActualWidth - 48));
                if (double.IsNaN(host.Width) || Math.Abs(host.Width - hostWidth) > .1)
                    host.Width = hostWidth;
                if (double.IsNaN(host.MaxWidth) || Math.Abs(host.MaxWidth - hostWidth) > .1)
                    host.MaxWidth = hostWidth;
                if (Math.Abs(host.MaxHeight - maxHeight) > .1)
                    host.MaxHeight = maxHeight;
            }
        };
        System.Threading.CancellationTokenRegistration windowCloseRegistration = default;
        void CloseDialog(bool force = false)
        {
            if (closed) return;
            closed = true;
            if (applying)
            {
                try { applyCts?.Cancel(); } catch { }
            }
            RootGrid.SizeChanged -= resizeHandler;
            windowCloseRegistration.Dispose();
            UnmountDialog(overlay);
            completion.TrySetResult(true);
        }
        void RequestClose() => CloseDialog();
        _communityCursorDialog = overlay;
        _closeCommunityCursorDialog = RequestClose;
        windowCloseRegistration = _windowLifetimeCts.Token.Register(() =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => CloseDialog(force: true));
        });
        Button? applyActionButton = null;

        // The nine category controls are independent of the current search
        // query. Keep their WinUI instances stable while filtering cards; the
        // previous Rebuild recreated all nine buttons on every keystroke.
        var categoryChips = new System.Collections.Generic.List<(string Category, Button Chip)>();
        string? renderedCategory = null;
        string[] renderedPackIds = Array.Empty<string>();
        bool hasRenderedResults = false;
        void Rebuild()
        {
            if (closed || applying) return;
            if (categoryChips.Count == 0)
            {
                foreach (string category in CommunityCursorCategories)
                {
                    var chip = QuickButton(category, () =>
                    {
                        selectedCategory = category;
                        page = 0;
                        Rebuild();
                        return Task.CompletedTask;
                    });
                    ApplyMaterialButtonAppearance(chip);
                    chip.CornerRadius = new CornerRadius(18);
                    chip.Padding = new Thickness(14, 4, 14, 4);
                    categoryRow.Children.Add(chip);
                    categoryChips.Add((category, chip));
                }
            }

            if (!string.Equals(renderedCategory, selectedCategory, StringComparison.Ordinal))
            {
                foreach (var (category, chip) in categoryChips)
                {
                    // A category switch updates the visual selection in place;
                    // text edits do not touch these controls or their brushes.
                    ApplyMaterialButtonAppearance(chip);
                    chip.CornerRadius = new CornerRadius(18);
                    chip.Padding = new Thickness(14, 4, 14, 4);
                    if (selectedCategory == category)
                    {
                        chip.Background = GlassNavigationBrush();
                        chip.BorderBrush = GlassBlueEdgeBrush();
                    }
                }
                renderedCategory = selectedCategory;
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

            // Incremental search commonly produces the same visible results
            // (especially after the first no-match character). Rebuilding
            // identical preview cards forces redundant XAML layout and image
            // decode. Update the count/category above but retain the cards
            // whenever their ordered identities are unchanged.
            string[] visibleIds = visible.Select(p => p.Id).ToArray();
            if (hasRenderedResults &&
                renderedPackIds.SequenceEqual(visibleIds, StringComparer.OrdinalIgnoreCase))
                return;
            renderedPackIds = visibleIds;
            hasRenderedResults = true;

            cardsHost.Children.Clear();
            selectedCard = null;
            StackPanel row = NewCursorSkinRow();
            int index = 0;

            foreach (CommunityCursorPack pack in visible)
            {
                var cardButton = BuildCommunityCursorCard(pack, countText, (picked, pickedButton) =>
                {
                    if (closed || applying) return;
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
            Name = "CursorClose",
            Content = "Zavřít",
            MinWidth = 118,
            Height = 38,
            Padding = new Thickness(18, 0, 18, 0),
            Background = _darkTheme ? GlassSurfaceElevatedBrush() : SurfaceElevatedBrush(),
            Foreground = TextPrimaryBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ApplyMaterialButtonAppearance(closeActionButton);
        closeActionButton.Padding = new Thickness(18, 0, 18, 0);
        closeActionButton.CornerRadius = new CornerRadius(19);
        closeActionButton.Click += (_, _) =>
        {
            LogService.Info("cursor", "COMMUNITY_CLOSE_CLICK");
            try
            {
                CloseDialog();
                LogService.Info("cursor", "COMMUNITY_CLOSE_OK");
            }
            catch (Exception ex)
            {
                LogService.Error("cursor", "COMMUNITY_CLOSE_HIDE_FAIL", ex);
            }
        };
        EnsureAccessibleButton(closeActionButton, "Zavřít knihovnu kurzorů");
        Grid.SetColumn(closeActionButton, 2);
        Grid.SetRow(closeActionButton, 1);
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
        ApplyMaterialButtonAppearance(applyActionButton, true);
        applyActionButton.Padding = new Thickness(18, 0, 18, 0);
        applyActionButton.CornerRadius = new CornerRadius(19);
        async Task ApplySelectedCursorAsync()
        {
            if (closed || applying) return;
            if (pendingApplyTask is { IsCompleted: false })
            {
                status.Text = "Předchozí aplikování ještě doběhá, zkus to za chvíli.";
                return;
            }
            if (selectedPack == null)
            {
                status.Text = "Nejdřív vyber cursor pack.";
                return;
            }

            CommunityCursorPack packToApply = selectedPack;
            applying = true;
            search.IsEnabled = false;
            categoryScroll.IsEnabled = false;
            cardsScroll.IsEnabled = false;
            // Apply may touch Windows shell state, but the modal must never become a trap.
            // Keep both close controls active so X/Esc/Zavřít can cancel and dismiss immediately.
            applyActionButton.IsEnabled = false;
            closeActionButton.IsEnabled = true;
            if (popupCloseButton != null) popupCloseButton.IsEnabled = true;
            status.Text = "Aplikuji • " + packToApply.Name + "…";

            try
            {
                LogService.Info("cursor", $"COMMUNITY_APPLY_START id={packToApply.Id} name={packToApply.Name}");
                applyCts?.Dispose();
                applyCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
                System.Threading.CancellationToken applyToken = applyCts.Token;
                Task applyTask = Task.Run(() => CursorCommunityPackService.Apply(packToApply), applyToken);
                pendingApplyTask = applyTask;
                await applyTask.WaitAsync(TimeSpan.FromSeconds(8), applyToken);

                applyToken.ThrowIfCancellationRequested();
                if (closed) return;
                LogService.Info("cursor", $"COMMUNITY_APPLY_OK id={packToApply.Id} name={packToApply.Name}");
                _cursorStatusText = "Aktivováno: " + packToApply.Name;
                status.Text = "Aktivováno • " + packToApply.Name;
                applied = true;
                applying = false;
                CloseDialog();
            }
            catch (OperationCanceledException)
            {
                if (!closed)
                    status.Text = "Aplikování zrušeno.";
            }
            catch (TimeoutException ex)
            {
                LogService.Error("cursor", $"COMMUNITY_APPLY_TIMEOUT id={packToApply.Id} name={packToApply.Name}", ex);
                try { applyCts?.Cancel(); } catch { }
                if (!closed)
                    status.Text = "Aplikování trvalo příliš dlouho. Můžeš zkusit pack znovu.";
            }
            catch (Exception ex)
            {
                LogService.Error("cursor", $"COMMUNITY_APPLY_FAIL id={packToApply.Id} name={packToApply.Name}", ex);
                if (!closed) status.Text = "Apply selhal • " + ex.Message;
            }
            finally
            {
                applying = false;
                applyCts?.Dispose();
                applyCts = null;
                if (!closed)
                {
                    search.IsEnabled = true;
                    categoryScroll.IsEnabled = true;
                    cardsScroll.IsEnabled = true;
                    applyActionButton.IsEnabled = selectedPack != null;
                    closeActionButton.IsEnabled = true;
                    if (popupCloseButton != null) popupCloseButton.IsEnabled = true;
                }
            }
        }
        applyActionButton.Click += (_, _) => _ = ApplySelectedCursorAsync();
        EnsureAccessibleButton(applyActionButton, "Použít vybraný cursor pack");
        Grid.SetColumn(applyActionButton, 1);
        Grid.SetRow(applyActionButton, 1);
        actionBar.Children.Add(applyActionButton);

        Rebuild();

        try
        {
            popupCloseButton = MountDialog(overlay, card, RequestClose);
            RootGrid.SizeChanged += resizeHandler;
            await completion.Task;
        }
        finally
        {
            CloseDialog();
            if (ReferenceEquals(_communityCursorDialog, overlay))
            {
                _communityCursorDialog = null;
                _closeCommunityCursorDialog = null;
            }
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
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 32
        });
        body.Children.Add(new TextBlock
        {
            Text = pack.License,
            FontSize = 10.5,
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
