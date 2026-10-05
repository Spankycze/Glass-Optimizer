using SpankyhoOptimizer.Services;

public sealed class VoiceTranscriptCleanupPolicyTests
{
    [Theory]
    [InlineData("Hejvola!", "hej vole!")]
    [InlineData("P miejvola.", "hej vole.")]
    [InlineData("P miejvolo, hej.", "hej vole.")]
    [InlineData("Hej, vola.", "hej vole.")]
    [InlineData("Nucni rezim vetraku", "noční rezim vetraku")]
    [InlineData("Diablo imortal", "Diablo Immortal")]
    [InlineData("fire fox", "Firefox")]
    [InlineData("chat gpt", "ChatGPT")]
    [InlineData("rajzen", "Ryzen")]
    [InlineData("ge force", "GeForce")]
    [InlineData("slysis me", "slyšíš me")]
    public void Normalizes_common_wake_phrase_stt_errors(string input, string expected)
    {
        Assert.Equal(expected, VoiceTranscriptCleanupPolicy.Normalize(input));
    }

    [Theory]
    [InlineData("Paginapisuj püüüüüüüüesn", true)]
    [InlineData("aaaaaaaa", true)]
    [InlineData("jaká je teplota procesoru", false)]
    [InlineData("hej vole", false)]
    [InlineData("noční režim ventilátorů", false)]
    public void Detects_only_obvious_repeated_character_garbage(string input, bool expected)
    {
        Assert.Equal(expected, VoiceTranscriptCleanupPolicy.HasExcessiveCharacterRun(input));
    }
}
