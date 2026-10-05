$ErrorActionPreference='Stop'
$env:MCP_COMPRESS_ROUTER_HOME=(Join-Path $PSScriptRoot '.mcp-router')
mcp-compress-router list
