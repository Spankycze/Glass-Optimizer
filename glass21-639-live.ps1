$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
Get-Process SpankyhoOptimizer -ErrorAction SilentlyContinue|Where-Object {$_.MainWindowHandle -ne 0}|ForEach-Object {$null=$_.CloseMainWindow()}
Start-Sleep -Seconds 1
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
$p=Start-Process $exe -ArgumentList '--open-section=system' -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 5
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)
$root=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
if(!$root){throw 'App window missing'}
Add-Type -AssemblyName System.Drawing
$rect=$root.Current.BoundingRectangle
$bmp=New-Object Drawing.Bitmap([int]$rect.Width,[int]$rect.Height)
$gfx=[Drawing.Graphics]::FromImage($bmp)
$gfx.CopyFromScreen([int]$rect.X,[int]$rect.Y,0,0,$bmp.Size)
$bmp.Save((Join-Path $PWD 'artifacts\glass21-639\actual-system.png'),[Drawing.Imaging.ImageFormat]::Png)
$gfx.Dispose();$bmp.Dispose()
"ACTUAL_WINDOW_CAPTURE pid=$($p.Id)"|Set-Content 'artifacts\glass21-639\actual-result.txt'
