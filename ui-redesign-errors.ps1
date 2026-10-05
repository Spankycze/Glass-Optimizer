$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$report=& {
$ver=Get-Content 'artifacts\ui-neutral-redesign\avalonia-version.json' -Raw | ConvertFrom-Json
"AVALONIA_LATEST "+(@($ver.versions | Where-Object {$_ -notmatch '-'})[-1])
Get-ChildItem 'C:\ProgramData\SpankyhoOptimizer' -File -Recurse | Where-Object {$_.Extension -in @('.log','.txt') -and $_.LastWriteTime -gt (Get-Date).AddMinutes(-8)} | Sort-Object LastWriteTime -Descending | Select-Object -First 5 | ForEach-Object { $_.FullName; Get-Content $_.FullName -Tail 35 }
} | Out-String -Width 220
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign\errors.txt'),$report)
Write-Output 'ERROR_READ_OK'
