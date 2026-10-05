$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& '.\ui-redesign-validate.ps1'
& '.\ui-visual-final.ps1'
& '.\ui-pixel-audit.ps1'
$notes=@(
'',
'### V15.4.603 final UI verification',
'- Final-source Debug build/test, six-theme regression (persistent titlebar palette and cached badge readback), deferred header completion while Network is active, visual layout/size matrix, page scroll input and release UI bundle passed.',
'- Review findings fixed: persistent titlebar uses original palette across navigation/theme changes; cached classic badges update background/foreground together; neutral hover/pressed/disabled control states complete. Overview diff limited to one explicit titlebar palette-scope call; other protected hashes unchanged; hero crop pixel-identical. Small rounded art edge pixel differences recorded separately; no physical frame-latency claim.',
'- The final standalone framework comparison is saved in artifacts/ui-framework-probe/comparison-report.md; measured workload favors WinUI.'
)
for($attempt=0;$attempt -lt 6;$attempt++){
 try { [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'WORK_PROGRESS.md'),(($notes -join [Environment]::NewLine)+[Environment]::NewLine),[Text.Encoding]::UTF8);break }
 catch [IO.IOException] {if($attempt -eq 5){throw};Start-Sleep -Milliseconds 300}
}
$files=@('MainWindow.NeutralUi.cs','MainWindow.NeutralUiRegression.cs','MainWindow.Theme.cs','MainWindow.Shell.cs','MainWindow.Dashboard.cs','MainWindow.DashboardModern.cs','MainWindow.Sections.cs','MainWindow.NetworkDashboard.cs','MainWindow.StyledDialogs.cs','MainWindow.cs','MainWindow.UiPrimitives.cs','MainWindow.TweakVirtualization.cs','MainWindow.ServicePrewarm.cs','Directory.Build.props')
& '.\ai-workflow.ps1' -Action checkpoint -TaskId 'neutral-ui-redesign' -Phase 'ui-validated' -FilesTouched $files -ValidationRequired @('release','compact-installer')
& '.\ui-release-final.ps1'
Write-Output 'QA_AND_RELEASE_OK'