using System;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public interface IOptimizerModule
{
    string Id { get; }
    string Name { get; }
    string Section { get; }
    string Safety { get; }
    string CompatibilityScope { get; }
    Task<(bool Supported, string Reason)> CheckCompatibilityAsync(CancellationToken token);
    Task<bool> DetectAsync(CancellationToken token);
    Task ApplyAsync(bool enabled, CancellationToken token);
    Task<bool> VerifyAsync(CancellationToken token);
    Task RollbackAsync(bool previous, CancellationToken token);
    bool CanRestoreOriginal { get; }
    Task RestoreOriginalAsync(CancellationToken token);
}

public static class OptimizerModuleRuntime
{
    public static IOptimizerModule From(TweakDefinition tweak) => new TweakModuleAdapter(tweak);

    private sealed class TweakModuleAdapter : IOptimizerModule
    {
        private readonly TweakDefinition _tweak;
        public TweakModuleAdapter(TweakDefinition tweak) => _tweak = tweak;
        public string Id => _tweak.Id;
        public string Name => _tweak.Name;
        public string Section => _tweak.Section;
        public string Safety => _tweak.Risk.ToString();
        public string CompatibilityScope => _tweak.HardwareScope;

        public Task<(bool Supported, string Reason)> CheckCompatibilityAsync(CancellationToken token) =>
            TweakCompatibilityService.CheckAsync(_tweak, token);

        public Task<bool> DetectAsync(CancellationToken token) => _tweak.DetectAsync(token);
        public Task ApplyAsync(bool enabled, CancellationToken token) => _tweak.SetAsync(enabled, token);
        public Task<bool> VerifyAsync(CancellationToken token) => (_tweak.VerifyAsync ?? _tweak.DetectAsync)(token);
        public Task RollbackAsync(bool previous, CancellationToken token) => _tweak.SetAsync(previous, token);
        public bool CanRestoreOriginal => _tweak.RevertAsync != null;
        public Task RestoreOriginalAsync(CancellationToken token) =>
            _tweak.RevertAsync != null
                ? _tweak.RevertAsync(token)
                : throw new InvalidOperationException("Tweak nemá explicitní original-value revert.");
    }
}
