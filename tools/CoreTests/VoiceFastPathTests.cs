using SpankyhoOptimizer.Services;

public sealed class VoiceFastPathTests
{
    [Fact]
    public async Task Local_tool_reply_does_not_wait_for_history_sync()
    {
        var rememberGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool spoken = false;

        Task delivery = VoiceLocalToolFastPathService.DeliverAsync(
            "kolik ma cpu stupnu",
            "CPU má 54 °C.",
            (_, _) => { spoken = true; return Task.CompletedTask; },
            (_, _, _) => rememberGate.Task,
            CancellationToken.None);

        await delivery.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(spoken);
        Assert.False(rememberGate.Task.IsCompleted);
    }
}
