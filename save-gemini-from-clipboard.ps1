$ErrorActionPreference='Stop'
$key = (Get-Clipboard -Raw).Trim()
if([string]::IsNullOrWhiteSpace($key) -or $key.Length -lt 20){
  Write-Output 'NO_API_KEY_IN_CLIPBOARD'
  exit 2
}
$bytes=[Text.Encoding]::UTF8.GetBytes($key)
$enc=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
$obj=[ordered]@{
  Provider='Google Gemini'
  Model='gemini-3.8-flash'
  EncryptedApiKey=[Convert]::ToBase64String($enc)
  Enabled=$true
}
$dir=Join-Path $env:LOCALAPPDATA 'SpankyhoOptimizer'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$obj | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dir 'voice-cloud-ai.json') -Encoding UTF8
Write-Output ('API_KEY_SAVED chars='+$key.Length)
