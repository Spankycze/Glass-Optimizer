using SpankyhoOptimizer.Services;

public sealed class VoiceTtsChunkingTests
{
    [Fact]
    public void Long_reply_uses_short_first_chunk_without_losing_text()
    {
        const string text = "CPU má padesát čtyři stupňů a vytížení je dvacet procent, grafická karta má šedesát stupňů a využití je třicet procent, paměť je využitá přibližně z poloviny.";
        string[] chunks = VoiceTtsChunkingPolicy.Split(text);

        Assert.True(chunks.Length >= 2);
        Assert.InRange(chunks[0].Length, 1, 55);
        Assert.Equal(
            Normalize(text),
            Normalize(string.Join(" ", chunks)));
    }

    private static string Normalize(string value) =>
        string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
