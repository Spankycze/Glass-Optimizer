using Microsoft.Coyote;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using SpankyhoOptimizer.Services;

Environment.SetEnvironmentVariable("SPANKY_TEST_DISABLE_ASYNC_LOG", "1");

var configuration = Configuration.Create()
    .WithTestingIterations(1)
    .WithTestingTimeout(30)
    .WithTelemetryEnabled(false);

var engine = TestingEngine.Create(configuration, SingleFlightAndCancellationAsync);
engine.Run();

if (engine.TestReport.NumOfFoundBugs > 0)
{
    string dir = Path.Combine(AppContext.BaseDirectory, "CoyoteOutput");
    Directory.CreateDirectory(dir);
    engine.TryEmitReports(dir, "OperationCoordinator", out _);
    Console.Error.WriteLine("COYOTE_FAIL " + string.Join(" | ", engine.TestReport.BugReports));
    return 42;
}

Console.WriteLine($"COYOTE_OK schedules={engine.TestReport.NumOfExploredFairPaths + engine.TestReport.NumOfExploredUnfairPaths}");
return 0;

static async Task SingleFlightAndCancellationAsync()
{
    int active = 0;
    int maxActive = 0;
    int completed = 0;
    string key = "coyote-" + Guid.NewGuid().ToString("N");

    var jobs = Enumerable.Range(0, 4).Select(async i =>
    {
        await OperationCoordinator.RunExclusiveAsync(key, async token =>
        {
            int now = Interlocked.Increment(ref active);
            int observed;
            while ((observed = Volatile.Read(ref maxActive)) < now &&
                   Interlocked.CompareExchange(ref maxActive, now, observed) != observed) { }

            await Task.Yield();
            await Task.Yield();

            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref completed);
        }, TimeSpan.FromSeconds(5));
    }).ToArray();

    await Task.WhenAll(jobs);
    Specification.Assert(maxActive == 1, "Single-flight gate allowed {0} concurrent operations.", maxActive);
    Specification.Assert(completed == 4, "Expected 4 completed operations, got {0}.", completed);

    using var cts = new CancellationTokenSource();
    cts.Cancel();
    bool cancelled = false;
    try
    {
        await OperationCoordinator.RunExclusiveAsync(key + "-cancel", _ => Task.CompletedTask,
            TimeSpan.FromSeconds(1), cts.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    Specification.Assert(cancelled, "Pre-cancelled operation was not cancelled.");
}
