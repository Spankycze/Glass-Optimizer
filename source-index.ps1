param([string]$OutputPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputPath)){$OutputPath=Join-Path $root '.ai-state\source-index.json'}
$rg=(Get-Command rg -ErrorAction Stop).Source
$patterns=[ordered]@{
  process='ProcessStartInfo|Process\.Start\('
  syncwait='\.Result\b|\.Wait\('
  wmi='Get-CimInstance|Get-WmiObject|System\.Management|ManagementObject'
  registry='Registry(Key)?\.|Microsoft\.Win32'
  taskrun='Task\.Run\('
  dispatcher='DispatcherQueue'
  powershell='powershell\.exe|pwsh\.exe'
  netadapter='Get-NetAdapter|Get-NetIP|Get-DnsClient|netsh\.exe'
  locks='\block\s*\(|SemaphoreSlim|Monitor\.'
}
$items=@()
foreach($name in $patterns.Keys){
  $lines=& $rg -n --no-heading --glob '*.cs' --glob '*.xaml' --glob '*.ps1' --glob '!**/bin/**' --glob '!**/obj/**' $patterns[$name] (Join-Path $root 'src') 2>$null
  foreach($line in $lines){
    if($line -match '^(.*?):(\d+):(.*)$'){
      $items += [pscustomobject]@{category=$name;file=$matches[1];line=[int]$matches[2];text=$matches[3].Trim()}
    }
  }
}
$summary=$items|Group-Object category|Sort-Object Name|ForEach-Object{[pscustomobject]@{category=$_.Name;count=$_.Count}}
$result=[pscustomobject]@{generatedAt=(Get-Date).ToString('o');count=$items.Count;summary=$summary;items=$items}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath)|Out-Null
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output ('SOURCE_INDEX_OK total={0} path={1}' -f $items.Count,$OutputPath)
$summary|ForEach-Object{Write-Output ('SOURCE_INDEX {0}={1}' -f $_.category,$_.count)}
