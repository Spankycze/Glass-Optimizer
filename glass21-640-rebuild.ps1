$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$helper=Join-Path $PWD 'glass21-640-close-preview.ps1'
$p=Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"')) -PassThru
if(!$p.WaitForExit(10000) -or $p.ExitCode -ne 0){throw 'Preview close failed'}
& .\ai-workflow.ps1 -Action checkpoint -TaskId glass21-639 -Phase staged-lock-recovered -Summary '640 color-only rebuild was blocked by own staged preview PID19760; gracefully closed preview and retrying fresh build. Native OFF/Discard already proven.' -NextAction 'Fresh640 test and true-blue capture before final release.' -Pending @('Final640 verification and install') -Evidence @('artifacts\glass21-640\installed-ui-result.json')
& .\glass21-640-build.ps1
