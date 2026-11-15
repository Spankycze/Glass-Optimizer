using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace SpankyhoOptimizer;

internal sealed record DesktopUpdateProgress(int Percent, string Status, string? Detail = null);
internal sealed record DesktopUpdateCompletion(
    string Status,
    string PrimaryText,
    Func<Task>? PrimaryAction = null,
    string? Detail = null,
    Func<IProgress<DesktopUpdateProgress>, CancellationToken, Task>? PrimaryProgressAction = null);

internal sealed class DesktopUpdateNotificationWindow : Window
{
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private const int WindowWidth = 480;
    private const int SwShowNoActivate = 4;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);



    private readonly Func<IProgress<DesktopUpdateProgress>, CancellationToken, Task<DesktopUpdateCompletion>> _downloadAction;
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly TextBlock _percent;
    private readonly Border _fill;
    private readonly Border _track;
    private Storyboard? _fillAnimation;
    private readonly Button _primary;
    private readonly Button _secondary;
    private Func<Task>? _completionAction;
    private Func<IProgress<DesktopUpdateProgress>, CancellationToken, Task>? _completionProgressAction;
    private readonly PopupChromeState _chrome = new();
    private bool _busy;
    private bool _automaticUpdate;
    private long _downloadGeneration;
    private bool _closed;
    private bool _ctsDisposed;
    internal bool CompletedSuccessfully { get; private set; }
    private readonly int _stackIndex;
    private readonly bool _visualPreview;

    public DesktopUpdateNotificationWindow(
        string category,
        string title,
        string version,
        string description,
        Func<IProgress<DesktopUpdateProgress>, CancellationToken, Task<DesktopUpdateCompletion>> downloadAction,
        int stackIndex = 0,
        bool visualPreview = false)
    {
        _downloadAction = downloadAction;
        _stackIndex = Math.Max(0, stackIndex);
        _visualPreview = visualPreview;
        Title = "Glass Optimizer";
        ExtendsContentIntoTitleBar = true;

        var root = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 22, 29)),
            Padding = new Thickness(6),
            RequestedTheme = ElementTheme.Dark
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            // Quiet neutral edge, no bright blue decorative border.
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 64, 64, 64)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 34, 44)),
            Padding = new Thickness(0)
        };
        root.Children.Add(card);

        var body = new StackPanel { Spacing = 5 };

        card.Child = new Border { Padding = new Thickness(14, 10, 14, 10), Child = body };

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png");
        var logo = new Image
        {
            Width = 36,
            Height = 36,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (File.Exists(logoPath))
            logo.Source = new BitmapImage(new Uri(logoPath));
        header.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 48, 48)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 80, 80)),
            BorderThickness = new Thickness(1),
            Child = logo
        });

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 249, 252))
        });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        body.Children.Add(header);

        var statusRow = new Grid();
        statusRow.ColumnDefinitions.Add(new ColumnDefinition());
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = new TextBlock
        {
            Text = "",
            Visibility = Visibility.Collapsed,
            FontSize = 13,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 206, 206, 206)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _percent = new TextBlock
        {
            Text = "",
            Visibility = Visibility.Collapsed,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 83, 252, 24)),
            Margin = new Thickness(10, 0, 0, 0)
        };
        statusRow.Children.Add(_status);
        Grid.SetColumn(_percent, 1);
        statusRow.Children.Add(_percent);
        body.Children.Add(statusRow);

        _track = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 36, 24)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 75, 54)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _fill = new Border
        {
            Width = 0,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 83, 252, 24)),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _track.Child = _fill;
        body.Children.Add(_track);

        _detail = new TextBlock
        {
            Text = "",
            Visibility = Visibility.Collapsed,
            FontSize = 13,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 206, 206, 206)),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };
        body.Children.Add(_detail);

        var buttons = new Grid { ColumnSpacing = 9 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        _secondary = BuildButton("Později", false);
        _primary = BuildButton("Stáhnout", true);
        _secondary.Click += (_, _) =>
        {
            if (_busy) _cts.Cancel();
            Close();
        };
        _primary.Click += (_, _) => SafeTaskService.Forget(
            OnPrimaryAsync(),
            "update",
            "Desktop update notification action");
        buttons.Children.Add(_secondary);
        Grid.SetColumn(_primary, 1);
        buttons.Children.Add(_primary);
        body.Children.Add(buttons);

        Content = root;
        Activated += (_, _) => ConfigureWindow();
        Closed += (_, _) =>
        {
            _closed = true;
            _fillAnimation?.Stop();
            _fillAnimation = null;
            _completionAction = null;
            _completionProgressAction = null;
            try { _cts.Cancel(); } catch { }
            DisposeCancellationWhenIdle();
        };
    }

    private static Button BuildButton(string text, bool primary) => new()
    {
        Content = text,
        Height = 38,
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(primary
            ? Windows.UI.Color.FromArgb(255, 120, 120, 120)
            : Windows.UI.Color.FromArgb(255, 80, 80, 80)),
        Background = new SolidColorBrush(primary
            ? Windows.UI.Color.FromArgb(255, 64, 64, 64)
            : Windows.UI.Color.FromArgb(255, 40, 40, 40)),
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 249, 252)),
        FontWeight = FontWeights.SemiBold
    };

    // Green while working; red after an actual failure. Reset before retries
    // so stale error colors do not leak into a new download.
    private void SetProgressFailureTone(bool failed)
    {
        _status.Visibility = Visibility.Visible;
        _percent.Visibility = Visibility.Visible;
        _detail.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        var color = failed
            ? Windows.UI.Color.FromArgb(255, 251, 130, 133)
            : Windows.UI.Color.FromArgb(255, 83, 252, 24);
        _status.Foreground = new SolidColorBrush(failed ? color : Windows.UI.Color.FromArgb(255, 226, 238, 229));
        _percent.Foreground = new SolidColorBrush(color);
        _fill.Background = new SolidColorBrush(failed
            ? Windows.UI.Color.FromArgb(255, 210, 78, 92)
            : Windows.UI.Color.FromArgb(255, 83, 252, 24));
    }

    private void SetFillPercent(int percent)
    {
        _track.UpdateLayout();
        double width = Math.Max(0, _track.ActualWidth - _track.BorderThickness.Left - _track.BorderThickness.Right);
        SetFillWidth(width * Math.Clamp(percent, 0, 100) / 100d);
    }

    private void SetFillWidth(double width)
    {
        double currentWidth = _fill.ActualWidth;
        _fillAnimation?.Stop();
        _fill.Width = currentWidth;
        if (_closed || (App.IsSmokeUi && !_visualPreview) || !UiMotionSettingsService.EffectiveEnabled ||
            !Timeline.AllowDependentAnimations || !_track.IsLoaded || Math.Abs(width - currentWidth) < .5)
        {
            _fill.Width = width;
            return;
        }

        var animation = new DoubleAnimation
        {
            EnableDependentAnimation = true,
            From = currentWidth,
            To = width,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, _fill);
        Storyboard.SetTargetProperty(animation, "Width");
        _fillAnimation = new Storyboard();
        _fillAnimation.Children.Add(animation);
        _fillAnimation.Begin();
    }

    private void DisposeCancellationWhenIdle()
    {
        if (!_closed || _busy || _ctsDisposed) return;
        _ctsDisposed = true;
        _cts.Dispose();
    }

    private async Task OnPrimaryAsync()
    {
        if (_closed || _busy) return;
        if (_completionAction != null || _completionProgressAction != null)
        {
            try
            {
                _busy = true;
                _primary.IsEnabled = false;
                _secondary.IsEnabled = false;
                if (_completionProgressAction != null)
                {
                    IProgress<DesktopUpdateProgress> completionProgress = new InlineProgress<DesktopUpdateProgress>(p =>
                    {
                        if (!_closed && _busy && !_cts.IsCancellationRequested)
                        {
                            int percent = Math.Clamp(p.Percent, 0, 100);
                            SetFillPercent(percent);
                            _percent.Text = percent + " %";
                            _status.Text = p.Status;
                            if (p.Detail != null) _detail.Text = p.Detail;
                            ConfigureWindow();
                        }
                    });
                    await _completionProgressAction(completionProgress, _cts.Token);
                }
                else
                    await _completionAction();
                CompletedSuccessfully = true;
                if (!_closed) Close();
            }
            catch (Exception ex)
            {
                if (_closed) return;
                _status.Text = "Akce selhala.";
                _detail.Text = ex.Message;
                SetProgressFailureTone(true);
                _primary.IsEnabled = true;
                _secondary.IsEnabled = true;
            }
            finally { _busy = false; ConfigureWindow(); DisposeCancellationWhenIdle(); }
            return;
        }

        _busy = true;
        SetProgressFailureTone(false);
        long generation = ++_downloadGeneration;
        CancellationToken token = _cts.Token;
        _primary.IsEnabled = false;
        _secondary.Content = "Zru\u0161it";
        SetFillWidth(0);
        _track.Visibility = Visibility.Visible;
        _status.Text = "P\u0159ipravuji stahov\u00e1n\u00ed\u2026";
        _detail.Text = "";
        _percent.Text = "0 %";
        ConfigureWindow();

        var progress = new Progress<DesktopUpdateProgress>(p =>
        {
            if (_closed || !_busy || token.IsCancellationRequested || generation != _downloadGeneration) return;
            int percent = Math.Clamp(p.Percent, 0, 100);
            SetFillPercent(percent);
            _percent.Text = percent + " %";
            _status.Text = p.Status;
            if (p.Detail != null) _detail.Text = p.Detail;
            ConfigureWindow();
        });

        try
        {
            DesktopUpdateCompletion result = await _downloadAction(progress, token);
            if (_closed) return;
            token.ThrowIfCancellationRequested();
            _status.Text = result.Status;
            if (!string.IsNullOrWhiteSpace(result.Detail)) _detail.Text = result.Detail;
            bool hasPostDownloadProgress = result.PrimaryProgressAction != null;
            SetFillPercent(hasPostDownloadProgress ? 50 : 100);
            _percent.Text = hasPostDownloadProgress ? "50 %" : "100 %";
            _primary.Content = result.PrimaryText;
            _completionAction = result.PrimaryAction;
            _completionProgressAction = result.PrimaryProgressAction;
            _primary.IsEnabled = result.PrimaryAction != null;
            if (_completionProgressAction != null)
                _primary.IsEnabled = true;
            _secondary.Content = "Zav\u0159\u00edt";
            if (_automaticUpdate && (_completionProgressAction != null || _completionAction != null))
            {
                // Reject queued download reports before the installer becomes the active operation.
                _downloadGeneration++;
                _busy = false;
                await OnPrimaryAsync();
            }
        }
        catch (OperationCanceledException)
        {
            if (_closed) return;
            _status.Text = "Stahov\u00e1n\u00ed zru\u0161eno.";
            _detail.Text = "";
            _percent.Text = "";
            _track.Visibility = Visibility.Collapsed;
            _primary.Content = "St\u00e1hnout";
            _primary.IsEnabled = true;
            _secondary.Content = "Pozd\u011bji";
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _status.Text = "Aktualizaci se nepoda\u0159ilo st\u00e1hnout.";
            _detail.Text = ex.Message;
            SetProgressFailureTone(true);
            _percent.Text = "CHYBA";
            _primary.Content = "Zkusit znovu";
            _primary.IsEnabled = true;
            _secondary.Content = "Zav\u0159\u00edt";
        }
        finally
        {
            // Invalidate queued callbacks before a retry or completion action can become busy.
            if (generation == _downloadGeneration) _downloadGeneration++;
            _busy = false;
            ConfigureWindow();
            DisposeCancellationWhenIdle();
        }
    }

    public async Task StartAutomaticUpdateAsync()
    {
        _automaticUpdate = true;
        ShowWithoutActivation();
        await OnPrimaryAsync();
    }

    public void ShowWithoutActivation()
    {
        try
        {
            ConfigureWindow();
            var hwnd = WindowNative.GetWindowHandle(this);
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(id);
            appWindow.Show(false);
            RunEntranceAnimation();
        }
        catch (Exception initialEx)
        {
            // Native no-activate fallback: updater notifications must never steal focus.
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SwShowNoActivate);
            }
            catch (Exception fallbackEx)
            {
                LogService.Error("update", "No-activate popup fallback failed: " + initialEx.Message, fallbackEx);
            }
        }
    }

    private void RunEntranceAnimation()
    {
        if ((App.IsSmokeUi && !_visualPreview) || !UiMotionSettingsService.EffectiveEnabled || Content is not UIElement element) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.Opacity = 1f; // A failed animation must never leave the content invisible.
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.45f, 0.72f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation("Opacity", fade);
        }
        catch { }
    }

    private void ConfigureWindow()
    {
        if (_closed) return;
        _ = DesktopPopupWindowChrome.TryConfigure(
            this, WindowWidth, _stackIndex, _visualPreview,
            "POPUP", "update",
            "Desktop update popup positioning failed; later activation may retry", _chrome);
    }
}
