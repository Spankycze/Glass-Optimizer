$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$proj=Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\SpankyhoOptimizer.RuntimeHost.csproj'
dotnet build $proj -c Release -r win-x64 --no-restore --nologo -v:minimal
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
