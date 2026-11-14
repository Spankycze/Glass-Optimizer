param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Args)
$ErrorActionPreference='Stop'
$env:PYTHONPATH='V:\SpankyDevCache\SharedTools\token-savers\AgentDiff'
py -m agentdiff.cli @Args
exit $LASTEXITCODE
