$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$files=@(ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $PWD 'artifacts\weight-635\changed-files.json'))) | ForEach-Object {'src\SpankyhoOptimizer\'+$_})
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'weight-635' -Summary 'Fresh635 six suites passed; sidebar and overall heavier text checked in actual captures.' -NextAction 'Gated release635 then verify installed payload and live fan agent.' -Done @('Body Medium','Sidebar13SemiBold and action SemiBold','Six smoke suites passed') -Pending @('Release and installation') -FilesTouched $files -Evidence @('artifacts\weight-635\validation-result.json','artifacts\weight-635\screens\dark-network.png')
$text=[IO.File]::ReadAllText((Join-Path $PWD 'fonts-634-release.ps1')).Replace('fonts-634','weight-635').Replace('15.4.634','15.4.635').Replace('V15_4_634','V15_4_635').Replace('FONTS634','WEIGHT635')
$text=$text.Replace('Installed634: Variable Text/Display font, stronger page/dialog titles, clearer metric numbers and action labels. Six suites and gated release/install passed.','Installed635: body Medium, larger13SemiBold sidebar and SemiBold actions. Six suites and gated release/install passed.').Replace('Installed634 typography update complete. Next UI batch: GPU/Startup/Storage consistency audit. Source635 is automatic bump.','Installed635 heavier typography update complete. Next UI batch GPU/Startup/Storage. Source636 is automatic bump.').Replace("'Variable Text/Display typography','Page/dialog headings and metric/action readability'","'Body Medium','Sidebar13SemiBold and action labels SemiBold'").Replace('Fresh634','Fresh635')
[IO.File]::WriteAllText((Join-Path $PWD 'weight-635-release.ps1'),$text)
& .\weight-635-release.ps1
if(!$?){throw 'Release/install failed'}
