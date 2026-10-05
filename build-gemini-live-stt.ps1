$ErrorActionPreference='Stop'
$proj='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer.RuntimeHost\SpankyhoOptimizer.RuntimeHost.csproj'
$out='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\artifacts\gemini-live-stt-test'
if(Test-Path $out){Remove-Item $out -Recurse -Force}
dotnet restore $proj --nologo
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet build $proj -c Release -r win-x64 --no-restore --nologo -v:minimal -o $out
exit $LASTEXITCODE
