using System;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace SpankyhoOptimizer.Services;

internal static class UiResourceService
{
    private static readonly ConcurrentDictionary<string, object> Cache = new(StringComparer.Ordinal);

    public static T Get<T>(string key) where T : class
    {
        if (Cache.TryGetValue(key, out var cached) && cached is T hit) return hit;
        var root = Application.Current?.Resources;
        if (root != null)
        {
            if (TryGet(root, key, out T? value)) { Cache[key] = value; return value; }
            foreach (var merged in root.MergedDictionaries)
                if (TryGet(merged, key, out value)) { Cache[key] = value; return value; }
        }
        var fallback = Cache.GetOrAdd(key, CreateFallback);
        if (fallback is T typed) return typed;
        throw new InvalidOperationException($"UI resource '{key}' has wrong type.");
    }

    private static bool TryGet<T>(ResourceDictionary dictionary, string key, out T? value) where T : class
    {
        try
        {
            if (dictionary.ContainsKey(key) && dictionary[key] is T typed)
            {
                value = typed;
                return true;
            }
        }
        catch { }
        value = null;
        return false;
    }

    private static object CreateFallback(string key) => key switch
    {
        "SpankyVirtualizingItemsPanel" => Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel AreStickyGroupHeadersEnabled='False'/></ItemsPanelTemplate>"),
        "StartupRowTemplateV209" => Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Margin='0,0,0,7' Padding='11,9' CornerRadius='10' Background='{ThemeResource SpankyRowSurfaceBrush}' BorderBrush='{ThemeResource SpankyRowBorderBrush}' BorderThickness='1'><Grid ColumnSpacing='11'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><Image Width='33' Height='33' Source='ms-appx:///Assets/SidebarIcons/startup.svg' Stretch='Uniform' VerticalAlignment='Center'/><StackPanel Grid.Column='1' Spacing='2' VerticalAlignment='Center'><TextBlock Text='{Binding Name}' FontWeight='SemiBold' FontSize='13.5' TextWrapping='Wrap'/><TextBlock Text='{Binding Subtitle}' FontSize='11.2' Opacity='.68' TextWrapping='Wrap'/><TextBlock Text='{Binding FilePath}' FontSize='10.6' Opacity='.55' TextWrapping='Wrap' MaxLines='1' TextTrimming='CharacterEllipsis'/></StackPanel><StackPanel Grid.Column='2' Orientation='Horizontal' Spacing='8' VerticalAlignment='Center'><TextBlock Text='{Binding StateText}' FontSize='10.2' Opacity='.72' VerticalAlignment='Center'/><ToggleSwitch IsOn='{Binding Enabled,Mode=TwoWay}' IsEnabled='{Binding CanEdit}' AutomationProperties.Name='{Binding Name}' OffContent='' OnContent='' MinWidth='50'/></StackPanel></Grid></Border></DataTemplate>"),
        "DeviceInventoryRowTemplateV137" => Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding}' FontSize='12' TextWrapping='Wrap' Margin='2,5'/></DataTemplate>"),
        "ServiceRowTemplateV136" => Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Padding='10,8' ColumnSpacing='13'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='150'/></Grid.ColumnDefinitions><StackPanel Spacing='2'><TextBlock Text='{Binding DisplayName}' FontWeight='SemiBold' FontSize='13.5'/><TextBlock Text='{Binding Subtitle}' FontSize='11.2' Opacity='0.60'/><TextBlock Text='{Binding SafetyText}' FontSize='10.6' Opacity='0.72' TextWrapping='Wrap'/></StackPanel><ComboBox Grid.Column='1' Width='145' SelectedIndex='{Binding SelectedIndex,Mode=TwoWay}' VerticalAlignment='Center'><ComboBoxItem Content='Automaticky'/><ComboBoxItem Content='Ručně'/><ComboBoxItem Content='Zakázáno'/></ComboBox></Grid></DataTemplate>"),
        "TaskRowTemplateV136" => Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Margin='0,0,0,8' Padding='12,10' CornerRadius='10' Background='{ThemeResource SpankyRowSurfaceBrush}' BorderBrush='{ThemeResource SpankyRowBorderBrush}' BorderThickness='1'><Grid ColumnSpacing='12'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><Border Width='31' Height='31' CornerRadius='8' Background='{ThemeResource SpankyRowAccentSurfaceBrush}'><FontIcon Glyph='&#xE823;' FontFamily='Segoe Fluent Icons' FontSize='21'/></Border><StackPanel Grid.Column='1' Spacing='2'><TextBlock Text='{Binding Name}' FontWeight='SemiBold' FontSize='14'/><TextBlock Text='{Binding Path}' FontSize='11' Opacity='0.62' TextWrapping='Wrap'/><TextBlock Text='{Binding SafetyText}' FontSize='10.6' Opacity='0.75' TextWrapping='Wrap'/></StackPanel><StackPanel Grid.Column='2' Spacing='4' VerticalAlignment='Center'><Border Padding='7,3' CornerRadius='5' Background='{ThemeResource SpankyRowBadgeSurfaceBrush}'><TextBlock Text='{Binding StateText}' FontSize='9' FontWeight='SemiBold'/></Border><Border Padding='7,3' CornerRadius='5' Background='{ThemeResource SpankyRowAccentSurfaceBrush}'><TextBlock Text='{Binding ProtectionText}' FontSize='8.5' FontWeight='SemiBold'/></Border></StackPanel><ToggleSwitch Grid.Column='3' IsOn='{Binding Enabled,Mode=TwoWay}' IsEnabled='{Binding CanEdit}' AutomationProperties.Name='{Binding Name}' OffContent='' OnContent='' VerticalAlignment='Center'/></Grid></Border></DataTemplate>"),
        "DebloatRowTemplateV136" => Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Padding='10,8' ColumnSpacing='13'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><CheckBox IsChecked='{Binding Keep,Mode=TwoWay}' IsEnabled='{Binding CanEdit}' VerticalAlignment='Center'/><StackPanel Grid.Column='1' Spacing='2'><TextBlock Text='{Binding DisplayName}' FontWeight='SemiBold' FontSize='13.5' TextWrapping='Wrap'/><TextBlock Text='{Binding Subtitle}' FontSize='11.2' Opacity='0.65' TextWrapping='Wrap'/></StackPanel><Border Grid.Column='2' Padding='7,3' CornerRadius='7' Background='{ThemeResource SpankyRowAccentSurfaceBrush}'><TextBlock Text='{Binding BadgeText}' FontSize='10'/></Border></Grid></DataTemplate>"),
        "DebloatRowLogoTemplate" => Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Padding='10,8' ColumnSpacing='13'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><CheckBox IsChecked='{Binding Keep,Mode=TwoWay}' IsEnabled='{Binding CanEdit}' VerticalAlignment='Center'/><Image Grid.Column='1' Source='{Binding IconSource}' Width='33' Height='33' Stretch='Uniform'/><StackPanel Grid.Column='2' Spacing='2'><TextBlock Text='{Binding DisplayName}' FontWeight='SemiBold' FontSize='13.5' TextWrapping='Wrap'/><TextBlock Text='{Binding Subtitle}' FontSize='11.2' Opacity='0.65' TextWrapping='Wrap'/></StackPanel><Border Grid.Column='3' Padding='7,3' CornerRadius='7' Background='{ThemeResource SpankyRowAccentSurfaceBrush}'><TextBlock Text='{Binding BadgeText}' FontSize='10'/></Border></Grid></DataTemplate>"),
        "DebloatRowLogoTemplateV151" => Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Margin='0,0,0,8' Padding='12,10' CornerRadius='10' Background='{ThemeResource SpankyRowSurfaceBrush}' BorderBrush='{ThemeResource SpankyRowBorderBrush}' BorderThickness='1'><Grid ColumnSpacing='12'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><CheckBox IsChecked='{Binding Keep,Mode=TwoWay}' IsEnabled='{Binding CanEdit}' VerticalAlignment='Center'/><Border Grid.Column='1' Width='46' Height='46' CornerRadius='9' Background='{ThemeResource SpankyRowAccentSurfaceBrush}'><Grid><FontIcon Glyph='&#xE71D;' FontFamily='Segoe Fluent Icons' FontSize='20'/><Image Source='{Binding IconSource}' Width='33' Height='33' Stretch='Uniform'/></Grid></Border><StackPanel Grid.Column='2' Spacing='2'><TextBlock Text='{Binding DisplayName}' FontWeight='SemiBold' FontSize='14' TextWrapping='Wrap'/><TextBlock Text='{Binding Subtitle}' FontSize='11.2' Opacity='0.68' TextWrapping='Wrap'/></StackPanel><StackPanel Grid.Column='3' Spacing='4' VerticalAlignment='Center'><Border Padding='8,4' CornerRadius='4' Background='{ThemeResource SpankyRowAccentSurfaceBrush}'><TextBlock Text='{Binding BadgeText}' FontSize='10'/></Border><Border Padding='8,3' CornerRadius='4' Background='{ThemeResource SpankyRowBadgeSurfaceBrush}'><TextBlock Text='{Binding StateText}' FontSize='9' FontWeight='SemiBold'/></Border></StackPanel></Grid></Border></DataTemplate>"),
        "TweakVirtualRowTemplate" => Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Grid HorizontalAlignment='Stretch'><Border Visibility='{Binding CardVisibility}' Background='{Binding CardBackground}' BorderBrush='{Binding CardBorder}' BorderThickness='1' CornerRadius='12' Margin='0,0,0,8' Padding='13,10'><Grid ColumnSpacing='12'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><Border Width='38' Height='38' CornerRadius='10' Background='{ThemeResource SpankyRowSurfaceBrush}' BorderThickness='0' VerticalAlignment='Center'><Grid><FontIcon Glyph='&#xE713;' FontFamily='Segoe Fluent Icons' FontSize='20' Opacity='.42'/><Image Source='{Binding IconSource}' Width='30' Height='30' Stretch='Uniform'/></Grid></Border><StackPanel Grid.Column='1' Spacing='2' VerticalAlignment='Center'><TextBlock Text='{Binding Name}' FontSize='14' FontWeight='SemiBold' Foreground='{Binding TextPrimary}'/><TextBlock Text='{Binding Description}' FontSize='12' Foreground='{Binding TextSecondary}' TextWrapping='Wrap' MaxLines='1' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Meta}' FontSize='10.7' Foreground='{Binding TextSecondary}' TextWrapping='Wrap'/></StackPanel><StackPanel Grid.Column='2' Orientation='Horizontal' Spacing='7' VerticalAlignment='Center'><TextBlock Text='{Binding ToggleStateText}' FontSize='11' FontWeight='SemiBold' Opacity='0.72' VerticalAlignment='Center'/><ToggleButton IsChecked='{Binding Pending,Mode=TwoWay}' IsEnabled='{Binding CanEdit}' Width='44' Height='22' Padding='0' BorderThickness='0' Background='Transparent' VerticalAlignment='Center'><ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Grid Width='44' Height='22'><VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CheckStates'><VisualState x:Name='Unchecked'><VisualState.Setters><Setter Target='KnobTranslate.X' Value='0'/><Setter Target='Track.Background' Value='{ThemeResource SpankyRowSurfaceBrush}'/><Setter Target='Track.BorderBrush' Value='{ThemeResource SpankyRowBorderBrush}'/></VisualState.Setters></VisualState><VisualState x:Name='Checked'><VisualState.Setters><Setter Target='KnobTranslate.X' Value='22'/><Setter Target='Track.Background' Value='{ThemeResource AccentFillColorDefaultBrush}'/><Setter Target='Track.BorderBrush' Value='{ThemeResource AccentFillColorDefaultBrush}'/></VisualState.Setters></VisualState><VisualState x:Name='Indeterminate'/></VisualStateGroup></VisualStateManager.VisualStateGroups><Border x:Name='Track' Width='44' Height='22' CornerRadius='11' Background='{ThemeResource SpankyRowSurfaceBrush}' BorderBrush='{ThemeResource SpankyRowBorderBrush}' BorderThickness='1'><Border x:Name='Knob' Width='16' Height='16' CornerRadius='8' Background='White' HorizontalAlignment='Left' Margin='3,0'><Border.RenderTransform><TranslateTransform x:Name='KnobTranslate'/></Border.RenderTransform></Border></Border></Grid></ControlTemplate></ToggleButton.Template></ToggleButton></StackPanel></Grid></Border></Grid></DataTemplate>"),
        _ => throw new InvalidOperationException($"Unknown UI resource '{key}'.")
    };

    private static object Load(string xaml)
    {
        try { return XamlReader.Load(xaml); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to create fallback UI resource.", ex);
        }
    }
}




