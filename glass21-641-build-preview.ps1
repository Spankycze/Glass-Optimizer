$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item artifacts\glass21-641 -ItemType Directory -Force|Out-Null
& .\ai-workflow.ps1 -Action checkpoint -TaskId glass21-fidelity-641 -Phase implementation -Summary 'Reference proportions and low-tint actual backdrop implemented; Windows/Privacy featured panels use real shared sources with positive-state inversion only where required.' -NextAction 'Fresh build, compare compositor screenshot and native pending/discard, then full suites and gated release.' -FilesTouched @('MainWindow.Glass21.cs','MainWindow.Shell.cs','MainWindow.cs','MainWindow.TweakVirtualization.cs','App.xaml') -Pending @('Actual fidelity screenshot','Build/test','Release/install')
& .\build-fast.ps1 *> artifacts\glass21-641\build-fast.log
if(!$?){throw 'Build failed'}
& .\resolve-debug-exe.ps1
