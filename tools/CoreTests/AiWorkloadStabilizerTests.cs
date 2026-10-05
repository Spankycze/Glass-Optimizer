using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiWorkloadStabilizerTests
{
    [Fact]
    public void GameEntersImmediatelyButNeedsTwoVotesToExit()
    {
        var s = new AiWorkloadStabilizer();
        Assert.Equal(AiWorkloadClass.Background, s.Update(AiWorkloadClass.Background));
        Assert.Equal(AiWorkloadClass.Game, s.Update(AiWorkloadClass.Game));
        Assert.Equal(AiWorkloadClass.Game, s.Update(AiWorkloadClass.Interactive));
        Assert.Equal(AiWorkloadClass.Interactive, s.Update(AiWorkloadClass.Interactive));
    }

    [Fact]
    public void InteractiveBackgroundFlapDoesNotSwitchOnSingleSample()
    {
        var s = new AiWorkloadStabilizer();
        Assert.Equal(AiWorkloadClass.Interactive, s.Update(AiWorkloadClass.Interactive));
        Assert.Equal(AiWorkloadClass.Interactive, s.Update(AiWorkloadClass.Background));
        Assert.Equal(AiWorkloadClass.Background, s.Update(AiWorkloadClass.Background));
        Assert.Equal(AiWorkloadClass.Background, s.Update(AiWorkloadClass.Interactive));
    }

    [Fact]
    public void IdleRequiresThreeVotes()
    {
        var s = new AiWorkloadStabilizer();
        s.Update(AiWorkloadClass.Interactive);
        Assert.Equal(AiWorkloadClass.Interactive, s.Update(AiWorkloadClass.Idle));
        Assert.Equal(AiWorkloadClass.Interactive, s.Update(AiWorkloadClass.Idle));
        Assert.Equal(AiWorkloadClass.Idle, s.Update(AiWorkloadClass.Idle));
    }
}
