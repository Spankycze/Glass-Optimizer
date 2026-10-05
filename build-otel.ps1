$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
dotnet restore $app -p:EnableOpenTelemetry=true --nologo
if($LASTEXITCODE -ne 0){throw 'OpenTelemetry restore failed'}
dotnet build $app -c Debug -r win-x64 --no-restore --nologo -p:EnableOpenTelemetry=true
if($LASTEXITCODE -ne 0){throw 'OpenTelemetry diagnostic build failed'}
Write-Output 'OPENTELEMETRY_BUILD_OK set SPANKY_OTEL=1 before running this diagnostic build'
