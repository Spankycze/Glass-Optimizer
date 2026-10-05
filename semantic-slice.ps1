param(
  [Parameter(Mandatory=$true)][string]$Path,
  [string]$OutputPath='',
  [int]$MaxLines=80,
  [int]$MaxChars=12000
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$full=if([IO.Path]::IsPathRooted($Path)){$Path}else{Join-Path $root $Path}
if(-not(Test-Path $full)){throw 'Input not found: '+$full}
if([string]::IsNullOrWhiteSpace($OutputPath)){$OutputPath=Join-Path $root '.ai-state\semantic-slices.json'}
elseif(-not [IO.Path]::IsPathRooted($OutputPath)){$OutputPath=Join-Path $root $OutputPath}
$lines=Get-Content -LiteralPath $full -Encoding UTF8
$chunks=New-Object System.Collections.Generic.List[object]
$buf=New-Object System.Collections.Generic.List[string]
$heading=''
$start=1
function Flush-Chunk {
  param([int]$end)
  if($buf.Count -eq 0){return}
  $text=$buf -join [Environment]::NewLine
  $chunks.Add([pscustomobject][ordered]@{heading=$heading;startLine=$start;endLine=$end;chars=$text.Length;text=$text})|Out-Null
  $buf.Clear()
}
for($i=0;$i -lt $lines.Count;$i++){
  $line=[string]$lines[$i]
  $isHeading=$line -match '^\s{0,3}#{1,6}\s+'
  if($isHeading -and $buf.Count -gt 0){Flush-Chunk -end $i;$start=$i+1}
  if($isHeading){$heading=$line.Trim()}
  $buf.Add($line)
  $chars=($buf|Measure-Object -Property Length -Sum).Sum
  if($buf.Count -ge $MaxLines -or $chars -ge $MaxChars){Flush-Chunk -end ($i+1);$start=$i+2}
}
Flush-Chunk -end $lines.Count
$result=[ordered]@{schema=1;source=$full;generatedAt=(Get-Date).ToString('o');maxLines=$MaxLines;maxChars=$MaxChars;chunks=$chunks}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath)|Out-Null
[IO.File]::WriteAllText($OutputPath,($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Output ('SEMANTIC_SLICE_OK chunks='+$chunks.Count+' output='+$OutputPath)
