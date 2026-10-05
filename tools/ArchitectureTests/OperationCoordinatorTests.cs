using SpankyhoOptimizer.Services;

public sealed class OperationCoordinatorTests
{
    [Fact]
    public async Task PreCancelledTokenCancelsImmediately()
    {
        Environment.SetEnvironmentVariable("SPANKY_TEST_DISABLE_ASYNC_LOG", "1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OperationCoordinator.RunExclusiveAsync(
                "unit-precancel",
                _ => Task.CompletedTask,
                TimeSpan.FromSeconds(1),
                cts.Token));
    }

    [Fact]
    public async Task SameKeyRunsSingleFlight()
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

            await Task.Delay(20, token);
            Interlocked.Decrement(ref active);
        }

        Task[] tasks = Enumerable.Range(0, 4)
            .Select(_ => OperationCoordinator.RunExclusiveAsync(
                "unit-singleflight",
                Work,
                TimeSpan.FromSeconds(2)))
            .ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(1, maxActive);
    }
}
