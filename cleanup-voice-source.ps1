$ErrorActionPreference='Stop'
$p='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer\Services\SpankyVoiceListenerService.cs'
$lines=Get-Content -LiteralPath $p
$clean=$lines | Where-Object { $_ -notmatch '^\[Reading ' -and $_ -notmatch '^\[executed on device:' }
[IO.File]::WriteAllLines($p,$clean,[Text.UTF8Encoding]::new($false))
Write-Output ('VOICE_SOURCE_CLEAN_OK lines='+$clean.Count)
