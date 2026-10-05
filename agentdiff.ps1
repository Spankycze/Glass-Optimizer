param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Args)
$ErrorActionPreference='Stop'
$env:PYTHONPATH='C:\Users\spnka\Desktop\ChatGPT Projects\.tools\token-savers\AgentDiff'
py -m agentdiff.cli @Args
exit $LASTEXITCODE
