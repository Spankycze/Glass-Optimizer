param([int]$TimeoutSeconds=45)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$testProj=Join-Path $root 'tools\ConcurrencyTests\ConcurrencyTests.csproj'
$rewriterProj=Join-Path $root 'tools\CoyoteRewriter\CoyoteRewriter.csproj'
$out=Join-Path $root 'artifacts\quality\coyote\rewritten'

dotnet build $testProj -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Coyote harness build failed'}
dotnet build $rewriterProj -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Coyote rewriter build failed'}

$src=Join-Path $root 'tools\ConcurrencyTests\bin\Release\net10.0-windows10.0.26100.0\ConcurrencyTests.dll'
$rewriter=Join-Path $root 'tools\CoyoteRewriter\bin\Release\net10.0\CoyoteRewriter.dll'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out|Out-Null

dotnet $rewriter $src $out
if($LASTEXITCODE -ne 0){throw 'Coyote rewrite failed'}

$psi=[Diagnostics.ProcessStartInfo]::new('dotnet',('"' + (Join-Path $out 'ConcurrencyTests.dll') + '"'))
$psi.WorkingDirectory=$out
$psi.UseShellExecute=$false
$psi.RedirectStandardOutput=$true
$psi.RedirectStandardError=$true
$p=[Diagnostics.Process]::Start($psi)
if(-not $p.WaitForExit($TimeoutSeconds*1000)){
  try{$p.Kill($true)}catch{}
  throw ('Coyote run timeout after '+$TimeoutSeconds+'s')
}
$text=$p.StandardOutput.ReadToEnd()+$p.StandardError.ReadToEnd()
Write-Output $text
if($p.ExitCode -ne 0){throw ('Coyote concurrency harness failed exit='+$p.ExitCode)}
if($text -notmatch 'COYOTE_OK schedules=(\d+)'){throw 'Coyote success marker missing'}
Write-Output ('COYOTE_HARNESS_OK schedules='+$matches[1])
