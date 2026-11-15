using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace SpankyhoOptimizer;

internal sealed class DesktopStatusNotificationWindow : Window
{
    private const int WindowWidth = 420;
    private const int SwShowNoActivate = 4;
    private const int AutoHideMs = 7000;
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly DispatcherTimer _autoHide;
    private readonly int _stackIndex;
    private readonly bool _visualPreview;
    private readonly PopupChromeState _chrome = new();
    private bool _closed;

    internal int PopupWidth { get; private set; }
    internal int PopupHeight { get; private set; }
    internal UIElement PreviewElement => (UIElement)Content;

    public DesktopStatusNotificationWindow(
        string title,
        string body,
        ActionFeedbackKind kind,
        int stackIndex = 0,
        bool visualPreview = false)
    {
        _stackIndex = Math.Max(0, stackIndex);
        _visualPreview = visualPreview;
        Title = "Glass Optimizer";
        ExtendsContentIntoTitleBar = true;
        (string glyph, Windows.UI.Color accent) = kind switch
        {
            ActionFeedbackKind.Verified => ("\uE73E", Windows.UI.Color.FromArgb(255, 108, 203, 124)),
            ActionFeedbackKind.RestartRequired => ("\uE777", Windows.UI.Color.FromArgb(255, 240, 190, 90)),
            ActionFeedbackKind.Unsupported => ("\uE7BA", Windows.UI.Color.FromArgb(255, 240, 190, 90)),
            ActionFeedbackKind.Failed => ("\uEA39", Windows.UI.Color.FromArgb(255, 251, 130, 133)),
            ActionFeedbackKind.Applied => ("\uE73E", Windows.UI.Color.FromArgb(255, 108, 203, 124)),
            _ => ("\uE946", Windows.UI.Color.FromArgb(255, 150, 190, 240))
        };

        var root = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 22, 29)),
            Padding = new Thickness(6),
            RequestedTheme = ElementTheme.Dark
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 64, 64, 64)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 34, 44))
        };
        root.Children.Add(card);

        var row = new Grid { ColumnSpacing = 11 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = new Image { Width = 30, Height = 30, Stretch = Stretch.Uniform };
        string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png");
        if (File.Exists(logoPath)) logo.Source = new BitmapImage(new Uri(logoPath));
        row.Children.Add(new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 48, 48)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 80, 80)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = logo
        });

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 249, 252)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleRow.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(36, accent.R, accent.G, accent.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 11,
                Foreground = new SolidColorBrush(accent)
            }
        });
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 198, 206, 218)),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 4,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var close = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 198, 206, 218))
            },
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Top
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        row.Children.Add(close);

        card.Child = new Border { Padding = new Thickness(13, 11, 11, 11), Child = row };
        Content = root;

        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoHideMs) };
        _autoHide.Tick += (_, _) =>
        {
            _autoHide.Stop();
            Close();
        };

        Activated += (_, _) => ConfigureWindow();
        Closed += (_, _) =>
        {
            _closed = true;
            _autoHide.Stop();
        };
    }

    public void ShowWithoutActivation()
    {
        try
        {
            ConfigureWindow();
            var hwnd = WindowNative.GetWindowHandle(this);
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(id).Show(false);
            _autoHide.Start();
        }
        catch (Exception initialEx)
        {
            // Native no-activate fallback: feedback popups must never steal focus.
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SwShowNoActivate);
            }
            catch (Exception fallbackEx)
            {
                LogService.Error("ui", "Status popup fallback failed: " + initialEx.Message, fallbackEx);
            }
        }
    }

    internal bool IsShownWithoutActivation()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            return DesktopPopupWindowFrame.HasNoActivateStyle(hwnd) &&
                   IsWindowVisible(hwnd) && _chrome.Height > 0;
        }
        catch (Exception ex)
        {
            LogService.Error("ui", "Status popup visibility probe failed", ex);
            return false;
        }
    }

    // Only this popup taking the foreground proves focus theft; another window
    // becoming foreground during a smoke run is unrelated desktop activity.
    internal bool HoldsForeground()
    {
        try
        {
            return GetForegroundWindow() == WindowNative.GetWindowHandle(this);
        }
        catch (Exception ex)
        {
            LogService.Error("ui", "Status popup foreground probe failed", ex);
            return true;
        }
    }

    private void ConfigureWindow()
    {
        if (_closed) return;
        if (DesktopPopupWindowFrame.TryConfigure(
                this, WindowWidth, _stackIndex, _visualPreview,
                "STATUS_POPUP", "ui",
                "Status popup positioning failed; later activation may retry", _chrome) is { } size)
        {
            PopupWidth = size.Width;
            PopupHeight = size.Height;
        }
    }
}
