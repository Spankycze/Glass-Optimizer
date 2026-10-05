param(
  [switch]$UpdateBaseline,
  [double]$MeanThreshold=4.0,
  [double]$ChangedPercentThreshold=5.0,
  [string]$ExePath=''
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=if([string]::IsNullOrWhiteSpace($ExePath)){
  Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
}else{[IO.Path]::GetFullPath($ExePath)}
$visual=Join-Path $root 'artifacts\visual'
$current=Join-Path $visual 'current'
$baseline=Join-Path $visual 'baseline'
$diffDir=Join-Path $visual 'diff'
New-Item -ItemType Directory -Force -Path $current,$baseline,$diffDir|Out-Null
Get-ChildItem $current -File -Filter '*.png' -ErrorAction SilentlyContinue|Remove-Item -Force
Get-ChildItem $diffDir -File -Filter '*.png' -ErrorAction SilentlyContinue|Remove-Item -Force
if(-not(Test-Path $exe)){throw 'Debug EXE missing. Run build-fast first.'}

$commonData=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$captureSource=Join-Path $commonData 'SpankyhoOptimizer\visual-current'
$sw=[Diagnostics.Stopwatch]::StartNew()
$proc=Start-Process -FilePath $exe -ArgumentList '--visual-capture-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
if(-not $proc.WaitForExit(120000)){try{Stop-Process -Id $proc.Id -Force}catch{};throw 'Visual capture timeout'}
$sw.Stop()
if($proc.ExitCode -ne 0){throw ('Visual capture failed exit='+$proc.ExitCode)}
if(-not(Test-Path $captureSource)){throw ('Capture source missing: '+$captureSource)}
Copy-Item (Join-Path $captureSource '*.png') $current -Force

$shots=@(Get-ChildItem $current -File -Filter '*.png'|Sort-Object Name)
if($shots.Count -ne 6){throw ('Expected 6 visual snapshots, got '+$shots.Count)}

if($UpdateBaseline -or @(Get-ChildItem $baseline -File -Filter '*.png' -ErrorAction SilentlyContinue).Count -ne 6){
  Get-ChildItem $baseline -File -Filter '*.png' -ErrorAction SilentlyContinue|Remove-Item -Force
  Copy-Item (Join-Path $current '*.png') $baseline
  Write-Output ('VISUAL_BASELINE_SAVED shots='+$shots.Count+' captureMs='+[math]::Round($sw.Elapsed.TotalMilliseconds,1))
  return
}

Add-Type -AssemblyName System.Drawing
$results=[Collections.Generic.List[object]]::new()
$failed=$false
foreach($shot in $shots){
  $basePath=Join-Path $baseline $shot.Name
  if(-not(Test-Path $basePath)){throw ('Missing baseline: '+$shot.Name)}
  $a=[Drawing.Bitmap]::new($basePath)
  $b=[Drawing.Bitmap]::new($shot.FullName)
  try{
    $size=[Drawing.Size]::new(160,100)
    $aa=[Drawing.Bitmap]::new($a,$size)
    $bb=[Drawing.Bitmap]::new($b,$size)
    $dd=[Drawing.Bitmap]::new(160,100)
    try{
      [long]$sum=0;[int]$changed=0;$total=160*100
      for($y=0;$y -lt 100;$y++){
        for($x=0;$x -lt 160;$x++){
          $ca=$aa.GetPixel($x,$y);$cb=$bb.GetPixel($x,$y)
          $delta=[math]::Abs([int]$ca.R-[int]$cb.R)+[math]::Abs([int]$ca.G-[int]$cb.G)+[math]::Abs([int]$ca.B-[int]$cb.B)
          $sum+=$delta
          if($delta -gt 75){$changed++}
          $v=[math]::Min(255,[int]($delta/3))
          $dd.SetPixel($x,$y,[Drawing.Color]::FromArgb(255,$v,0,0))
        }
      }

      $mean=[math]::Round($sum/(3.0*$total),3)
      $pct=[math]::Round(100.0*$changed/$total,3)
      $fail=$mean -gt $MeanThreshold -or $pct -gt $ChangedPercentThreshold
      if($fail){
        $failed=$true
        $dd.Save((Join-Path $diffDir $shot.Name),[Drawing.Imaging.ImageFormat]::Png)
      }
      $results.Add([pscustomobject]@{file=$shot.Name;meanRgbDelta=$mean;changedPercent=$pct;fail=$fail})|Out-Null
      Write-Output ('[VISUAL] '+$shot.Name+' mean='+$mean+' changed='+$pct+'% '+$(if($fail){'FAIL'}else{'OK'}))
    }finally{$aa.Dispose();$bb.Dispose();$dd.Dispose()}
  }finally{$a.Dispose();$b.Dispose()}
}
$report=Join-Path $visual 'latest.json'
$tmp=$report+'.tmp'
[IO.File]::WriteAllText($tmp,([ordered]@{
  generatedAt=(Get-Date).ToString('o');captureMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1)
  meanThreshold=$MeanThreshold;changedPercentThreshold=$ChangedPercentThreshold
  results=@($results)
}|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false))
Move-Item $tmp $report -Force
if($failed){throw ('Visual regression detected. Diff images: '+$diffDir)}
Write-Output ('VISUAL_REGRESSION_OK shots='+$shots.Count+' captureMs='+[math]::Round($sw.Elapsed.TotalMilliseconds,1))
