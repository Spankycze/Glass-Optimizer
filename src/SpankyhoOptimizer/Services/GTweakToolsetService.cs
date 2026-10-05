using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record GTweakToolsetItem(string Name, string Author, string Group, string[] Tags,
    string Source, string DownloadPath, string? FilePattern, string? UrlPattern, string? FileName);

public static class GTweakToolsetService
{
    private static readonly HttpClient Http = CreateClient();
    public static string DownloadRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Spankyho Toolset");

    public static IReadOnlyList<GTweakToolsetItem> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "GTweakToolsetStore.json");
        if (!File.Exists(path)) return Array.Empty<GTweakToolsetItem>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var items = new List<GTweakToolsetItem>();
        foreach (var node in doc.RootElement.EnumerateArray())
        {
            string name = Get(node, "name");
            if (name.Equals("NVIDIA Profile Inspector", StringComparison.OrdinalIgnoreCase)) continue;
            string[] tags = node.TryGetProperty("tags", out var tagsNode)
                ? tagsNode.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
                : Array.Empty<string>();
            items.Add(new GTweakToolsetItem(name, Get(node, "author"), Get(node, "group"), tags,
                Get(node, "source"), Get(node, "downloadPath"), GetNullable(node, "filePattern"),
                GetNullable(node, "urlPattern"), GetNullable(node, "fileName")));
        }
        return items.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static async Task<string> DownloadAsync(GTweakToolsetItem item, CancellationToken token = default)
    {
        Directory.CreateDirectory(DownloadRoot);
        Uri uri = await ResolveDownloadUriAsync(item, token).ConfigureAwait(false);
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string fileName = ResolveFileName(item, uri, response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        string destination = UniquePath(Path.Combine(DownloadRoot, SanitizeFileName(fileName)));
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
        if (output.Length <= 0) throw new InvalidOperationException("Stažený soubor je prázdný.");
        return destination;
    }
    private static async Task<Uri> ResolveDownloadUriAsync(GTweakToolsetItem item, CancellationToken token)
    {
        if (item.Group.Equals("github", StringComparison.OrdinalIgnoreCase))
            return await ResolveGithubAsync(item, token).ConfigureAwait(false);

        if (Uri.TryCreate(item.DownloadPath, UriKind.Absolute, out var direct) && LooksLikeDirectDownload(direct))
            return direct;

        if (!Uri.TryCreate(item.DownloadPath, UriKind.Absolute, out var page))
            throw new InvalidOperationException("Neplatná download adresa.");
        string html = await Http.GetStringAsync(page, token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(item.UrlPattern))
        {
            var m = Regex.Match(html, item.UrlPattern, RegexOptions.IgnoreCase);
            if (m.Success && Uri.TryCreate(System.Net.WebUtility.HtmlDecode(m.Value), UriKind.Absolute, out var matched)) return matched;
        }
        if (!string.IsNullOrWhiteSpace(item.FilePattern))
        {
            foreach (Match m in Regex.Matches(html, @"https?://[^""'<>\s]+", RegexOptions.IgnoreCase))
            {
                string candidate = System.Net.WebUtility.HtmlDecode(m.Value.Replace("\\/", "/"));
                if (Regex.IsMatch(candidate, item.FilePattern, RegexOptions.IgnoreCase) && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return uri;
            }
        }
        throw new InvalidOperationException("Automatický download link se nepodařilo zjistit. Použij tlačítko Zdroj.");
    }
    private static async Task<Uri> ResolveGithubAsync(GTweakToolsetItem item, CancellationToken token)
    {
        string repo = item.DownloadPath.Trim('/');
        if (item.FilePattern?.StartsWith("raw:", StringComparison.OrdinalIgnoreCase) == true)
            return new Uri($"https://github.com/{repo}/archive/refs/heads/master.zip");
        using var response = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) throw new InvalidOperationException("GitHub release nemá assets.");
        Regex? pattern = string.IsNullOrWhiteSpace(item.FilePattern) ? null : new Regex(item.FilePattern, RegexOptions.IgnoreCase);
        foreach (var asset in assets.EnumerateArray())
        {
            string name = Get(asset, "name");
            if (pattern != null && !pattern.IsMatch(name)) continue;
            string url = Get(asset, "browser_download_url");
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri;
        }
        throw new InvalidOperationException("V posledním GitHub release nebyl nalezen odpovídající soubor.");
    }

    private static bool LooksLikeDirectDownload(Uri uri)
    {
        string p = uri.AbsolutePath.ToLowerInvariant();
        return p.EndsWith(".exe", StringComparison.Ordinal) ||
               p.EndsWith(".zip", StringComparison.Ordinal) ||
               p.EndsWith(".msi", StringComparison.Ordinal) ||
               p.Contains("/latest/download", StringComparison.Ordinal);
    }
    private static string ResolveFileName(GTweakToolsetItem item, Uri uri, string? disposition)
    {
        if (!string.IsNullOrWhiteSpace(item.FileName)) return item.FileName;
        if (!string.IsNullOrWhiteSpace(disposition)) return disposition.Trim('"');
        string name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (!string.IsNullOrWhiteSpace(name) && Path.HasExtension(name)) return name;
        return SanitizeFileName(item.Name) + ".bin";
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "download.bin" : value;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Nelze vytvořit unikátní název staženého souboru.");
    }
    private static string Get(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string? GetNullable(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpankyhoOptimizer/15.3.18");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream, application/json, text/html");
        return client;
    }
}
