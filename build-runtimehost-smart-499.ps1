$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$proj=Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\SpankyhoOptimizer.RuntimeHost.csproj'
$out=Join-Path $root 'artifacts\runtimehost-smart-499'
if(Test-Path $out){Remove-Item $out -Recurse -Force}
New-Item -ItemType Directory -Path $out -Force | Out-Null
dotnet build $proj -c Release -r win-x64 --no-restore --nologo -v:minimal -o $out
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
