using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;
namespace SpankyhoOptimizer.Services {
  public sealed record HardwareTelemetryBridgeSnapshot(DateTime UpdatedUtc, int ProcessId,
      string Version, bool Elevated, double? CpuTempC, string CpuSensor, string CpuName,
      double? CpuLoadPercent, double? EffectiveClockMHz, double? PackagePowerW, int? SchemaVersion = null);
}
// Reference implementation equivalent to the previous no-TTL FileInfo.Refresh
// cache, used only for local apples-to-apples microbenchmarks.
internal sealed class PriorCache(string path) {
  private readonly object gate = new();
  private readonly FileInfo info = new(path);
  private DateTime lastWriteUtc = DateTime.MinValue;
  private long lastLength = -1;
  private HardwareTelemetryBridgeSnapshot? cached;
  public HardwareTelemetryBridgeSnapshot? Read() {
    lock(gate) {
      info.Refresh();
      if(!info.Exists) return null;
      var time=info.LastWriteTimeUtc;
      var length=info.Length;
      if(cached!=null && time==lastWriteUtc && length==lastLength) return cached;
      var snapshot=JsonSerializer.Deserialize<HardwareTelemetryBridgeSnapshot>(File.ReadAllText(path));
      if(snapshot is null) return null;
      cached=snapshot;lastWriteUtc=time;lastLength=length;
      return snapshot;
    }
  }
}

internal static class Program {
  private static int tests;
  static void Check(bool ok, string name) {
    if(!ok) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    tests++;
  }
  static void Write(string path, string version) =>
    File.WriteAllText(path, JsonSerializer.Serialize(
      new HardwareTelemetryBridgeSnapshot(DateTime.UtcNow, 1, version, false, 45, "CPU", "Ryzen", 8, 4200, 20, 1)));
  static void Main() {
    string dir=Path.Combine(Path.GetTempPath(),"spanky-cache-regression-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string path=Path.Combine(dir,"telemetry.json");
    try {
      var cache=new HardwareTelemetrySnapshotReadCache(path);
      Check(cache.Read() is null,"missing-returns-null");
      Write(path,"first");
      Check(cache.Read() is null,"missing-cache-bounded");
      Thread.Sleep(235);
      var first=cache.Read();
      Check(first?.Version=="first","new-file-detected");
      Check(ReferenceEquals(first,cache.Read()),"same-snapshot-reused");
      int errors=0;
      Parallel.For(0,2000,_=>{ if(cache.Read()?.Version!="first") Interlocked.Increment(ref errors); });
      Check(errors==0,"parallel-reads-stable");
      Write(path,"second-version-longer");
      Check(cache.Read()?.Version=="first","metadata-poll-throttled");
      Thread.Sleep(235);
      Check(cache.Read()?.Version=="second-version-longer","modified-file-detected");
      File.Delete(path);
      Thread.Sleep(235);
      Check(cache.Read() is null,"deletion-clears-cache");
      Write(path,"restored");
      Thread.Sleep(235);
      Check(cache.Read()?.Version=="restored","recreated-file-detected");
      // Same source file, same JSON and same number of calls. Warm both caches,
      // then alternate order to reduce first-run CPU/cache warming bias.
      const int calls = 10000;
      var oldCache = new PriorCache(path);
      if(oldCache.Read()?.Version != "restored") throw new Exception("Prior baseline invalid");
      double[] oldMs = new double[3], newMs = new double[3];
      for(int sample=0;sample<3;sample++) {
        static double Measure(Func<HardwareTelemetryBridgeSnapshot?> read,int n) {
          var sw=Stopwatch.StartNew();
          for(int i=0;i<n;i++) if(read() is null) throw new Exception("Unexpected cache miss");
          sw.Stop();
          return sw.Elapsed.TotalMilliseconds;
        }
        if(sample%2==0) {
          oldMs[sample] = Measure(oldCache.Read,calls);
          newMs[sample] = Measure(cache.Read,calls);
        } else {
          newMs[sample] = Measure(cache.Read,calls);
          oldMs[sample] = Measure(oldCache.Read,calls);
        }
      }
      Array.Sort(oldMs); Array.Sort(newMs);
      Console.WriteLine("BASELINE_FILESTAT_10K_MEDIAN_MS "+oldMs[1].ToString("F2"));
      Console.WriteLine("OPTIMIZED_CACHE_10K_MEDIAN_MS "+newMs[1].ToString("F2"));
      Console.WriteLine("CACHE_MICROBENCH_SPEEDUP_X "+(oldMs[1]/Math.Max(newMs[1],0.001)).ToString("F2"));
      Check(tests==9,"expected-test-count");
      Console.WriteLine("TELEMETRY_CACHE_TESTS_OK 10/10");
    } finally { Directory.Delete(dir,true); }
  }
}