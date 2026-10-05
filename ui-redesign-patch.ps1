$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$src=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer'
$backup=Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign\backup'
function Edit([string]$name,[scriptblock]$change) {
 $path=Join-Path $src $name
 $raw=[IO.File]::ReadAllText($path)
 $stamp=(Get-FileHash $path).Hash
 if(-not(Test-Path (Join-Path $backup $name))){Copy-Item $path (Join-Path $backup $name)}
 $script:code=$raw
 & $change
 if((Get-FileHash $path).Hash -ne $stamp){throw "Concurrent edit detected: $name"}
 [IO.File]::WriteAllText($path,$script:code,[Text.UTF8Encoding]::new($false))
 Write-Output "UPDATED $name"
}
function Swap([string]$old,[string]$new) {
 if(-not $script:code.Contains($old)){throw "Expected source missing: $old"}
 $script:code=$script:code.Replace($old,$new)
}
function Re([string]$pattern,[string]$new) {
 if(-not [regex]::IsMatch($script:code,$pattern)){throw "Expected pattern missing: $pattern"}
 $script:code=[regex]::Replace($script:code,$pattern,$new)
}
$nl=[Environment]::NewLine
Edit 'MainWindow.Theme.cs' {
 foreach($property in @('TextPrimary','TextSecondary','Surface','SurfaceElevated','SurfaceAlt','Border','Accent','AccentSoft','PrimaryButton','PrimaryButtonSoft','PrimaryButtonBorder','Info')){
  Swap ("PaletteBrush(_themePalette."+$property+")") ("PaletteBrush(SectionPalette."+$property+")")
 }
 Swap 'private static SolidColorBrush NetworkAccentBrush() => _themePreference switch' 'private static SolidColorBrush NetworkAccentBrush() => UseNeutralSectionChrome ? ThemeColor(192,192,192) : _themePreference switch'
 Swap 'ThemeColor(alpha, 3, 2, 10)' 'ThemeColor(alpha, 0, 0, 0)'
 Swap '=> _darkTheme ? (_sectionDarkCardBackground ??= OverviewCardBackgroundBrush()) : SurfaceElevatedBrush();' '=> UseNeutralSectionChrome ? NeutralCardSurface() : (_darkTheme ? (_sectionDarkCardBackground ??= OverviewCardBackgroundBrush()) : SurfaceElevatedBrush());'
 Swap '=> _darkTheme ? (_sectionDarkBorder ??= OverviewNeonBorderBrush()) : BorderThemeBrush();' '=> UseNeutralSectionChrome ? NeutralCardEdge() : (_darkTheme ? (_sectionDarkBorder ??= OverviewNeonBorderBrush()) : BorderThemeBrush());'
 Re '(private static Brush SectionHeaderBackgroundBrush\(\)\s*\{)' ('$1'+$nl+'        if (UseNeutralSectionChrome) return ThemeColor(8,8,8);')
 $anchor='resources["AccentFillColorDisabled"] = Windows.UI.Color.FromArgb(102, _themePalette.PrimaryButton.R, _themePalette.PrimaryButton.G, _themePalette.PrimaryButton.B);'
 Swap $anchor ($anchor+$nl+'        ApplyNeutralControlResources(resources);')
}
Edit 'MainWindow.Shell.cs' {
 $start=$script:code.IndexOf('    private static Brush SidebarButtonBackground(bool active)')
 $end=$script:code.IndexOf('    private ', $script:code.IndexOf('    private static Brush SidebarButtonBorder(bool active)')+30)
 if($start -lt 0 -or $end -lt $start){throw 'Sidebar methods not found'}
 $replacement=@'
    private static Brush SidebarButtonBackground(bool active)
    {
        if (!_darkTheme) return active ? AccentSoftBrush() : SurfaceElevatedBrush();
        return active
            ? (_sidebarActiveBackground ??= NeutralDepthBrush(47, 31))
            : (_sidebarInactiveBackground ??= ThemeColor(0, 0, 0, 0));
    }

    private static Brush SidebarHoverBackground()
        => !_darkTheme ? SurfaceElevatedBrush() : (_sidebarHoverBackground ??= ThemeColor(27,27,27));

    private static Brush SidebarButtonBorder(bool active)
        => !_darkTheme ? BorderThemeBrush() : active
            ? (_sidebarActiveBorder ??= NeutralDepthBrush(85,55))
            : (_sidebarInactiveBorder ??= ThemeColor(0,0,0,0));

'@
 $script:code=$script:code.Substring(0,$start)+$replacement+$nl+$script:code.Substring($end)
 Swap 'MinHeight = preview ? 52 : 52,' 'MinHeight = 42,'
 Swap 'Spacing = 11,' 'Spacing = 9,'
 Swap 'Padding = preview ? new Thickness(10, 7, 12, 7) : new Thickness(12, 8, 13, 8),' 'Padding = new Thickness(10, 6, 10, 6),'
 Swap 'CornerRadius = new CornerRadius(12),' 'CornerRadius = new CornerRadius(8),'
 Swap 'button.BorderThickness = active ? new Thickness(1.5, 1.35, 3.4, 1.35) : new Thickness(1.15);' 'button.BorderThickness = new Thickness(1);'
 Swap 'ThemeColor(72, 84, 104)' 'ThemeColor(70,70,70)'
 Swap 'ThemeColor(213, 220, 231)' 'ThemeColor(192,192,192)'
 Swap 'ThemeColor(224, 230, 239)' 'ThemeColor(232,232,232)'
}
Edit 'MainWindow.Dashboard.cs' {
 Swap 'BorderThickness = new Thickness(_darkTheme ? 1.55 : 1),' 'BorderThickness = new Thickness(1),'
 Swap 'CornerRadius = new CornerRadius(14),' 'CornerRadius = new CornerRadius(12),'
 Swap 'AttachHoverMotion(card, 1.004f);' 'if (!UseNeutralSectionChrome) AttachHoverMotion(card, 1.004f);'
}
Edit 'MainWindow.DashboardModern.cs' {
 Re '(private Border BuildModernSectionHero\([\s\S]*?UIElement\? actions = null\)\s*\{)' ('$1'+$nl+'        if (UseNeutralSectionChrome) return BuildNeutralSectionIntro(title, subtitle, actions);')
 Swap 'Background = ButtonVioletBrush(),' 'Background = UseNeutralSectionChrome ? ThemeColor(128,128,128) : ButtonVioletBrush(),'
 Swap 'BorderThickness = new Thickness(_darkTheme ? 1.25 : 1),' 'BorderThickness = new Thickness(UseNeutralSectionChrome ? 0 : (_darkTheme ? 1.25 : 1)),'
 Swap 'Padding = new Thickness(11, 8, 11, 8),' 'Padding = UseNeutralSectionChrome ? new Thickness(4, 10, 4, 8) : new Thickness(11, 8, 11, 8),'
}
Edit 'MainWindow.Sections.cs' {
 Re '(private Border BuildSectionHero\([^\r\n]+\)\s*\{)' ('$1'+$nl+'        if (UseNeutralSectionChrome) return BuildNeutralSectionIntro(title, subtitle, actions);')
 Swap 'PaletteBrush(_themePalette.SectionMarker)' 'PaletteBrush(SectionPalette.SectionMarker)'
 Swap 'BorderThickness = new Thickness(_darkTheme ? 1.15 : 1)' 'BorderThickness = new Thickness(1)'
 Swap 'MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis' 'MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis'
}
Edit 'MainWindow.NetworkDashboard.cs' {
 Swap 'var root = new Grid { ColumnSpacing = 24, MinHeight = 176 };' 'var root = new Grid { ColumnSpacing = 18, MinHeight = UseNeutralSectionChrome ? 112 : 176 };'
 Swap 'root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });' 'root.ColumnDefinitions.Add(new ColumnDefinition { Width = UseNeutralSectionChrome ? new GridLength(0) : new GridLength(320) });'
 Swap 'ScheduleNetworkHeroIllustration(token);' 'if (!UseNeutralSectionChrome) ScheduleNetworkHeroIllustration(token);'
 Swap '        left.Children.Add(badges);' '        if (!UseNeutralSectionChrome) left.Children.Add(badges);'
 Swap '            Width = 300,' '            Width = UseNeutralSectionChrome ? 0 : 300,'
}
Edit 'MainWindow.StyledDialogs.cs' {
 Swap 'ThemeColor(190, 198, 211)' 'ThemeColor(176,176,176)'
 Swap '=> _darkTheme ? SurfaceElevatedBrush() : SurfaceAltBrush();' '=> _darkTheme ? ThemeColor(24,24,24) : SurfaceAltBrush();'
 Swap 'ThemeColor(66, 72, 82)' 'ThemeColor(56,56,56)'
 Swap 'ThemeColor(92, 100, 112)' 'ThemeColor(88,88,88)'
 Swap 'Background = _darkTheme ? SurfaceBrush() : SectionCardBackgroundBrush(),' 'Background = _darkTheme ? NeutralCardSurface() : SectionCardBackgroundBrush(),'
 Swap 'BorderThickness = new Thickness(_darkTheme ? 1.35 : 1),' 'BorderThickness = new Thickness(1),'
 Swap 'CornerRadius = new CornerRadius(24),' 'CornerRadius = new CornerRadius(14),'
 Swap 'Padding = new Thickness(27, 25, 27, 24),' 'Padding = new Thickness(24, 22, 24, 22),'
 Swap 'neutral.Color.R != 66 || neutral.Color.G != 72 || neutral.Color.B != 82' 'neutral.Color.R != 56 || neutral.Color.G != 56 || neutral.Color.B != 56'
 Swap 'Margin = new Thickness(11, 13, 0, 0),' 'Margin = new Thickness(4, 6, 0, 0),'
 Swap 'CornerRadius = new CornerRadius(25),' 'CornerRadius = new CornerRadius(15),'
 Swap 'ThemeColor(255, 2, 3, 5)' 'ThemeColor(255, 5, 5, 5)'
 Swap 'Width = 39,' 'Width = 32,'
 Swap 'Height = 39,' 'Height = 32,'
 Swap 'Margin = new Thickness(0, 14, 14, 0),' 'Margin = new Thickness(0, 12, 12, 0),'
 Swap 'CornerRadius = new CornerRadius(12),' 'CornerRadius = new CornerRadius(8),'
 Swap '            AttachHoverMotion(close, 1.035f);' '            // Neutral close button uses color feedback without scaling.'
}
Edit 'MainWindow.cs' {
 Swap '        AttachHoverMotion(b, 1.012f);' '        if (!UseNeutralSectionChrome) AttachHoverMotion(b, 1.012f);'
 Swap '        AttachHoverMotion(card, 1.006f);' '        if (!UseNeutralSectionChrome) AttachHoverMotion(card, 1.006f);'
 Swap '        AttachHoverMotion(row, 1.002f); ToolTipService.SetToolTip(row, policy.Detail);' '        if (!UseNeutralSectionChrome) AttachHoverMotion(row, 1.002f); ToolTipService.SetToolTip(row, policy.Detail);'
 Swap '        AttachHoverMotion(icon, 1.025f); grid.Children.Add(icon);' '        if (!UseNeutralSectionChrome) AttachHoverMotion(icon, 1.025f); grid.Children.Add(icon);'
 Swap 'titleRow.Children.Add(new TextBlock { Text = t.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), VerticalAlignment = VerticalAlignment.Center });' 'text.Children.Add(new TextBlock { Text = t.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });'
 Swap 'ThemeColor(196, 206, 255)' 'ThemeColor(224,224,224)'
 Swap 'ThemeColor(132, 139, 156)' 'ThemeColor(152,152,152)'
 Swap '            Background = background,' '            Background = UseNeutralSectionChrome ? ThemeColor(33,33,33) : background,'
 Swap '                Foreground = OnAccentTextBrush()' '                Foreground = UseNeutralSectionChrome ? background : OnAccentTextBrush()'
 Swap '// Compact violet-tinted secondary buttons; never inherit the Windows accent.' '// Compact neutral section buttons; Overview retains its original palette.'
}
Edit 'MainWindow.UiPrimitives.cs' {
 Swap 'button.Background = ButtonVioletBrush();' 'button.Background = UseNeutralSectionChrome ? NeutralPrimarySurface() : ButtonVioletBrush();'
}
Edit 'MainWindow.TweakVirtualization.cs' {
 Swap '            Background = ButtonVioletBrush(),' '            Background = UseNeutralSectionChrome ? ThemeColor(128,128,128) : ButtonVioletBrush(),'
 Swap '            Padding = new Thickness(11, 8, 11, 8),' '            Padding = UseNeutralSectionChrome ? new Thickness(4,10,4,8) : new Thickness(11, 8, 11, 8),'
 Swap '            BorderThickness = new Thickness(_darkTheme ? 1.2 : 1),' '            BorderThickness = new Thickness(UseNeutralSectionChrome ? 0 : (_darkTheme ? 1.2 : 1)),'
}
$props=Join-Path $PSScriptRoot 'Directory.Build.props'
$raw=[IO.File]::ReadAllText($props)
$m=[regex]::Match($raw,'<SpankyVersion>(\d+)\.(\d+)\.(\d+)</SpankyVersion>')
$version=$m.Groups[1].Value+'.'+$m.Groups[2].Value+'.'+([int]$m.Groups[3].Value+1)
[IO.File]::WriteAllText($props,$raw.Replace($m.Value,"<SpankyVersion>$version</SpankyVersion>"),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign\version.txt'),$version)
Write-Output "NEUTRAL_UI_PATCH_OK version=$version"

