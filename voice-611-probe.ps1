$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force 'artifacts\voice-611'|Out-Null
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Speech
$settings=Get-Content 'C:\Users\spnka\AppData\Local\SpankyhoOptimizer\voice-cloud-ai.json' -Raw|ConvertFrom-Json
$key=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($settings.EncryptedApiKey),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
$synth=New-Object System.Speech.Synthesis.SpeechSynthesizer
$wav=Join-Path (Get-Location) 'artifacts\voice-611\probe.wav'
$synth.SetOutputToWaveFile($wav)
$synth.Speak('Hej vole. Kolik je hodin?')
$synth.Dispose()
$body=@{contents=@(@{role='user';parts=@(@{inlineData=@{mimeType='audio/wav';data=[Convert]::ToBase64String([IO.File]::ReadAllBytes($wav))}})});generationConfig=@{audioTranscriptionConfig=@{languageCodes=@('cs-CZ');customVocabulary=@('Spanky')}}}|ConvertTo-Json -Depth 10 -Compress
try {
 $response=Invoke-WebRequest -UseBasicParsing -Uri 'https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-transcribe:generateContent' -Method Post -Headers @{'x-goog-api-key'=$key} -ContentType 'application/json' -Body $body -TimeoutSec 18
 $status=[int]$response.StatusCode
 $raw=$response.Content
} catch {
 if($null -eq $_.Exception.Response){throw 'Probe network failed'}
 $status=[int]$_.Exception.Response.StatusCode
 $reader=New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
 $raw=$reader.ReadToEnd();$reader.Dispose()
}
[IO.File]::WriteAllText((Join-Path (Get-Location) 'artifacts\voice-611\primary-response.json'),$raw)
$parsed=$raw|ConvertFrom-Json
'VOICE_PRIMARY_PROBE status='+$status+' keys='+(($parsed.PSObject.Properties.Name)-join ',')
if($parsed.error){'ERROR_STATUS='+$parsed.error.status}
$key=$null
