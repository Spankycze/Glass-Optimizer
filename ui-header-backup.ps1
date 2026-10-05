$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
Copy-Item 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs' 'artifacts\ui-neutral-redesign\backup\OverviewDashboard-before-header-scope.cs' -Force
Write-Output 'HEADER_SOURCE_BACKUP_OK'