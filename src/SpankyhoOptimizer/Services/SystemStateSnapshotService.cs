using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;
namespace SpankyhoOptimizer.Services;

public sealed record SnapshotTweak(string Id, bool Value);
public sealed record SnapshotServiceState(string Name, int StartType);
public sealed record SnapshotTaskState(string Path, bool Enabled);
public sealed record OptimizerSystemSnapshot(string Id, DateTimeOffset Created, int WindowsBuild, string PowerPlan,
    SnapshotTweak[] Tweaks, SnapshotServiceState[] Services, SnapshotTaskState[] Tasks, string[] KeepFeatures);
public sealed record SnapshotRestoreOptions(bool Tweaks = true, bool Services = true, bool Tasks = true, bool Power = true);
public static class SystemStateSnapshotService
{
    private static string Root => Path.Combine(LogService.Root, "snapshots");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task<string> CaptureAsync(CancellationToken token = default)
    {
        UserFeatureProfile featureProfile = UserFeatureProfileService.Load();
        if (featureProfile.IsUntrusted)
            throw new InvalidOperationException(
                "Snapshot nelze vytvořit: uložené používané funkce nejsou bezpečně čitelné.");
        Directory.CreateDirectory(Root); string id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        string dir = Path.Combine(Root, id); Directory.CreateDirectory(dir);
        var tweaks = BuildAllTweaks(); await new TweakEngine().DetectAsync(tweaks, token);
        // Tweak detection above already touches the service catalog. Prefer its fresh cache and
        // only scan when absent/stale instead of forcing a second registry enumeration.
        var servicesTask = new ServiceCatalogService().GetAsync(false, token);
        // Snapshot persists only Path + Enabled. Reuse the fresh inventory produced by tweak
        // detection above; forcing another Task Scheduler enumeration adds no snapshot fidelity.
        var tasksTask = new TaskSchedulerService().GetBasicAsync(false, token);
        await Task.WhenAll(servicesTask, tasksTask);
        var snap = new OptimizerSystemSnapshot(id, DateTimeOffset.Now, Environment.OSVersion.Version.Build,
            NativeCore.GetActivePowerScheme().ToString("D"), tweaks.Where(x => x.IsLoaded).Select(x => new SnapshotTweak(x.Id, x.Current)).ToArray(),
            (await servicesTask).Select(x => new SnapshotServiceState(x.Name, x.StartType)).ToArray(),
            (await tasksTask).Select(x => new SnapshotTaskState(x.Path, x.Enabled)).ToArray(), featureProfile.KeepFeatures);
        string file = Path.Combine(dir, "snapshot.json"); await AtomicFileService.WriteTextAsync(file, JsonSerializer.Serialize(snap, Json), token); return file;
    }
    public static OptimizerSystemSnapshot[] List() => ListRecent(int.MaxValue);

    public static OptimizerSystemSnapshot[] ListRecent(int maxCount)
    {
        try
        {
            if (maxCount <= 0 || !Directory.Exists(Root))
                return Array.Empty<OptimizerSystemSnapshot>();

            var files = Directory.EnumerateFiles(Root, "snapshot.json", SearchOption.AllDirectories)
                .Select(path => new
                {
                    Path = path,
                    LastWriteUtc = File.GetLastWriteTimeUtc(path)
                })
                .OrderByDescending(x => x.LastWriteUtc)
                .Take(maxCount)
                .Select(x => x.Path)
                .ToArray();

            return files
                .Select(path =>
                {
                    try { return JsonSerializer.Deserialize<OptimizerSystemSnapshot>(File.ReadAllText(path)); }
                    catch { return null; }
                })
                .Where(x => x != null)
                .Cast<OptimizerSystemSnapshot>()
                .OrderByDescending(x => x.Created)
                .ToArray();
        }
        catch { return Array.Empty<OptimizerSystemSnapshot>(); }
    }
    public static async Task<string[]> RestoreAsync(string id, SnapshotRestoreOptions options, CancellationToken token = default)
    {
        string file = Path.Combine(Root, id, "snapshot.json"); if (!File.Exists(file)) return new[] { "Snapshot nebyl nalezen." };
        var snap = JsonSerializer.Deserialize<OptimizerSystemSnapshot>(await File.ReadAllTextAsync(file, token)) ?? throw new InvalidDataException("Snapshot JSON je neplatný.");
        var errors = new List<string>();
        if (options.Tweaks)
        {
            var map = BuildAllTweaks().ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase); var selected = new List<TweakDefinition>();
            foreach (var s in snap.Tweaks) if (map.TryGetValue(s.Id, out var t)) { try { t.Current = await t.DetectAsync(token); t.IsLoaded = true; t.Pending = s.Value; if (t.IsDirty) selected.Add(t); } catch (Exception ex) { errors.Add(t.Name + ": " + ex.Message); } }
            if (selected.Count > 0) errors.AddRange(await new TweakEngine().ApplyAsync(selected, token));
        }
        if (options.Services)
        {
            var serviceChanges = snap.Services
                .Where(s => s.StartType != 4 || ServiceSafetyService.CanDisable(s.Name))
                .Select(s => new ServiceChangeRequest(s.Name, (uint)s.StartType)).ToArray();
            if (serviceChanges.Length > 0)
            {
                try
                {
                    var serviceErrors = await WorkerClientService.RunAsync<string[]>("service-set-batch",
                        WorkerPayloads.Encode(new ServiceBatchRequest(serviceChanges)), TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
                    errors.AddRange(serviceErrors);
                }
                catch (Exception ex) { errors.Add("Services batch: " + ex.Message); }
            }
        }
        if (options.Tasks)
        {
            var taskChanges = snap.Tasks
                .Where(s => s.Enabled || TaskSafetyService.CanDisable(s.Path))
                .Select(s => (s.Path, s.Enabled)).ToArray();
            if (taskChanges.Length > 0)
            {
                try { errors.AddRange(await new TaskSchedulerService().SetEnabledBatchAsync(taskChanges, token)); }
                catch (Exception ex) { errors.Add("Tasks batch: " + ex.Message); }
            }
        }
        if (options.Power && Guid.TryParse(snap.PowerPlan, out var plan)) { try { NativeCore.SetActivePowerScheme(plan); } catch (Exception ex) { errors.Add("Power plan: " + ex.Message); } }
        UserFeatureProfileService.Save(snap.KeepFeatures); return errors.ToArray();
    }
    public static List<TweakDefinition> BuildAllTweaks()
    {
        var src = new (string, string?)[] { ("system", null), ("gpu", null), ("memory", null), ("storage", null), ("network", null), ("ui", null), ("update", null), ("privacy", null), ("ai", null), ("defender", null), ("components", "Služby"), ("components", "Ovladače"), ("components", "Task Scheduler") };
        var map = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase); foreach (var x in src) foreach (var t in CatalogService.GetTweaks(x.Item1, x.Item2)) map.TryAdd(t.Id, t); return map.Values.ToList();
    }
}
