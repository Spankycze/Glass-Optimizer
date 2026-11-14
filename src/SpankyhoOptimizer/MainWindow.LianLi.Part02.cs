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
    private Border BuildFanRpmSurface(bool enabled, FanProfile profile)
    {
        int initialRpm = Math.Clamp(profile.TargetRpm, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm);
        var body = new StackPanel { Spacing = 13 };

        var top = new Grid { ColumnSpacing = 14 };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rpmInfo = new StackPanel { Spacing = 2 };
        rpmInfo.Children.Add(new TextBlock { Text = "Cílové otáčky", Foreground = TextSecondaryBrush(), FontSize = 11 });
        _lianLiTargetRpmText = new TextBlock { Text = $"{initialRpm} RPM", FontSize = 29, FontWeight = FontWeights.Bold, Foreground = TextPrimaryBrush() };
        rpmInfo.Children.Add(_lianLiTargetRpmText);
        top.Children.Add(rpmInfo);

        var maxBadge = new Border
        {
            Padding = new Thickness(12, 7, 12, 7),
            CornerRadius = new CornerRadius(9),
            Background = _darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "MAX 2100 RPM", FontWeight = FontWeights.SemiBold, Foreground = AccentBrush() }
        };
        Grid.SetColumn(maxBadge, 1);
        top.Children.Add(maxBadge);
        body.Children.Add(top);

        var slider = new Slider
        {
            Minimum = LianLiSlInfinityService.MinimumSafeRpm,
            Maximum = LianLiSlInfinityService.MaximumRpm,
            Value = initialRpm,
            StepFrequency = 50,
            IsEnabled = enabled
        };
        slider.ValueChanged += (_, e) =>
        {
            int rpm = (int)(Math.Round(e.NewValue / 50d) * 50d);
            if (_lianLiTargetRpmText != null) _lianLiTargetRpmText.Text = $"{rpm} RPM";
        };
        body.Children.Add(slider);

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var preset in new[] { ("Silent", 700), ("Balanced", 1100), ("Performance", 1600), ("MAX", 2100) })
        {
            var button = new Button { Content = $"{preset.Item1}  {preset.Item2} RPM", Tag = preset.Item2, IsEnabled = enabled };
            ApplyPillButtonAppearance(button);
            button.Click += (_, _) => slider.Value = (int)button.Tag;
            presets.Children.Add(button);
        }
        body.Children.Add(presets);

        var liveGrid = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < 4; i++) liveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 4; i++)
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock { Text = $"KANÁL {i + 1}", FontSize = 10.5, CharacterSpacing = 90, Foreground = AccentBrush() });
            var rpmText = new TextBlock { Text = "— RPM", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = TextPrimaryBrush() };
            _lianLiRpmTexts[i] = rpmText;
            stack.Children.Add(rpmText);
            var card = new Border
            {
                Padding = new Thickness(11, 8, 11, 8),
                CornerRadius = new CornerRadius(9),
                Background = _darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush(),
                BorderBrush = SectionBorderBrush(),
                BorderThickness = new Thickness(1),
                Child = stack
            };
            Grid.SetColumn(card, i);
            liveGrid.Children.Add(card);
        }
        body.Children.Add(liveGrid);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "Použít RPM", IsEnabled = enabled };
        ApplyMaterialButtonAppearance(apply, primary: true);
        apply.Click += (_, _) =>
        {
            int rpm = (int)(Math.Round(slider.Value / 50d) * 50d);
            FanProfile saved = FanProfileService.Load() with
            {
                TargetRpm = rpm,
                MotherboardPwm = false,
                CpuTempTargetEnabled = false
            };
            SafeTaskService.Forget(
                RunLianLiActionAsync(() =>
                {
                    LianLiSlInfinityService.SetAllFanRpm(rpm);
                    FanProfileService.Save(saved);
                }, $"Ventilátory nastaveny na {rpm} RPM • profil uložen"),
                "lianli", "Set fan RPM");
        };

        var pwm = new Button { Content = "Motherboard PWM", IsEnabled = enabled };
        ApplyPillButtonAppearance(pwm);
        pwm.Click += (_, _) =>
        {
            FanProfile saved = FanProfileService.Load() with
            {
                MotherboardPwm = true,
                CpuTempTargetEnabled = false
            };
            SafeTaskService.Forget(
                RunLianLiActionAsync(() =>
                {
                    LianLiSlInfinityService.SetAllMotherboardPwm();
                    FanProfileService.Save(saved);
                }, "Řízení předáno základní desce • profil uložen"),
                "lianli", "Enable motherboard PWM");
        };

        var auto65 = new Button
        {
            Content = profile.CpuTempTargetEnabled ? "AUTO 65 °C • ZAPNUTO" : "AUTO ≤65 °C",
            IsEnabled = enabled
        };
        ApplyMaterialButtonAppearance(auto65, primary: profile.CpuTempTargetEnabled);
        int auto65Busy = 0;
        auto65.Click += (_, _) =>
        {
            if (Interlocked.Exchange(ref auto65Busy, 1) != 0) return;
            int actionGeneration = _navigationGeneration;
            auto65.IsEnabled = false;
            SafeTaskService.Forget(ApplyAuto65Async(), "lianli", "Verified AUTO 65 toggle");

            async Task ApplyAuto65Async()
            {
                CancellationToken windowToken = _windowLifetimeCts.Token;
                try
                {
                    await EnsureLianLiSupportedAsync(windowToken).ConfigureAwait(false);
                    var result = await Task.Run(() =>
                    {
                        FanProfile current = FanProfileService.Load();
                        bool turnOn = !current.CpuTempTargetEnabled;
                        FanProfile saved = current with
                        {
                            CpuTempTargetEnabled = turnOn,
                            CpuTempTargetC = 65,
                            MotherboardPwm = false,
                            AutoCurveEnabled = false,
                            ChannelCurveEnabled = new[] { false, false, false, false }
                        };
                        FanProfileService.Save(saved);
                        FanProfile persisted = FanProfileService.Load();
                        if (persisted.CpuTempTargetEnabled != turnOn)
                            throw new InvalidOperationException("AUTO 65 °C read-back nepotvrdil uložený stav.");
                        if (turnOn)
                            FanProfileService.StartAgentIfNeeded();
                        return (TurnOn: turnOn, Saved: saved);
                    }, windowToken).ConfigureAwait(false);

                    if (windowToken.IsCancellationRequested) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (windowToken.IsCancellationRequested || actionGeneration != _navigationGeneration || _section != "lianli" || auto65.Parent == null) return;
                        auto65.Content = result.TurnOn ? "AUTO 65 °C • ZAPNUTO" : "AUTO ≤65 °C";
                        auto65.Background = result.TurnOn
                            ? (_darkTheme ? GlassAccentTintBrush() : ButtonVioletBrush())
                            : (_darkTheme ? GlassPanelBrush() : null);
                        auto65.BorderBrush = result.TurnOn
                            ? (_darkTheme ? GlassBorderStrongBrush() : ButtonVioletBorderBrush())
                            : (_darkTheme ? GlassEdgeBrush() : BorderThemeBrush());
                        auto65.Foreground = result.TurnOn
                            ? (_darkTheme ? TextPrimaryBrush() : OnAccentTextBrush())
                            : TextPrimaryBrush();
                        SetFanStatus(
                            result.TurnOn
                                ? "AUTO 65 °C aktivní • minimum 1620 RPM • otáčky se budou průběžně přizpůsobovat CPU teplotě"
                                : "AUTO 65 °C vypnuto • zůstává poslední nastavená rychlost",
                            true);
                    });
                }
                catch (OperationCanceledException) when (windowToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    LogService.Error("lianli", "AUTO 65 toggle failed", ex);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (actionGeneration == _navigationGeneration && _section == "lianli" && auto65.Parent != null)
                            SetFanStatus("AUTO 65 °C: " + ex.Message, false);
                    });
                }
                finally
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        Interlocked.Exchange(ref auto65Busy, 0);
                        if (actionGeneration == _navigationGeneration && _section == "lianli" && auto65.Parent != null) auto65.IsEnabled = enabled;
                    });
                }
            }
        };

        actions.Children.Add(apply);
        actions.Children.Add(pwm);
        actions.Children.Add(auto65);
        body.Children.Add(actions);

        var aiSmart = new ToggleSwitch
        {
            Header = "AI Smart Runtime",
            OffContent = "Vypnuto",
            OnContent = "Zapnuto",
            IsOn = profile.AiSmartRuntimeEnabled,
            IsEnabled = enabled
        };
        NormalizeToggleAppearance(aiSmart);
        bool aiSmartSuppress = false;
        aiSmart.Toggled += (_, _) =>
        {
            if (aiSmartSuppress || !aiSmart.IsEnabled) return;
            int actionGeneration = _navigationGeneration;
            bool requested = aiSmart.IsOn;
            SafeTaskService.Forget(ApplyAiSmartAsync(), "fan", "Verified AI Smart Runtime toggle");

            async Task ApplyAiSmartAsync()
            {
                aiSmart.IsEnabled = false;
                try
                {
                    await EnsureLianLiSupportedAsync(_windowLifetimeCts.Token).ConfigureAwait(false);
                    FanProfile current = FanProfileService.Load();
                    FanProfileService.Save(current with { AiSmartRuntimeEnabled = requested });
                    FanProfile persisted = FanProfileService.Load();
                    if (persisted.AiSmartRuntimeEnabled != requested)
                        throw new InvalidOperationException($"AI Smart Runtime read-back nesouhlasí. Požadováno={requested}, skutečnost={persisted.AiSmartRuntimeEnabled}.");

                    if (requested)
                        FanProfileService.StartAgentIfNeeded();

                    if (actionGeneration == _navigationGeneration && _section == "lianli" && aiSmart.Parent != null)
                        SetFanStatus(
                            requested
                                ? "AI Smart Runtime aktivní • lokální predikce, anomaly detection, smart fan/power"
                                : "AI Smart Runtime vypnutý • klasická AUTO65/power pravidla zůstávají",
                            true);
                    LogService.Info("fan", $"AI_SMART_TOGGLE_OK requested={requested}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    bool actual = FanProfileService.Load().AiSmartRuntimeEnabled;
                    if (actionGeneration == _navigationGeneration && _section == "lianli" && aiSmart.Parent != null)
                    {
                        aiSmartSuppress = true;
                        try { aiSmart.IsOn = actual; }
                        finally { aiSmartSuppress = false; }
                    }
                    LogService.Error("fan", $"AI_SMART_TOGGLE_FAIL requested={requested} actual={actual}", ex);
                    await ShowMessageAsync("AI Smart Runtime", ex.Message);
                }
                finally
                {
                    if (actionGeneration == _navigationGeneration && _section == "lianli" && aiSmart.Parent != null)
                        aiSmart.IsEnabled = enabled;
                }
            }
        };
        body.Children.Add(aiSmart);

        body.Children.Add(new TextBlock
        {
            Text = "AI Smart Runtime běží čistě lokálně bez cloudu. Učí se baseline teploty, package power, loadu a RPM; předvídá růst teploty, hlídá anomálie a při AUTO65 upravuje otáčky s minimem 1620 RPM. Diablo Immortal dál drží Performance.",
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        return BuildSectionSurface("usb", "Otáčky ventilátorů", "RPM místo procent • max 2100 RPM • profily • trvalé uložení", body);
    }
    private Border BuildFanCurveSurface(bool enabled, FanProfile profile)
    {
        var body = new StackPanel { Spacing = 12 };

        var auto = new ToggleSwitch
        {
            Header = "Automatická křivka podle CPU teploty",
            OffContent = "Vypnuto",
            OnContent = "Zapnuto",
            IsOn = profile.AutoCurveEnabled,
            IsEnabled = enabled
        };
        NormalizeToggleAppearance(auto);
        bool autoSuppress = false;
        bool autoBusy = false;
        bool autoAccepted = profile.AutoCurveEnabled;
        auto.Toggled += (_, _) =>
        {
            if (autoSuppress || !auto.IsEnabled) return;
            if (autoBusy)
            {
                autoSuppress = true;
                try { auto.IsOn = autoAccepted; }
                finally { autoSuppress = false; }
                return;
            }

            int actionGeneration = _navigationGeneration;
            bool requested = auto.IsOn;
            SafeTaskService.Forget(ApplyAutoCurveToggleAsync(), "lianli", "Verified auto fan curve toggle");

            async Task ApplyAutoCurveToggleAsync()
            {
                autoBusy = true;
                auto.IsEnabled = false;
                FanProfile original = FanProfileService.Load();
                try
                {
                    await EnsureLianLiSupportedAsync(_windowLifetimeCts.Token).ConfigureAwait(false);
                    FanProfileService.Save(original with { AutoCurveEnabled = requested, MotherboardPwm = false });
                    FanProfile persisted = FanProfileService.Load();
                    if (persisted.AutoCurveEnabled != requested)
                        throw new InvalidOperationException($"Auto fan curve read-back nesouhlasí. Požadováno={requested}, skutečnost={persisted.AutoCurveEnabled}.");

                    autoAccepted = requested;
                    if (requested)
                        FanProfileService.StartAgentIfNeeded();

                    if (actionGeneration == _navigationGeneration && _section == "lianli" && auto.Parent != null)
                        SetFanStatus(
                            requested ? "Auto křivka zapnuta • profil uložen a agent aktivní" : "Auto křivka vypnuta • profil uložen",
                            true);
                    LogService.Info("lianli", $"AUTO_CURVE_TOGGLE_OK requested={requested}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (LianLiSlInfinityService.Detect().Present)
                            FanProfileService.Save(original);
                    }
                    catch { }
                    bool actual = FanProfileService.Load().AutoCurveEnabled;
                    autoAccepted = actual;
                    if (actionGeneration == _navigationGeneration && _section == "lianli" && auto.Parent != null)
                    {
                        autoSuppress = true;
                        try { auto.IsOn = actual; }
                        finally { autoSuppress = false; }
                    }

                    LogService.Error("lianli", $"AUTO_CURVE_TOGGLE_FAIL requested={requested} actual={actual}", ex);
                    await ShowMessageAsync("Automatická křivka", ex.Message);
                }
                finally
                {
                    if (actionGeneration == _navigationGeneration && _section == "lianli" && auto.Parent != null)
                        auto.IsEnabled = enabled;
                    autoBusy = false;
                }
            }
        };
        body.Children.Add(auto);

        var curveGrid = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        curveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        curveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        curveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        curveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        curveGrid.Children.Add(new TextBlock { Text = "#", Foreground = TextSecondaryBrush(), FontSize = 10.5 });
        var tempHead = new TextBlock { Text = "CPU TEPLOTA", Foreground = AccentBrush(), FontSize = 10.5, CharacterSpacing = 80 };
        Grid.SetColumn(tempHead, 1);
        curveGrid.Children.Add(tempHead);
        var rpmHead = new TextBlock { Text = "CÍLOVÉ RPM", Foreground = AccentBrush(), FontSize = 10.5, CharacterSpacing = 80 };
        Grid.SetColumn(rpmHead, 2);
        curveGrid.Children.Add(rpmHead);

        int[] temps = { profile.CurveTemp1, profile.CurveTemp2, profile.CurveTemp3, profile.CurveTemp4 };
        int[] rpms = { profile.CurveRpm1, profile.CurveRpm2, profile.CurveRpm3, profile.CurveRpm4 };
        var tempBoxes = new NumberBox[4];
        var rpmBoxes = new NumberBox[4];

        for (int i = 0; i < 4; i++)
        {
            curveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = i + 1;

            var idx = new TextBlock
            {
                Text = (i + 1).ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextSecondaryBrush()
            };
            Grid.SetRow(idx, row);
            curveGrid.Children.Add(idx);

            var t = new NumberBox
            {
                Minimum = 25,
                Maximum = 100,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = temps[i],
                IsEnabled = enabled
            };
            Grid.SetRow(t, row);
            Grid.SetColumn(t, 1);
            curveGrid.Children.Add(t);
            tempBoxes[i] = t;

            var rpm = new NumberBox
            {
                Minimum = LianLiSlInfinityService.MinimumSafeRpm,
                Maximum = LianLiSlInfinityService.MaximumRpm,
                SmallChange = 50,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = rpms[i],
                IsEnabled = enabled
            };
            Grid.SetRow(rpm, row);
            Grid.SetColumn(rpm, 2);
            curveGrid.Children.Add(rpm);
            rpmBoxes[i] = rpm;
        }

        body.Children.Add(curveGrid);

        var hint = new TextBlock
        {
            Text = "Agent každé 2 s čte CPU Tctl/Tdie a mezi body plynule dopočítá RPM. Změny menší než 75 RPM ignoruje, aby ventilátory nehonily každou drobnou změnu teploty.",
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        body.Children.Add(hint);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "Uložit křivku", IsEnabled = enabled };
        ApplyMaterialButtonAppearance(apply, primary: true);
        apply.Click += (_, _) =>
        {
            int[] t = new int[4];
            int[] r = new int[4];
            for (int i = 0; i < 4; i++)
            {
                t[i] = (int)Math.Round(tempBoxes[i].Value);
                r[i] = (int)Math.Round(rpmBoxes[i].Value / 50d) * 50;
            }

            if (!(t[0] < t[1] && t[1] < t[2] && t[2] < t[3]))
            {
                SetFanStatus("Teplotní body musí být seřazené od nejnižšího po nejvyšší.", false);
                return;
            }

            bool enableCurve = auto.IsOn;
            SafeTaskService.Forget(
                RunLianLiActionAsync(() =>
                {
                    FanProfile saved = FanProfileService.Load() with
                    {
                        AutoCurveEnabled = enableCurve,
                        MotherboardPwm = false,
                        CurveTemp1 = t[0],
                        CurveRpm1 = r[0],
                        CurveTemp2 = t[1],
                        CurveRpm2 = r[1],
                        CurveTemp3 = t[2],
                        CurveRpm3 = r[2],
                        CurveTemp4 = t[3],
                        CurveRpm4 = r[3]
                    };

                    if (enableCurve)
                    {
                        CpuTemperatureService.CpuTemperatureSample sample = HardwareTelemetryBridgeService.ReadBestTemperature();
                        int target = sample.Celsius is double c
                            ? FanProfileService.CalculateCurveRpm(saved, c)
                            : saved.TargetRpm;
                        LianLiSlInfinityService.SetAllFanRpm(target);
                    }

                    FanProfileService.Save(saved);
                    if (enableCurve)
                        FanProfileService.StartAgentIfNeeded();
                },
                enableCurve ? "Auto křivka uložena a aktivní" : "Auto křivka vypnuta • profil uložen"),
                "lianli", "Apply auto fan curve");
        };

        var balanced = new Button { Content = "Výchozí křivka", IsEnabled = enabled };
        ApplyPillButtonAppearance(balanced);
        balanced.Click += (_, _) =>
        {
            int[] dt = { 40, 55, 70, 82 };
            int[] dr = { 700, 1000, 1500, 2100 };
            for (int i = 0; i < 4; i++)
            {
                tempBoxes[i].Value = dt[i];
                rpmBoxes[i].Value = dr[i];
            }
        };

        actions.Children.Add(apply);
        actions.Children.Add(balanced);
        body.Children.Add(actions);

        return BuildSectionSurface("usb", "Smart Fan Curve", "4 body • CPU Tctl/Tdie • plynulá interpolace • běží i po zavření aplikace", body);
    }
}
