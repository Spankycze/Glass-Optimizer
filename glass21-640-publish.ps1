$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\ai-workflow.ps1 -Action checkpoint -TaskId glass21-639 -Phase final640-before-release -Summary 'Fresh640 native switch resolves observed pending OFF visual defect and bootstrap blue resource color; full build-test and six UI suites pass.' -NextAction 'Gated640 release/install, fresh agent/HID and installed UI capture.' -Done @('Glass21 native backdrop','Responsive real tweak groups','Native pending-state switch') -Pending @('640 release/install','Final installed screen and resume') -FilesTouched @('src\SpankyhoOptimizer\App.xaml') -Evidence @('artifacts\glass21-640\validation-result.json','artifacts\glass21-640\installed-ui-result.json','artifacts\glass21-639\review.txt')
& .\glass21-640-release.ps1
