using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static class AtomicApplySmokeService
{
    public static async Task<string> RunAsync(CancellationToken token = default)
    {
        const string path = @"Software\SpankyhoOptimizer\Smoke\AtomicApply";
        const string name = "RawValue";
        using (var key = Registry.CurrentUser.CreateSubKey(path, true))
            key.SetValue(name, 7, RegistryValueKind.DWord);

        bool second = false;
        var first = new TweakDefinition
        {
            Id = "smoke.atomic.registry",
            Section = "smoke",
            Group = "Smoke",
            Name = "Atomic registry",
            Description = "Atomic rollback smoke",
            RequiresAdmin = false,
            RegistryHive = RegistryHive.CurrentUser,
            RegistryPath = path,
            RegistryValueName = name,
            RegistryEnabledValue = 1,
            DetectAsync = _ => Task.FromResult(ReadRaw(path, name) == 1),
            SetAsync = (on, _) => { using var k = Registry.CurrentUser.CreateSubKey(path, true); k.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord); return Task.CompletedTask; }
        };
        first.IsLoaded = true; first.Current = false; first.Pending = true;
        var failing = new TweakDefinition
        {
            Id = "smoke.atomic.fail",
            Section = "smoke",
            Group = "Smoke",
            Name = "Forced failure",
            Description = "Forces rollback",
            RequiresAdmin = false,
            DetectAsync = _ => Task.FromResult(second),
            SetAsync = (on, _) => { second = on; if (on) throw new InvalidOperationException("Expected smoke failure"); return Task.CompletedTask; }
        };
        failing.IsLoaded = true; failing.Current = false; failing.Pending = true;

        try
        {
            var errors = await new TweakEngine().ApplyAsync(new[] { first, failing }, token);
            int raw = ReadRaw(path, name);
            if (errors.Count == 0) throw new InvalidOperationException("Atomic smoke expected a failed batch.");
            if (raw != 7) throw new InvalidOperationException($"Atomic rollback did not restore raw registry value. Actual={raw}.");
            if (first.Current) throw new InvalidOperationException("Atomic rollback left first tweak active.");
            return $"errors={errors.Count}; raw={raw}; rolledBack={!first.Current}";
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\SpankyhoOptimizer\Smoke", false); } catch { }
        }
    }

    private static int ReadRaw(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, false);
        return Convert.ToInt32(key?.GetValue(name, -1) ?? -1);
    }
}
