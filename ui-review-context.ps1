$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$report=& {
Select-String -Path 'src\SpankyhoOptimizer\MainWindow.Shell.cs' -Pattern 'SidebarIcon|SidebarButton|BuildMainTabs' | ForEach-Object {"$($_.LineNumber):$($_.Line)"}
Select-String -Path 'src\SpankyhoOptimizer\MainWindow.Theme.cs' -Pattern 'SetThemeContext|_themeSection|ApplyThemeContext' -Context 2,2 | Out-String
Select-String -Path 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs' -Pattern 'Build.*Card|ArtCard|BorderThickness' | ForEach-Object {"$($_.LineNumber):$($_.Line)"} | Select-Object -First 35
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign\review-context.txt'),($report|Out-String -Width 150))
Write-Output 'REVIEW_CONTEXT_OK'