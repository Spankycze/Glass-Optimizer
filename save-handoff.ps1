param([string]$Note='')
$ErrorActionPreference='SilentlyContinue'
$root='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer'
& (Join-Path $root 'update-project-state.ps1') -Reason 'handoff' | Out-Null
$state=Join-Path $root 'PROJECT_STATE.md'
$work=Join-Path $root 'WORK_PROGRESS.md'
$out=Join-Path $root 'SESSION_HANDOFF.md'
$version='unknown'
$props=Join-Path $root 'Directory.Build.props'
if(Test-Path $props){$m=[regex]::Match([IO.File]::ReadAllText($props),'<SpankyVersion>([^<]+)</SpankyVersion>');if($m.Success){$version=$m.Groups[1].Value}}
$recent='none'
if(Test-Path $work){$x=@(Get-Content $work -Tail 35 -ErrorAction SilentlyContinue | Where-Object {$_} | Select-Object -Last 18);if($x.Count){$recent=$x -join [Environment]::NewLine}}
$body=@()
$body+='# SESSION_HANDOFF'
$body+=''
$body+='Generated: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
$body+='Version: '+$version
$body+='User note: '+$(if([string]::IsNullOrWhiteSpace($Note)){'none'}else{$Note})
$body+=''
$body+='## Resume order'
$body+='1. Read PROJECT_STATE.md.'
$body+='2. Read this SESSION_HANDOFF.md.'
$body+='3. Read only the relevant tail/section of WORK_PROGRESS.md if more detail is needed.'
$body+='4. Use dc-smart.ps1 for verbose DC Remote commands.'
$body+=''
$body+='## Recent work notes'
$body+=$recent
[IO.File]::WriteAllText($out,($body -join [Environment]::NewLine),[Text.UTF8Encoding]::new($false))
& (Join-Path $root 'update-project-state.ps1') -Reason 'handoff-saved' | Out-Null
Write-Output ('HANDOFF_OK '+$out)