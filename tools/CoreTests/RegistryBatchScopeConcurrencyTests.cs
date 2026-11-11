using Microsoft.Win32;
using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class RegistryBatchScopeConcurrencyTests
{
    private const string ExistingPath = @"Software\Microsoft\Windows\CurrentVersion";

    [Fact]
    public async Task ConcurrentReadOnlyDetectionReusesOneScopedHandle()
    {
        using var scope = RegistryBatchScope.Begin();
        using var release = new ManualResetEventSlim(false);
        Task<RegistryKey?>[] tasks = Enumerable.Range(0, 32)
            .Select(index => Task.Run(() =>
            {
                release.Wait();
                string path = index % 2 == 0 ? ExistingPath : ExistingPath.ToUpperInvariant();
                return RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, path);
            })).ToArray();
        release.Set();
        RegistryKey?[] handles = await Task.WhenAll(tasks);
        Assert.NotNull(handles[0]);
        Assert.All(handles, handle => Assert.Same(handles[0], handle));
    }

    [Fact]
    public void NestedScopeDoesNotReplaceParentReadOnlyCache()
    {
        using var outer = RegistryBatchScope.Begin();
        RegistryKey? parentHandle = RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath);
        Assert.NotNull(parentHandle);
        using (RegistryBatchScope.Begin())
        {
            RegistryKey? childHandle = RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath);
            Assert.NotNull(childHandle);
            Assert.NotSame(parentHandle, childHandle);
            Assert.Same(childHandle, RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath));
        }
        Assert.True(RegistryBatchScope.IsActive);
        Assert.Same(parentHandle, RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath));
    }

    [Fact]
    public void CaseInsensitiveRegistryPathsShareReadOnlyHandle()
    {
        // Windows registry paths are case insensitive. One scope should reuse a single handle
        // instead of opening/caching duplicates for names with different letter case.
        using var scope = RegistryBatchScope.Begin();
        RegistryKey? first = RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath);
        RegistryKey? second = RegistryBatchScope.OpenReadOnly(
            RegistryHive.CurrentUser, ExistingPath.ToUpperInvariant());
        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void OutOfOrderDisposeCannotReplaceActiveChildOrLeaveDisposedParentCurrent()
    {
        var parent = RegistryBatchScope.Begin();
        var child = RegistryBatchScope.Begin();
        try
        {
            // Reject closing the outer scope while an inner scope still owns the ambient context.
            Assert.Throws<InvalidOperationException>(() => parent.Dispose());
            Assert.True(RegistryBatchScope.IsActive);
            Assert.NotNull(RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath));

            child.Dispose();
            Assert.True(RegistryBatchScope.IsActive);
            Assert.NotNull(RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath));
            parent.Dispose();
            Assert.False(RegistryBatchScope.IsActive);
        }
        finally
        {
            // Normal cleanup is idempotent, and does not close user applications or mutate Windows.
            child.Dispose();
            parent.Dispose();
        }
    }

    [Fact]
    public async Task DelayedChildCannotBeginNewScopeOnDisposedParent()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending;
        using (RegistryBatchScope.Begin())
        {
            // AsyncLocal flows into the child even though its parent later closes the scope.
            pending = Task.Run(async () =>
            {
                await release.Task;
                Assert.Throws<ObjectDisposedException>(() => RegistryBatchScope.Begin());
            });
        }

        release.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(RegistryBatchScope.IsActive);
    }

    [Fact]
    public async Task NestedScopePreservesParentAcrossAwaitAndChildTask()
    {
        using var parent = RegistryBatchScope.Begin();
        RegistryKey? original = RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath);
        Assert.NotNull(original);

        await Task.Run(async () =>
        {
            using (RegistryBatchScope.Begin())
            {
                await Task.Yield();
                RegistryKey? nested = RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath);
                Assert.NotNull(nested);
                Assert.NotSame(original, nested);
                Assert.Same(nested, RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath));
            }

            Assert.Same(original, RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath));
        });

        Assert.Same(original, RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath));
    }

    [Fact]
    public void SeparateHivesDoNotShareCachedReadOnlyHandle()
    {
        const string commonPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
        using var scope = RegistryBatchScope.Begin();
        RegistryKey? user = RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, commonPath);
        RegistryKey? machine = RegistryBatchScope.OpenReadOnly(RegistryHive.LocalMachine, commonPath);
        Assert.NotNull(user);
        Assert.NotNull(machine);
        Assert.NotSame(user, machine);
        Assert.Same(user, RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, commonPath.ToLowerInvariant()));
        Assert.Same(machine, RegistryBatchScope.OpenReadOnly(RegistryHive.LocalMachine, commonPath.ToLowerInvariant()));
    }

    [Fact]
    public async Task DelayedChildCannotReopenDisposedScope()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending;
        using (RegistryBatchScope.Begin())
        {
            // Task.Run captures the current AsyncLocal even if the parent later disposes it.
            pending = Task.Run(async () =>
            {
                await release.Task;
                Assert.Throws<ObjectDisposedException>(() =>
                    RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, ExistingPath));
            });
        }
        release.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(RegistryBatchScope.IsActive);
    }
}
