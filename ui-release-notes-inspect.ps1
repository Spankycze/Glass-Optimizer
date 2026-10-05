$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$out=foreach($dir in @($PSScriptRoot,'src\SpankyhoOptimizer','src\SpankyhoOptimizer\Services')){
 Get-ChildItem $dir -File | Where-Object {$_.Name -match 'changelog|release.*note|version|change.*log'} | Select-Object -ExpandProperty FullName
}
[IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign\release-notes-files.txt'),@($out))
Write-Output 'NOTES_FILES_OK'