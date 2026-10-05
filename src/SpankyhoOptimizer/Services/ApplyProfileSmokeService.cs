using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static class ApplyProfileSmokeService
{
    public static async Task<string> RunAsync(CancellationToken token = default)
    {
        const string path = @"Software\SpankyhoOptimizer\Smoke\ApplyProfile";
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
            var tweaks = Enumerable.Range(1, 12).Select(i => Create(path, i)).ToArray();
            var engine = new TweakEngine();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var errors = await engine.ApplyAsync(tweaks, token);
            sw.Stop();
            long coldMs = sw.ElapsedMilliseconds;
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(" | ", errors));
            if (tweaks.Any(x => !x.Current)) throw new InvalidOperationException("Apply profile enable read-back mismatch.");

            foreach (var tweak in tweaks) tweak.Pending = false;
            sw.Restart();
            errors = await engine.ApplyAsync(tweaks, token);
            sw.Stop();
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(" | ", errors));
            if (tweaks.Any(x => x.Current)) throw new InvalidOperationException("Apply profile disable read-back mismatch.");
            return $"count={tweaks.Length}; coldMs={coldMs}; warmMs={sw.ElapsedMilliseconds}";
        }
        finally { try { Registry.CurrentUser.DeleteSubKeyTree(path, false); } catch { } }
    }
    public static async Task<string> RunStartupWarmApplyAsync(CancellationToken token = default)
    {
        const string path = @"Software\SpankyhoOptimizer\Smoke\StartupWarmApply";
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
            var tweaks = Enumerable.Range(1, 12).Select(i => Create(path, i)).ToArray();
            await IdlePrewarmService.WarmApplyInfrastructureAsync(token);
            var engine = new TweakEngine();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var errors = await engine.ApplyAsync(tweaks, token);
            sw.Stop();
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(" | ", errors));
            if (tweaks.Any(x => !x.Current)) throw new InvalidOperationException("Startup warm apply read-back mismatch.");
            return $"count={tweaks.Length}; totalMs={sw.ElapsedMilliseconds}";
        }
        finally { try { Registry.CurrentUser.DeleteSubKeyTree(path, false); } catch { } }
    }

    public static async Task<string> RunPreflightWarmAsync(CancellationToken token = default)
    {
        const string path = @"Software\SpankyhoOptimizer\Smoke\PreflightWarm";
        var tweaks = Enumerable.Range(1, 12).Select(i => Create(path, i)).ToArray();
        TweakPreflightService.Warm();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await TweakPreflightService.ValidateAsync(tweaks, token);
        sw.Stop();
        if (!report.CanApply) throw new InvalidOperationException(string.Join(" | ", report.Issues.Select(x => x.Message)));
        return $"count={tweaks.Length}; validateMs={sw.ElapsedMilliseconds}";
    }

    private static TweakDefinition Create(string path, int index)
    {
        string name = "Value" + index;
        var tweak = new TweakDefinition
        {
            Id = "smoke.apply.profile." + index,
            Section = "smoke",
            Group = "Apply Profile",
            Name = "Apply profile " + index,
            Description = "Apply phase benchmark",
            RequiresAdmin = false,
            RegistryHive = RegistryHive.CurrentUser,
            RegistryPath = path,
            RegistryValueName = name,
            RegistryEnabledValue = 1,
            DetectAsync = _ => Task.FromResult(Read(path, name) == 1),
            SetAsync = (on, _) =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(path, true);
                key.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
                return Task.CompletedTask;
            }
        };
        tweak.IsLoaded = true;
        tweak.Current = false;
        tweak.Pending = true;
        return tweak;
    }
    private static int Read(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, false);
        return Convert.ToInt32(key?.GetValue(name, 0) ?? 0);
    }
}
