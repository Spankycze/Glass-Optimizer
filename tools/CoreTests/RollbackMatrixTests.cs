using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class RollbackMatrixTests
{
    private static TweakDefinition Create(
        Func<bool, CancellationToken, Task> set,
        Func<CancellationToken, Task<bool>> detect,
        Func<CancellationToken, Task>? revert = null)
        => new()
        {
            Id = "test.rollback",
            Section = "Tests",
            Group = "Rollback",
            Name = "Rollback test",
            Description = "In-memory rollback contract test.",
            RequiresAdmin = false,
            SetAsync = set,
            DetectAsync = detect,
            VerifyAsync = detect,
            RevertAsync = revert
        };

    [Fact]
    public async Task ApplyVerifyRollback_RestoresPreviousState()
    {
        bool state = false;
        var tweak = Create(
            (enabled, _) => { state = enabled; return Task.CompletedTask; },
            _ => Task.FromResult(state));
        IOptimizerModule module = OptimizerModuleRuntime.From(tweak);

        await module.ApplyAsync(true, CancellationToken.None);
        Assert.True(await module.VerifyAsync(CancellationToken.None));

        await module.RollbackAsync(false, CancellationToken.None);
        Assert.False(await module.VerifyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_CanRecoverAfterApplyThrows()
    {
        bool state = false;
        bool first = true;
        var tweak = Create(
            (enabled, _) =>
            {
                state = enabled;
                if (first) { first = false; throw new InvalidOperationException("Injected apply failure"); }
                return Task.CompletedTask;
            },
            _ => Task.FromResult(state));
        IOptimizerModule module = OptimizerModuleRuntime.From(tweak);

        await Assert.ThrowsAsync<InvalidOperationException>(() => module.ApplyAsync(true, CancellationToken.None));
        Assert.True(state);

        await module.RollbackAsync(false, CancellationToken.None);
        Assert.False(await module.VerifyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_AfterCancellation_RestoresPreviousState()
    {
        bool state = false;
        var tweak = Create(
            (enabled, token) =>
            {
                state = enabled;
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            _ => Task.FromResult(state));
        IOptimizerModule module = OptimizerModuleRuntime.From(tweak);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ApplyAsync(true, cts.Token));
        Assert.True(state);

        await module.RollbackAsync(false, CancellationToken.None);
        Assert.False(state);
    }

    [Fact]
    public async Task RestoreOriginal_UsesExplicitRevertContract()
    {
        bool state = true;
        var tweak = Create(
            (enabled, _) => { state = enabled; return Task.CompletedTask; },
            _ => Task.FromResult(state),
            _ => { state = false; return Task.CompletedTask; });
        IOptimizerModule module = OptimizerModuleRuntime.From(tweak);

        Assert.True(module.CanRestoreOriginal);
        await module.RestoreOriginalAsync(CancellationToken.None);
        Assert.False(await module.VerifyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RestoreOriginal_WithoutRevert_IsRejected()
    {
        bool state = true;
        var tweak = Create(
            (enabled, _) => { state = enabled; return Task.CompletedTask; },
            _ => Task.FromResult(state));
        IOptimizerModule module = OptimizerModuleRuntime.From(tweak);

        Assert.False(module.CanRestoreOriginal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.RestoreOriginalAsync(CancellationToken.None));
    }
}
