using SpankyhoOptimizer.Services;

public sealed class VoiceTranscriptionContextTests
{
    [Theory]
    [InlineData("procesor")]
    [InlineData("ventilátory")]
    [InlineData("noční režim")]
    [InlineData("počasí")]
    [InlineData("Diablo Immortal")]
    [InlineData("ChatGPT")]
    [InlineData("Gemini")]
    [InlineData("Ryzen")]
    [InlineData("GeForce")]
    [InlineData("Gigabyte")]
    [InlineData("Kick")]
    [InlineData("Task Scheduler")]
    [InlineData("PowerShell")]
    [InlineData("Spicetify")]
    [InlineData("Defender")]
    [InlineData("Debloater")]
    [InlineData("Windows Update")]
    [InlineData("DNS")]
    [InlineData("MTU")]
    [InlineData("NVMe")]
    [InlineData("hej vole")]
    [InlineData("slyšíš")]
    [InlineData("větráky")]
    [InlineData("otáčky ventilátorů")]
    [InlineData("automatický režim ventilátorů")]
    [InlineData("tichý režim")]
    [InlineData("výkonný režim")]
    public void Context_contains_common_voice_terms(string term)
    {
        Assert.Contains(term, VoiceTranscriptionContext.Keywords);
    }
}
