using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using SpankyhoOptimizer.Services;

public static class CoyoteScenarios
{
    private static int ScenarioId;

    private static string NextKey(string prefix) => prefix + "-" + Interlocked.Increment(ref ScenarioId);

    [Test]
    public static async Task SingleFlightSameKey()
    {
        int active = 0;
        int maxActive = 0;
        int completed = 0;
        string key = NextKey("coyote-single-flight");

        Task[] runs = Enumerable.Range(0, 4).Select(i =>
            OperationCoordinator.RunExclusiveAsync(
                key,
                async token =>
                {
                    int now = Interlocked.Increment(ref active);
                    int snapshot;
                    do
                    {
                        snapshot = Volatile.Read(ref maxActive);
                        if (snapshot >= now) break;
                    }
                    while (Interlocked.CompareExchange(ref maxActive, now, snapshot) != snapshot);

                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    Interlocked.Decrement(ref active);
                    Interlocked.Increment(ref completed);
                },
                TimeSpan.FromSeconds(5))).ToArray();

        await Task.WhenAll(runs);
        Specification.Assert(maxActive == 1, $"single-flight violated, maxActive={maxActive}");
        Specification.Assert(completed == 4, $"expected 4 completions, got {completed}");
        Specification.Assert(active == 0, $"active leaked: {active}");
    }

    [Test]
    public static async Task CancelWaitingOperationDoesNotPoisonGate()
    {
        string key = NextKey("coyote-cancel-gate");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = OperationCoordinator.RunExclusiveAsync(
            key,
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            },
            TimeSpan.FromSeconds(5));

        await entered.Task;

        using var waitingCts = new CancellationTokenSource();
        Task second = OperationCoordinator.RunExclusiveAsync(
            key,
            async _ => await Task.Yield(),
            TimeSpan.FromSeconds(5),
            waitingCts.Token);

        waitingCts.Cancel();
        await Task.Yield();
        release.TrySetResult();

        await first;

        bool secondCompleted = false;
        bool secondCancelled = false;
        try
        {
            await second;
            secondCompleted = true;
        }
        catch (OperationCanceledException)
        {
            secondCancelled = true;
        }

        Specification.Assert(secondCompleted || secondCancelled,
            "waiting operation neither completed nor observed cancellation");

        int finalRuns = 0;
        await OperationCoordinator.RunExclusiveAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref finalRuns);
                await Task.Yield();
            },
            TimeSpan.FromSeconds(5));

        Specification.Assert(finalRuns == 1, "gate was poisoned after cancellation");
    }
}

public static class Program
{
    public static void Main() => Console.WriteLine("Spankyho Coyote scenarios. Run with coyote rewrite/test.");
}
