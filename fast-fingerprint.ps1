param([switch]$Save)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$state=Join-Path $root '.ai-state\fast-input.fp'
$lines=[Collections.Generic.List[string]]::new()

function Add-Tree([string]$path){
  if(-not(Test-Path $path)){return}
  $stack=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
  $stack.Push([IO.DirectoryInfo]::new($path))
  while($stack.Count){
    $dir=$stack.Pop()
    foreach($f in $dir.EnumerateFiles()){
      if($f.Extension -notin '.cs','.xaml','.ps1','.csproj','.props','.targets','.json','.toml','.rs'){continue}
      $rel=$f.FullName.Substring($root.Length+1)
      $lines.Add($rel+'|'+$f.Length+'|'+$f.LastWriteTimeUtc.Ticks)
    }
    foreach($child in $dir.EnumerateDirectories()){
      if($child.Name -in 'bin','obj','target','.git','.dc-logs','artifacts'){continue}
      $stack.Push($child)
    }
  }
}
Add-Tree (Join-Path $root 'src')
Add-Tree (Join-Path $root 'native')
foreach($name in 'Directory.Build.props','Directory.Build.targets','global.json','NuGet.Config'){
  $f=Join-Path $root $name
  if(Test-Path $f){
    $i=Get-Item $f;$lines.Add($name+'|'+$i.Length+'|'+$i.LastWriteTimeUtc.Ticks)
  }
}
$sorted=@($lines|Sort-Object)
$bytes=[Text.Encoding]::UTF8.GetBytes([string]::Join([Environment]::NewLine,[string[]]$sorted))
$sha=[Security.Cryptography.SHA256]::Create()
try{$fp=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')}finally{$sha.Dispose()}
if($Save){
  [IO.File]::WriteAllText($state,$fp,[Text.UTF8Encoding]::new($false))
  Write-Output $fp
  return
}
if(Test-Path $state){
  $old=[IO.File]::ReadAllText($state).Trim()
  if($old -eq $fp){Write-Output ('FRESH '+$fp);return}
}
Write-Output ('CHANGED '+$fp)
