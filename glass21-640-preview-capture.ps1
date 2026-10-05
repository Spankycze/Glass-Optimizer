$ErrorActionPreference='Stop'
try{
Set-Location $PSScriptRoot
Get-Process SpankyhoOptimizer -ErrorAction SilentlyContinue|Where-Object {$_.MainWindowHandle -ne 0}|ForEach-Object {$null=$_.CloseMainWindow()}
Start-Sleep -Seconds 1
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
if((Get-Item $exe).VersionInfo.ProductVersion -notlike '15.4.640*'){throw 'Installed version not639'}
$p=Start-Process $exe -ArgumentList '--open-section=system' -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 5
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GlassCaptureNative {
 [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
'@
[GlassCaptureNative]::ShowWindow([GlassCaptureNative]::GetConsoleWindow(),0)|Out-Null
$c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)
$root=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Spankyho Optimizer')))
if(!$root){throw 'Installed UI window missing'}
function Capture($name){
 [GlassCaptureNative]::SetCursorPos([int]$root.Current.BoundingRectangle.X+30,[int]$root.Current.BoundingRectangle.Y+18)|Out-Null
 [GlassCaptureNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle)|Out-Null
 Start-Sleep -Milliseconds 300
 $rect=$root.Current.BoundingRectangle
 $bmp=New-Object Drawing.Bitmap([int]$rect.Width,[int]$rect.Height)
 $gfx=[Drawing.Graphics]::FromImage($bmp)
 $gfx.CopyFromScreen([int]$rect.X,[int]$rect.Y,0,0,$bmp.Size)
 $bmp.Save((Join-Path $PWD ('artifacts\glass21-640\'+$name+'.png')),[Drawing.Imaging.ImageFormat]::Png)
 $gfx.Dispose();$bmp.Dispose()
}
function InvokeName($name){
 $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name)
 $elements=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
 foreach($el in $elements){$pat=$null;if($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pat)){$pat.Invoke();return}}
 throw "Invoke unavailable $name"
}
Capture 'installed-system'
$toggleCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty,$true)
$toggles=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$toggleCondition)
$game=$null
foreach($el in $toggles){if($el.Current.Name -eq 'Game Mode' -and $el.Current.IsEnabled){$game=$el;break}}
if(!$game){throw 'Real Game Mode toggle unavailable'}
$toggle=$game.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$before=$toggle.Current.ToggleState
try{
 $toggle.Toggle()
 Start-Sleep -Milliseconds 500
 $selected=$toggle.Current.ToggleState
 if($selected -eq $before){throw 'Pending switch did not change'}
 Capture 'installed-system-dock'
 $buttons=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
 foreach($b in $buttons){if($b.Current.Name -match '^Zru'){($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke();break}}
 Start-Sleep -Milliseconds 200
 if($toggle.Current.ToggleState -ne $before){throw 'Discard failed'}
}finally{
 if($toggle.Current.ToggleState -ne $before){$toggle.Toggle()}
}
InvokeName 'Power Plan'
Start-Sleep -Seconds 2
Capture 'installed-power'
$buttons=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
foreach($b in $buttons){if($b.Current.Name -match '^P.*ehled$'){($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke();break}}
Start-Sleep -Seconds 2
Capture 'installed-overview'
$buttons=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
foreach($b in $buttons){if($b.Current.Name -match 'tweaky'){($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke();break}}
@{version=(Get-Item $exe).VersionInfo.ProductVersion;pid=$root.Current.ProcessId;beforeState=$before.ToString();pendingState=$selected.ToString();togglePendingChanged=$true;discardVerified=$true;windowsApplyInvoked=$false;captures=@('installed-system','installed-system-dock','installed-power','installed-overview')}|ConvertTo-Json|Set-Content 'artifacts\glass21-640\installed-ui-result.json'
}catch{$_|Out-String|Set-Content (Join-Path $PSScriptRoot 'artifacts\glass21-640\installed-ui-error.txt');exit 1}