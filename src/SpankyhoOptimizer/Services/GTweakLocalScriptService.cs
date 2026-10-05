using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record LocalScriptItem(string Path, string Name, string Extension);

public static class GTweakLocalScriptService
{
    private static readonly string[] Allowed = { ".ps1", ".bat", ".cmd", ".reg" };
    public static readonly string Root = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpankyhoOptimizer", "LocalScripts");

    public static IReadOnlyList<LocalScriptItem> Scan()
    {
        Directory.CreateDirectory(Root);
        return Directory.EnumerateFiles(Root, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p => Allowed.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new LocalScriptItem(p, System.IO.Path.GetFileName(p), System.IO.Path.GetExtension(p).ToLowerInvariant()))
            .ToArray();
    }

    public static async Task<ProcessResult> RunAsync(LocalScriptItem item, CancellationToken token = default)
    {
        string full = System.IO.Path.GetFullPath(item.Path);
        string root = System.IO.Path.GetFullPath(Root) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("Script není v povolené LocalScripts složce.");
        string ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
        if (!Allowed.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nepodporovaný typ scriptu.");

        return ext switch
        {
            ".ps1" => await ProcessRunner.RunAsync("powershell.exe", $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{full}\"", TimeSpan.FromMinutes(15), token).ConfigureAwait(false),
            ".reg" => await ProcessRunner.RunAsync("reg.exe", $"import \"{full}\"", TimeSpan.FromMinutes(5), token).ConfigureAwait(false),
            _ => await ProcessRunner.RunAsync("cmd.exe", $"/d /c \"\"{full}\"\"", TimeSpan.FromMinutes(15), token).ConfigureAwait(false)
        };
    }
}