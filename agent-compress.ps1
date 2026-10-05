param(
  [Parameter(Mandatory=$true,Position=0)][string]$Path
)
$ErrorActionPreference='Stop'
$env:PYTHONUTF8='1'
$env:PYTHONIOENCODING='utf-8'
$full=Resolve-Path (Join-Path $PSScriptRoot $Path)
& 'C:\Users\spnka\AppData\Roaming\Python\Python314\Scripts\agent-compress.exe' compress $full
