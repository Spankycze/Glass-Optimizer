using System;using System.Collections.Generic;using System.Diagnostics;using System.Linq;using SpankyhoOptimizer.Services;
static bool Old(IReadOnlyCollection<AiBackgroundLoadSample> s){
 if(s.Count<4||s.Any(x=>!double.IsFinite(x.LoadPercent)))return false;
 var a=s.OrderBy(x=>x.Utc).ToArray();
 if(a.Any(x=>x.LoadPercent<0||x.LoadPercent>5))return false;
 var duration=a[^1].Utc-a[0].Utc;if(duration<TimeSpan.FromMinutes(2)||duration>TimeSpan.FromMinutes(20))return false;
 for(int i=1;i<a.Length;i++){var g=a[i].Utc-a[i-1].Utc;if(g<TimeSpan.FromSeconds(20)||g>TimeSpan.FromMinutes(5))return false;}
 var v=a.Select(x=>x.LoadPercent).OrderBy(x=>x).ToArray();
 var m=v.Length%2==0?(v[v.Length/2-1]+v[v.Length/2])/2:v[v.Length/2];
 return m>=.75&&v[^1]-v[0]<=2;
}
const int n=300000; foreach(int size in new[]{4,5}){
 var q=new Queue<AiBackgroundLoadSample>();var t=new DateTime(2026,10,14,0,0,0,DateTimeKind.Utc);
 for(int i=0;i<size;i++)q.Enqueue(new AiBackgroundLoadSample(t.AddSeconds(i*45),1+i*.05));
 for(int i=0;i<4000;i++){Old(q);AiBackgroundPerformancePolicy.HasStableMeasuredPressure(q);}
 (double ms,long alloc,bool answer) Measure(bool modern){
  long before=GC.GetAllocatedBytesForCurrentThread();var sw=Stopwatch.StartNew();bool answer=true;
  for(int i=0;i<n;i++)answer &= modern?AiBackgroundPerformancePolicy.HasStableMeasuredPressure(q):Old(q);
  sw.Stop();return(sw.Elapsed.TotalMilliseconds,GC.GetAllocatedBytesForCurrentThread()-before,answer);
 }
 var a=Measure(false);var b=Measure(true);
 Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{size,n,oldMs=a.ms,newMs=b.ms,oldBytes=a.alloc,newBytes=b.alloc,oldResult=a.answer,newResult=b.answer}));
}
// Same median algorithm, compared through the interface-typed live Queue<T>.
static double OldMedian(IReadOnlyCollection<AiBackgroundLoadSample> samples){
 Span<double> values=stackalloc double[8];int c=0;
 foreach(var s in samples)if(double.IsFinite(s.LoadPercent))values[c++]=s.LoadPercent;
 if(c==0)return double.NaN;var sorted=values[..c];sorted.Sort();
 return c%2==0?(sorted[c/2-1]+sorted[c/2])/2:sorted[c/2];
}
foreach(int size in new[]{4,5,8}){
 IReadOnlyCollection<AiBackgroundLoadSample> samples=new Queue<AiBackgroundLoadSample>(Enumerable.Range(0,size).Select(i=>new AiBackgroundLoadSample(new DateTime(2026,10,14,0,0,0,DateTimeKind.Utc).AddSeconds(i*45),1+i*.05)));
 for(int i=0;i<2000;i++){OldMedian(samples);AiBackgroundPerformancePolicy.MedianLoad(samples);}
 (double ms,long bytes,double value) MeasureMedian(bool modern){
  long before=GC.GetAllocatedBytesForCurrentThread();var sw=Stopwatch.StartNew();double last=0;
  for(int i=0;i<n;i++)last=modern?AiBackgroundPerformancePolicy.MedianLoad(samples):OldMedian(samples);
  sw.Stop();return(sw.Elapsed.TotalMilliseconds,GC.GetAllocatedBytesForCurrentThread()-before,last);
 }
 var before=MeasureMedian(false);var after=MeasureMedian(true);
 Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{operation="median",size,n,oldMs=before.ms,newMs=after.ms,oldBytes=before.bytes,newBytes=after.bytes,oldMedian=before.value,newMedian=after.value}));
}

// Controlled 4-sample steady update: prior LINQ Last vs current direct Queue<T> traversal.
static void LegacyAdd(Queue<AiBackgroundLoadSample> samples, DateTime now){
 if(samples.Count>0){
  var gap=now-samples.Last().Utc;
  if(gap<TimeSpan.Zero||gap>TimeSpan.FromMinutes(5))samples.Clear();
  else if(gap<TimeSpan.FromSeconds(20))return;
 }
 samples.Enqueue(new AiBackgroundLoadSample(now,1.2));
 while(samples.Count>4)samples.Dequeue();
}
foreach(bool optimized in new[]{false,true}){
 var start=new DateTime(2026,10,14,1,0,0,DateTimeKind.Utc);
 var q=new Queue<AiBackgroundLoadSample>();
 for(int i=0;i<4;i++)q.Enqueue(new AiBackgroundLoadSample(start.AddSeconds(i*45),1.2));
 long allocation=GC.GetAllocatedBytesForCurrentThread();
 var watch=Stopwatch.StartNew();
 for(int i=0;i<300000;i++){
  var now=start.AddSeconds((i+4)*45);
  if(optimized)AiBackgroundPerformancePolicy.AddSample(q,true,1.2,now);
  else LegacyAdd(q,now);
 }
 watch.Stop();
 Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{operation="queue-add",optimized,loops=300000,elapsedMs=watch.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocation,count=q.Count}));
}

// Profile-key lookup microbenchmark: allocation and time for enum formatting vs canonical key.
static string Key(AiWorkloadClass w) => w switch { AiWorkloadClass.Idle => "Idle", AiWorkloadClass.Background => "Background", AiWorkloadClass.Interactive => "Interactive", AiWorkloadClass.Game => "Game", _ => "Unknown" };
foreach(bool optimized in new[]{false,true}){
 var keys=new[]{AiWorkloadClass.Idle,AiWorkloadClass.Background,AiWorkloadClass.Interactive,AiWorkloadClass.Game,AiWorkloadClass.Unknown};
 for(int i=0;i<5000;i++){_ = optimized?Key(keys[i%5]):keys[i%5].ToString();}
 long before=GC.GetAllocatedBytesForCurrentThread(); var sw=Stopwatch.StartNew();int checksum=0;
 for(int i=0;i<300000;i++){string k=optimized?Key(keys[i%5]):keys[i%5].ToString();checksum+=k.Length;}
 sw.Stop();Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{operation="workload-key",optimized,iterations=300000,elapsedMs=sw.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-before,checksum}));
}

// FanAgent atomic-file path setup: old per-write concatenation vs cached immutable path.
const int pathLoops=300000;
string healthPath=@"C:\Users\spnka\AppData\Local\SpankyhoOptimizer\fan-agent-health.json";
string cachedTemp=healthPath+".tmp";
foreach(bool cached in new[]{false,true}){
 for(int i=0;i<1000;i++){_ = cached?cachedTemp:healthPath+".tmp";}
 long bytes=GC.GetAllocatedBytesForCurrentThread();var sw=Stopwatch.StartNew();int checksum=0;
 for(int i=0;i<pathLoops;i++){string temp=cached?cachedTemp:healthPath+".tmp";checksum+=temp.Length;}
 sw.Stop();Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{operation="fan-temp-path",cached,iterations=pathLoops,elapsedMs=sw.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-bytes,checksum}));
}

// Compare real JSON file parsing pathways with identical telemetry content.
string telemetryJsonPath=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"spanky-telemetry-parse-bench-"+Guid.NewGuid().ToString("N")+".json");
try {
 var sensor=new HardwareTelemetryBridgeSnapshot(DateTime.UtcNow,42,"15.4.1145",true,56,"CPU package","Ryzen 7",12,3800,60,1);
 System.IO.File.WriteAllText(telemetryJsonPath,System.Text.Json.JsonSerializer.Serialize(sensor));
 foreach(bool streamMode in new[]{false,true}) {
  for(int i=0;i<100;i++){if(streamMode){using var s=System.IO.File.OpenRead(telemetryJsonPath);_ = System.Text.Json.JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(s);}else{_ = System.Text.Json.JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(System.IO.File.ReadAllText(telemetryJsonPath));}}
  const int loops=2000;long begin=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();double sum=0;
  for(int i=0;i<loops;i++){HardwareTelemetryBridgeSnapshot? value;if(streamMode){using var s=new System.IO.FileStream(telemetryJsonPath,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.ReadWrite|System.IO.FileShare.Delete);value=System.Text.Json.JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(s);}else{value=System.Text.Json.JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(System.IO.File.ReadAllText(telemetryJsonPath));}sum+=value?.CpuTempC??0;}
  timer.Stop();Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{operation="telemetry-json-file-read",streamMode,loops,ms=timer.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-begin,checksum=sum}));
 }
} finally {System.IO.File.Delete(telemetryJsonPath);}

// Fan profile JSON: compare equivalent file-backed deserialization with a real default profile.
string fanBenchPath=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"spanky-fan-bench-"+Guid.NewGuid().ToString("N")+".json");
try {
 System.IO.File.WriteAllText(fanBenchPath,System.Text.Json.JsonSerializer.Serialize(FanProfileService.Default));
 foreach(bool streamMode in new[]{false,true}){
  for(int i=0;i<50;i++){if(streamMode){using var s=System.IO.File.OpenRead(fanBenchPath);_ = System.Text.Json.JsonSerializer.Deserialize<FanProfile>(s);}else{_ = System.Text.Json.JsonSerializer.Deserialize<FanProfile>(System.IO.File.ReadAllText(fanBenchPath));}}
  const int loops=1000;long alloc=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();long sum=0;
  for(int i=0;i<loops;i++){FanProfile? v;if(streamMode){using var s=new System.IO.FileStream(fanBenchPath,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.ReadWrite|System.IO.FileShare.Delete);v=System.Text.Json.JsonSerializer.Deserialize<FanProfile>(s);}else{v=System.Text.Json.JsonSerializer.Deserialize<FanProfile>(System.IO.File.ReadAllText(fanBenchPath));}sum+=v?.TargetRpm??0;}
  timer.Stop();Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{operation="fan-profile-json",streamMode,loops,ms=timer.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-alloc,checksum=sum}));
 }
}finally{System.IO.File.Delete(fanBenchPath);}

// AI fallback baseline: old per-tick record allocation vs identity-keyed reuse.
var globalModel=new BenchmarkModel(40,55,60,25,1700,3500,DateTime.UtcNow);
BenchmarkBaseline? cachedGlobal=null;BenchmarkModel? lastGlobal=null;
foreach(bool reuse in new[]{false,true}){
 cachedGlobal=null;lastGlobal=null; const int loops=300000;long start=GC.GetAllocatedBytesForCurrentThread();var t=Stopwatch.StartNew();double checksum=0;
 for(int i=0;i<loops;i++){
  BenchmarkBaseline b;
  if(reuse&&ReferenceEquals(lastGlobal,globalModel)&&cachedGlobal!=null){b=cachedGlobal;}
  else{b=new BenchmarkBaseline(globalModel.Samples,globalModel.AvgTempC,globalModel.AvgPowerW,globalModel.AvgLoadPercent,globalModel.AvgRpm,globalModel.AvgClockMHz,globalModel.UpdatedUtc);if(reuse){lastGlobal=globalModel;cachedGlobal=b;}}
  checksum+=b.AvgTempC;
 }
 t.Stop();Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{operation="ai-global-baseline",reuse,loops,ms=t.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-start,checksum}));
}

file sealed record BenchmarkModel(int Samples,double AvgTempC,double AvgPowerW,double AvgLoadPercent,double AvgRpm,double AvgClockMHz,DateTime UpdatedUtc);
file sealed record BenchmarkBaseline(int Samples,double AvgTempC,double AvgPowerW,double AvgLoadPercent,double AvgRpm,double AvgClockMHz,DateTime UpdatedUtc);
