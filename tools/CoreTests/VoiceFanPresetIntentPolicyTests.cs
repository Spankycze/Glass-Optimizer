using SpankyhoOptimizer.Services;

public sealed class VoiceFanPresetIntentPolicyTests
{
    [Theory]
    [InlineData("dej vetraky na nocni rezim", "night")]
    [InlineData("nucni rezim zapni", "night")]
    [InlineData("dej vetraky na tichy profil", "quiet")]
    [InlineData("automaticky rezim vetraku", "auto")]
    [InlineData("dej vetraky na auto", "auto")]
    [InlineData("normalni rezim vetraku", "balanced")]
    [InlineData("vetraky na vykon", "performance")]
    [InlineData("gaming rezim vetraku", "performance")]
    public void Maps_common_czech_fan_commands(string utterance, string expected)
    {
        string normalized = SpankyVoiceToolService.NormalizeRoutingText(utterance);
        Assert.Equal(expected, VoiceFanPresetIntentPolicy.TryResolve(normalized));
    }

    [Theory]
    [InlineData("jaka je teplota procesoru")]
    [InlineData("jake je pocasi")]
    [InlineData("otevri firefox")]
    public void Does_not_capture_unrelated_commands(string utterance)
    {
        string normalized = SpankyVoiceToolService.NormalizeRoutingText(utterance);
        Assert.Null(VoiceFanPresetIntentPolicy.TryResolve(normalized));
    }
}
