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
    private const int WindowHeight = 226;
    private const int SwShowNoActivate = 4;
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

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
    private bool _configured;
    private int _configureFailures;
    private bool _busy;
    private long _downloadGeneration;
    private bool _closed;
    private bool _ctsDisposed;
    private readonly int _stackIndex;

    public DesktopUpdateNotificationWindow(
        string category,
        string title,
        string version,
        string description,
        Func<IProgress<DesktopUpdateProgress>, CancellationToken, Task<DesktopUpdateCompletion>> downloadAction,
        int stackIndex = 0)
    {
        _downloadAction = downloadAction;
        _stackIndex = Math.Max(0, stackIndex);
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
            Text = category.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 206, 206, 206))
        });
        titles.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 249, 252))
        });
        titles.Children.Add(new TextBlock
        {
            Text = version,
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 206, 206, 206))
        });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        body.Children.Add(header);

        body.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(222, 221, 236, 244)),
            MaxLines = 2
        });

        var statusRow = new Grid();
        statusRow.ColumnDefinitions.Add(new ColumnDefinition());
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = new TextBlock
        {
            Text = "Aktualizace je připravená ke stažení.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 206, 206, 206)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _percent = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 206, 206, 206)),
            Margin = new Thickness(10, 0, 0, 0)
        };
        statusRow.Children.Add(_status);
        Grid.SetColumn(_percent, 1);
        statusRow.Children.Add(_percent);
        body.Children.Add(statusRow);

        _track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 64, 64, 64)),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _fill = new Border
        {
            Width = 0,
            Height = 6,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 92, 183, 255)),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _track.Child = _fill;
        body.Children.Add(_track);

        _detail = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 190, 190, 190)),
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

    // Blue while working; red after an actual failure. Reset before retries
    // so stale error colors do not leak into a new download.
    private void SetProgressFailureTone(bool failed)
    {
        var color = failed
            ? Windows.UI.Color.FromArgb(255, 251, 130, 133)
            : Windows.UI.Color.FromArgb(255, 92, 183, 255);
        _status.Foreground = new SolidColorBrush(color);
        _percent.Foreground = new SolidColorBrush(color);
        _fill.Background = new SolidColorBrush(failed
            ? Windows.UI.Color.FromArgb(255, 210, 78, 92)
            : Windows.UI.Color.FromArgb(255, 92, 183, 255));
    }

    private void SetFillWidth(double width)
    {
        double currentWidth = _fill.ActualWidth;
        _fillAnimation?.Stop();
        _fill.Width = currentWidth;
        if (_closed || App.IsSmokeUi || !UiMotionSettingsService.EffectiveEnabled || !_track.IsLoaded)
        {
            _fill.Width = width;
            return;
        }

        var animation = new DoubleAnimation
        {
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
                            double width = _track.ActualWidth > 0 ? _track.ActualWidth : 350;
                            SetFillWidth(width * percent / 100d);
                            _percent.Text = percent + " %";
                            _status.Text = p.Status;
                            if (p.Detail != null) _detail.Text = p.Detail;
                        }
                    });
                    await _completionProgressAction(completionProgress, _cts.Token);
                }
                else
                    await _completionAction();
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
            finally { _busy = false; DisposeCancellationWhenIdle(); }
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

        var progress = new Progress<DesktopUpdateProgress>(p =>
        {
            if (_closed || !_busy || token.IsCancellationRequested || generation != _downloadGeneration) return;
            int percent = Math.Clamp(p.Percent, 0, 100);
            double width = _track.ActualWidth > 0 ? _track.ActualWidth : 350;
            SetFillWidth(width * percent / 100d);
            _percent.Text = percent + " %";
            _status.Text = p.Status;
            if (p.Detail != null) _detail.Text = p.Detail;
        });

        try
        {
            DesktopUpdateCompletion result = await _downloadAction(progress, token);
            if (_closed) return;
            token.ThrowIfCancellationRequested();
            _status.Text = result.Status;
            if (!string.IsNullOrWhiteSpace(result.Detail)) _detail.Text = result.Detail;
            bool hasPostDownloadProgress = result.PrimaryProgressAction != null;
            SetFillWidth((_track.ActualWidth > 0 ? _track.ActualWidth : 350) * (hasPostDownloadProgress ? 0.5 : 1));
            _percent.Text = hasPostDownloadProgress ? "50 %" : "100 %";
            _primary.Content = result.PrimaryText;
            _completionAction = result.PrimaryAction;
            _completionProgressAction = result.PrimaryProgressAction;
            _primary.IsEnabled = result.PrimaryAction != null;
            if (_completionProgressAction != null)
                _primary.IsEnabled = true;
            _secondary.Content = "Zav\u0159\u00edt";
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
            DisposeCancellationWhenIdle();
        }
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
        if (App.IsSmokeUi || !UiMotionSettingsService.EffectiveEnabled || Content is not UIElement element) return;
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
        if (_configured || _closed) return;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            exStyle |= WsExNoActivate | WsExToolWindow;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));

            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(id);
            // Size the client after removing native chrome; XAML dimensions are DIPs.
            appWindow.IsShownInSwitchers = false;

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(false, false);
            }

            // The presenter leaves WS_DLGFRAME on this runtime even with HasBorder=false.
            // Clear only this popup's native frame before calculating client size.
            if (!NativeTitleBarService.TryRemovePopupNativeFrame(hwnd, out int chromeError))
                LogService.Info("window", "POPUP_NATIVE_FRAME_DEGRADED error=" + chromeError);
            double scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
            appWindow.ResizeClient(new SizeInt32(
                (int)Math.Ceiling(WindowWidth * scale), (int)Math.Ceiling(WindowHeight * scale)));

            // Same native silhouette as the main shell, without a bright OS frame.
            if (!NativeTitleBarService.TryApplyGlassBorder(hwnd, true, out int borderError))
                LogService.Info("window", "POPUP_GLASS_BORDER_DEGRADED error=" + borderError);
            NativeTitleBarService.TryRoundCorners(hwnd, out _);
            if (!NativeTitleBarService.TrySetRoundedGlassRegion(hwnd, true, 22 * scale, out int regionError, resizeEdges: false))
                LogService.Info("window", "POPUP_GLASS_REGION_DEGRADED error=" + regionError);

            if (App.IsSmokeUi)
            {
                if (appWindow.Presenter is OverlappedPresenter smokePresenter)
                    smokePresenter.IsAlwaysOnTop = false;
                appWindow.IsShownInSwitchers = false;
                appWindow.Move(new PointInt32(-32000, -32000));
                _configured = true;
                return;
            }

            DisplayArea area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            SizeInt32 actualSize = appWindow.Size;
            int margin = (int)Math.Ceiling(18 * scale);
            int gap = (int)Math.Ceiling(10 * scale);
            appWindow.Move(new PointInt32(
                work.X + Math.Max(0, work.Width - actualSize.Width - margin),
                work.Y + Math.Max(0, work.Height - actualSize.Height - margin - _stackIndex * (actualSize.Height + gap))));
            _configured = true;
        }
        catch (Exception ex)
        {
            // Do not permanently mark the window as configured after transient native failure.
            if (++_configureFailures <= 3)
                LogService.Error("update", "Desktop update popup positioning failed; later activation may retry", ex);
        }
    }
}
