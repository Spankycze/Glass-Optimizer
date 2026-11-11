using System;
using System.IO;
using SpankyhoOptimizer.Services;

public sealed class FanAgentRestartPolicyTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("15.4.562", true)]
    [InlineData("15.4.567|2026-10-03T08:00:00Z", true)]
    [InlineData("restart", true)]
    public void Any_non_empty_restart_request_is_accepted(string payload, bool expected)
    {
        Assert.Equal(expected, FanAgentRestartPolicy.ShouldRestart(payload));
    }

    [Fact]
    public void Valid_restart_request_is_consumed_exactly_once()
    {
        string path = Path.Combine(Path.GetTempPath(), "spanky-restart-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "15.4.832|2026-10-09T22:00:00Z");
            Assert.True(FanAgentRestartPolicy.TryConsumeRestartRequest(path));
            Assert.False(File.Exists(path));
            Assert.False(FanAgentRestartPolicy.TryConsumeRestartRequest(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Locked_restart_marker_never_causes_restart_storm()
    {
        string path = Path.Combine(Path.GetTempPath(), "spanky-restart-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "restart");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Exception? readError = null;
                Assert.False(FanAgentRestartPolicy.TryConsumeRestartRequest(path, ex => readError = ex));
                Assert.IsType<IOException>(readError);
                Assert.True(File.Exists(path));
            }
            Assert.True(FanAgentRestartPolicy.TryConsumeRestartRequest(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Blank_restart_marker_does_not_trigger_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), "spanky-restart-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "   ");
            Assert.False(FanAgentRestartPolicy.TryConsumeRestartRequest(path));
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
}
