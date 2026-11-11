using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task FillDetectedAppsPanelAsync(StackPanel body, CancellationToken token)
    {
        int generation = _navigationGeneration;
        IReadOnlyList<DetectedDesktopApp> apps;
        try
        {
            apps = await AppDetectionService.DetectAsync(token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            LogService.Error("apps", "Installed app detection failed", ex);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "apps" ||
                body.Parent == null) return;
            body.Children.Clear();
            body.Children.Add(new TextBlock
            {
                Text = "Nainstalované aplikace se nepodařilo načíst: " + ex.Message,
                Foreground = WarningBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(QuickButton("Načíst znovu", () =>
                FillDetectedAppsPanelAsync(body, token)));
            return;
        }

        if (token.IsCancellationRequested ||
            generation != _navigationGeneration ||
            _section != "apps" ||
            body.Parent == null) return;
        body.Children.Clear();
        foreach (var app in apps)
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "apps" ||
                body.Parent == null) return;
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = app.Name, Foreground = TextPrimaryBrush(), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = app.Category + " • " + app.Source, Foreground = TextSecondaryBrush(), FontSize = 10.5 });
            row.Children.Add(text);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!string.IsNullOrWhiteSpace(app.ExePath))
                actions.Children.Add(QuickButton("Otevřít", () =>
                {
                    ProcessRunner.Open(app.ExePath!);
                    return Task.CompletedTask;
                }));

            actions.Children.Add(QuickButton("Odinstalovat + cleanup", async () =>
            {
                bool confirm = await ShowStyledConfirmAsync(
                    "Odinstalovat " + app.Name + "?",
                    "Aplikace bude odinstalována přes Winget. Po úspěšné odinstalaci Optimizer odstraní jen známé bezpečné zbytky v AppData / LocalAppData / ProgramData. Uživatelská data mimo tato známá umístění nesmaže.",
                    "Odinstalovat",
                    "Zrušit",
                    "Programy / Launchery");
                if (!confirm) return;

                var progressResult = await RunCancellableWithProgressAsync(
                    "Odinstalace " + app.Name,
                    "Odinstaluji aplikaci a kontroluji známé zbytky…",
                    progressToken => AppUninstallService.UninstallWithCleanupAsync(app, progressToken),
                    x => x.Success,
                    x => x.Message);
                if (!progressResult.Completed)
                {
                    LogService.Info("apps", $"Uninstall cancelled by user: {app.Name}");
                    return;
                }

                var result = progressResult.Result;
                ShowActionFeedback(
                    app.Name,
                    result.Success ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed,
                    result.Message);

                if (result.Success)
                {
                    AppDetectionService.Invalidate();
                    if (!token.IsCancellationRequested && _section == "apps")
                        await FillDetectedAppsPanelAsync(body, token);
                }
            }));

            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            body.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(0, 0, 0, _darkTheme ? 1.1 : 1), Padding = new Thickness(10, 8, 10, 8), Child = row });
        }
        if (apps.Count == 0) body.Children.Add(new TextBlock { Text = "Nebyla nalezena žádná podporovaná desktop aplikace.", Foreground = TextSecondaryBrush() });
    }
}
