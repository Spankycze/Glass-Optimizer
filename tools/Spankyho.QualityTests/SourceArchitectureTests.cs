using System.Text.RegularExpressions;

namespace Spankyho.QualityTests;

public sealed class SourceArchitectureTests
{
    private static string ProjectRoot
    {
        get
        {
            DirectoryInfo? d = new(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "PROJECT_STATE.md")))
                d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Project root not found.");
        }
    }

    [Fact]
    public void Direct_ProcessStart_does_not_expand_beyond_known_debt()
    {
        string services = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        string[] knownDebt =
        [
            "AppUpdateService.cs",
            "GpuDriverService.cs",
            "NativeNicTuningService.cs",
            "PowerShellTweakerService.cs",
            "ProcessRunner.cs",
            "SafeModeLauncherService.cs",
            "WindowsActions.cs",
            "WindowsModernizerService.cs"
        ];

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(services, "*.cs"))
        {
            string text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"\bProcess\s*\.\s*Start\s*\(",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                offenders.Add(Path.GetFileName(file));
        }

        string[] newDebt = offenders.Except(knownDebt, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(newDebt.Length == 0,
            "New direct Process.Start call sites: " + string.Join(", ", newDebt));
    }

    [Fact]
    public void Sync_over_async_does_not_expand_beyond_known_debt()
    {
        string services = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        var baselineMax = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["AdvancedCenterService.cs"] = 1,
            ["DirectXGlobalSettingsService.cs"] = 1,
            ["DnsBenchmarkService.cs"] = 4,
            ["NativeNicTuningService.cs"] = 1,
            ["ServiceCatalogService.cs"] = 1,
            ["SpicetifyService.cs"] = 1,
            ["V10PortServices.cs"] = 2,
            ["WindowsActions.cs"] = 3
        };

        var actual = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(services, "*.cs"))
        {
            int count = 0;
            foreach (string line in File.ReadLines(file))
            {
                if (Regex.IsMatch(line, @"\.Result\b|\.Wait\s*\(",
                        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    count++;
            }
            if (count > 0) actual[Path.GetFileName(file)] = count;
        }

        var regressions = actual
            .Where(x => !baselineMax.TryGetValue(x.Key, out int max) || x.Value > max)
            .Select(x => $"{x.Key}:{x.Value}>{(baselineMax.TryGetValue(x.Key, out int max) ? max : 0)}")
            .ToArray();

        Assert.True(regressions.Length == 0,
            "New sync-over-async debt: " + string.Join(", ", regressions));
    }
}
