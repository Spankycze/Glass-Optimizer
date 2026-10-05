using SpankyhoOptimizer.Services;

public sealed class VoiceEchoPolicyTests
{
    [Theory]
    [InlineData("teplota procesoru padesat stupnu", "teplota procesru padesat stupnu", true)]
    [InlineData("pocasi dnes oblacne", "kolik je ram", false)]
    [InlineData("graficka karta ma sedesat stupnu", "graficka karta ma sedesat stupnu", true)]
    public void Detects_distorted_tts_echo(string spoken, string heard, bool expected)
    {
        Assert.Equal(expected, VoiceEchoPolicy.IsLikelyEcho(heard, spoken));
    }
}
