using Microsoft.Win32;
using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class RegistryBatchScopeReadOnlyTests
{
    [Fact]
    public void MissingReadOnlyRegistryKeyReturnsNullWithoutCreatingAnything()
    {
        // A unique missing HKCU path: no writes to Windows are performed.
        string path = @"Software\SpankyhoOptimizer\ReadOnlyProbe\" + Guid.NewGuid().ToString("N");
        using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        {
            using RegistryKey? before = root.OpenSubKey(path, writable: false);
            Assert.Null(before);
        }

        using (RegistryBatchScope.Begin())
        {
            Assert.True(RegistryBatchScope.IsActive);
            Assert.Null(RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, path));
            Assert.Null(RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, path));
        }

        using RegistryKey afterRoot = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? after = afterRoot.OpenSubKey(path, writable: false);
        Assert.Null(after);
    }

    [Fact]
    public void MissingReadOnlyOpenDoesNotCreateARegistryKey()
    {
        string path = @"Software\SpankyhoOptimizer\ReadOnlyOpenProbe\" + Guid.NewGuid().ToString("N");
        // Legacy Open(writable:false) must not fall back to CreateSubKey.
        Assert.Throws<InvalidOperationException>(() =>
            RegistryBatchScope.Open(RegistryHive.CurrentUser, path, writable: false));
        using (RegistryBatchScope.Begin())
        {
            Assert.Throws<InvalidOperationException>(() =>
                RegistryBatchScope.Open(RegistryHive.CurrentUser, path, writable: false));
        }

        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? after = root.OpenSubKey(path, writable: false);
        Assert.Null(after);
    }

    [Fact]
    public void ExistingReadOnlyRegistryHandleIsReusedOnlyForItsScope()
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion";
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? prerequisite = root.OpenSubKey(path, writable: false);
        Assert.NotNull(prerequisite);

        RegistryKey? first;
        using (RegistryBatchScope.Begin())
        {
            first = RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, path);
            Assert.NotNull(first);
            Assert.Same(first, RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, path));
        }
        Assert.False(RegistryBatchScope.IsActive);
        using RegistryKey? nextScope = RegistryBatchScope.OpenReadOnly(RegistryHive.CurrentUser, path);
        Assert.NotNull(nextScope);
        Assert.NotSame(first, nextScope);
    }

    [Fact]
    public async Task DwordDetectionForMissingKeyRemainsReadOnlyWithinBatch()
    {
        string path = @"Software\SpankyhoOptimizer\MissingDetect\" + Guid.NewGuid().ToString("N");
        var tweak = TweakFactory.Dword(
            "test.readonly.missing", "system", "Tests", "Missing registry key", "Read-only regression",
            RegistryHive.CurrentUser, path, "Enabled", 1, requiresAdmin: false);

        using (RegistryBatchScope.Begin())
        {
            Assert.False(await tweak.DetectAsync(CancellationToken.None));
            Assert.False(await tweak.DetectAsync(CancellationToken.None));
        }

        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? after = root.OpenSubKey(path, writable: false);
        Assert.Null(after);
    }
}
