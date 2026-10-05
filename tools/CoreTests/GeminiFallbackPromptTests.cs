using SpankyhoOptimizer.Services;

public sealed class GeminiFallbackPromptTests
{
    [Fact]
    public void FallbackRequiresLiteralCzechSpeechAndEmptySilence()
    {
        string prompt = GeminiFallbackPrompt.Build();
        Assert.Contains("češtin", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("doslov", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ticho", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prázdný", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FallbackDoesNotPrimeCommandsOrExampleUtterances()
    {
        string prompt = GeminiFallbackPrompt.Build();
        Assert.DoesNotContain("Spanky", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hej vole", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("benchmark", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NVIDIA", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Diablo Immortal", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
