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
    private readonly SemaphoreSlim _lianLiActionGate = new(1, 1);

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
        int generation = _navigationGeneration;
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
            if (generation != _navigationGeneration || _section != "lianli") return;

            present = (await infoTask).Present;
            temp = await tempTask;
            LogService.Info("performance", "LIANLI_COLD_FALLBACK directDetect=true");
        }

        if (!present)
            return;

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

        SafeTaskService.Forget(RefreshLianLiStatusAsync(generation), "lianli", "Deferred fan controller refresh");
        SafeTaskService.Forget(RunFanTelemetryLoopAsync(token, generation), "lianli", "Fan CPU temperature telemetry");
        SafeTaskService.Forget(
            LoadLianLiRgbStudioDeferredAsync(rgbHost, present, profile, token, generation),
            "lianli",
            "Deferred RGB Studio render");
    }

    private async Task LoadLianLiRgbStudioDeferredAsync(
        Grid host,
        bool enabled,
        FanProfile profile,
        CancellationToken token,
        int generation)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "lianli" || !ContentStack.Children.Contains(host))
            return;

        var perf = System.Diagnostics.Stopwatch.StartNew();
        var rgbUi = BuildRgbStudioSurface(enabled, profile);
        long shellUiMs = perf.ElapsedMilliseconds;

        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "lianli" || !ContentStack.Children.Contains(host))
            return;

        host.Children.Clear();
        host.Children.Add(rgbUi.Surface);

        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "lianli" || !ContentStack.Children.Contains(host))
            return;

        var previewPerf = System.Diagnostics.Stopwatch.StartNew();
        if (_lianLiRgbPreview != null)
            _lianLiRgbPreview.Child = BuildFanRingPreview();
        long previewUiMs = previewPerf.ElapsedMilliseconds;

        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "lianli" || !ContentStack.Children.Contains(host))
            return;

        const int batchSize = 2;
        long maxBatchUiMs = 0;
        long tileUiMs = 0;
        var batchPerf = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < LianLiEffects.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "lianli" || !ContentStack.Children.Contains(host))
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
            RunRgbPreviewLoopAsync(token, generation),
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
        cpuStack.Children.Add(new TextBlock { Text = "CPU TEMP", FontSize = 10.5, CharacterSpacing = 95, Foreground = AccentBrush() });
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
            Background = _darkTheme ? GlassSurfaceElevatedBrush() : SurfaceElevatedBrush(),
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
        refresh.Click += (_, _) => SafeTaskService.Forget(RefreshLianLiStatusAsync(_navigationGeneration), "lianli", "Refresh fan controller");
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
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10.5, CharacterSpacing = 95, Foreground = AccentBrush() });
        stack.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = TextPrimaryBrush() });
        stack.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = TextSecondaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis });

        var card = new Border
        {
            Padding = new Thickness(13, 10, 13, 10),
            CornerRadius = new CornerRadius(11),
            Background = _darkTheme ? GlassSurfaceElevatedBrush() : SurfaceElevatedBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1),
            Child = stack
        };
        Grid.SetColumn(card, column);
        return card;
    }
}
