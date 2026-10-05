$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
Add-Type -AssemblyName System.Drawing
$work=Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign'
$before=[Drawing.Bitmap]::new("$work\before\dark-overview.png")
$after=[Drawing.Bitmap]::new("$work\after\dark-overview.png")
$regions=@(@('header',232,38,1056,180),@('cpu-art',547,481,208,180),@('gpu-art',1079,481,208,180),@('ram-art',547,677,208,174),@('storage-art',1079,677,208,174))
$out=foreach($region in $regions){
 $different=0
 for($y=$region[2];$y -lt ($region[2]+$region[4]);$y++){for($x=$region[1];$x -lt ($region[1]+$region[3]);$x++){if($before.GetPixel($x,$y).ToArgb() -ne $after.GetPixel($x,$y).ToArgb()){$different++}}}
 "$($region[0]) changedPixels=$different"
 if($different -gt 0 -and $region[0] -eq 'header'){throw 'Persistent header changed'}
}
$before.Dispose();$after.Dispose()
[IO.File]::WriteAllLines("$work\pixel-audit.txt",@($out))
Write-Output 'HEADER_PIXELS_OK overview-art-differences-recorded'