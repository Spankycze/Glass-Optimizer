param(
  [int]$SinceMinutes=240,
  [string]$OutputPath='',
  [switch]$SaveBaseline
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if(-not $OutputPath){$OutputPath=Join-Path $root '.ai-state\change-impact.json'}
$snapshotPath=Join-Path $root '.ai-state\quality-source.snapshot.json'
$pendingPath=Join-Path $root '.ai-state\quality-source.pending.json'
$schema=3

function Get-TrackedFiles {
  $all=[Collections.Generic.List[IO.FileInfo]]::new()
  $extensions=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach($ext in @('.cs','.xaml','.ps1','.csproj','.props','.targets','.json','.toml','.rs','.config')){[void]$extensions.Add($ext)}
  $skip=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach($name in @('bin','obj','artifacts','.ai-state','.perf','.dc-logs','StrykerOutput','_backups','target')){[void]$skip.Add($name)}
  $rootInfo=[IO.DirectoryInfo]::new($root)
  foreach($f in $rootInfo.EnumerateFiles()){
    if($extensions.Contains($f.Extension)){ $all.Add($f) }
  }
  foreach($name in @('src','native','installer','tools','fuzz','benchmarks')){
    $dirPath=Join-Path $root $name
    if(-not(Test-Path $dirPath)){continue}
    $stack=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $stack.Push([IO.DirectoryInfo]::new($dirPath))
    while($stack.Count){
      $dir=$stack.Pop()
      foreach($f in $dir.EnumerateFiles()){
        if($extensions.Contains($f.Extension)){ $all.Add($f) }
      }
      foreach($child in $dir.EnumerateDirectories()){
        if(-not $skip.Contains($child.Name)){ $stack.Push($child) }
      }
    }
  }
  @($all|Sort-Object FullName -Unique)
}

function Get-Hash([IO.FileInfo]$f){
  (Get-FileHash $f.FullName -Algorithm SHA256).Hash
}

function New-Row([IO.FileInfo]$f,[string]$hash){
  [pscustomobject]@{
    path=$f.FullName.Substring($root.Length+1)
    length=[long]$f.Length
    ticks=[long]$f.LastWriteTimeUtc.Ticks
    sha256=$hash
  }
}

function Write-Snapshot([string]$path,$rows){
  New-Item -ItemType Directory -Force -Path (Split-Path $path)|Out-Null
  $fileRows=@($rows | ForEach-Object { $_ })
  $payload=[ordered]@{
    schema=$schema
    generatedAt=(Get-Date).ToString('o')
    files=$fileRows
  }
  $payload|ConvertTo-Json -Depth 5|Set-Content $path -Encoding UTF8
}

if($SaveBaseline){
  if(Test-Path $pendingPath){
    try{$pending=Get-Content $pendingPath -Raw|ConvertFrom-Json}catch{$pending=$null}
    if($null -ne $pending -and [int]$pending.schema -eq $schema){
      Copy-Item $pendingPath $snapshotPath -Force
      Write-Output ('CHANGE_IMPACT_BASELINE_PROMOTED files='+@($pending.files).Count+' path='+$snapshotPath)
      return
    }
  }
  $rows=foreach($f in Get-TrackedFiles){New-Row $f (Get-Hash $f)}
  Write-Snapshot $snapshotPath $rows
  Write-Output ('CHANGE_IMPACT_BASELINE_SAVED files='+@($rows).Count+' path='+$snapshotPath)
  return
}

$tracked=@(Get-TrackedFiles)
$files=@()
$rows=New-Object Collections.Generic.List[object]
$mode='window'
$old=$null
if(Test-Path $snapshotPath){
  try{$old=Get-Content $snapshotPath -Raw|ConvertFrom-Json}catch{$old=$null}
}
$oldValid=$null -ne $old -and [int]$old.schema -eq $schema

if(Test-Path (Join-Path $root '.git')){
  $mode='git'
  $files=@(git -C $root diff --name-only HEAD; git -C $root ls-files --others --exclude-standard)
  foreach($f in $tracked){$rows.Add((New-Row $f (Get-Hash $f)))|Out-Null}
}elseif($oldValid){
  $mode='snapshot'
  $oldMap=@{}
  foreach($x in @($old.files)){$oldMap[[string]$x.path]=$x}
  $curPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach($f in $tracked){
    $rel=$f.FullName.Substring($root.Length+1)
    [void]$curPaths.Add($rel)
    $hash=$null
    if($oldMap.ContainsKey($rel)){
      $prior=$oldMap[$rel]
      if([long]$prior.length -eq [long]$f.Length -and [long]$prior.ticks -eq [long]$f.LastWriteTimeUtc.Ticks){
        $hash=[string]$prior.sha256
      }else{
        $hash=Get-Hash $f
        if($hash -ne [string]$prior.sha256){$files+=$rel}
      }
    }else{
      $hash=Get-Hash $f
      $files+=$rel
    }
    $rows.Add((New-Row $f $hash))|Out-Null
  }
  foreach($x in @($old.files)){
    if(-not $curPaths.Contains([string]$x.path)){$files+=[string]$x.path}
  }
}else{
  $mode=$(if(Test-Path $snapshotPath){'window-schema-upgrade'}else{'window'})
  $cut=(Get-Date).ToUniversalTime().AddMinutes(-[math]::Max(1,$SinceMinutes))
  foreach($f in $tracked){
    $hash=Get-Hash $f
    $rows.Add((New-Row $f $hash))|Out-Null
    if($f.LastWriteTimeUtc -ge $cut){$files+=$f.FullName.Substring($root.Length+1)}
  }
}

Write-Snapshot $pendingPath $rows
$files=@($files|Where-Object{$_}|Sort-Object -Unique)
$areas=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$tests=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$tests.Add('build-fast')
[void]$tests.Add('validate-tooling')

function Hit([string]$pattern,[string]$area,[string[]]$add){
  if(@($files|Where-Object{$_ -match $pattern}).Count){
    [void]$areas.Add($area)
    foreach($x in $add){[void]$tests.Add($x)}
  }
}

Hit '(MainWindow|\.xaml$|UiResource|Visual|Motion|StyledDialog)' 'ui' @('navigation-race','page-scroll-input','visual-size')
Hit '(Network|Dns|Mtu|Tcp|Adapter)' 'network' @('perf','interference','fault','cache')
Hit '(Storage|Nvme|Disk)' 'storage' @('perf','interference','cache')
Hit '(Gpu|Nvidia|Graphics)' 'gpu' @('perf','interference','cache')
Hit '(Startup|TaskScheduler)' 'startup' @('perf','interference','cache')
Hit '(ServiceCatalog|TaskScheduler|Component|WindowsModernizer)' 'services' @('perf','interference','cache')
Hit '(ProcessRunner|WorkerHost|WorkerClient|FaultInjection|AdvancedCenter|ShellToolkit)' 'process' @('fault')
Hit '\\Services\\' 'backend' @()
Hit '(Parser|Json|Changelog|Version|Dns)' 'parser' @('fuzz')
Hit '(Tweak|Rollback|Registry|OperationCoordinator)' 'tweaks' @('rollback','fault')
Hit '(SpankyhoOptimizer\.Core|public|Contract|Api)' 'core' @('api-compat','architecture','platform')
Hit '(\.ps1$|build-|Directory\.Build|\.csproj$|\.props$|\.targets$)' 'tooling' @('source-index')
Hit '(build-fast|build-dev|build-state|build-fast-xaml-reuse|build-speed|Directory\.Build)' 'build' @('build-speed')
if(@($files|Where-Object{$_ -match '\.(cs|xaml)$'}).Count){
  [void]$tests.Add('source-index')
  [void]$tests.Add('extended-analyzers')
}
if($files.Count -eq 0){
  [void]$areas.Add('none')
}elseif($areas.Count -eq 0){
  [void]$areas.Add('general')
  [void]$tests.Add('source-index')
}

$r=[ordered]@{
  generatedAt=(Get-Date).ToString('o')
  mode=$mode
  fileCount=$files.Count
  files=$files
  areas=@($areas|Sort-Object)
  tests=@($tests|Sort-Object)
}
New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath)|Out-Null
$r|ConvertTo-Json -Depth 6|Set-Content $OutputPath -Encoding UTF8
Write-Output ('CHANGE_IMPACT_OK mode='+$mode+' files='+$r.fileCount+' areas='+($r.areas -join ',')+' tests='+($r.tests -join ','))
