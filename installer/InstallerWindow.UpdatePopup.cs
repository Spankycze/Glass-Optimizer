using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace SpankyhoOptimizer.ModernSetup;

// The installer owns this window so replacing the old application cannot close its progress UI.
internal sealed class UpdatePopupWindow : Window
{
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _percent = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    private readonly Border _fill = new() { Width = 0, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _track = new() { Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _close = new() { Content = "Zavřít", FontSize = 13, Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private bool _busy;
    private bool _finished;
    private int _value = 50;

    internal UpdatePopupWindow(string target, bool desktop)
    {
        Title = "Glass Optimizer";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(226, 238, 229));
        _percent.Foreground = _fill.Background = new SolidColorBrush(Color.FromRgb(83, 252, 24));
        _track.Background = new SolidColorBrush(Color.FromRgb(20, 36, 24));
        _track.BorderBrush = new SolidColorBrush(Color.FromRgb(48, 75, 54));
        _track.Child = _fill;

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "Aktualizuji Glass Optimizer", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_percent, 1);
        _percent.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(_status);
        row.Children.Add(_percent);
        body.Children.Add(row);
        body.Children.Add(_track);
        body.Children.Add(_close);
        Content = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 34, 44)), BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(14), Child = body };
        _track.SizeChanged += (_, _) => SetFill(false);
        SizeChanged += (_, _) =>
        {
            Rect work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, work.Right - ActualWidth - 18);
            Top = Math.Max(work.Top, work.Bottom - ActualHeight - 18);
        };
        _close.Click += (_, _) => Close();
        Closing += (_, e) => e.Cancel = _busy;
        Loaded += async (_, _) => await RunUpdateAsync(report => InstallAsync(target, desktop, report));
        Report(50, "Připravuji instalaci…");
    }

    private static Task InstallAsync(string target, bool desktop, Action<int, string> report) => Task.Run(async () =>
    {
        await Program.EnsureRuntimePrerequisitesAsync((value, status) => report(50 + Math.Clamp(value, 0, 51) * 10 / 51, status));
        report(60, "Instaluji aktualizaci…");
        Program.InstallPayloadTransactional(target, desktop, (value, status) => report(60 + Math.Clamp(value, 0, 100) * 34 / 100, status));
        report(95, "Dokončuji aktualizaci…");
        if (!Program.ProvisionHardwareBridge(target))
            throw new InvalidOperationException("Aktualizace souborů proběhla, ale nepodařilo se připravit hardwarové funkce.");
        report(98, "Ověřuji nainstalovanou verzi…");
        using var registration = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SpankyhoOptimizer");
        if (!Program.VerifyUpdateReadback(target, registration?.GetValue("DisplayVersion")?.ToString(), registration?.GetValue("InstallLocation")?.ToString()))
            throw new InvalidDataException("Nainstalovanou verzi se nepodařilo ověřit.");
    });

    internal async Task RunUpdateAsync(Func<Action<int, string>, Task> operation)
    {
        if (_busy || _finished) return;
        _busy = true;
        try
        {
            await operation(Report);
            _finished = true;
            SetProgress(100, "Aktualizace je dokončená.");
            await Task.Delay(1200);
            _busy = false;
            Close();
        }
        catch (Exception ex)
        {
            _finished = true;
            Program.Log("UPDATE FAILED " + ex);
            Environment.ExitCode = 1;
            _status.Text = "Aktualizace se nezdařila. " + ex.Message;
            _status.Foreground = _percent.Foreground = new SolidColorBrush(Color.FromRgb(251, 130, 133));
            _percent.Text = "CHYBA";
            _close.Visibility = Visibility.Visible;
        }
        finally { _busy = false; }
    }

    private void Report(int value, string status)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_finished) SetProgress(Math.Clamp(value, _value, 99), status);
        });
    }

    private void SetProgress(int value, string status)
    {
        _value = value;
        _status.Text = status;
        _percent.Text = value + " %";
        SetFill(true);
    }

    private void SetFill(bool animate)
    {
        double width = Math.Max(0, _track.ActualWidth - 2) * _value / 100d;
        double current = _fill.ActualWidth;
        _fill.BeginAnimation(WidthProperty, null);
        _fill.Width = width;
        if (animate && SystemParameters.ClientAreaAnimation && _track.IsLoaded)
            _fill.BeginAnimation(WidthProperty, new DoubleAnimation(current, width, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
