$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\Assets\SidebarIcons'
$backup=Join-Path $PSScriptRoot 'artifacts\black-ui-621\before\icons'
New-Item $backup -ItemType Directory -Force|Out-Null
Copy-Item "$dir\*.svg" $backup -Force
$icons=@{
 system='<g stroke="#d2b66e" stroke-width="3"><path d="M18 8v6m9-6v6m10-6v6m9-6v6M18 50v6m9-6v6m10-6v6m9-6v6M8 18h6m-6 9h6m-6 10h6m-6 9h6m36-28h6m-6 9h6m-6 10h6m-6 9h6"/></g><rect x="14" y="14" width="36" height="36" rx="5" fill="url(#metal)" stroke="#edf5ff"/><rect x="21" y="21" width="22" height="22" rx="3" fill="url(#blue)" stroke="#8addff"/><path d="M25 28h14M25 32h9M25 36h14" stroke="#c8f6ff" opacity=".6"/>'
 gpu='<path d="M7 21h51v23H7z" fill="url(#green)" stroke="#97d9ad"/><path d="M5 17h53v23H5z" fill="url(#dark)" stroke="#90a0b5"/><path d="M11 18h41v4H11z" fill="url(#metal)"/><g fill="#18232d" stroke="#a7b9c8" stroke-width="2"><circle cx="21" cy="31" r="9"/><circle cx="43" cy="31" r="9"/></g><g stroke="#748da1" stroke-width="3"><path d="m16 26 10 10m-10 0 10-10m12 0 10 10m-10 0 10-10"/></g><g fill="#acc2d4"><circle cx="21" cy="31" r="3"/><circle cx="43" cy="31" r="3"/></g><path d="M13 45h30v4H13z" fill="#d6b85a"/><path d="M3 17v32m0-2h7" stroke="#b6c6d9" stroke-width="3"/>'
 storage='<rect x="8" y="20" width="48" height="25" rx="4" fill="url(#green)" stroke="#8fe0bb"/><g fill="url(#dark)" stroke="#516276"><rect x="17" y="24" width="11" height="16" rx="2"/><rect x="32" y="24" width="11" height="16" rx="2"/></g><path d="M50 23h7v18h-7z" fill="#d4ae57"/><path d="M53 24v15" stroke="#715a2c"/><circle cx="11" cy="32" r="3" fill="#141b1f" stroke="#cad0d2"/><path d="M18 27h7m10 0h5" stroke="#64768a"/>'
 ram='<rect x="5" y="22" width="54" height="22" rx="3" fill="url(#green)" stroke="#96ddb2"/><g fill="url(#dark)" stroke="#526a73"><rect x="9" y="26" width="9" height="13" rx="1"/><rect x="21" y="26" width="9" height="13" rx="1"/><rect x="34" y="26" width="9" height="13" rx="1"/><rect x="46" y="26" width="9" height="13" rx="1"/></g><path d="M9 44h20v5H9m25-5h21v5H34" fill="#dfbc67"/><path d="M12 44v5m5-5v5m5-5v5m16-5v5m5-5v5m5-5v5m5-5v5" stroke="#785b28"/>'
 usb='<path d="M24 8h16v19H24z" fill="url(#metal)" stroke="#eef6ff"/><path d="M28 12h4v7h-4m6-7h3v7h-3" fill="#405167"/><path d="M22 26h20v23a10 10 0 0 1-20 0z" fill="url(#blue)" stroke="#8ddaff"/><path d="M27 31h10v14H27z" fill="#173a6c"/><path d="M32 56v5" stroke="#a7b6c7" stroke-width="3"/>'
 lianli='<rect x="10" y="10" width="44" height="44" rx="7" fill="url(#dark)" stroke="#a4b0c3"/><circle cx="32" cy="32" r="17" fill="#111b27" stroke="#59cbe6" stroke-width="2"/><g fill="url(#blue)"><path d="M31 28c-14-15-20-6-9 5l9 1z"/><path d="M36 31c15-14 6-20-5-9l-1 9z"/><path d="M33 36c14 15 20 6 9-5l-9-1z"/><path d="M28 33c-15 14-6 20 5 9l1-9z"/></g><circle cx="32" cy="32" r="5" fill="url(#metal)"/><g fill="#9babbc"><circle cx="15" cy="15" r="2"/><circle cx="49" cy="15" r="2"/><circle cx="15" cy="49" r="2"/><circle cx="49" cy="49" r="2"/></g>'
 overview='<rect x="6" y="10" width="37" height="30" rx="4" fill="url(#metal)" stroke="#dae7f6"/><rect x="10" y="14" width="29" height="22" rx="2" fill="url(#blue)"/><path d="m13 29 8-8 6 5 9-10" fill="none" stroke="#aeefff" stroke-width="2"/><path d="M22 40v8H13v4h25v-4H29v-8" fill="url(#metal)"/><rect x="47" y="11" width="12" height="42" rx="3" fill="url(#dark)" stroke="#93aac0"/><circle cx="53" cy="24" r="4" stroke="#65cdf4" fill="#0e2434"/><circle cx="53" cy="39" r="4" stroke="#54c6dd" fill="#0e2434"/>'
 network='<rect x="10" y="27" width="44" height="23" rx="5" fill="url(#metal)" stroke="#d9e9f9"/><path d="M14 28V13m36 15V13" stroke="#7395ac" stroke-width="4" stroke-linecap="round"/><path d="M23 18q9-8 18 0m-14 4q5-4 10 0" fill="none" stroke="#68cffa" stroke-width="3" stroke-linecap="round"/><circle cx="32" cy="26" r="2" fill="#7fdffc"/><path d="M16 37h19v6H16" fill="#1a3c5b"/><g fill="#5bdd9a"><circle cx="41" cy="41" r="2"/><circle cx="47" cy="41" r="2"/></g>'
 power='<rect x="18" y="8" width="28" height="48" rx="5" fill="url(#dark)" stroke="#a1b5c9"/><path d="M24 15h16v6H24" fill="#6eb686"/><path d="m34 24-11 15h9l-2 12 13-18H33z" fill="#e6c26d" stroke="#ffe5a1"/>'
 components='<rect x="10" y="10" width="44" height="44" rx="4" fill="url(#green)" stroke="#7ac6a1"/><rect x="20" y="20" width="23" height="23" rx="3" fill="url(#metal)"/><rect x="25" y="25" width="13" height="13" rx="2" fill="url(#blue)"/><path d="M13 15h20m13 0h4M15 46h10m8 3h17M15 23v15m35-16v15" stroke="#d1b460" stroke-width="2"/>'
 startup='<rect x="10" y="9" width="44" height="45" rx="5" fill="url(#dark)" stroke="#9eb1c8"/><path d="M15 14h34v5H15" fill="#73c49c"/><path d="m26 25 16 11-16 11z" fill="url(#green)" stroke="#acf0c8"/>'
 ui='<path d="M5 21h22l6-6h26v36H5z" fill="url(#gold)" stroke="#ffe2a4"/><path d="M5 20v-7h19l7 7" fill="#d3983f"/><path d="M10 29h42v19H10z" fill="#dfb860"/><path d="M11 24h39" stroke="#fff1b7" stroke-width="2"/>'
 update='<rect x="10" y="10" width="44" height="44" rx="5" fill="url(#dark)" stroke="#8caac6"/><g fill="none" stroke="#5cb6ef" stroke-width="5"><path d="M44 24a14 14 0 0 0-24-3m0 0v-9m0 9h9M20 40a14 14 0 0 0 24 3m0 0v9m0-9h-9"/></g>'
 spicetify='<circle cx="32" cy="32" r="23" fill="url(#green)" stroke="#b0efc2"/><g fill="none" stroke="#0d3226" stroke-width="4" stroke-linecap="round"><path d="M17 24q15-7 30 2M19 33q13-5 26 2M21 41q11-4 21 2"/></g>'
 apps='<g stroke="#bdd7f7"><rect x="9" y="9" width="21" height="21" rx="4" fill="url(#blue)"/><rect x="34" y="9" width="21" height="21" rx="4" fill="url(#green)"/><rect x="9" y="34" width="21" height="21" rx="4" fill="url(#gold)"/><rect x="34" y="34" width="21" height="21" rx="4" fill="url(#metal)"/></g>'
 privacy='<path d="m32 8 21 8v17c0 12-12 20-21 24-9-4-21-12-21-24V16z" fill="url(#blue)" stroke="#a9e0ff"/><rect x="23" y="28" width="18" height="16" rx="3" fill="url(#metal)"/><path d="M26 28v-5a6 6 0 0 1 12 0v5" fill="none" stroke="#e1f4ff" stroke-width="3"/><circle cx="32" cy="34" r="2" fill="#204966"/><path d="M32 35v4" stroke="#204966" stroke-width="2"/>'
 defender='<path d="m32 8 21 8v17c0 12-12 20-21 24-9-4-21-12-21-24V16z" fill="url(#green)" stroke="#b0efd0"/><path d="m22 32 7 8 15-17" fill="none" stroke="#e7fff5" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>'
 ai='<rect x="13" y="13" width="38" height="38" rx="8" fill="url(#dark)" stroke="#80d7d2"/><g stroke="#80d7d2" stroke-width="2"><path d="M24 6v7m16-7v7M24 51v7m16-7v7M6 24h7m-7 16h7m38-16h7m-7 16h7M24 25l16 14m-16 0 16-14m-8-3v20"/></g><g fill="#7ddbd0"><circle cx="24" cy="25" r="3"/><circle cx="40" cy="25" r="3"/><circle cx="24" cy="39" r="3"/><circle cx="40" cy="39" r="3"/><circle cx="32" cy="32" r="4"/></g>'
 debloat='<rect x="17" y="17" width="30" height="38" rx="4" fill="url(#metal)" stroke="#e4ecf6"/><path d="M13 17h38M24 17V9h16v8" fill="none" stroke="#afc0d5" stroke-width="4"/><path d="M25 25v21m7-21v21m7-21v21" stroke="#718da2" stroke-width="3"/>'
 fixer='<path d="M40 10a13 13 0 0 0-16 17L10 43a7 7 0 0 0 10 10l17-15a13 13 0 0 0 17-16L44 32l-12-2-2-12z" fill="url(#metal)" stroke="#d9e8f5"/><circle cx="16" cy="48" r="3" fill="#38546c"/>'
 powershell='<rect x="7" y="13" width="50" height="38" rx="5" fill="url(#blue)" stroke="#a1d9fd"/><path d="m18 23 9 9-10 10m17 1h12" fill="none" stroke="#e8f6ff" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>'
 settings='<path d="m27 7 10 0 2 7 6 3 7-2 5 9-5 5v7l5 5-5 9-7-2-6 3-2 7H27l-2-7-6-3-7 2-5-9 5-5v-7l-5-5 5-9 7 2 6-3z" fill="url(#metal)" stroke="#dae5f3"/><circle cx="32" cy="32" r="12" fill="url(#dark)" stroke="#90a9bd"/><circle cx="32" cy="32" r="6" fill="url(#blue)"/>'
 browsers='<circle cx="32" cy="32" r="24" fill="url(#blue)" stroke="#ade2ff"/><g stroke="#e9f6ff" fill="none" stroke-width="2"><ellipse cx="32" cy="32" rx="10" ry="24"/><path d="M9 25h46M9 39h46M32 8v48"/></g>'
 cursor='<path d="M15 8v45l12-12 8 16 10-5-9-16h17z" fill="url(#metal)" stroke="#f2f7fc" stroke-width="2"/>'
}
$defs='<defs><linearGradient id="metal" x2=".25" y2="1"><stop stop-color="#eff4fb"/><stop offset=".45" stop-color="#a1b4c8"/><stop offset="1" stop-color="#53677e"/></linearGradient><linearGradient id="blue" x2=".3" y2="1"><stop stop-color="#80defd"/><stop offset=".5" stop-color="#348fc9"/><stop offset="1" stop-color="#195086"/></linearGradient><linearGradient id="green" x2=".2" y2="1"><stop stop-color="#88d8ae"/><stop offset=".5" stop-color="#338662"/><stop offset="1" stop-color="#155540"/></linearGradient><linearGradient id="dark" x2=".2" y2="1"><stop stop-color="#63798e"/><stop offset=".5" stop-color="#293b4b"/><stop offset="1" stop-color="#142332"/></linearGradient><linearGradient id="gold" x2=".3" y2="1"><stop stop-color="#ffe4a0"/><stop offset=".5" stop-color="#d2a34c"/><stop offset="1" stop-color="#95692d"/></linearGradient></defs>'
foreach($name in $icons.Keys){
 $svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">'+$defs+'<ellipse cx="32" cy="58" rx="22" ry="3" fill="#000000" opacity=".25"/>'+$icons[$name]+'</svg>'
 [xml]$check=$svg
 [IO.File]::WriteAllText((Join-Path $dir ($name+'.svg')),$svg,[Text.UTF8Encoding]::new($false))
}
$f='src\SpankyhoOptimizer\MainWindow.ShellHelpers.cs';$t=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $f))
$t=$t.Replace('["lianli"] = "usb"','["lianli"] = "lianli"')
$t=$t.Replace('["memory"] = "system",','["memory"] = "ram",'+[Environment]::NewLine+'            ["ram"] = "ram",')
$t=$t.Replace('["settings"] = "settings"','["cursor"] = "cursor",'+[Environment]::NewLine+'            ["settings"] = "settings"')
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $f),$t,[Text.UTF8Encoding]::new($false))
$f='src\SpankyhoOptimizer\MainWindow.Shell.cs';$t=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $f)).Replace('SidebarIcon(section, 20)','SidebarIcon(section, 26)')
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $f),$t,[Text.UTF8Encoding]::new($false))
$f='src\SpankyhoOptimizer\MainWindow.BlackUi.cs';$t=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $f)).Replace('label == "DISK" ? "storage" : "system"','label == "DISK" ? "storage" : label == "RAM" ? "ram" : "system"')
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $f),$t,[Text.UTF8Encoding]::new($false))
$f='src\SpankyhoOptimizer\Services\AppThemeEngine.cs';$t=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $f))
Copy-Item (Join-Path $PSScriptRoot $f) 'artifacts\black-ui-621\before\AppThemeEngine.cs'
$pattern='(?s)private static readonly AppThemePalette GraphiteDark = new\(.*?\);'
$replacement='private static readonly AppThemePalette GraphiteDark = new(
        new(242,242,242), new(176,176,176),
        new(16,16,16), new(25,25,25), new(8,8,8), new(48,48,48),
        new(211,211,211), new(32,32,32), new(188,188,188), new(112,112,112),
        new(82,82,82), new(29,29,29), new(117,117,117),
        new(112,184,140), new(221,176,88), new(192,192,192),
        new(25,25,25), new(20,20,20), new(16,16,16));'
$t=[regex]::Replace($t,$pattern,$replacement)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $f),$t,[Text.UTF8Encoding]::new($false))
Write-Output "HARDWARE_ICONS_OK count=$($icons.Count)"
