$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$src=Join-Path $root 'AGENT_RULES.md'
if(-not(Test-Path $src)){throw 'AGENT_RULES.md missing'}
$text=[IO.File]::ReadAllText($src)
foreach($dest in @('AGENTS.md')){
  [IO.File]::WriteAllText((Join-Path $root $dest),$text,[Text.UTF8Encoding]::new($false))
}
$meta=[ordered]@{schema=1;updatedAt=(Get-Date).ToString('o');source='AGENT_RULES.md';targets=@('AGENTS.md')}
[IO.File]::WriteAllText((Join-Path $root '.ai-state\agent-rules.json'),($meta|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
Write-Output 'AGENT_RULES_SYNC_OK'
