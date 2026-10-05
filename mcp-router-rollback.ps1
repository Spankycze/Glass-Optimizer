$ErrorActionPreference='Stop'
$root=$PSScriptRoot
Copy-Item (Join-Path $root '.mcp.direct-backup.json') (Join-Path $root '.mcp.json') -Force
Write-Host 'MCP_ROUTER_ROLLBACK_OK'
