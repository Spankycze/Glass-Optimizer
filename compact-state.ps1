$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$resume=Join-Path $root 'RESUME.md'
$project=Join-Path $root 'PROJECT_STATE.md'
$r=Get-Content -Raw -LiteralPath $resume
$p=Get-Content -Raw -LiteralPath $project
$projectHead=(($p -split '(?m)^## Recent source changes')[0]).Trim()
$out=@(
'# CURRENT_STATE - compact',
'Generated: '+(Get-Date -Format o),
'',
'## Current work',
$r.Trim(),
'',
'## Project snapshot',
$projectHead,
'',
'## Rules',
'- SESSION_HISTORY.md and WORK_PROGRESS.md are history; read only targeted excerpts.',
'- Full PROJECT_STATE.md is secondary to RESUME.md for current work.'
) -join [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $root 'CURRENT_STATE.md') -Value $out -Encoding utf8
Write-Host 'CURRENT_STATE_OK'
