$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item 'artifacts\glass21-640' -ItemType Directory -Force|Out-Null
Copy-Item 'artifacts\glass21-639\release-note.txt' 'artifacts\glass21-640\release-note.txt' -Force
$n=Join-Path $PWD 'artifacts\glass21-640\release-note.txt'
$s=[IO.File]::ReadAllText($n).Replace('15.4.639','15.4.640')+'Native toggle correction: pending OFF state now uses the standard WinUI switch visual/automation lifecycle.'
[IO.File]::WriteAllText($n,$s)
$b=[IO.File]::ReadAllText((Join-Path $PWD 'glass21-639-build.ps1')).Replace('artifacts\glass21-639','artifacts\glass21-640').Replace('GLASS21639','GLASS21640')
[IO.File]::WriteAllText((Join-Path $PWD 'glass21-640-build.ps1'),$b)
$r=[IO.File]::ReadAllText((Join-Path $PWD 'glass21-639-release.ps1')).Replace('glass21-639','glass21-640').Replace('15.4.639','15.4.640').Replace('V15_4_639','V15_4_640').Replace('GLASS21639','GLASS21640')
[IO.File]::WriteAllText((Join-Path $PWD 'glass21-640-release.ps1'),$r)
& .\ai-workflow.ps1 -Action checkpoint -TaskId glass21-639 -Phase native-toggle-correction -Summary '639 installed; actual pending-state capture showed custom toggle visual can remain ON after OFF selection. Replace with native ToggleSwitch, retain bindings and policy;640 pending verification.' -NextAction 'Fresh640 full UI regression, actual OFF/Discard proof, gated640 release/install.' -FilesTouched @('src\SpankyhoOptimizer\App.xaml') -Done @('639 glass installed and fan/HID verified','Actual Apply/Discard flow checked') -Pending @('640 native-toggle build/test','Final640 release/install and screenshot') -Evidence @('artifacts\glass21-639\installed-ui-result.json','artifacts\glass21-639\installed-system-dock.png')
& .\glass21-640-build.ps1
