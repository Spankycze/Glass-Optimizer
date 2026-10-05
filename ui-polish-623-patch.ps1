$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
function Patch($file,$old,$new){$p=Join-Path $PWD ('src\SpankyhoOptimizer\'+$file);$s=[IO.File]::ReadAllText($p);if(!$s.Contains($old)){throw "Missing patch anchor $file"};[IO.File]::WriteAllText($p,$s.Replace($old,$new))}
Patch 'MainWindow.BlackUi.cs' 'FontSize = 30' 'FontSize = 26'
Patch 'MainWindow.BlackUi.cs' 'new Thickness(24, 42, 24, 14)' 'new Thickness(24, 38, 24, 8)'
Patch 'MainWindow.BlackUi.cs' 'Width = 50, Height = 50, Opacity = .35' 'Width = 40, Height = 40, Opacity = .25'
Patch 'MainWindow.BlackUi.cs' 'root.Children.Add(heading);' 'root.Children.Add(heading); root.Children.Add(BlackHardwareVisual(label == "CPU" ? "cpu" : label == "GPU" ? "gpu" : label == "RAM" ? "ram" : "nvme", label == "CPU" ? 0 : label == "GPU" ? 1 : label == "RAM" ? 2 : 3));'
$old='        var secondary = new TextBlock { Text = subtitle, FontSize = 11, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MinHeight = 32 };'
$new=@'
        var secondary = _overviewTextRefs is { } inventory
            ? label switch { "CPU" => inventory.Cpu, "GPU" => inventory.Gpu, "RAM" => inventory.Ram, _ => inventory.Disk }
            : new TextBlock { Text = subtitle };
        secondary.FontSize = 12;
        secondary.FontWeight = FontWeights.SemiBold;
        secondary.Foreground = TextPrimaryBrush();
        secondary.TextWrapping = TextWrapping.Wrap;
        secondary.MinHeight = 34;
'@
Patch 'MainWindow.BlackUi.cs' $old $new
Patch 'MainWindow.BlackUi.cs' '        root.Children.Add(value);' ''
Patch 'MainWindow.BlackUi.cs' '        root.Children.Add(secondary);' '        root.Children.Add(secondary); root.Children.Add(value);'
Patch 'MainWindow.BlackUi.cs' 'Height = 86,' 'Height = 108,'
$p=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.BlackUi.cs';$s=[IO.File]::ReadAllText($p)
$start=$s.IndexOf('    private Border BuildBlackHardwareStrip(');$end=$s.IndexOf('    private StackPanel BlackInfoHeading(', $start)
if($start -lt 0 -or $end -lt 0){throw 'Hardware strip bounds missing'}
$s=$s.Substring(0,$start)+@'
    // Retain the inventory readiness host without building duplicate visual cards.
    // Inventory TextBlocks are now attached directly to the live telemetry cards.
    private Border BuildBlackHardwareStrip(OverviewTextRefs refs, bool compact)
        => new() { Visibility = Visibility.Collapsed };

'@+$s.Substring($end)
[IO.File]::WriteAllText($p,$s)
Patch 'MainWindow.Shell.cs' '? 142d' '? 100d'
Patch 'MainWindow.OverviewDashboard.cs' '            MinHeight = compact ? 250 : 120' '            MinHeight = 0, Visibility = Visibility.Collapsed'
Patch 'MainWindow.OverviewDashboard.cs' 'var telemetry = new Grid { ColumnSpacing = 8 };' 'var telemetry = new Grid { ColumnSpacing = 10, RowSpacing = 10 };'
Patch 'MainWindow.OverviewDashboard.cs' '            for (int i = 0; i < 4; i++) telemetry.ColumnDefinitions.Add(new ColumnDefinition());' @'
            int telemetryColumns = _overviewCacheCompact ? 2 : 4;
            for (int i = 0; i < telemetryColumns; i++) telemetry.ColumnDefinitions.Add(new ColumnDefinition());
            telemetry.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (telemetryColumns == 2) telemetry.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
'@
Patch 'MainWindow.OverviewDashboard.cs' '                        Grid.SetColumn(card, index);' '                        Grid.SetColumn(card, index % telemetryColumns); Grid.SetRow(card, index / telemetryColumns);'
Patch 'MainWindow.NeutralUi.cs' 'var stack = new StackPanel { Spacing = 7 };' 'var stack = new StackPanel { Spacing = 5 };'
Patch 'MainWindow.NeutralUi.cs' 'Padding = new Thickness(0, 12, 0, 22)' 'Padding = new Thickness(0, 8, 0, 16)'
Patch 'MainWindow.NeutralUi.cs' 'var stack = new StackPanel { Spacing = 14 };' 'var stack = new StackPanel { Spacing = 12 };'
Patch 'MainWindow.NeutralUi.cs' 'Padding = new Thickness(18,16,18,16)' 'Padding = new Thickness(16)'
Patch 'MainWindow.cs' 'CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, UseCompactSectionChrome ? 9 : 7), Padding = UseCompactSectionChrome ? new Thickness(16,14,16,14)' 'CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, UseCompactSectionChrome ? 10 : 7), Padding = UseCompactSectionChrome ? new Thickness(16,12,16,12)'
Patch 'MainWindow.cs' 't.Pending != t.Current ? "SpankyAccentBrush"' 't.Pending != t.Current ? "SpankyWarningBrush"'
Patch 'MainWindow.cs' "tweakDefinition.Pending != tweakDefinition.Current`r`n                    ? `"SpankyAccentBrush`"" "tweakDefinition.Pending != tweakDefinition.Current`r`n                    ? `"SpankyWarningBrush`""
Patch 'MainWindow.cs' '// Compact neutral section buttons; Overview retains its original palette.' '// Shared compact buttons across every page.'
# Quick actions: preserve their normal label and show a busy state during the await.
Patch 'MainWindow.cs' @'
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                LogService.Error($"Quick action failed:
'@ @'
            object normalContent = b.Content;
            b.IsEnabled = false;
            b.Content = "Pracuji\u2026";
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                LogService.Error($"Quick action failed:
'@
Patch 'MainWindow.cs' @'
            finally
            {
                Interlocked.Exchange(ref actionBusy, 0);
            }
        };
        return b;
'@ @'
            finally
            {
                b.Content = normalContent;
                b.IsEnabled = true;
                Interlocked.Exchange(ref actionBusy, 0);
            }
        };
        return b;
'@
Patch 'MainWindow.StyledDialogs.cs' 'Math.Min(resolvedMaxHeight, 260)' 'Math.Min(resolvedMaxHeight, 180)'
Patch 'MainWindow.StyledDialogs.cs' 'Padding = new Thickness(24, 22, 24, 22)' 'Padding = new Thickness(20)'
Patch 'MainWindow.StyledDialogs.cs' 'var grid = new Grid { RowSpacing = 18 };' 'var grid = new Grid { RowSpacing = 14 };'
Patch 'MainWindow.StyledDialogs.cs' 'Spacing = 10, Margin = new Thickness(0, 13, 0, 0)' 'Spacing = 8, Margin = new Thickness(0, 8, 0, 0)'
Patch 'MainWindow.StyledDialogs.cs' 'Height = 38,' 'Height = 34,'
Patch 'MainWindow.StyledDialogs.cs' 'FontSize = 23,' 'FontSize = 21,'
Patch 'MainWindow.StyledDialogs.cs' 'if (!hideDefaultCancel && !string.IsNullOrWhiteSpace(secondaryText))' 'if (!string.IsNullOrWhiteSpace(secondaryText))'
Patch 'MainWindow.StyledDialogs.cs' @'
            try
            {
                await action();
            }
            finally
            {
                Interlocked.Exchange(ref actionBusy, 0);
            }
'@ @'
            object normalContent = b.Content;
            b.IsEnabled = false;
            b.Content = "Pracuji\u2026";
            try
            {
                await action();
            }
            finally
            {
                b.Content = normalContent;
                b.IsEnabled = true;
                Interlocked.Exchange(ref actionBusy, 0);
            }
'@
Write-Output 'UI_POLISH_PATCH_OK'
