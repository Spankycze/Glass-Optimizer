using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task<bool> ShowDriverLoadErrorAsync(Exception error, System.Threading.CancellationToken token, int pageGeneration)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = CreateDialogOverlay(alpha: 82);
        var card = CreateDialogCard(540, 500, 620);

        var root = new StackPanel { Spacing = 14 };

        var header = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 46, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());

        var icon = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = ThemeColor(30, 255, 184, 74),
            BorderBrush = WarningBrush(),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                Glyph = "\uE7BA",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
                Foreground = WarningBrush()
            }
        };
        header.Children.Add(icon);

        var titleStack = new StackPanel { Spacing = 5 };
        Grid.SetColumn(titleStack, 1);
        titleStack.Children.Add(new TextBlock
        {
            Text = "Ovladače",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        titleStack.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2, 7, 2),
            Background = ThemeColor(30, 255, 184, 74),
            BorderBrush = WarningBrush(),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "NAČTENÍ SELHALO",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = WarningBrush()
            }
        });
        header.Children.Add(titleStack);
        root.Children.Add(header);

        root.Children.Add(new TextBlock
        {
            Text = "Nepodařilo se načíst aktuální stav ovladačů. Aplikace zůstala spuštěná a ostatní sekce můžeš dál používat.",
            FontSize = 13.5,
            LineHeight = 20,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(new TextBlock
        {
            Text = "Můžeš načtení zkusit znovu. Pokud chyba pokračuje, otevři detail chyby.",
            FontSize = 11.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        var detailText = new TextBlock
        {
            Text = $"{error.GetType().Name}: {error.Message}\n\nHRESULT: 0x{error.HResult:X8}\n\n{error}",
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var detailBorder = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = DialogNeutralBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            MaxHeight = 150,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = detailText
            }
        };
        root.Children.Add(detailBorder);

        var actionsGrid = new Grid { Margin = new Thickness(0, 4, 0, 0), ColumnSpacing = 12 };
        actionsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Button? detailButton = null;
        detailButton = StyledDialogButton("Detail chyby", false, () =>
        {
            bool show = detailBorder.Visibility != Visibility.Visible;
            detailBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (detailButton != null)
                detailButton.Content = show ? "Skrýt detail" : "Detail chyby";
            return Task.CompletedTask;
        });
        detailButton.HorizontalAlignment = HorizontalAlignment.Left;
        actionsGrid.Children.Add(detailButton);

        var rightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(rightActions, 1);

        bool closed = false;
        System.Threading.CancellationTokenRegistration lifetimeRegistration = default;
        Task Finish(bool retry)
        {
            if (closed) return Task.CompletedTask;
            closed = true;
            lifetimeRegistration.Dispose();
            UnmountDialog(overlay);
            tcs.TrySetResult(retry);
            return Task.CompletedTask;
        }

        lifetimeRegistration = token.Register(() =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => _ = Finish(false));
        });

        rightActions.Children.Add(StyledDialogButton("Zavřít", false, () => Finish(false)));
        rightActions.Children.Add(StyledDialogButton("Zkusit znovu", true, () => Finish(true)));
        actionsGrid.Children.Add(rightActions);
        root.Children.Add(actionsGrid);

        card.Child = root;
        MountDialog(overlay, card, () => _ = Finish(false));

        if (pageGeneration != _driverPageLoadGeneration ||
            _section != "components" || _sub != "Ovladače")
            await Finish(false);

        return await tcs.Task;
    }
}
