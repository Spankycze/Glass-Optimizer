using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

internal sealed record WinUtilPolicyDocument(string Repository, string Commit, DateTime ImportedUtc, ServiceChangeRequest[] Services);

// Only declarative data for the existing native Recommended action is accepted.
// No upstream InvokeScript, registry operation, new service or shell command is executed.
internal static class WinUtilServicePolicy
{
    internal const string ReviewedBehaviorCommit = "07ccd8e2e755a706f31569808b31f5b77acad6a9";
    private const string Repository = "ChrisTitusTech/winutil";
    private static readonly string[] Allowed = { "CscService", "DiagTrack", "MapsBroker", "StorSvc" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string ActivePath => Path.Combine(UpstreamSourceUpdateService.Root, "winutil", "active-policy.json");
    private static string BundledPath => Path.Combine(AppContext.BaseDirectory, "Assets", "WinUtilServicePolicy.json");

    internal static ServiceChangeRequest[] ReadRecommended() => LoadCurrent().Services;
    internal static string ActiveCommit => LoadCurrent().Commit;

    private static WinUtilPolicyDocument LoadCurrent()
    {
        if (File.Exists(ActivePath))
        {
            try { return Load(ActivePath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { LogService.Info("upstream", "WinUtil active data invalid; retaining bundled policy: " + ex.Message); }
        }
        return Load(BundledPath);
    }

    internal static ServiceChangeRequest[] ReadFile(string path) => Load(path).Services;

    private static WinUtilPolicyDocument Load(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 32768)
            throw new InvalidDataException("WinUtil policy file is missing or exceeds its size limit.");
        var document = JsonSerializer.Deserialize<WinUtilPolicyDocument>(File.ReadAllText(path))
            ?? throw new InvalidDataException("WinUtil policy is empty.");
        if (document.Repository != Repository || !IsCommit(document.Commit))
            throw new InvalidDataException("WinUtil policy provenance is invalid.");
        Validate(document.Services);
        return document;
    }

    private static bool IsCommit(string? commit) => commit is { Length: 40 } && commit.All(Uri.IsHexDigit);

    private static void Validate(ServiceChangeRequest[]? services)
    {
        if (services == null || services.Length != Allowed.Length ||
            services.Any(x => x == null || !Allowed.Contains(x.Name, StringComparer.OrdinalIgnoreCase) || x.StartType is < 2 or > 4) ||
            services.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Allowed.Length)
            throw new InvalidDataException("WinUtil service data is incomplete, duplicated or outside the supported scope.");
    }

    internal static void ImportSnapshot(string snapshot, string commit, string destination)
    {
        if (!IsCommit(commit)) throw new InvalidDataException("Invalid WinUtil source commit.");
        string source = Path.Combine(snapshot, "config", "tweaks.json");
        if (!File.Exists(source) || new FileInfo(source).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("WinUtil service source is missing or too large.");
        ServiceChangeRequest[] services;
        try
        {
            using var json = JsonDocument.Parse(NormalizeMultilineStrings(File.ReadAllText(source)), new JsonDocumentOptions { MaxDepth = 32 });
            if (!json.RootElement.TryGetProperty("WPFTweaksServices", out var tweak) ||
                !tweak.TryGetProperty("service", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("WinUtil service schema is not supported.");
            var found = new Dictionary<string, ServiceChangeRequest>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in entries.EnumerateArray())
            {
                string? name = item.GetProperty("Name").GetString();
                if (name == null || !Allowed.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                uint type = item.GetProperty("StartupType").GetString() switch
                { "Automatic" => 2, "Manual" => 3, "Disabled" => 4, _ => throw new InvalidDataException("Unsupported service start type.") };
                if (!found.TryAdd(name, new ServiceChangeRequest(name, type)))
                    throw new InvalidDataException("Duplicate WinUtil service entry: " + name);
            }
            services = Allowed.Where(found.ContainsKey).Select(name => found[name]).ToArray();
            Validate(services);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Invalid WinUtil declarative policy.", ex); }

        var document = new WinUtilPolicyDocument(Repository, commit, DateTime.UtcNow, services);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            _ = Load(temporary); // Validate and read back before replacing the active data.
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // WinUtil config files use literal CR/LF inside quoted script strings (accepted by its
    // PowerShell JSON reader). Escape only control characters within strings, then run the
    // strict JSON parser. Script strings remain inert data and are never interpreted.
    private static string NormalizeMultilineStrings(string input)
    {
        var output = new System.Text.StringBuilder(input.Length);
        bool quoted = false, escaped = false;
        foreach (char c in input)
        {
            if (!quoted)
            {
                output.Append(c);
                if (c == '"') quoted = true;
            }
            else if (escaped) { output.Append(c); escaped = false; }
            else if (c == '\\') { output.Append(c); escaped = true; }
            else if (c == '"') { output.Append(c); quoted = false; }
            else if (c < 32)
            {
                output.Append("\\u");
                output.Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else output.Append(c);
        }
        return output.ToString();
    }
    internal static void Activate(string snapshot, string commit) => ImportSnapshot(snapshot, commit, ActivePath);
}
