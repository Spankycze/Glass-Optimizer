param(
  [Parameter(Mandatory=$true,Position=0)][string]$Path
)
$ErrorActionPreference='Stop'
$full=Resolve-Path (Join-Path $PSScriptRoot $Path)
Get-Content -Raw -LiteralPath $full | & 'C:\Users\spnka\.cargo\bin\tare.exe' skeletonize --path $full
