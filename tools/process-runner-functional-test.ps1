param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ProcessRunnerFunctionalTests\ProcessRunnerFunctionalTests.csproj'
& dotnet run --project $project
if ($LASTEXITCODE -ne 0) { throw "Process functional regressions failed (exit $LASTEXITCODE)." }
