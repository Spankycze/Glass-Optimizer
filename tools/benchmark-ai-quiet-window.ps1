$ErrorActionPreference='Stop'
$core=Join-Path (Split-Path $PSScriptRoot -Parent) 'src\SpankyhoOptimizer.Core\bin\Release\net10.0-windows10.0.26100.0\SpankyhoOptimizer.Core.dll'
[void][System.Reflection.Assembly]::LoadFrom($core)
$src=@'
using System; using System.Collections.Generic; using System.Linq; using System.Diagnostics;
using SpankyhoOptimizer.Services;
public static class QuietWindowBench {
 static bool Legacy(IReadOnlyCollection<AiBackgroundLoadSample> samples) {
  if(samples.Count < 4) return false;
  if(samples.Any(x => !double.IsFinite(x.LoadPercent))) return false;
  var ordered=samples.OrderBy(x=>x.Utc).ToArray();
  if(ordered.Any(x=>x.LoadPercent<0 || x.LoadPercent>5)) return false;
  var window=ordered[ordered.Length-1].Utc - ordered[0].Utc;
  if(window<TimeSpan.FromMinutes(2) || window>TimeSpan.FromMinutes(20))return false;
  for(int i=1;i<ordered.Length;i++){var gap=ordered[i].Utc-ordered[i-1].Utc;if(gap<TimeSpan.FromSeconds(20)||gap>TimeSpan.FromMinutes(5))return false;}
  var loads=ordered.Select(x=>x.LoadPercent).OrderBy(x=>x).ToArray();
  var mid=loads.Length%2==0?(loads[loads.Length/2-1]+loads[loads.Length/2])/2:loads[loads.Length/2];
  return mid>=0.75 && loads[loads.Length-1]-loads[0]<=2;
 }
 public static string Run(int loops, int count) {
  var baseTime=new DateTime(2026,10,14,1,0,0,DateTimeKind.Utc);
  var q=new Queue<AiBackgroundLoadSample>();
  for(int i=0;i<count;i++)q.Enqueue(new AiBackgroundLoadSample(baseTime.AddSeconds(i*45),1.0+i*0.05));
  for(int i=0;i<1000;i++){Legacy(q);AiBackgroundPerformancePolicy.HasStableMeasuredPressure(q);}
  (double ms,long bytes,bool ok) Measure(bool modern){
    long before=GC.GetAllocatedBytesForCurrentThread();
    var sw=Stopwatch.StartNew();bool answer=true;
    for(int i=0;i<loops;i++) answer &= modern ? AiBackgroundPerformancePolicy.HasStableMeasuredPressure(q) : Legacy(q);
    sw.Stop();
    return(sw.Elapsed.TotalMilliseconds,GC.GetAllocatedBytesForCurrentThread()-before,answer);
  }
  var legacy=Measure(false);var modern=Measure(true);
  return System.Text.Json.JsonSerializer.Serialize(new {count,iterations=loops,legacyMs=legacy.ms,newMs=modern.ms,legacyAllocBytes=legacy.bytes,newAllocBytes=modern.bytes,legacyResult=legacy.ok,newResult=modern.ok});
 }
}
'@
$runtime=Split-Path ([System.Linq.Enumerable].Assembly.Location)
$refs=@($core,(Join-Path $runtime 'System.Runtime.dll'),(Join-Path $runtime 'System.Collections.dll'),(Join-Path $runtime 'System.Linq.dll'),(Join-Path $runtime 'System.Text.Json.dll'),(Join-Path $runtime 'System.Diagnostics.TraceSource.dll'),(Join-Path $runtime 'System.Runtime.InteropServices.dll'),[object].Assembly.Location) | Select-Object -Unique
Add-Type -TypeDefinition $src -ReferencedAssemblies $refs
[QuietWindowBench]::Run(200000,4)
[QuietWindowBench]::Run(200000,5)
