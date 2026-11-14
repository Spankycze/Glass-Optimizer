$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=Get-Content (Join-Path $root 'build-dev.ps1') -Raw
if(!$source.Contains('if($null -eq $restoreText)') -or !$source.Contains('$restoreText.Trim()')){
 throw 'RESTORE_NULL_SAFETY_CONTRACT_MISSING'
}
$path=Join-Path ([IO.Path]::GetTempPath()) ('spanky-restore-test-'+[guid]::NewGuid().ToString('N'))
try{
 $text=Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
 $value=if($null -eq $text){''}else{$text.Trim()}
 if($value.Trim() -ne ''){throw 'MISSING_RESTORE_CACHE_NOT_EMPTY'}
 [IO.File]::WriteAllText($path,'')
 $text=Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
 $value=if($null -eq $text){''}else{$text.Trim()}
 if($value.Trim() -ne ''){throw 'EMPTY_RESTORE_CACHE_NOT_EMPTY'}
 [IO.File]::WriteAllText($path,"  AbCd1234  ")
 $text=Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
 $value=if($null -eq $text){''}else{$text.Trim()}
 if($value.Trim() -ne 'AbCd1234'){throw 'EXISTING_RESTORE_HASH_NOT_TRIMMED'}
 'DEV_RESTORE_NULL_CACHE_REGRESSIONS_PASS cases=3 realCacheUnchanged=true'
}finally{if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
