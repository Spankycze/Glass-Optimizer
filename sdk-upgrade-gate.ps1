param([switch]$UpdateBaseline)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$baseline=Join-Path $root '.ai-state\sdk-contract.json'
$outDir=Join-Path $root 'artifacts\quality\sdk'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null

$xml=[xml](Get-Content $app -Raw)
function PackageVersion([string]$name){
  $n=$xml.Project.ItemGroup.PackageReference|Where-Object Include -eq $name|Select-Object -First 1
  if($null -eq $n){return ''}
  return [string]$n.Version
}
$current=[ordered]@{
  sdk=((& dotnet --version) -join '').Trim()
  targetFramework=[string]($xml.Project.PropertyGroup.TargetFramework|Where-Object{$_}|Select-Object -First 1)
  windowsAppSdkWinUI=PackageVersion 'Microsoft.WindowsAppSDK.WinUI'
  windowsAppSdkRuntime=PackageVersion 'Microsoft.WindowsAppSDK.Runtime'
}
$currentPath=Join-Path $outDir 'current.json'
$current|ConvertTo-Json|Set-Content $currentPath -Encoding UTF8
if($UpdateBaseline -or -not(Test-Path $baseline)){
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $baseline)|Out-Null
  $current|ConvertTo-Json|Set-Content $baseline -Encoding UTF8
  Write-Output ('SDK_GATE_BASELINE_SAVED '+$baseline)
  exit 0
}
$expected=Get-Content $baseline -Raw|ConvertFrom-Json
$changes=@()
foreach($k in 'sdk','targetFramework','windowsAppSdkWinUI','windowsAppSdkRuntime'){
  if([string]$expected.$k -ne [string]$current.$k){$changes+=($k+': '+$expected.$k+' -> '+$current.$k)}
}
if($changes.Count -gt 0){
  $changes|ForEach-Object{Write-Output ('SDK_GATE_CHANGE '+$_)}
  Write-Output 'SDK_GATE_BLOCKED Run build/test/api compatibility checks, review breaking changes, then update the baseline intentionally.'
  exit 42
}
Write-Output ('SDK_GATE_OK sdk='+$current.sdk+' winui='+$current.windowsAppSdkWinUI+' runtime='+$current.windowsAppSdkRuntime)
