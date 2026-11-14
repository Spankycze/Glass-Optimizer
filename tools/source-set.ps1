# Reads a source file together with every partial that belongs to it.
# Type files (App.cs, MainWindow.cs, ...) pick up every sibling that declares
# "partial [class|struct|record|interface] <Type>"; page slices such as
# MainWindow.PowerNetwork.cs pick up only their own "name.*.cs" parts.
function Get-SpankySourceText([string]$path){
  if(-not (Test-Path -LiteralPath $path)){ throw "Source guard missing $path" }
  $base=(Get-Item -LiteralPath $path)
  $text=[IO.File]::ReadAllText($base.FullName,[Text.Encoding]::UTF8)
  if($text.Trim().Length -eq 0){ throw "Source guard empty $path" }
  $dir=$base.DirectoryName
  $stem=[IO.Path]::GetFileNameWithoutExtension($base.Name)
  $match=[regex]::Match($text,'\b(?:class|struct|record|interface)\s+([A-Za-z_]\w*)')
  $type=if($match.Success){$match.Groups[1].Value}else{$null}
  $isTypeFile=($null -ne $type) -and ($stem -eq $type)
  $builder=[Text.StringBuilder]::new()
  [void]$builder.Append($text)
  foreach($file in @(Get-ChildItem -Path $dir -Filter '*.cs' -File | Sort-Object Name)){
    if($file.FullName -eq $base.FullName){ continue }
    if($isTypeFile){
      $other=[IO.File]::ReadAllText($file.FullName,[Text.Encoding]::UTF8)
      $pattern='\bpartial\s+(?:class|struct|record|interface)\s+' + [regex]::Escape($type) + '\b'
      if(-not [regex]::IsMatch($other,$pattern)){ continue }
    }
    elseif(-not $file.Name.StartsWith($stem + '.',[StringComparison]::OrdinalIgnoreCase)){ continue }
    [void]$builder.Append("`n")
    [void]$builder.Append([IO.File]::ReadAllText($file.FullName,[Text.Encoding]::UTF8))
  }
  $builder.ToString()
}

function Get-SpankySourceSet([string]$root,[string]$dir,[string[]]$patterns){
  $files=@()
  foreach($p in $patterns){ $files += @(Get-ChildItem -Path (Join-Path $root $dir) -File -Filter $p | Sort-Object Name) }
  if($files.Count -eq 0){ throw ("Source guard missing {0} in {1}" -f ($patterns -join ','),$dir) }
  (($files | ForEach-Object { [IO.File]::ReadAllText($_.FullName,[Text.Encoding]::UTF8) }) -join "`n")
}

function Get-SpankyAppSource([string]$root){
  Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\App.cs')
}

function Get-SpankyMainWindowSource([string]$root){
  Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.cs')
}

function Get-SpankyWindowsModernizerSource([string]$root){
  Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\Services\WindowsModernizerService.cs')
}

function Get-SpankyAiSystemSource([string]$root){
  Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\Services\AiSystemResourceManagerService.cs')
}

function Get-SpankyPowerNetworkSource([string]$root){
  Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.PowerNetwork.cs')
}

function Get-SpankyVisualQaSource([string]$root){
  Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.VisualQa.cs')
}

function Get-SpankyOverviewDashboardSource([string]$root){
  Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs')
}
