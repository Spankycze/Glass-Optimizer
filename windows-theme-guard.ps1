$ErrorActionPreference='SilentlyContinue'
Start-Sleep -Seconds 8
$p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$d='HKCU:\Software\Microsoft\Windows\DWM'
New-Item $p -Force|Out-Null
New-Item $d -Force|Out-Null

# Re-assert dark mode and transparency after Explorer/DWM have initialized.
Set-ItemProperty $p AppsUseLightTheme 0 -Type DWord
Set-ItemProperty $p SystemUsesLightTheme 0 -Type DWord
Set-ItemProperty $p EnableTransparency 1 -Type DWord
Set-ItemProperty $d ColorPrevalence 0 -Type DWord
Remove-ItemProperty $d AccentColorInactive -ErrorAction SilentlyContinue

# Force Windows to invalidate cached personalization state.
Set-ItemProperty $p EnableTransparency 0 -Type DWord
Start-Sleep -Milliseconds 250
Set-ItemProperty $p EnableTransparency 1 -Type DWord
Start-Process rundll32.exe -ArgumentList 'user32.dll,UpdatePerUserSystemParameters 1,True' -WindowStyle Hidden -Wait

# Refresh Explorer only if transparency was missing at logon.
$explorer=Get-Process explorer -ErrorAction SilentlyContinue
if($explorer){
  Stop-Process -Name explorer -Force
  Start-Sleep -Milliseconds 500
  Start-Process explorer.exe
}
