param(
  [Parameter(Mandatory=$true,Position=0)][string]$Task,
  [ValidateSet('Plan','Run')][string]$Mode='Run',
  [ValidateSet('Quick','Normal','Deep')][string]$Depth='Normal',
  [string[]]$Files=@()
)
$ErrorActionPreference='Stop'
$router='V:\SpankyDevCache\SharedTools\dev-assist\task-tool-router.mjs'
if(!(Test-Path -LiteralPath $router)){throw 'Missing shared developer router: '+$router}
$argsList=@($router,'--project','optimizer','--task',$Task,
    '--mode',$Mode.ToLowerInvariant(),'--depth',$Depth.ToLowerInvariant())
foreach($file in $Files){$argsList+=@('--file',$file)}
& node @argsList
exit $LASTEXITCODE
