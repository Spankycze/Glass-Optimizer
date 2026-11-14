using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;
using Windows.UI;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private (Border Surface, StackPanel ModeRow, ComboBox EffectBox) BuildRgbStudioSurface(
        bool enabled,
        FanProfile profile)
    {
        var body = new StackPanel { Spacing = 12 };

        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });

        var effects = new StackPanel { Spacing = 8 };
        effects.Children.Add(new TextBlock { Text = "LIGHTING EFFECT", FontSize = 10.5, CharacterSpacing = 100, Foreground = AccentBrush() });

        var effectBox = new ComboBox { MinWidth = 260, IsEnabled = enabled };
        int selectedEffectIndex = 0;
        for (int i = 0; i < LianLiEffects.Length; i++)
        {
            LianLiEffect effect = LianLiEffects[i];
            effectBox.Items.Add($"{effect.Name}  •  {effect.Subtitle}");
            if (string.Equals(effect.Name, profile.EffectName, StringComparison.OrdinalIgnoreCase)) selectedEffectIndex = i;
        }
        effectBox.SelectedIndex = selectedEffectIndex;
        effects.Children.Add(effectBox);

        var modeStrip = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            Height = 76
        };
        var modeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        modeStrip.Content = modeRow;
        effects.Children.Add(modeStrip);
        header.Children.Add(effects);
        _lianLiPreviewA = LianLiEffects[selectedEffectIndex].PreviewA;
        _lianLiPreviewB = LianLiEffects[selectedEffectIndex].PreviewB;
        _lianLiPreviewAnimated = LianLiEffects[selectedEffectIndex].Animated;
        _lianLiPreviewPhase = 0;
        _lianLiPreviewBrush = new SolidColorBrush(ParseColor(_lianLiPreviewA));
        _lianLiRgbPreview = new Border
        {
            Height = 112,
            CornerRadius = new CornerRadius(14),
            Background = _darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "Načítám fan preview…",
                FontSize = 11,
                Foreground = TextSecondaryBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(_lianLiRgbPreview, 1);
        header.Children.Add(_lianLiRgbPreview);
        body.Children.Add(header);

        var controls = new Grid { ColumnSpacing = 12 };
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition());

        var hex = new TextBox { Header = "Barva", Text = profile.HexColor, PlaceholderText = "#RRGGBB", IsEnabled = enabled };
        controls.Children.Add(hex);

        var speed = new ComboBox { Header = "Rychlost efektu", SelectedIndex = Math.Clamp(profile.EffectSpeed, (byte)0, (byte)3), IsEnabled = enabled };
        speed.Items.Add("Velmi pomalu");
        speed.Items.Add("Pomalu");
        speed.Items.Add("Rychle");
        speed.Items.Add("Velmi rychle");
        Grid.SetColumn(speed, 1);
        controls.Children.Add(speed);

        var brightness = new ComboBox { Header = "Jas", IsEnabled = enabled };
        brightness.Items.Add("25 %");
        brightness.Items.Add("50 %");
        brightness.Items.Add("75 %");
        brightness.Items.Add("100 %");
        brightness.SelectedIndex = profile.BrightnessPercent switch { <= 25 => 0, <= 50 => 1, <= 75 => 2, _ => 3 };
        Grid.SetColumn(brightness, 2);
        controls.Children.Add(brightness);
        body.Children.Add(controls);

        var direction = new ToggleSwitch { Header = "Směr efektu", OffContent = "← Vlevo", OnContent = "Vpravo →", IsOn = profile.EffectDirection != 0, IsEnabled = enabled };
        NormalizeToggleAppearance(direction);
        body.Children.Add(direction);
        effectBox.SelectionChanged += (_, _) =>
        {
            int index = Math.Clamp(effectBox.SelectedIndex, 0, LianLiEffects.Length - 1);
            LianLiEffect effect = LianLiEffects[index];
            _lianLiPreviewA = effect.PreviewA;
            _lianLiPreviewB = effect.PreviewB;
            _lianLiPreviewAnimated = effect.Animated;
            _lianLiPreviewPhase = 0;
            Color previewA = ParseColor(effect.PreviewA);
            Color previewB = ParseColor(effect.PreviewB);
            if (_lianLiPreviewBrush != null) _lianLiPreviewBrush.Color = previewA;
            UpdateFanRingPreview(previewA, previewB);
        };
        effectBox.SelectedIndex = selectedEffectIndex;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "Použít efekt", IsEnabled = enabled };
        ApplyMaterialButtonAppearance(apply, primary: true);
        apply.Click += (_, _) =>
        {
            int index = Math.Clamp(effectBox.SelectedIndex, 0, LianLiEffects.Length - 1);
            LianLiEffect effect = LianLiEffects[index];
            if (!TryParseRgb(hex.Text, out byte r, out byte g, out byte b))
            {
                SetFanStatus("Neplatná RGB barva. Použij #RRGGBB.", false);
                return;
            }
            int brightnessValue = new[] { 25, 50, 75, 100 }[Math.Max(0, brightness.SelectedIndex)];
            byte speedValue = (byte)Math.Max(0, speed.SelectedIndex);
            byte directionValue = direction.IsOn ? (byte)1 : (byte)0;

            FanProfile saved = FanProfileService.Load() with
            {
                ArgbSync = false,
                EffectName = effect.Name,
                EffectMode = effect.Mode,
                EffectSpeed = speedValue,
                EffectDirection = directionValue,
                HexColor = hex.Text.ToUpperInvariant(),
                BrightnessPercent = brightnessValue
            };
            SafeTaskService.Forget(
                RunLianLiActionAsync(
                    () =>
                    {
                        LianLiSlInfinityService.SetAllEffect(effect.Mode, speedValue, directionValue, r, g, b, brightnessValue);
                        FanProfileService.Save(saved);
                    },
                    $"{effect.Name} • {hex.Text.ToUpperInvariant()} • {brightnessValue} % • profil uložen"),
                "lianli", "Apply RGB effect");
        };
        var sync = new Button { Content = "ARGB Sync z desky", IsEnabled = enabled };
        ApplyPillButtonAppearance(sync);
        sync.Click += (_, _) =>
        {
            FanProfile saved = FanProfileService.Load() with { ArgbSync = true };
            SafeTaskService.Forget(
                RunLianLiActionAsync(() =>
                {
                    LianLiSlInfinityService.SetArgbSync(true);
                    FanProfileService.Save(saved);
                }, "ARGB sync zapnut • profil uložen"),
                "lianli", "Enable ARGB sync");
        };

        actions.Children.Add(apply);
        actions.Children.Add(sync);
        body.Children.Add(actions);

        Border surface = BuildSectionSurface(
            "usb",
            "RGB Studio",
            $"{LianLiEffects.Length} režimů • skutečný fan preview • zóny • rychlost • směr • jas",
            body);
        return (surface, modeRow, effectBox);
    }

    private async Task RunFanTelemetryLoopAsync(CancellationToken token, int generation)
    {
        while (!token.IsCancellationRequested && generation == _navigationGeneration && _section == "lianli")
        {
            FanAgentHealth? health = await Task.Run(FanProfileService.ReadHealthStatus, token).ConfigureAwait(false);
            bool healthFresh = health != null && DateTime.UtcNow - health.UpdatedUtc <= TimeSpan.FromSeconds(10);

            CpuTemperatureService.CpuTemperatureSample? fallbackSample = null;
            int[]? fallbackRpms = null;

            if (!healthFresh)
            {
                fallbackSample = await Task.Run(HardwareTelemetryBridgeService.ReadBestTemperature, token).ConfigureAwait(false);
                try { fallbackRpms = await Task.Run(LianLiSlInfinityService.ReadFanRpms, token).ConfigureAwait(false); }
                catch { }
            }

            if (token.IsCancellationRequested) break;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _navigationGeneration || _section != "lianli" || _lianLiCpuTempText == null) return;

                double? tempC = healthFresh ? health!.CpuTempC : fallbackSample?.Celsius;
                int[]? rpms = healthFresh ? health!.Rpms : fallbackRpms;

                _lianLiCpuTempText.Text = tempC.HasValue ? $"{tempC.Value:0.0} °C" : "N/A";
                _lianLiCpuTempText.Foreground = tempC.HasValue ? TextPrimaryBrush() : WarningBrush();

                if (_lianLiCpuSensorText != null)
                {
                    _lianLiCpuSensorText.Text = healthFresh
                        ? "Fan Agent telemetry"
                        : fallbackSample?.Celsius.HasValue == true
                            ? fallbackSample.Sensor
                            : "CPU senzor nedostupný";
                }

                if (rpms != null)
                {
                    for (int i = 0; i < Math.Min(4, rpms.Length); i++)
                    {
                        if (_lianLiRpmTexts[i] != null)
                        {
                            _lianLiRpmTexts[i]!.Text = rpms[i] > 0 ? $"{rpms[i]} RPM" : "0 RPM";
                            _lianLiRpmTexts[i]!.Foreground = rpms[i] > 0 ? TextPrimaryBrush() : TextSecondaryBrush();
                        }
                    }
                }

                if (_lianLiAgentStatusText != null)
                {
                    if (healthFresh)
                    {
                        string mode = health!.GameActive
                            ? "DIABLO PERFORMANCE"
                            : health.Auto65
                                ? "AUTO65"
                                : health.PowerMode;
                        string hid = health.HidOk ? "HID OK" : "HID problém";
                        string idle = health.RealIdle ? "IDLE" : $"load {health.LoadPercent:0}%";
                        string packagePower = health.PackagePowerW is double watts ? $"{watts:0} W" : "— W";
                        string ai = health.AiEnabled
                            ? $"AI {health.AiMode} • conf {health.AiConfidence:0}% • 10s {health.AiPredictedTemp10s:0.0}°C • anomaly {health.AiAnomalyScore:0}"
                            : "AI OFF";
                        _lianLiAgentStatusText.Text =
                            $"Fan Agent {health.Version} • PID {health.ProcessId} • {mode} • {idle} • {packagePower} • {ai} • {hid}";
                        _lianLiAgentStatusText.Foreground = health.Healthy ? SuccessBrush() : WarningBrush();
                    }
                    else
                    {
                        _lianLiAgentStatusText.Text = "Fan Agent: stav je zastaralý • používám přímý fallback";
                        _lianLiAgentStatusText.Foreground = WarningBrush();
                    }
                }
            });

            await Task.Delay(2000, token).ConfigureAwait(false);
        }
    }

    private async Task RunRgbPreviewLoopAsync(CancellationToken token, int generation)
    {
        while (!token.IsCancellationRequested && generation == _navigationGeneration && _section == "lianli")
        {
            await Task.Delay(40, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) break;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _navigationGeneration || _section != "lianli" || _lianLiPreviewBrush == null || !_lianLiPreviewAnimated) return;
                _lianLiPreviewPhase = (_lianLiPreviewPhase + 1) % 120;
                double t = (Math.Sin(_lianLiPreviewPhase * Math.PI / 60d - Math.PI / 2d) + 1d) * 0.5d;
                Color a = ParseColor(_lianLiPreviewA);
                Color b = ParseColor(_lianLiPreviewB);
                _lianLiPreviewBrush.Color = LerpColor(a, b, t);
                UpdateFanRingPreview(a, b);
            });
        }
    }
    private async Task RefreshLianLiStatusAsync(int generation)
    {
        CancellationToken token = _windowLifetimeCts.Token;
        try
        {
            var info = await Task.Run(LianLiSlInfinityService.Detect, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "lianli" || _lianLiStatus == null) return;
                if (!info.Present)
                {
                    SetFanStatus("Lian Li controller 0CF2:A102 nebyl nalezen.", false);
                    SafeTaskService.Forget(ShowSectionAsync("overview"), "lianli", "Leave unsupported fan page");
                    return;
                }
                SetFanStatus(info.Present
                    ? "Připojeno • Lian Li UNI FAN SL-Infinity • Direct HID"
                    : "Lian Li controller 0CF2:A102 nebyl nalezen.", info.Present);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal window shutdown.
        }
    }

    private async Task RunLianLiActionAsync(Action action, string success)
    {
        CancellationToken token = _windowLifetimeCts.Token;
        int generation = _navigationGeneration;
        var status = _lianLiStatus;
        bool entered = false;
        try
        {
            entered = await _lianLiActionGate.WaitAsync(0, token).ConfigureAwait(false);
            if (!entered)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested && generation == _navigationGeneration && _section == "lianli" && ReferenceEquals(_lianLiStatus, status))
                        SetFanStatus("Ventilátory: předchozí příkaz ještě probíhá.", false);
                });
                return;
            }

            await EnsureLianLiSupportedAsync(token).ConfigureAwait(false);
            await Task.Run(action, token).ConfigureAwait(false);
            if (!token.IsCancellationRequested)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation == _navigationGeneration && _section == "lianli" && ReferenceEquals(_lianLiStatus, status)) SetFanStatus(success, true);
                });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal window shutdown.
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Direct controller action failed", ex);
            if (!token.IsCancellationRequested)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation == _navigationGeneration && _section == "lianli" && ReferenceEquals(_lianLiStatus, status)) SetFanStatus("Ventilátory: " + ex.Message, false);
                });
        }
        finally
        {
            if (entered) _lianLiActionGate.Release();
        }
    }

    private static async Task EnsureLianLiSupportedAsync(CancellationToken token)
    {
        var info = await Task.Run(LianLiSlInfinityService.Detect, token).ConfigureAwait(false);
        if (!info.Present)
            throw new InvalidOperationException("Lian Li controller 0CF2:A102 nebyl nalezen.");
    }

    private static bool IsLianLiSupported()
    {
        return LianLiSlInfinityService.Detect().Present;
    }

    private void SetFanStatus(string text, bool ok)
    {
        if (_lianLiStatus == null) return;
        _lianLiStatus.Text = text;
        _lianLiStatus.Foreground = ok ? SuccessBrush() : WarningBrush();
    }

    private static Brush BuildEffectGradient(string a, string b)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        gradient.GradientStops.Add(new GradientStop { Color = ParseColor(a), Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = ParseColor(b), Offset = 1 });
        return gradient;
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0d, 1d);
        return Color.FromArgb(
            255,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static Color ParseColor(string value)
    {
        string text = value.Trim().TrimStart('#');
        if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Microsoft.UI.Colors.Magenta;
    }

    private static bool TryParseRgb(string? value, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string text = (value ?? string.Empty).Trim().TrimStart('#');
        return text.Length == 6
            && byte.TryParse(text.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(text.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(text.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
