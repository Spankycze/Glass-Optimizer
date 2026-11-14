param(
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$SkipRuntime
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\ContextMenuService.cs'
$helper = Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\ContextMenuNativeActions.cs'
$program = Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Program.cs'
$src = Get-Content -LiteralPath $source -Raw
$native = Get-Content -LiteralPath $helper -Raw
$main = Get-Content -LiteralPath $program -Raw
. (Join-Path $ProjectRoot 'tools\source-set.ps1')
$app = Get-SpankyAppSource $ProjectRoot
if(!$src.Contains('public static void MigrateExistingNativeCommands()') -or
    !$app.Contains('MigrateContextCommandsDeferredAsync(_lifetimeCts.Token)')){
    throw 'Existing enabled context commands must migrate after a normal app upgrade.'
}
$verbs = @('copy-path','new-file','sha256','advanced-startup','admin-terminal','admin-powershell')
foreach($verb in $verbs){
    if(!$src.Contains('--context-' + $verb) -or !$native.Contains('case "--context-' + $verb + '"')){
        throw "Native context action not wired: $verb"
    }
}
if(!$main.Contains('if (ContextMenuNativeActions.TryHandle(args)) return;')){
    throw 'Native context actions not routed before WinUI startup.'
}
if(!$native.Contains('FileMode.CreateNew') -or !$native.Contains('SHA256.HashData') -or
    !$native.Contains('OpenClipboard(clipboardOwner)') -or !$native.Contains('Verb = "runas"')){
    throw 'Context file/clipboard/admin safety contract broken.'
}
foreach($method in @('SetCopyPath','SetSha256','SetNewEmptyFile','SetAdminTerminalHere','SetAdminPowerShellHere','SetAdvancedStartup')){
    $start=$src.IndexOf('public static void ' + $method + '(')
    if($start -lt 0){throw "Missing menu method: $method"}
    $end=$src.IndexOf('public static ', $start + 20)
    if($end -lt 0){$end=$src.Length}
    $section=$src.Substring($start, $end - $start)
    if($section.Contains('powershell.exe -NoProfile')){throw "PowerShell launcher still used in $method"}
}
Write-Output "NATIVE_CONTEXT_STATIC_GUARD_OK actions=$($verbs.Count) earlyCli=true"
if(!$SkipRuntime){
    $exe=Join-Path $ProjectRoot 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
    if(!(Test-Path -LiteralPath $exe)){throw "Missing compiled EXE: $exe"}
    # PowerShell does not reliably populate LASTEXITCODE for a WinExe process.
    $testProcess = Start-Process -FilePath $exe -ArgumentList '--context-action-smoke' -PassThru -Wait
    if($testProcess.ExitCode -ne 0){throw "Native context smoke exited $($testProcess.ExitCode)"}
    Write-Output 'NATIVE_CONTEXT_RUNTIME_GUARD_OK'
}
