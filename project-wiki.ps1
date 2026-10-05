param([string]$OutputPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputPath)){$OutputPath=Join-Path $root 'PROJECT_WIKI.md'}
$index=Join-Path $root 'PROJECT_INDEX.json'
$state=Join-Path $root 'PROJECT_STATE.md'
$perf=Join-Path $root 'PERFORMANCE_BASELINE.md'
if(-not(Test-Path $index)){& (Join-Path $root 'project-index.ps1') -Force|Out-Null}
$ix=Get-Content $index -Raw -Encoding UTF8|ConvertFrom-Json
$lines=@()
$lines+='# Spankyho Optimizer Project Wiki'
$lines+=''
$lines+='Generated: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
$lines+=''
$lines+='## Inventory'
$lines+='- Files: '+$ix.counts.files
$lines+='- Classes: '+$ix.counts.classes
$lines+='- Methods: '+$ix.counts.methods
$lines+='- Services: '+$ix.counts.services
$lines+='- Call edges: '+$ix.counts.calls
$lines+='- Hot-pattern references: '+$ix.counts.hotPatterns
$lines+=''
$lines+='## Major services'
$ix.services|Sort-Object name|Select-Object -First 80|ForEach-Object{$lines+='- '+$_.name+' — '+$_.file+':'+$_.line}
$lines+=''
$lines+='## Skills'
Get-ChildItem (Join-Path $root 'skills') -File -Filter '*.md' -ErrorAction SilentlyContinue|Sort-Object Name|ForEach-Object{$lines+='- '+$_.Name}
$lines+=''
$lines+='## Current project state'
if(Test-Path $state){$lines+=@(Get-Content $state -Encoding UTF8|Select-Object -First 45)}
$lines+=''
$lines+='## Performance baseline'
if(Test-Path $perf){$lines+=@(Get-Content $perf -Encoding UTF8|Select-Object -First 80)}
[IO.File]::WriteAllText($OutputPath,($lines -join [Environment]::NewLine),[Text.UTF8Encoding]::new($false))
Write-Output ('PROJECT_WIKI_OK '+$OutputPath)
