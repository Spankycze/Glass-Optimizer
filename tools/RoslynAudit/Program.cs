using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root, "artifacts", "quality", "roslyn-audit.json");

var findings = new List<Finding>();
string[] files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
    .ToArray();

foreach (string file in files)
{
    string text = File.ReadAllText(file);
    SyntaxTree tree = CSharpSyntaxTree.ParseText(text, path: file);
    SyntaxNode syntaxRoot = tree.GetRoot();

    foreach (MemberAccessExpressionSyntax member in syntaxRoot.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
    {
        string name = member.Name.Identifier.ValueText;
        if (name == "Result")
            Add("SYNC001", "high", member, "Task/ValueTask .Result can cause sync-over-async or UI-thread blocking.");
    }

    foreach (InvocationExpressionSyntax invocation in syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        string expr = invocation.Expression.ToString();
        string method = invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => ""
        };

        if (method == "Wait" || expr.EndsWith(".GetAwaiter().GetResult()", StringComparison.Ordinal))
            Add("SYNC002", "high", invocation, "Blocking async wait detected.");
        if (expr is "Process.Start" or "System.Diagnostics.Process.Start")
            Add("PROC001", "medium", invocation, "Direct Process.Start bypasses ProcessRunner timeout/cancellation/telemetry.");
        if (method is "WaitOne" or "Sleep")
            Add("SYNC003", "medium", invocation, "Blocking wait/sleep detected.");
        if (expr.Contains("Registry.", StringComparison.Ordinal) || expr.StartsWith("Registry", StringComparison.Ordinal))
            Add("IO001", "low", invocation, "Registry access; verify it is not on a hot UI-thread path.");

        if (method == "Run" && expr.Contains("Task.", StringComparison.Ordinal))
        {
            string owner = invocation.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() switch
            {
                MethodDeclarationSyntax m => m.Identifier.ValueText,
                ConstructorDeclarationSyntax c => c.Identifier.ValueText,
                _ => ""
            };
            if (owner.StartsWith("Build", StringComparison.OrdinalIgnoreCase) ||
                owner.StartsWith("Show", StringComparison.OrdinalIgnoreCase) ||
                owner.StartsWith("OnLaunch", StringComparison.OrdinalIgnoreCase) ||
                owner.StartsWith("Navigate", StringComparison.OrdinalIgnoreCase) ||
                owner.Contains("Render", StringComparison.OrdinalIgnoreCase))
                Add("ASYNC001", "medium", invocation, $"Task.Run inside hot-path candidate '{owner}'; verify scheduling is necessary.");
        }

        if (method is "Where" or "Select" or "OrderBy" or "OrderByDescending" or "GroupBy" or "ToArray" or "ToList")
        {
            string owner = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "";
            if (owner.StartsWith("Build", StringComparison.OrdinalIgnoreCase) ||
                owner.StartsWith("Render", StringComparison.OrdinalIgnoreCase) ||
                owner.StartsWith("Refresh", StringComparison.OrdinalIgnoreCase))
                Add("PERF001", "low", invocation, $"LINQ in likely hot path '{owner}'; benchmark before replacing.");
        }
    }

    foreach (ObjectCreationExpressionSyntax creation in syntaxRoot.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
    {
        string type = creation.Type.ToString();
        if (type.EndsWith("Regex", StringComparison.Ordinal) && creation.ArgumentList?.Arguments.Count < 3)
            Add("REGEX001", "medium", creation, "Regex creation without an obvious timeout; verify bounded input/timeout.");
        if (type.Contains("ManagementObject", StringComparison.Ordinal) || type.Contains("ManagementClass", StringComparison.Ordinal))
            Add("WMI001", "medium", creation, "WMI object creation; keep off UI thread and cache stable results.");
    }

    foreach (AssignmentExpressionSyntax assignment in syntaxRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>())
    {
        if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) && assignment.Right is LambdaExpressionSyntax)
            Add("EVENT001", "low", assignment, "Lambda event subscription; verify unsubscribe/lifetime to avoid retained UI objects.");
    }

    void Add(string rule, string severity, SyntaxNode node, string message)
    {
        FileLinePositionSpan span = tree.GetLineSpan(node.Span);
        int line = span.StartLinePosition.Line + 1;
        string snippet = node.ToString().Replace("\r", " ").Replace("\n", " ");
        if (snippet.Length > 240) snippet = snippet[..240];
        findings.Add(new Finding(rule, severity, Path.GetRelativePath(root, file), line, message, snippet));
    }
}

var result = new
{
    generatedAt = DateTimeOffset.Now,
    root,
    files = files.Length,
    findings = findings.Count,
    byRule = findings.GroupBy(x => x.Rule).OrderByDescending(g => g.Count())
        .Select(g => new { rule = g.Key, count = g.Count() }).ToArray(),
    items = findings.OrderByDescending(x => SeverityRank(x.Severity)).ThenBy(x => x.File).ThenBy(x => x.Line).ToArray()
};

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"ROSLYN_AUDIT_OK files={files.Length} findings={findings.Count} output={output}");
foreach (var g in findings.GroupBy(x => x.Rule).OrderByDescending(g => g.Count()))
    Console.WriteLine($"ROSLYN_RULE {g.Key}={g.Count()}");

return 0;

static int SeverityRank(string severity) => severity switch { "high" => 3, "medium" => 2, _ => 1 };
sealed record Finding(string Rule, string Severity, string File, int Line, string Message, string Snippet);
