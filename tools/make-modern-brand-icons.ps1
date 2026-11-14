$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
$app=Join-Path $root 'src\SpankyhoOptimizer\Assets'
$installer=Join-Path $root 'installer'
function RoundedPath([int]$x,[int]$y,[int]$w,[int]$h,[int]$r){
  $p=[Drawing.Drawing2D.GraphicsPath]::new()
  $d=2*$r
  $p.AddArc($x,$y,$d,$d,180,90)
  $p.AddArc($x+$w-$d,$y,$d,$d,270,90)
  $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90)
  $p.AddArc($x,$y+$h-$d,$d,$d,90,90)
  $p.CloseFigure()
  return $p
}
function Brand([string]$state){
  $bmp=[Drawing.Bitmap]::new(256,256,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g=[Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([Drawing.Color]::Transparent)
  $r=[Drawing.Rectangle]::new(12,12,232,232)
  $path=RoundedPath 12 12 232 232 54

  # Multicolor performance core: violet/cyan/magenta/orange/teal, with high
  # contrast dark cutouts for legibility in a 16px Windows notification tray.
  $bg=[Drawing.Drawing2D.LinearGradientBrush]::new($r,
    [Drawing.ColorTranslator]::FromHtml('#4224A8'),
    [Drawing.ColorTranslator]::FromHtml('#05132F'),57)
  $g.FillPath($bg,$path)
  $g.SetClip($path)
  $g.FillEllipse([Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(70,7,218,248)),3,12,148,140)
  $g.FillEllipse([Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(78,255,24,190)),142,-8,138,132)
  $g.FillEllipse([Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(60,250,155,22)),150,140,156,128)
  $g.ResetClip()
  $outer=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#3544B2'),5)
  $g.DrawPath($outer,$path)

  # Four unmistakable colored rotor segments around a geometric central hub.
  $ring=[Drawing.Rectangle]::new(38,38,180,180)
  $back=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#050C26'),32)
  $g.DrawEllipse($back,$ring)
  foreach($arc in @(
     @{Start=185; Sweep=76; Color='#04EBFF'},
     @{Start=275; Sweep=75; Color='#FF35C4'},
     @{Start=5; Sweep=75; Color='#FFB32B'},
     @{Start=95; Sweep=75; Color='#36F7A0'}
  )){
    $p=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($arc.Color),26)
    $p.StartCap=[Drawing.Drawing2D.LineCap]::Round
    $p.EndCap=[Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($p,$ring,[single]$arc.Start,[single]$arc.Sweep)
    $p.Dispose()
  }

  # Angular central hardware die. No thin white circle that would wash out in tray.
  $hex=[Drawing.Point[]]@(
    [Drawing.Point]::new(128,68),[Drawing.Point]::new(180,100),
    [Drawing.Point]::new(180,157),[Drawing.Point]::new(128,188),
    [Drawing.Point]::new(76,157),[Drawing.Point]::new(76,100)
  )
  $hub=[Drawing.Drawing2D.LinearGradientBrush]::new(
    [Drawing.Rectangle]::new(74,65,110,130),
    [Drawing.ColorTranslator]::FromHtml('#1C2A75'),
    [Drawing.ColorTranslator]::FromHtml('#06162F'),95)
  $g.FillPolygon($hub,$hex)
  $rim=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#2E8BD6'),6)
  $g.DrawPolygon($rim,$hex)

  # Oversized sharp kinetic mark, readable at 16px.
  $bolt=[Drawing.Point[]]@(
    [Drawing.Point]::new(131,76),[Drawing.Point]::new(99,136),
    [Drawing.Point]::new(125,136),[Drawing.Point]::new(116,181),
    [Drawing.Point]::new(164,116),[Drawing.Point]::new(135,116),
    [Drawing.Point]::new(146,76)
  )
  $boltBrush=[Drawing.Drawing2D.LinearGradientBrush]::new(
    [Drawing.Rectangle]::new(90,75,80,110),
    [Drawing.ColorTranslator]::FromHtml('#FFFFFF'),
    [Drawing.ColorTranslator]::FromHtml('#9CFFFB'),80)
  $g.FillPolygon($boltBrush,$bolt)
  $boltBrush.Dispose()
  $rim.Dispose()
  $hub.Dispose()
  $back.Dispose()
  $outer.Dispose()
  $bg.Dispose()

  if($state -ne 'Normal'){
    $status=if($state -eq 'Warning'){'#FFB926'}else{'#FF3559'}
    $badgeFill=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($status))
    $badgeOutline=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#08112B'),9)
    $g.FillEllipse($badgeFill,176,174,66,66)
    $g.DrawEllipse($badgeOutline,176,174,66,66)
    $sign=[Drawing.Pen]::new([Drawing.Color]::White,9)
    $sign.StartCap=[Drawing.Drawing2D.LineCap]::Round
    $sign.EndCap=[Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($sign,209,189,209,212)
    $g.FillEllipse([Drawing.Brushes]::White,204,222,10,10)
    $badgeFill.Dispose()
    $badgeOutline.Dispose()
    $sign.Dispose()
  }
  $g.Dispose()
  return $bmp
}
function SaveIco([Drawing.Bitmap]$base,[string]$out){
  $sizes=16,20,24,32,40,48,64,128,256
  $frames=[Collections.Generic.List[byte[]]]::new()
  foreach($size in $sizes){
    $b=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($base,[Drawing.Rectangle]::new(0,0,$size,$size),0,0,256,256,[Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $ms=[IO.MemoryStream]::new()
    $b.Save($ms,[Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($ms.ToArray())
    $ms.Dispose()
    $b.Dispose()
  }
  $file=[IO.File]::Create($out)
  $bw=[IO.BinaryWriter]::new($file)
  $bw.Write([uint16]0)
  $bw.Write([uint16]1)
  $bw.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($i=0;$i -lt $sizes.Count;$i++){
    $size=$sizes[$i]
    $bw.Write([byte]$(if($size -eq 256){0}else{$size}))
    $bw.Write([byte]$(if($size -eq 256){0}else{$size}))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$i].Length)
    $bw.Write([uint32]$offset)
    $offset+=$frames[$i].Length
  }
  foreach($buf in $frames){$bw.Write($buf)}
  $bw.Dispose()
}
$normal=Brand 'Normal'
$normal.Save((Join-Path $app 'AppLogo.png'),[Drawing.Imaging.ImageFormat]::Png)
SaveIco $normal (Join-Path $app 'App.ico')
SaveIco $normal (Join-Path $installer 'App.ico')
$normal.Dispose()
foreach($s in @('Warning','Error')){
  $bmp=Brand $s
  SaveIco $bmp (Join-Path $app ('Tray'+$s+'.ico'))
  $bmp.Dispose()
}
foreach($path in @((Join-Path $app 'App.ico'),(Join-Path $installer 'App.ico'),(Join-Path $app 'AppLogo.png'),(Join-Path $app 'TrayWarning.ico'),(Join-Path $app 'TrayError.ico'))){
  $fi=Get-Item -LiteralPath $path
  Write-Host ('BRAND_ASSET_OK {0} bytes={1}' -f $fi.Name,$fi.Length)
}
