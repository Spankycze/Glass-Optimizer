using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;
namespace SpankyhoOptimizer;
public sealed partial class MainWindow
{
    private async System.Threading.Tasks.Task RunNeutralStartupTitlebarSmokeAsync()
    {
        await ShowSectionAsync("network");
        AppTitleBar.Children.Clear();
        AppTitleBar.ColumnDefinitions.Clear();
        _customTitleBarPrimed = false;
        _startupTitlebarThemeCompleted = false;
        ActivateCustomTitleBarNow();
        for (int i = 0; i < 80 && !_startupTitlebarThemeCompleted; i++)
            await System.Threading.Tasks.Task.Delay(25);
        if (!_startupTitlebarThemeCompleted)
            throw new InvalidOperationException("Deferred persistent header did not complete");
        ValidateNeutralUiRegressionSmoke();
        LogService.Info("theme", "NEUTRAL_STARTUP_HEADER_SMOKE_OK section=network");
    }

    private void ValidateNeutralUiRegressionSmoke()
    {
        var errors = new List<string>();
        try
        {
            foreach (string page in new[] { "network", "settings", "storage", "gpu" })
                if (Math.Abs(CalculateSectionHeroHeight(page, 1320) - ShellTitleBarHeight) > 0.1)
                    throw new InvalidOperationException("Non-Overview page still reserves a decorative hero: " + page);
            bool overview = string.Equals(_section, "overview", StringComparison.OrdinalIgnoreCase);
            if (HeaderCard.Visibility != (overview ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed))
                throw new InvalidOperationException("Hero visibility did not follow navigation");
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        try
        {
            if (_titleBarLogoImage == null || AppTitleBar.Children.Count == 0)
                throw new InvalidOperationException("Persistent title brand is missing");
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        if (_darkTheme)
        {
            string originalSection = _themeSection;
            try
            {
                _themeSection = "network";
                var tweak = new TweakDefinition
                {
                    Id="smoke.neutral.badge", Section="network", Group="Smoke", Name="Badge refresh",
                    Description="Non-mutating UI regression", Recommended=true, RequiresAdmin=false,
                    IsLoaded=true, Current=false, Pending=false
                };
                var card = CreateTweakCard(tweak);
                var refs = (ClassicTweakCardRefs)((Grid)card.Child).Tag;
                tweak.Pending = true;
                RefreshClassicTweakCard(card);
                AssertReadableNeutralBadge(refs.StateBadge, refs.StateText);
                // Cached cards keep their own appearance even after navigation elsewhere.
                _themeSection = "overview";
                tweak.Current = true;
                RefreshClassicTweakCard(card);
                AssertReadableNeutralBadge(refs.StateBadge, refs.StateText);
                AssertReadableNeutralBadge(refs.VerdictBadge, refs.VerdictText);
            }
            catch (Exception ex) { errors.Add(ex.Message); }
            finally { _themeSection = originalSection; }
        }
        try
        {
            var resources = Microsoft.UI.Xaml.Application.Current.Resources;
            var foreground = (SolidColorBrush)resources["ButtonForegroundDisabled"];
            var background = (SolidColorBrush)resources["ButtonBackgroundDisabled"];
            static double Luminance(Windows.UI.Color color)
            {
                static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
                return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
            }
            double fg = Luminance(foreground.Color), bg = Luminance(background.Color);
            double contrast = (Math.Max(fg, bg) + .05) / (Math.Min(fg, bg) + .05);
            if (contrast < 4.5) throw new InvalidOperationException("Disabled label contrast below 4.5:1: " + contrast);
            var model = new TweakDefinition { Id="smoke.ui.state", Section="system", Name="State", IsLoaded=true, Current=false, Pending=false };
            void Expect(string expected)
            {
                var row = new TweakVirtualRowViewModel(model, TweakViewMode.Diagnostics, _ => { }, SectionIconSource("system"));
                var card = CreateTweakCard(model);
                var refs = (ClassicTweakCardRefs)((Grid)card.Child).Tag;
                if (row.StatusText != expected || refs.StateText.Text != expected)
                    throw new InvalidOperationException("Classic/virtual state mismatch: " + expected);
                RefreshClassicTweakCard(card); row.Refresh(TweakViewMode.Diagnostics, force:true);
                if (row.StatusText != expected || refs.StateText.Text != expected)
                    throw new InvalidOperationException("Readback state mismatch: " + expected);
            }
            Expect("NEAKTIVN\u00cd");
            model.Pending=true; Expect("\u010cEK\u00c1 NA POU\u017dIT\u00cd");
            model.IsBusy=true; Expect("PRACUJI\u2026");
            model.IsBusy=false; model.LastError="test readback failure"; Expect("CHYBA");
            model.LastError=null; model.RestartRequired=true; model.Status="Ove\u0159eno \u2022 restart"; Expect("VY\u017dADUJE RESTART");
            model.RestartRequired=false; model.Status="Detected"; model.Current=true;
            model.LastVerifiedUtc=DateTime.UtcNow; Expect("AKTIVN\u00cd \u2022 OV\u011a\u0158ENO");
            model.IsLoaded=false; Expect("NEDOSTUPN\u00c9");
            LogService.Info("theme", $"CONTROL_CONTRAST_STATE_SMOKE_OK contrast={contrast:F2} states=7 classicAndVirtual=true");
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(" | ", errors));
        LogService.Info("theme", "NEUTRAL_UI_REGRESSION_SMOKE_OK titlebar=true badgeRefresh=true");
    }

    private static void AssertReadableNeutralBadge(Border badge, TextBlock text)
    {
        if (badge.Background is not SolidColorBrush bg || bg.Color.R != 10 || bg.Color.G != 10 || bg.Color.B != 10)
            throw new InvalidOperationException("Classic badge lost neutral background after readback");
        if (text.Foreground is not SolidColorBrush fg || fg.Color == bg.Color)
            throw new InvalidOperationException("Classic badge text disappeared after readback");
    }
}

