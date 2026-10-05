$ErrorActionPreference='Stop'
$p='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer\Services\SpankyVoiceListenerService.cs'
$s=Get-Content -LiteralPath $p -Raw
$pattern='(?s)        bool authorizationHeard =.*?             normalizedHeard\.Length <= 24\);'
$replacement=@'
        bool hasWakeLead =
            normalizedHeard.Contains("hej", StringComparison.Ordinal) ||
            normalizedHeard.Contains("hey", StringComparison.Ordinal) ||
            normalizedHeard.StartsWith("he ", StringComparison.Ordinal) ||
            normalizedHeard.StartsWith("jej ", StringComparison.Ordinal) ||
            normalizedHeard.StartsWith("je ", StringComparison.Ordinal) ||
            normalizedHeard.EndsWith(" he", StringComparison.Ordinal);

        bool hasWakeTail =
            normalizedHeard.Contains("vole", StringComparison.Ordinal) ||
            normalizedHeard.Contains("vola", StringComparison.Ordinal) ||
            normalizedHeard.Contains("volá", StringComparison.Ordinal) ||
            normalizedHeard.Contains("volé", StringComparison.Ordinal) ||
            normalizedHeard.Contains(" vol", StringComparison.Ordinal);

        bool authorizationHeard =
            normalizedHeard.Contains(AuthorizationPhrase, StringComparison.Ordinal) ||
            (hasWakeLead && hasWakeTail && normalizedHeard.Length <= 32);
'@
$updated=[regex]::Replace($s,$pattern,$replacement,1)
if($updated -eq $s){ throw 'Wake phrase block not replaced' }
[IO.File]::WriteAllText($p,$updated,[Text.UTF8Encoding]::new($false))
Write-Output 'WAKE_PATCH_OK'
