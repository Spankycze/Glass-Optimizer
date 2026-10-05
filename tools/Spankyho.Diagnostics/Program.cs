using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Build.Logging.StructuredLogger;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: dump-summary <dump> | roslyn-scan <root> | binlog-top <binlog>");
                return 2;
            }

            return args[0].ToLowerInvariant() switch
            {
                "dump-summary" => DumpSummary(args[1]),
                "roslyn-scan" => RoslynScan(args[1]),
                "binlog-top" => BinlogTop(args[1]),
                _ => 2
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int DumpSummary(string dumpPath)
    {
        string full = Path.GetFullPath(dumpPath);
        using DataTarget target = DataTarget.LoadDump(full);
        ClrInfo? clr = target.ClrVersions.FirstOrDefault();
        if (clr is null)
            throw new InvalidOperationException("No CLR runtime found in dump.");

        ClrRuntime runtime = clr.CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var byType = new Dictionary<string, TypeAgg>(StringComparer.Ordinal);
        long objectCount = 0;
        ulong shallowBytes = 0;

        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            string typeName = obj.Type?.Name ?? "<unknown>";
            ulong size = obj.Size;
            objectCount++;
            shallowBytes += size;

            byType.TryGetValue(typeName, out TypeAgg? agg);
            agg ??= new TypeAgg();

            agg.Count++;
            agg.Bytes += size;
            byType[typeName] = agg;
        }

        var threadItems = new List<ThreadInfo>();
        foreach (ClrThread? thread in runtime.Threads)
        {
            if (thread is null || !thread.IsAlive)
                continue;

            string[] stack = thread.EnumerateStackTrace()
                .Take(32)
                .Select(f => f.ToString() ?? "<unknown>")
                .ToArray();

            bool waitLike = stack.Any(s =>
                s.Contains("Monitor", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Semaphore", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Wait", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Lock", StringComparison.OrdinalIgnoreCase));

            threadItems.Add(new ThreadInfo(
                thread.OSThreadId,
                thread.ManagedThreadId,
                thread.GCMode.ToString(),
                waitLike,
                stack));
        }

        var result = new
        {
            schema = 1,
            source = full,
            clrVersion = clr.Version.ToString(),
            heap = new
            {
                canWalk = heap.CanWalkHeap,
                objectCount,
                shallowBytes,
                topTypes = byType
                    .OrderByDescending(x => x.Value.Bytes)
                    .Take(50)
                    .Select(x => new { type = x.Key, count = x.Value.Count, shallowBytes = x.Value.Bytes })
                    .ToArray()
            },
            threads = new
            {
                alive = threadItems.Count,
                waitLike = threadItems.Count(x => x.WaitLike),
                items = threadItems
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static int RoslynScan(string rootPath)
    {
        string root = Path.GetFullPath(rootPath);
        var findings = new List<RoslynFinding>();

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                 !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            string source = File.ReadAllText(file);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: file);
            SyntaxNode syntaxRoot = tree.GetRoot();

            foreach (MemberAccessExpressionSyntax m in syntaxRoot.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (m.Name.Identifier.ValueText == "Result")
                    Add("sync-over-async", "high", m, m.ToString());
            }

            foreach (InvocationExpressionSyntax inv in syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string expr = inv.Expression.ToString();

                if (expr.EndsWith(".Wait", StringComparison.Ordinal) || expr == "Wait")
                    Add("sync-over-async", "high", inv, expr);
                else if (expr is "Process.Start" or "System.Diagnostics.Process.Start" ||
                         expr.EndsWith(".Process.Start", StringComparison.Ordinal))
                    Add("process-start", "medium", inv, expr);
                else if (expr is "Task.Run" or "System.Threading.Tasks.Task.Run")
                    Add("task-run", "info", inv, expr);
                else if (expr.Contains("Registry.", StringComparison.Ordinal) ||
                         expr.Contains("RegistryKey.", StringComparison.Ordinal))
                    Add("registry", "info", inv, expr);
                else if (expr.Contains("ManagementObject", StringComparison.Ordinal) ||
                         expr.Contains("ManagementClass", StringComparison.Ordinal) ||
                         expr.Contains("GetCim", StringComparison.OrdinalIgnoreCase))
                    Add("wmi-cim", "medium", inv, expr);
                else if (IsLinq(expr) && IsLikelyHotMethod(inv))
                    Add("linq-hotpath", "info", inv, expr);
            }

            foreach (ObjectCreationExpressionSyntax oc in syntaxRoot.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                string typeName = oc.Type.ToString();
                if (typeName.Contains("ManagementObject", StringComparison.Ordinal) ||
                    typeName.Contains("ManagementClass", StringComparison.Ordinal))
                    Add("wmi-cim", "medium", oc, typeName);

                if (typeName.Contains("Timer", StringComparison.Ordinal))
                    Add("timer-lifetime-review", "info", oc, typeName);
            }

            foreach (AssignmentExpressionSyntax a in syntaxRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (a.IsKind(SyntaxKind.AddAssignmentExpression))
                    Add("event-subscription-review", "info", a, a.Left.ToString());
            }

            void Add(string category, string severity, SyntaxNode node, string detail)
            {
                FileLinePositionSpan span = tree.GetLineSpan(node.Span);
                findings.Add(new RoslynFinding(
                    category,
                    severity,
                    Path.GetRelativePath(root, file),
                    span.StartLinePosition.Line + 1,
                    detail));
            }
        }

        var result = new
        {
            schema = 1,
            root,
            count = findings.Count,
            summary = findings.GroupBy(x => x.Category)
                .OrderBy(x => x.Key)
                .Select(g => new { category = g.Key, count = g.Count() })
                .ToArray(),
            findings
        };

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static bool IsLinq(string expr)
    {
        string[] names =
        [
            ".Where", ".Select", ".OrderBy", ".OrderByDescending", ".GroupBy",
            ".ToArray", ".ToList", ".Any", ".Count", ".First", ".FirstOrDefault"
        ];
        return names.Any(expr.Contains);
    }

    private static bool IsLikelyHotMethod(SyntaxNode node)
    {
        MethodDeclarationSyntax? method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method is null)
            return false;

        string name = method.Identifier.ValueText;
        return name.Contains("Build", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Render", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Refresh", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Measure", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("On", StringComparison.Ordinal);
    }

    private static int BinlogTop(string binlogPath)
    {
        string full = Path.GetFullPath(binlogPath);
        Build build = BinaryLog.ReadBuild(full);

        var nodes = new List<TimedEntry>();
        Visit(build, "");

        var top = nodes
            .Where(x => x.DurationMs > 0.01)
            .OrderByDescending(x => x.DurationMs)
            .Take(80)
            .ToArray();

        var result = new
        {
            schema = 1,
            source = full,
            durationMs = (build.EndTime - build.StartTime).TotalMilliseconds,
            top
        };

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;

        void Visit(object? node, string path)
        {
            if (node is null)
                return;

            Type type = node.GetType();
            string name = type.GetProperty("Name")?.GetValue(node)?.ToString() ??
                          type.GetProperty("Text")?.GetValue(node)?.ToString() ??
                          type.Name;
            string currentPath = string.IsNullOrWhiteSpace(path) ? name : path + " > " + name;

            DateTime? start = ReadDateTime(type, node, "StartTime");
            DateTime? end = ReadDateTime(type, node, "EndTime");
            double durationMs = start.HasValue && end.HasValue && end >= start
                ? (end.Value - start.Value).TotalMilliseconds
                : 0;

            if (type.Name is "Target" or "Task" or "Project" or "ProjectEvaluation" ||
                type.Name.EndsWith("Task", StringComparison.Ordinal))
            {
                nodes.Add(new TimedEntry(type.Name, name, currentPath, durationMs));
            }

            object? childrenObj = type.GetProperty("Children")?.GetValue(node);
            if (childrenObj is System.Collections.IEnumerable children)
            {
                foreach (object? child in children)
                    Visit(child, currentPath);
            }
        }

        static DateTime? ReadDateTime(Type type, object instance, string property)
        {
            object? value = type.GetProperty(property)?.GetValue(instance);
            return value is DateTime dt ? dt : null;
        }
    }

    private sealed class TypeAgg
    {
        public long Count { get; set; }
        public ulong Bytes { get; set; }
    }

    private sealed record ThreadInfo(uint OsThreadId, int ManagedThreadId, string GcMode, bool WaitLike, string[] Stack);
    private sealed record RoslynFinding(string Category, string Severity, string File, int Line, string Detail);
    private sealed record TimedEntry(string Kind, string Name, string Path, double DurationMs);
}
