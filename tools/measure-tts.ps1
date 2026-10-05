$ErrorActionPreference = 'Stop'
$exe = 'C:\Users\spnka\AppData\Local\SpankyhoOptimizer\VoiceTools\OpenWakeWord\.venv\Scripts\edge-tts.exe'
1..3 | ForEach-Object {
    $f = Join-Path $env:TEMP ('tts-measure-' + [guid]::NewGuid().ToString('N') + '.mp3')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $exe --voice cs-CZ-AntoninNeural --rate=-4% --pitch=-5Hz --text 'Test hlasu.' --write-media $f 2>$null | Out-Null
    $sw.Stop()
    Write-Output ('RUN=' + $_ + ' MS=' + $sw.ElapsedMilliseconds + ' SIZE=' + (Get-Item $f).Length)
    Remove-Item $f -Force
}
