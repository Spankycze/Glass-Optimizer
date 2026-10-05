$ErrorActionPreference='SilentlyContinue'
$p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$d='HKCU:\Software\Microsoft\Windows\DWM'
$hc='HKCU:\Control Panel\Accessibility\HighContrast'
$changed=$false
New-Item $p -Force|Out-Null
New-Item $d -Force|Out-Null
New-Item $hc -Force|Out-Null
function Set-DwordIfDiff($path,$name,[uint32]$value){
  $cur=(Get-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue).$name
  if($null -eq $cur -or [uint32]$cur -ne $value){
    Set-ItemProperty -Path $path -Name $name -Value $value -Type DWord
    $script:changed=$true
  }
}
Set-DwordIfDiff $p 'AppsUseLightTheme' 0
Set-DwordIfDiff $p 'SystemUsesLightTheme' 0
Set-DwordIfDiff $p 'EnableTransparency' 1
Set-DwordIfDiff $d 'ColorPrevalence' 0
Set-DwordIfDiff $d 'ColorizationColor' ([uint32]3288365268)
Set-DwordIfDiff $d 'AccentColor' ([uint32]4292114432)
$inactive=(Get-ItemProperty -Path $d -Name 'AccentColorInactive' -ErrorAction SilentlyContinue).AccentColorInactive
if($null -ne $inactive){Remove-ItemProperty -Path $d -Name 'AccentColorInactive' -ErrorAction SilentlyContinue;$changed=$true}
$flags=(Get-ItemProperty -Path $hc -Name 'Flags' -ErrorAction SilentlyContinue).Flags
if([string]$flags -ne '0'){Set-ItemProperty -Path $hc -Name 'Flags' -Value '0';$changed=$true}
if($changed){
  Start-Process rundll32.exe -ArgumentList 'user32.dll,UpdatePerUserSystemParameters 1,True' -WindowStyle Hidden -Wait
}