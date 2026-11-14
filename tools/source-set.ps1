function Get-SpankySourceSet([string]$root,[string]$dir,[string[]]$patterns){
  $files=@()
  foreach($p in $patterns){ $files += @(Get-ChildItem -Path (Join-Path $root $dir) -File -Filter $p | Sort-Object Name) }
  if($files.Count -eq 0){ throw ("Source guard missing {0} in {1}" -f ($patterns -join ','),$dir) }
  (($files | ForEach-Object { [IO.File]::ReadAllText($_.FullName,[Text.Encoding]::UTF8) }) -join "`n")
}

function Get-SpankyAppSource([string]$root){
  Get-SpankySourceSet $root 'src\SpankyhoOptimizer' @('App.cs','App.Cli.*.cs','App.Lifecycle.cs','App.LaunchCore.cs','App.Smoke.cs')
}

function Get-SpankyMainWindowSource([string]$root){
  Get-SpankySourceSet $root 'src\SpankyhoOptimizer' @('MainWindow.cs','MainWindow.Navigation.cs','MainWindow.SectionState.cs','MainWindow.SectionCore.cs','MainWindow.Render.cs','MainWindow.TweakCards.cs','MainWindow.Dialogs.cs')
}

function Get-SpankyAiSystemSource([string]$root){
  Get-SpankySourceSet $root 'src\SpankyhoOptimizer\Services' @('AiSystemResourceManagerService*.cs')
}

function Get-SpankyPowerNetworkSource([string]$root){
  Get-SpankySourceSet $root 'src\SpankyhoOptimizer' @('MainWindow.PowerNetwork.cs','MainWindow.PowerNetwork.*.cs')
}

function Get-SpankyVisualQaSource([string]$root){
  Get-SpankySourceSet $root 'src\SpankyhoOptimizer' @('MainWindow.VisualQa.cs','MainWindow.VisualQa.*.cs')
}

function Get-SpankyOverviewDashboardSource([string]$root){
  Get-SpankySourceSet $root 'src\SpankyhoOptimizer' @('MainWindow.OverviewDashboard.cs','MainWindow.OverviewDashboard.*.cs')
}
