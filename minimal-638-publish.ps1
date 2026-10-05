$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'minimal-638' -Summary 'Fresh638 build/test and six suites passed. Actual Network capture: no large outer workspace cards, compact section layout, full MTU actions visible. Tighter sidebar exposes PowerShell/Cursor.' -NextAction 'Gated release638/install and installed Release timing samples.' -Done @('Open sections','Compact heading/navigation','Redundant DNS banner removed','Six suites') -Pending @('Release/install','Measurements') -FilesTouched @('src\SpankyhoOptimizer\MainWindow.NeutralUi.cs','src\SpankyhoOptimizer\MainWindow.NetworkDashboard.cs','src\SpankyhoOptimizer\MainWindow.PowerNetwork.cs','src\SpankyhoOptimizer\MainWindow.Shell.cs','src\SpankyhoOptimizer\MainWindow.Theme.cs') -Evidence @('artifacts\minimal-638\validation-result.json','artifacts\minimal-638\screens\dark-network.png')
& .\minimal-638-release.ps1
if(!$?){throw 'Release/install failed'}
& .\minimal-638-measure.ps1 -Label 'after'
if(!$?){throw 'Release timing samples failed'}
Write-Output 'MINIMAL638_INSTALLED_MEASURED'
