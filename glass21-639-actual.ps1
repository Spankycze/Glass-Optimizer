$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
$p=Start-Process $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 4
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)
$root=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
if(!$root){throw 'App window missing'}
$buttons=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
foreach($b in $buttons){if($b.Current.Name -match 'tweaky|Tweaks'){($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke();break}}
Start-Sleep -Seconds 2
Add-Type -AssemblyName System.Drawing
$rect=$root.Current.BoundingRectangle
$bmp=New-Object Drawing.Bitmap([int]$rect.Width,[int]$rect.Height)
$gfx=[Drawing.Graphics]::FromImage($bmp)
$gfx.CopyFromScreen([int]$rect.X,[int]$rect.Y,0,0,$bmp.Size)
$bmp.Save((Join-Path $PWD 'artifacts\glass21-639\actual-system.png'),[Drawing.Imaging.ImageFormat]::Png)
$gfx.Dispose();$bmp.Dispose()
Write-Output "ACTUAL_WINDOW_CAPTURE pid=$($p.Id)"
