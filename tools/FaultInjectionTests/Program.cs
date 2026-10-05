using SpankyhoOptimizer.Services;

if (args.Length == 0)
{
    Console.Error.WriteLine("Use scenario name.");
    return 2;
}

string scenario = args[0].ToLowerInvariant();

try
{
    switch (scenario)
    {
        case "process-timeout":
            await ExpectAsync<TimeoutException>(() =>
                ProcessRunner.RunAsync("cmd.exe", "/C echo SHOULD_NOT_RUN", TimeSpan.FromSeconds(2)));
            break;

        case "dns-read-denied":
            Expect<UnauthorizedAccessException>(() => DnsProfileService.GetActiveDnsSummary());
            break;

        case "dns-timeout":
            Expect<TimeoutException>(() => DnsProfileService.GetActiveDnsSummary());
            break;

        case "powershell-timeout":
            await ExpectAsync<TimeoutException>(() => PowerShellCenterService.GetStatusAsync());
            break;

        case "adapter-restart-fail":
            await ExpectAsync<InvalidOperationException>(() =>
                FaultInjectionService.InjectAsync("network.adapter-restart").AsTask());
            break;

        case "adapter-timeout":
            await ExpectAsync<TimeoutException>(() => MtuOptimizerService.GetActiveAdapterAsync());
            break;

        case "mtu-probe-timeout":
            await ExpectAsync<TimeoutException>(() => MtuOptimizerService.DetectRecommendedAsync());
            break;

        case "pre-cancel":
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await ExpectAsync<OperationCanceledException>(() =>
                    OperationCoordinator.RunExclusiveAsync(
                        "fault-pre-cancel",
                        _ => Task.CompletedTask,
                        TimeSpan.FromSeconds(1),
                        cts.Token));
            }
            break;

        case "cancellation-race":
            await RunCancellationRaceAsync();
            break;

        case "external-profile":
            Expect<TimeoutException>(() => DnsProfileService.GetActiveDnsSummary());
            await ExpectAsync<TimeoutException>(() => PowerShellCenterService.GetStatusAsync());
            await ExpectAsync<InvalidOperationException>(() =>
                FaultInjectionService.InjectAsync("network.adapter-restart").AsTask());
            await ExpectAsync<UnauthorizedAccessException>(() =>
                FaultInjectionService.InjectAsync("registry.read").AsTask());
            await ExpectAsync<InvalidOperationException>(() =>
                FaultInjectionService.InjectAsync("wmi.query").AsTask());
            await ExpectAsync<InvalidOperationException>(() =>
                FaultInjectionService.InjectAsync("service.query").AsTask());
            break;

        case "registry-denied":
            await ExpectAsync<UnauthorizedAccessException>(() =>
                FaultInjectionService.InjectAsync("registry.read").AsTask());
            break;

        case "wmi-fail":
            await ExpectAsync<InvalidOperationException>(() =>
                FaultInjectionService.InjectAsync("wmi.query").AsTask());
            break;

        case "service-fail":
            await ExpectAsync<InvalidOperationException>(() =>
                FaultInjectionService.InjectAsync("service.query").AsTask());
            break;

        default:
            throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario");
    }

    Console.WriteLine("FAULT_SCENARIO_OK " + scenario);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAULT_SCENARIO_FAIL " + scenario + " " + ex);
    return 42;
}

static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown.");
}

static void Expect<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown.");
}

static async Task RunCancellationRaceAsync()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    Task first = OperationCoordinator.RunExclusiveAsync(
        "fault-cancellation-race",
        async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        },
        TimeSpan.FromSeconds(2));

    await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
    await ExpectAsync<OperationCanceledException>(() =>
        OperationCoordinator.RunExclusiveAsync(
            "fault-cancellation-race",
            _ => Task.CompletedTask,
            TimeSpan.FromSeconds(1),
            cts.Token));

    release.TrySetResult();
    await first;

    await OperationCoordinator.RunExclusiveAsync(
        "fault-cancellation-race",
        _ => Task.CompletedTask,
        TimeSpan.FromSeconds(1));
}
