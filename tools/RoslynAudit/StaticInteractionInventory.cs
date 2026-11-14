using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class StaticInteractionInventory
{
    private sealed record Site(string Kind, string File, int Line, string Owner, string Expression, string[] Calls);
    public static void Write(string root, string output)
    {
        var sites = new List<Site>();
        var methods = new HashSet<string>(StringComparer.Ordinal);
        var trees = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p)).ToArray();
        foreach (var tree in trees)
        {
            var syntax = tree.GetRoot();
            foreach (var method in syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()) methods.Add(method.Identifier.ValueText);
            foreach (var node in syntax.DescendantNodes())
            {
                string? kind = node switch
                {
                    ObjectCreationExpressionSyntax c when c.Type.ToString().Split('.').Last() is "Button" or "ToggleSwitch" or "CheckBox" or "RadioButton" or "TextBox" or "ComboBox" or "Slider" => "control",
                    InvocationExpressionSyntax i when i.Expression.ToString().Split('.').Last() is "QuickButton" or "PrimaryQuickButton" or "StyledDialogButton" or "ActionButton" => "action-factory",
                    AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.AddAssignmentExpression) && a.Left is MemberAccessExpressionSyntax m && m.Name.Identifier.ValueText is "Click" or "Toggled" or "Checked" or "Unchecked" or "SelectionChanged" or "ValueChanged" or "TextChanged" or "Invoked" => "event-handler",
                    InvocationExpressionSyntax i when i.Expression.ToString().Contains("Process.Start", StringComparison.Ordinal) || i.Expression.ToString().Contains("ProcessRunner.", StringComparison.Ordinal) => "process-site",
                    InvocationExpressionSyntax i when i.Expression.ToString().Split('.').Last() is "MountDialog" or "ShowAsync" or "CreateDialogOverlay" => "dialog-site",
                    AssignmentExpressionSyntax a when a.Left.ToString().EndsWith("DetectAsync", StringComparison.Ordinal) || a.Left.ToString().EndsWith("SetAsync", StringComparison.Ordinal) || a.Left.ToString().EndsWith("RevertAsync", StringComparison.Ordinal) => "tweak-contract",
                    CatchClauseSyntax c when c.Block.Statements.Count == 0 => "empty-catch-candidate",
                    _ => null
                };
                if (kind == null) continue;
                string owner = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
                    ?? node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "initializer";
                string expr = node.ToString().Replace("\r", " ").Replace("\n", " ");
                if (expr.Length > 600) expr = expr[..600];
                var calls = node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Select(i => i.Expression.ToString()).Distinct(StringComparer.Ordinal).ToArray();
                sites.Add(new(kind, Path.GetRelativePath(root, tree.FilePath), tree.GetLineSpan(node.Span).StartLinePosition.Line + 1, owner, expr, calls));
            }
        }
        var missingXamlHandlers = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories).Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var attr in doc.Descendants().Attributes())
            {
                bool handler = attr.Name.LocalName is "Click" or "Toggled" or "Checked" or "Unchecked" or "SelectionChanged" or "ValueChanged" or "TextChanged" or "Loaded";
                bool binding = attr.Value.StartsWith("{Binding", StringComparison.Ordinal) || attr.Value.StartsWith("{x:Bind", StringComparison.Ordinal);
                if (!handler && !binding) continue;
                int line = (attr as IXmlLineInfo)?.LineNumber ?? 0;
                sites.Add(new(handler ? "xaml-handler" : "xaml-binding", Path.GetRelativePath(root, file), line, attr.Parent?.Name.LocalName ?? "", attr.Name + "=" + attr.Value, Array.Empty<string>()));
                if (handler && !binding && !methods.Contains(attr.Value)) missingXamlHandlers.Add(Path.GetRelativePath(root, file) + ":" + line + " " + attr.Value);
            }
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "interaction-inventory.json"), JsonSerializer.Serialize(new { generatedUtc = DateTimeOffset.UtcNow, files = trees.Length, counts = sites.GroupBy(s => s.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), missingXamlHandlers, sites }, new JsonSerializerOptions { WriteIndented = true }));
        static string Cell(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        File.WriteAllLines(Path.Combine(output, "interaction-inventory.csv"), new[] { "Kind,File,Line,Owner,Expression,Calls" }.Concat(sites.Select(s => string.Join(",", Cell(s.Kind), Cell(s.File), s.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), Cell(s.Owner), Cell(s.Expression), Cell(string.Join(" | ", s.Calls))))), Encoding.UTF8);
        Console.WriteLine($"INTERACTION_INVENTORY_OK files={trees.Length} sites={sites.Count} missingXamlHandlers={missingXamlHandlers.Count}");
    }
}
