$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item 'artifacts\changelog-630\before' -ItemType Directory -Force|Out-Null
& .\ai-workflow.ps1 -Action start-task -TaskId 'changelog-630' -Title 'Keep changelog stable after Overview refresh and dialogs' -Area 'winui' -Summary 'Confirmed old tweak batch history overwrites visible changelog caption and rows on cached Overview refresh.' -NextAction 'Add read-only regression and run it against old code to confirm failure.' -ValidationRequired @('changelog refresh regression','visual capture','navigation','build-test','release')
Copy-Item 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs','src\SpankyhoOptimizer\MainWindow.Changelog.cs','src\SpankyhoOptimizer\MainWindow.VisualQa.cs' 'artifacts\changelog-630\before' -Force
