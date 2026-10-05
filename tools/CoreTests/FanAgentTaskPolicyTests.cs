using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class FanAgentTaskPolicyTests
{
    [Fact]
    public void FanAgentTaskReplacesResidentWatchdog()
    {
        string xml = FanProfileService.BuildFanAgentTaskXml(
            @"C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.RuntimeHost.exe",
            "S-1-5-21-1-2-3-1001");

        Assert.Contains("<LogonTrigger>", xml, StringComparison.Ordinal);
        Assert.Contains("<RestartOnFailure>", xml, StringComparison.Ordinal);
        Assert.Contains("<Count>3</Count>", xml, StringComparison.Ordinal);
        Assert.Contains("<Interval>PT1M</Interval>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("PT5S", xml, StringComparison.Ordinal);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml, StringComparison.Ordinal);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains("--fan-agent", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("--fan-watchdog-loop", xml, StringComparison.Ordinal);
    }
}
