param(
  [string]$Input='.'
)
$ErrorActionPreference='Stop'
$target=Resolve-Path (Join-Path $PSScriptRoot $Input)
& 'C:\Users\spnka\.cargo\bin\tare.exe' perf --input $target
