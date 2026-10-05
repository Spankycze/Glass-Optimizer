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
    private TextBlock? _lianLiStatus;
    private TextBlock? _lianLiCpuTempText;
    private TextBlock? _lianLiCpuSensorText;
    private TextBlock? _lianLiTargetRpmText;
    private TextBlock? _lianLiAgentStatusText;
    private readonly TextBlock?[] _lianLiRpmTexts = new TextBlock?[4];
    private Border? _lianLiRgbPreview;
    private SolidColorBrush? _lianLiPreviewBrush;
    private string _lianLiPreviewA = "#5A00FF";
    private string _lianLiPreviewB = "#101020";
    private bool _lianLiPreviewAnimated;
    private int _lianLiPreviewPhase;

    private sealed record LianLiEffect(string Name, byte Mode, string Subtitle, string PreviewA, string PreviewB, bool Animated);

    private static readonly LianLiEffect[] LianLiEffects =
    {
        new("Static", 0x01, "Stálá barva", "#5A00FF", "#5A00FF", false),
        new("Breathing", 0x02, "Plynulé dýchání", "#5A00FF", "#151525", true),
        new("Rainbow Morph", 0x04, "Přelévání spektra", "#FF2070", "#20D9FF", true),
        new("Rainbow", 0x05, "Duhový tok", "#FF3D3D", "#20FF70", true),
        new("Staggered", 0x18, "Střídavý pohyb", "#7D4DFF", "#00E5FF", true),
        new("Tide", 0x1A, "Barevná vlna", "#20D9FF", "#5A00FF", true),
        new("Runway", 0x1C, "Běžící světlo", "#FFFFFF", "#5A00FF", true),
        new("Mixing", 0x1E, "Míchání barev", "#FF2070", "#2080FF", true),
        new("Stack", 0x20, "Skládání segmentů", "#5A00FF", "#20FF70", true),
        new("Stack Multi Color", 0x21, "Vícebarevný stack", "#FF2070", "#20FF70", true),
        new("Neon", 0x22, "Neonový přechod", "#00E5FF", "#FF20C8", true),
        new("Color Cycle", 0x23, "Cyklické barvy", "#FF9A20", "#20E0FF", true),
        new("Meteor", 0x24, "Meteor efekt", "#FFFFFF", "#5A00FF", true),
        new("Voice", 0x26, "Audio reactive", "#20FF70", "#5A00FF", true),
        new("Groove", 0x27, "Rytmický pohyb", "#FF2070", "#00E5FF", true),
        new("Render", 0x28, "Vícebarevný sweep", "#FF9A20", "#5A00FF", true),
        new("Tunnel", 0x29, "Tunelový efekt", "#00E5FF", "#151525", true),
        new("Meteor Merged", 0x2A, "Synchronně přes všechny kanály", "#FFFFFF", "#5A00FF", true),
        new("Runway Merged", 0x2B, "Synchronní chase", "#FF2070", "#2080FF", true),
        new("Tide Merged", 0x2C, "Synchronní vlna", "#20D9FF", "#5A00FF", true),
        new("Mixing Merged", 0x2D, "Synchronní blend", "#FF2070", "#20FF70", true),
        new("Stack Merged", 0x2E, "Synchronní rainbow stack", "#FF9A20", "#20E0FF", true)
    };
    private async Task BuildLianLiPageAsync(CancellationToken token)
    {
        FanProfile profile = FanProfileService.Load();
        FanAgentHealth? initialHealth = FanProfileService.ReadHealthStatus();
        bool healthFresh =
            initialHealth != null &&
            DateTime.UtcNow - initialHealth.UpdatedUtc <= TimeSpan.FromSeconds(10);

        bool present;
        CpuTemperatureService.CpuTemperatureSample temp;

        if (healthFresh)
        {
            present = initialHealth!.HidOk;
            temp = new CpuTemperatureService.CpuTemperatureSample(
                initialHealth.CpuTempC,
                "Fan Agent telemetry",
                "CPU");
            LogService.Info(
                "performance",
                $"LIANLI_FAST_HEALTH_HIT ageMs={(DateTime.UtcNow - initialHealth.UpdatedUtc).TotalMilliseconds:0} hid={present}");
        }
        else
        {
            var infoTask = Task.Run(LianLiSlInfinityService.Detect, token);
            var tempTask = Task.Run(HardwareTelemetryBridgeService.ReadBestTemperature, token);
            await Task.WhenAll(infoTask, tempTask);
            token.ThrowIfCancellationRequested();

            present = (await infoTask).Present;
            temp = await tempTask;
            LogService.Info("performance", "LIANLI_COLD_FALLBACK directDetect=true");
        }

        var foregroundPerf = System.Diagnostics.Stopwatch.StartNew();
        ContentStack.Children.Add(BuildFanDashboard(present, temp));
        long dashboardUiMs = foregroundPerf.ElapsedMilliseconds;

        ContentStack.Children.Add(BuildFanRpmSurface(present, profile));
        long rpmUiMs = foregroundPerf.ElapsedMilliseconds - dashboardUiMs;

        LogService.Info(
            "performance",
            $"LIANLI_FOREGROUND_PARTS dashboardUiMs={dashboardUiMs} rpmUiMs={rpmUiMs}");

        var rgbHost = new Grid();
        rgbHost.Children.Add(new TextBlock
        {
            Text = "Načítám RGB Studio…",
            FontSize = 12,
            Foreground = TextSecondaryBrush(),
            Margin = new Thickness(2, 4, 2, 4)
        });
        ContentStack.Children.Add(rgbHost);

        SafeTaskService.Forget(RefreshLianLiStatusAsync(), "lianli", "Deferred fan controller refresh");
        SafeTaskService.Forget(RunFanTelemetryLoopAsync(token), "lianli", "Fan CPU temperature telemetry");
        SafeTaskService.Forget(
            LoadLianLiRgbStudioDeferredAsync(rgbHost, present, profile, token),
            "lianli",
            "Deferred RGB Studio render");
    }

    private async Task LoadLianLiRgbStudioDeferredAsync(
        Grid host,
        bool enabled,
        FanProfile profile,
        CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (_section != "lianli")
            return;

        var perf = System.Diagnostics.Stopwatch.StartNew();
        var rgbUi = BuildRgbStudioSurface(enabled, profile);
        long shellUiMs = perf.ElapsedMilliseconds;

        token.ThrowIfCancellationRequested();
        if (_section != "lianli")
            return;

        host.Children.Clear();
        host.Children.Add(rgbUi.Surface);

        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (_section != "lianli")
            return;

        var previewPerf = System.Diagnostics.Stopwatch.StartNew();
        if (_lianLiRgbPreview != null)
            _lianLiRgbPreview.Child = BuildFanRingPreview();
        long previewUiMs = previewPerf.ElapsedMilliseconds;

        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (_section != "lianli")
            return;

        const int batchSize = 2;
        long maxBatchUiMs = 0;
        long tileUiMs = 0;
        var batchPerf = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < LianLiEffects.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (_section != "lianli")
                return;

            int index = i;
            LianLiEffect effect = LianLiEffects[i];
            var tile = new Button
            {
                Tag = index,
                MinWidth = 118,
                Height = 68,
                Padding = new Thickness(8),
                IsEnabled = enabled
            };
            var tileBody = new StackPanel { Spacing = 5 };
            tileBody.Children.Add(new Border
            {
                Height = 20,
                CornerRadius = new CornerRadius(5),
                Background = BuildEffectGradient(
                    effect.PreviewA,
                    effect.PreviewB)
            });
            tileBody.Children.Add(new TextBlock
            {
                Text = effect.Name,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            tile.Content = tileBody;
            tile.Click += (_, _) => rgbUi.EffectBox.SelectedIndex = index;
            rgbUi.ModeRow.Children.Add(tile);

            if ((i + 1) % batchSize == 0)
            {
                long batchMs = batchPerf.ElapsedMilliseconds;
                tileUiMs += batchMs;
                maxBatchUiMs = Math.Max(maxBatchUiMs, batchMs);
                batchPerf.Restart();
                await Task.Yield();
            }
        }

        long tailBatchMs = batchPerf.ElapsedMilliseconds;
        tileUiMs += tailBatchMs;
        maxBatchUiMs = Math.Max(maxBatchUiMs, tailBatchMs);
        long totalMs = perf.ElapsedMilliseconds;

        LogService.Info(
            "performance",
            $"LIANLI_RGB_BATCHED shellUiMs={shellUiMs} previewUiMs={previewUiMs} tileUiMs={tileUiMs} maxBatchUiMs={maxBatchUiMs} totalMs={totalMs} effects={LianLiEffects.Length}");
        SafeTaskService.Forget(
            RunRgbPreviewLoopAsync(token),
            "lianli",
            "Fan RGB preview animation");
    }

    private Border BuildFanDashboard(bool present, CpuTemperatureService.CpuTemperatureSample temp)
    {
        var body = new StackPanel { Spacing = 12 };

        _lianLiStatus = new TextBlock
        {
            Text = present
                ? "Připojeno • Lian Li UNI FAN SL-Infinity • Direct HID"
                : "Lian Li controller 0CF2:A102 nebyl nalezen.",
            Foreground = present ? SuccessBrush() : WarningBrush(),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        body.Children.Add(_lianLiStatus);

        var cards = new Grid { ColumnSpacing = 10 };
        for (int i = 0; i < 4; i++) cards.ColumnDefinitions.Add(new ColumnDefinition());

        cards.Children.Add(BuildFanMetricCard("CONTROLLER", present ? "ONLINE" : "OFFLINE", "USB 0CF2:A102", 0));
        cards.Children.Add(BuildFanMetricCard("RPM RANGE", "500–2100", "bezpečný rozsah", 1));

        var cpuStack = new StackPanel { Spacing = 2 };
        cpuStack.Children.Add(new TextBlock { Text = "CPU TEMP", FontSize = 9.5, CharacterSpacing = 95, Foreground = AccentBrush() });
        _lianLiCpuTempText = new TextBlock
        {
            Text = temp.Celsius.HasValue ? $"{temp.Celsius.Value:0.0} °C" : "N/A",
            FontSize = 25,
            FontWeight = FontWeights.Bold,
            Foreground = temp.Celsius.HasValue ? TextPrimaryBrush() : WarningBrush()
        };
        _lianLiCpuSensorText = new TextBlock
        {
            Text = temp.Celsius.HasValue ? temp.Sensor : "CPU senzor nedostupný",
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        cpuStack.Children.Add(_lianLiCpuTempText);
        cpuStack.Children.Add(_lianLiCpuSensorText);

        var cpuCard = new Border
        {
            Padding = new Thickness(13, 10, 13, 10),
            CornerRadius = new CornerRadius(11),
            Background = SurfaceElevatedBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1),
            Child = cpuStack
        };
        Grid.SetColumn(cpuCard, 2);
        cards.Children.Add(cpuCard);
        cards.Children.Add(BuildFanMetricCard("MAX FAN", "2100 RPM", "SL-Infinity profil", 3));
        body.Children.Add(cards);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var refresh = new Button { Content = "Obnovit zařízení" };
        ApplyPillButtonAppearance(refresh);
        refresh.Click += (_, _) => SafeTaskService.Forget(RefreshLianLiStatusAsync(), "lianli", "Refresh fan controller");
        footer.Children.Add(refresh);
        _lianLiAgentStatusText = new TextBlock
        {
            Text = "Fan Agent: načítám stav…",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };
        footer.Children.Add(_lianLiAgentStatusText);
        body.Children.Add(footer);

        return BuildSectionSurface("usb", "Ventilátory", "SL-Infinity • RPM • CPU teplota • RGB Studio • bez L-Connect", body);
    }

    private Border BuildFanMetricCard(string label, string value, string sub, int column)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 9.5, CharacterSpacing = 95, Foreground = AccentBrush() });
        stack.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = TextPrimaryBrush() });
        stack.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = TextSecondaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis });

        var card = new Border
        {
            Padding = new Thickness(13, 10, 13, 10),
            CornerRadius = new CornerRadius(11),
            Background = SurfaceElevatedBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1),
            Child = stack
        };
        Grid.SetColumn(card, column);
        return card;
    }
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
            Background = SurfaceAltBrush(),
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
            stack.Children.Add(new TextBlock { Text = $"KANÁL {i + 1}", FontSize = 9.5, CharacterSpacing = 90, Foreground = AccentBrush() });
            var rpmText = new TextBlock { Text = "— RPM", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = TextPrimaryBrush() };
            _lianLiRpmTexts[i] = rpmText;
            stack.Children.Add(rpmText);
            var card = new Border
            {
                Padding = new Thickness(11, 8, 11, 8),
                CornerRadius = new CornerRadius(9),
                Background = SurfaceAltBrush(),
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
        ApplyPillButtonAppearance(apply);
        apply.Background = ButtonVioletBrush();
        apply.Foreground = OnAccentTextBrush();
        apply.Click += (_, _) =>
        {
            int rpm = (int)(Math.Round(slider.Value / 50d) * 50d);
            FanProfile saved = FanProfileService.Load() with
            {
                TargetRpm = rpm,
                MotherboardPwm = false,
                CpuTempTargetEnabled = false
            };
            FanProfileService.Save(saved);
            SafeTaskService.Forget(
                RunLianLiActionAsync(() => LianLiSlInfinityService.SetAllFanRpm(rpm),
                    $"Ventilátory nastaveny na {rpm} RPM • profil uložen"),
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
            FanProfileService.Save(saved);
            SafeTaskService.Forget(
                RunLianLiActionAsync(LianLiSlInfinityService.SetAllMotherboardPwm, "Řízení předáno základní desce • profil uložen"),
                "lianli", "Enable motherboard PWM");
        };

        var auto65 = new Button
        {
            Content = profile.CpuTempTargetEnabled ? "AUTO 65 °C • ZAPNUTO" : "AUTO ≤65 °C",
            IsEnabled = enabled
        };
        ApplyPillButtonAppearance(auto65);
        if (profile.CpuTempTargetEnabled)
        {
            auto65.Background = ButtonVioletBrush();
            auto65.Foreground = OnAccentTextBrush();
        }
        auto65.Click += (_, _) =>
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

            auto65.Content = turnOn ? "AUTO 65 °C • ZAPNUTO" : "AUTO ≤65 °C";
            auto65.Background = turnOn ? ButtonVioletBrush() : null;
            auto65.Foreground = turnOn ? OnAccentTextBrush() : TextPrimaryBrush();

            if (turnOn)
            {
                FanProfileService.StartAgentIfNeeded();
                SetFanStatus("AUTO 65 °C aktivní • minimum 1620 RPM • otáčky se budou průběžně přizpůsobovat CPU teplotě", true);
            }
            else
            {
                SetFanStatus("AUTO 65 °C vypnuto • zůstává poslední nastavená rychlost", true);
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
        aiSmart.Toggled += (_, _) =>
        {
            FanProfile current = FanProfileService.Load();
            FanProfileService.Save(current with { AiSmartRuntimeEnabled = aiSmart.IsOn });
            if (aiSmart.IsOn)
                FanProfileService.StartAgentIfNeeded();

            SetFanStatus(
                aiSmart.IsOn
                    ? "AI Smart Runtime aktivní • lokální predikce, anomaly detection, smart fan/power"
                    : "AI Smart Runtime vypnutý • klasická AUTO65/power pravidla zůstávají",
                true);
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
        body.Children.Add(auto);

        var curveGrid = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        curveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        curveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        curveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        curveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        curveGrid.Children.Add(new TextBlock { Text = "#", Foreground = TextSecondaryBrush(), FontSize = 10 });
        var tempHead = new TextBlock { Text = "CPU TEPLOTA", Foreground = AccentBrush(), FontSize = 9.5, CharacterSpacing = 80 };
        Grid.SetColumn(tempHead, 1);
        curveGrid.Children.Add(tempHead);
        var rpmHead = new TextBlock { Text = "CÍLOVÉ RPM", Foreground = AccentBrush(), FontSize = 9.5, CharacterSpacing = 80 };
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
        ApplyPillButtonAppearance(apply);
        apply.Background = ButtonVioletBrush();
        apply.Foreground = OnAccentTextBrush();
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

            FanProfile saved = FanProfileService.Load() with
            {
                AutoCurveEnabled = auto.IsOn,
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
            FanProfileService.Save(saved);

            if (saved.AutoCurveEnabled)
            {
                FanProfileService.StartAgentIfNeeded();
                CpuTemperatureService.CpuTemperatureSample sample = HardwareTelemetryBridgeService.ReadBestTemperature();
                int target = sample.Celsius is double c
                    ? FanProfileService.CalculateCurveRpm(saved, c)
                    : saved.TargetRpm;
                SafeTaskService.Forget(
                    RunLianLiActionAsync(() => LianLiSlInfinityService.SetAllFanRpm(target),
                        $"Auto křivka zapnuta • aktuální cíl {target} RPM"),
                    "lianli", "Apply auto fan curve");
            }
            else
            {
                SetFanStatus("Auto křivka vypnuta • profil uložen", true);
            }
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

    private (Border Surface, StackPanel ModeRow, ComboBox EffectBox) BuildRgbStudioSurface(
        bool enabled,
        FanProfile profile)
    {
        var body = new StackPanel { Spacing = 12 };

        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });

        var effects = new StackPanel { Spacing = 8 };
        effects.Children.Add(new TextBlock { Text = "LIGHTING EFFECT", FontSize = 9.5, CharacterSpacing = 100, Foreground = AccentBrush() });

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
            Background = SurfaceAltBrush(),
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
        ApplyPillButtonAppearance(apply);
        apply.Background = ButtonVioletBrush();
        apply.Foreground = OnAccentTextBrush();
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
            FanProfileService.Save(saved);

            SafeTaskService.Forget(
                RunLianLiActionAsync(
                    () => LianLiSlInfinityService.SetAllEffect(effect.Mode, speedValue, directionValue, r, g, b, brightnessValue),
                    $"{effect.Name} • {hex.Text.ToUpperInvariant()} • {brightnessValue} % • profil uložen"),
                "lianli", "Apply RGB effect");
        };
        var sync = new Button { Content = "ARGB Sync z desky", IsEnabled = enabled };
        ApplyPillButtonAppearance(sync);
        sync.Click += (_, _) =>
        {
            FanProfileService.Save(FanProfileService.Load() with { ArgbSync = true });
            SafeTaskService.Forget(
                RunLianLiActionAsync(() => LianLiSlInfinityService.SetArgbSync(true), "ARGB sync zapnut • profil uložen"),
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

    private async Task RunFanTelemetryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _section == "lianli")
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
                if (_section != "lianli" || _lianLiCpuTempText == null) return;

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

    private async Task RunRgbPreviewLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _section == "lianli")
        {
            await Task.Delay(40, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) break;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "lianli" || _lianLiPreviewBrush == null || !_lianLiPreviewAnimated) return;
                _lianLiPreviewPhase = (_lianLiPreviewPhase + 1) % 120;
                double t = (Math.Sin(_lianLiPreviewPhase * Math.PI / 60d - Math.PI / 2d) + 1d) * 0.5d;
                Color a = ParseColor(_lianLiPreviewA);
                Color b = ParseColor(_lianLiPreviewB);
                _lianLiPreviewBrush.Color = LerpColor(a, b, t);
                UpdateFanRingPreview(a, b);
            });
        }
    }
    private async Task RefreshLianLiStatusAsync()
    {
        var info = await Task.Run(LianLiSlInfinityService.Detect);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_lianLiStatus == null) return;
            SetFanStatus(info.Present
                ? "Připojeno • Lian Li UNI FAN SL-Infinity • Direct HID"
                : "Lian Li controller 0CF2:A102 nebyl nalezen.", info.Present);
        });
    }

    private async Task RunLianLiActionAsync(Action action, string success)
    {
        try
        {
            await Task.Run(action).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(() => SetFanStatus(success, true));
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Direct controller action failed", ex);
            DispatcherQueue.TryEnqueue(() => SetFanStatus("Ventilátory: " + ex.Message, false));
        }
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
