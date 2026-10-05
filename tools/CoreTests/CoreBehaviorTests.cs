using SpankyhoOptimizer.Services;

public sealed class CoreBehaviorTests
{
    [Fact]
    public void DnsGuard_returns_only_unexpected_resolvers()
    {
        string[] expected = ["1.1.1.1", "9.9.9.9"];
        string[] current = ["1.1.1.1", "9.9.9.9", "192.168.1.1"];

        string[] unexpected = DnsProfileService.FindUnexpectedResolvers(expected, current);

        Assert.Single(unexpected);
        Assert.Equal("192.168.1.1", unexpected[0]);
    }

    [Fact]
    public void DnsGuard_is_case_insensitive_and_deduplicates()
    {
        string[] expected = ["DNS.EXAMPLE", "1.1.1.1"];
        string[] current = ["dns.example", "1.1.1.1", "1.1.1.1"];

        string[] unexpected = DnsProfileService.FindUnexpectedResolvers(expected, current);

        Assert.Empty(unexpected);
    }

    [Fact]
    public async Task OperationCoordinator_serializes_same_key()
    {
        int active = 0;
        int maxActive = 0;
        int completed = 0;
        string key = "test-" + Guid.NewGuid().ToString("N");

        Task[] jobs = Enumerable.Range(0, 6)
            .Select(_ => OperationCoordinator.RunExclusiveAsync(key, async token =>
            {
                int now = Interlocked.Increment(ref active);
                int observed;
                while ((observed = Volatile.Read(ref maxActive)) < now &&
                       Interlocked.CompareExchange(ref maxActive, now, observed) != observed)
                {
                }

                await Task.Delay(10, token);
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref completed);
            }, TimeSpan.FromSeconds(2)))
            .ToArray();

        await Task.WhenAll(jobs);

        Assert.Equal(1, maxActive);
        Assert.Equal(6, completed);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task OperationCoordinator_honors_precancelled_token()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OperationCoordinator.RunExclusiveAsync(
                "cancel-" + Guid.NewGuid().ToString("N"),
                _ => Task.CompletedTask,
                TimeSpan.FromSeconds(1),
                cts.Token));
    }
}
