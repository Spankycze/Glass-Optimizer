$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$source=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\WallpaperThemeService.cs'))
function Extract([string]$a,[string]$b){
 $i=$source.IndexOf($a);if($i -lt 0){throw "Start not found: $a"}
 $j=$source.IndexOf($b,$i+$a.Length);if($j -lt 0){throw "End not found: $b"}
 return $source.Substring($i,$j-$i)
}
$read=Extract 'public static string CurrentWallpaper' 'public static SpankyWallpaperTheme? CurrentTheme'
$restore=Extract 'public static bool RestoreOriginal()' 'private static void EnsurePackagedImage('
$copy=Extract 'private static void EnsurePackagedImage(' 'private static void CaptureOriginalWallpaperOnce()'
foreach($segment in @($read,$restore,$copy)){
 if($segment -match 'catch\s*\{'){throw 'Wallpaper service silently swallows an unclassified exception'}
 if($segment -notmatch 'catch\s*\(Exception ex\)\s*when\s*\('){throw 'Wallpaper fallback must only catch expected failures'}
 if($segment -notmatch 'LogService\.Error\(\"themes\",'){throw 'Missing wallpaper failure diagnosis'}
}
if($read -notmatch 'return string\.Empty' -or $restore -notmatch 'return false' -or $copy -notmatch 'copy = true'){throw 'Safe fallback behavior changed'}
Write-Output 'WALLPAPER_FAILURE_DIAGNOSTICS_GUARD_OK paths=3'
