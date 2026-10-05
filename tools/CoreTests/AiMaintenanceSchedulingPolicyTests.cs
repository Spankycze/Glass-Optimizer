using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiMaintenanceSchedulingPolicyTests
{
    [Fact]
    public void RotatesFirstPriorityAcrossKinds()
    {
        Assert.Equal("service", AiMaintenanceSchedulingPolicy.OrderForCycle(0)[0]);
        Assert.Equal("task", AiMaintenanceSchedulingPolicy.OrderForCycle(1)[0]);
        Assert.Equal("driver", AiMaintenanceSchedulingPolicy.OrderForCycle(2)[0]);
        Assert.Equal("service", AiMaintenanceSchedulingPolicy.OrderForCycle(3)[0]);
    }

    [Fact]
    public void EveryCycleContainsEveryKindOnce()
    {
        foreach (int cycle in Enumerable.Range(0, 9))
        {
            string[] order = AiMaintenanceSchedulingPolicy.OrderForCycle(cycle);
            Assert.Equal(3, order.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains("service", order);
            Assert.Contains("task", order);
            Assert.Contains("driver", order);
        }
    }
}
