$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
function Patch($file,$old,$new){$p=Join-Path $PWD ('src\SpankyhoOptimizer\'+$file);$s=[IO.File]::ReadAllText($p).Replace("`r`n","`n");$old=$old.Replace("`r`n","`n");$new=$new.Replace("`r`n","`n");if(!$s.Contains($old)){throw "Missing patch anchor $file"};[IO.File]::WriteAllText($p,$s.Replace($old,$new))}
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
