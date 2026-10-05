using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Build.Logging.StructuredLogger;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: DiagnosticsHarness dump <path> | binlog <path>");
    return 2;
}

string mode = args[0].ToLowerInvariant();
string path = Path.GetFullPath(args[1]);

if (mode == "dump")
{
    using DataTarget target = DataTarget.LoadDump(path);
    ClrInfo clr = target.ClrVersions.FirstOrDefault() ?? throw new InvalidOperationException("No CLR found in dump.");
    ClrRuntime runtime = clr.CreateRuntime();
    ClrHeap heap = runtime.Heap;

    var types = new Dictionary<string, (long Count, ulong Size)>(StringComparer.Ordinal);
    foreach (ClrObject obj in heap.EnumerateObjects())
    {
        string name = obj.Type?.Name ?? "<unknown>";
        ulong size = obj.Size;
        if (types.TryGetValue(name, out var cur))
            types[name] = (cur.Count + 1, cur.Size + size);
        else
            types[name] = (1, size);
    }

    var threads = new List<object>();
    foreach (ClrThread thread in runtime.Threads)
    {
        string[] frames = thread.EnumerateStackTrace().Take(80)
            .Select(f => f.ToString() ?? "<unknown>")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        bool waiting = frames.Any(x =>
            x.Contains("Monitor", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("SemaphoreSlim", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("WaitOne", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("WaitHandle", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Task.SpinThenBlockingWait", StringComparison.OrdinalIgnoreCase));
        threads.Add(new
        {
            managedId = thread.ManagedThreadId,
            osId = thread.OSThreadId,
            alive = thread.IsAlive,
            waiting,
            stack = frames.Take(30).ToArray()
        });
    }

    var result = new
    {
        kind = "clrmd-dump",
        path,
        clrVersion = clr.Version.ToString(),
        heap = new
        {
            canWalk = heap.CanWalkHeap,
            totalObjects = types.Sum(x => x.Value.Count),
            totalBytes = types.Aggregate(0UL, (a, x) => a + x.Value.Size),
            topTypes = types.OrderByDescending(x => x.Value.Size).Take(50)
                .Select(x => new { type = x.Key, count = x.Value.Count, bytes = x.Value.Size }).ToArray()
        },
        threads,
        waitThreadCount = threads.Count
    };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (mode == "binlog")
{
    object build = BinaryLog.ReadBuild(path);
    var rows = new List<(string Kind, string Name, double Ms)>();

    static IEnumerable<object> Children(object node)
    {
        PropertyInfo? p = node.GetType().GetProperty("Children", BindingFlags.Public | BindingFlags.Instance);
        if (p?.GetValue(node) is IEnumerable seq)
            foreach (object? child in seq)
                if (child is not null) yield return child;
    }

    static double DurationMs(object node)
    {
        Type t = node.GetType();
        object? duration = t.GetProperty("Duration")?.GetValue(node);
        if (duration is TimeSpan ts) return ts.TotalMilliseconds;
        object? start = t.GetProperty("StartTime")?.GetValue(node);
        object? end = t.GetProperty("EndTime")?.GetValue(node);
        if (start is DateTime s && end is DateTime e) return (e - s).TotalMilliseconds;
        return 0;
    }

    static string NodeName(object node)
        => Convert.ToString(node.GetType().GetProperty("Name")?.GetValue(node)) ?? node.GetType().Name;

    var stack = new Stack<object>();
    stack.Push(build);
    while (stack.Count > 0)
    {
        object node = stack.Pop();
        string kind = node.GetType().Name;
        if (kind is "Target" or "Task" or "Project")
        {
            double ms = DurationMs(node);
            if (ms > 0) rows.Add((kind, NodeName(node), ms));
        }
        foreach (object child in Children(node)) stack.Push(child);
    }

    var result = new
    {
        kind = "msbuild-binlog",
        path,
        topTargets = rows.Where(x => x.Kind == "Target").OrderByDescending(x => x.Ms).Take(30)
            .Select(x => new { name = x.Name, ms = Math.Round(x.Ms, 3) }).ToArray(),
        topTasks = rows.Where(x => x.Kind == "Task").OrderByDescending(x => x.Ms).Take(30)
            .Select(x => new { name = x.Name, ms = Math.Round(x.Ms, 3) }).ToArray(),
        topProjects = rows.Where(x => x.Kind == "Project").OrderByDescending(x => x.Ms).Take(15)
            .Select(x => new { name = x.Name, ms = Math.Round(x.Ms, 3) }).ToArray()
    };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

Console.Error.WriteLine("Unknown mode: " + mode);
return 2;
