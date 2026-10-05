using System.Text;
using System.Text.Json;
using SharpFuzz;
using SpankyhoOptimizer.Services;

string mode = args.FirstOrDefault() ?? "dns";

if (mode == "--smoke")
{
    RunDns("1.1.1.1,9.9.9.9|1.1.1.1,9.9.9.9,192.168.1.1");
    RunJson("{\"adapter\":\"Ethernet\",\"mtu\":1492}");
    RunVersion("572.83|571.96");
    Console.WriteLine("FUZZ_HARNESS_SMOKE_OK");
    return;
}

if (mode == "--regression")
{
    string root = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "regressions");
    RunRegressionCorpus(root);
    return;
}

Fuzzer.Run(stream =>
{
    using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
    string input = reader.ReadToEnd();
    if (mode.Equals("json", StringComparison.OrdinalIgnoreCase)) RunJson(input);
    else if (mode.Equals("version", StringComparison.OrdinalIgnoreCase)) RunVersion(input);
    else RunDns(input);
});

static void RunDns(string input)
{
    string[] halves = input.Split('|', 2);
    string[] expected = halves[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Take(32).ToArray();
    string[] current = (halves.Length > 1 ? halves[1] : halves[0])
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Take(64).ToArray();
    _ = DnsProfileService.FindUnexpectedResolvers(expected, current);
}

static void RunJson(string input)
{
    if (input.Length > 1_000_000) return;
    try
    {
        using JsonDocument doc = JsonDocument.Parse(input, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        });
        _ = doc.RootElement.ValueKind;
    }
    catch (JsonException)
    {
        // Invalid JSON is an expected fuzz input, not a crash.
    }
}

static void RunVersion(string input)
{
    if (input.Length > 4096) return;
    string[] halves = input.Split('|', 2);
    string candidate = halves[0];
    string installed = halves.Length > 1 ? halves[1] : string.Empty;
    _ = GpuDriverService.IsNewer(candidate, installed);
}

static void RunRegressionCorpus(string root)
{
    if (!Directory.Exists(root))
    {
        Console.WriteLine("FUZZ_REGRESSION_OK cases=0");
        return;
    }

    int cases = 0;
    foreach (string dir in Directory.EnumerateDirectories(root))
    {
        string mode = Path.GetFileName(dir);
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string input = File.ReadAllText(file, Encoding.UTF8);
            if (mode.Equals("json", StringComparison.OrdinalIgnoreCase)) RunJson(input);
            else if (mode.Equals("dns", StringComparison.OrdinalIgnoreCase)) RunDns(input);
            else if (mode.Equals("version", StringComparison.OrdinalIgnoreCase)) RunVersion(input);
            else continue;
            cases++;
        }
    }

    Console.WriteLine("FUZZ_REGRESSION_OK cases=" + cases);
}
