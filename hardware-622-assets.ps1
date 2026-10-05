$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
Add-Type -AssemblyName PresentationCore
foreach($name in @('cpu','gpu','ram','nvme')){
 $raw=[Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $PWD "artifacts\hardware-622\$name.b64")))
 $stream=New-Object IO.MemoryStream(,$raw)
 $bmp=New-Object Windows.Media.Imaging.BitmapImage
 $bmp.BeginInit(); $bmp.CacheOption=[Windows.Media.Imaging.BitmapCacheOption]::OnLoad; $bmp.DecodePixelWidth=480; $bmp.StreamSource=$stream; $bmp.EndInit(); $bmp.Freeze(); $stream.Dispose()
 $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
 $target=Join-Path $PWD "src\SpankyhoOptimizer\Assets\Hardware\$name.png"
 $output=[IO.File]::Create($target); $encoder.Save($output); $output.Dispose()
 Write-Output ("HARDWARE_ASSET_READY name="+$name+" width="+$bmp.PixelWidth+" bytes="+(Get-Item $target).Length)
}
