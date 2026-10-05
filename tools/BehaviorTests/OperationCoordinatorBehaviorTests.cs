using SpankyhoOptimizer.Services;

public sealed class OperationCoordinatorBehaviorTests
{
    [Fact]
    public async Task PreCancelledTokenCancelsBeforeWork()
    {
        Environment.SetEnvironmentVariable("SPANKY_TEST_DISABLE_ASYNC_LOG", "1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OperationCoordinator.RunExclusiveAsync(
                "behavior-precancel",
                _ => { ran = true; return Task.CompletedTask; },
                TimeSpan.FromSeconds(1),
                cts.Token));

        Assert.False(ran);
    }

    [Fact]
    public async Task SameKeyIsStrictlySingleFlight()
    {
        Environment.SetEnvironmentVariable("SPANKY_TEST_DISABLE_ASYNC_LOG", "1");
        int active = 0;
        int maxActive = 0;

        async Task Work(CancellationToken token)
        {
            int now = Interlocked.Increment(ref active);
            while (true)
            {
                int observed = Volatile.Read(ref maxActive);
                if (observed >= now || Interlocked.CompareExchange(ref maxActive, now, observed) == observed)
                    break;
            }

            await Task.Delay(15, token);
            Interlocked.Decrement(ref active);
        }

        Task[] runs = Enumerable.Range(0, 4)
            .Select(_ => OperationCoordinator.RunExclusiveAsync(
                "behavior-singleflight",
                Work,
                TimeSpan.FromSeconds(2)))
            .ToArray();

        await Task.WhenAll(runs);
        Assert.Equal(1, maxActive);
    }
}
