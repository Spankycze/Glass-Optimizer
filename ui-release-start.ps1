$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$files=@('MainWindow.NeutralUi.cs','MainWindow.NeutralUiRegression.cs','MainWindow.Theme.cs','MainWindow.Shell.cs','MainWindow.Dashboard.cs','MainWindow.DashboardModern.cs','MainWindow.Sections.cs','MainWindow.NetworkDashboard.cs','MainWindow.StyledDialogs.cs','MainWindow.cs','MainWindow.UiPrimitives.cs','MainWindow.TweakVirtualization.cs','MainWindow.ServicePrewarm.cs','MainWindow.OverviewDashboard.cs','Directory.Build.props')
& '.\ai-workflow.ps1' -Action checkpoint -TaskId 'neutral-ui-redesign' -Phase 'ui-validated' -FilesTouched $files -ValidationRequired @('release','compact-installer')
& '.\ui-release-final.ps1'
Write-Output 'VALIDATED_RELEASE_OK'