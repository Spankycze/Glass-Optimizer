$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$gui=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$runtimeProj=Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\SpankyhoOptimizer.RuntimeHost.csproj'
$guiOut=Join-Path $root 'artifacts\gemini-502-gui'
$hostOut=Join-Path $root 'artifacts\gemini-502-runtimehost'
foreach($outDir in @($guiOut,$hostOut)){ if(Test-Path $outDir){Remove-Item $outDir -Recurse -Force}; New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
dotnet build $gui -c Release -r win-x64 --no-restore --nologo -v:minimal -o $guiOut
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet build $runtimeProj -c Release -r win-x64 --no-restore --nologo -v:minimal -o $hostOut
exit $LASTEXITCODE
