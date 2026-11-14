$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$files=@(Get-ChildItem (Join-Path $root 'src\SpankyhoOptimizer') -Recurse -File -Filter '*.cs' |
 Where-Object {$_.FullName -notmatch '\\(bin|obj)\\'} |
 ForEach-Object {[IO.Path]::GetRelativePath($root,$_.FullName)})
$approved=@('user32.dll','kernel32.dll','pdh.dll','advapi32.dll','setupapi.dll',
 'hid.dll','wintrust.dll','powrprof.dll','iphlpapi.dll','shell32.dll','cfgmgr32.dll',
 'crypt32.dll','ntdll.dll','psapi.dll','comctl32.dll','dwmapi.dll','gdi32.dll',
 'winspool.drv','shcore.dll')
$attribute='[System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]'
$checked=0
$nativeFiles=0
foreach($rel in $files){
 $path=Join-Path $root $rel
 if(-not(Test-Path -LiteralPath $path)){throw "NATIVE_DLL_GUARD missing $rel"}
 $lines=[IO.File]::ReadAllLines($path)
 $found=0
 for($i=0;$i -lt $lines.Count;$i++){
  $match=[regex]::Match($lines[$i],'\[(?:System\.Runtime\.InteropServices\.)?DllImport\("([^"]+)"')
  if(-not $match.Success){continue}
  $found++;$checked++
  $dll=$match.Groups[1].Value.ToLowerInvariant()
  # NVML is part of the signed NVIDIA driver, not a redistributable.
  # Approve it ONLY from this GPU provider; never weaken the global DLL gate.
  if($dll -eq 'nvml.dll' -and $rel -ine 'src\SpankyhoOptimizer\Services\GpuNvmlUsageService.cs'){
   throw "NATIVE_DLL_GUARD NVML restricted to GPU provider: $rel"
  }
  if($approved -cnotcontains $dll -and $dll -cne 'nvml.dll'){
   throw "NATIVE_DLL_GUARD unapproved library $rel line=$($i+1) dll=$dll"
  }
  if($i -eq 0 -or $lines[$i-1].Trim() -cne $attribute){
   throw "NATIVE_DLL_GUARD missing System32 restriction $rel line=$($i+1) dll=$dll"
  }
 }
 if($found -gt 0){$nativeFiles++}
}
if($checked -lt 137 -or $nativeFiles -lt 27){throw "NATIVE_DLL_GUARD missing declarations: imports=$checked files=$nativeFiles minimum=137/27"}
Write-Output "NATIVE_DLL_SYSTEM32_GUARD_OK imports=$checked files=$nativeFiles totalScanned=$($files.Count)"
