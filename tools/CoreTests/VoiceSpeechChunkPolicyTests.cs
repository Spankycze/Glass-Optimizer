using SpankyhoOptimizer.Services;

public sealed class VoiceSpeechChunkPolicyTests
{
    [Theory]
    [InlineData("Jasně.", true, "Jasně.")]
    [InlineData("Teplota CPU je 52 stupňů.", true, "Teplota CPU je 52 stupňů.")]
    [InlineData("Tohle je delší rozpracovaná věta bez tečky která ještě nekončí", false, "")]
    public void First_chunk_prefers_complete_short_sentence(string text, bool expected, string chunk)
    {
        var buffer = new System.Text.StringBuilder(text);
        bool ok = VoiceSpeechChunkPolicy.TryTake(buffer, firstChunk: true, out string actual);
        Assert.Equal(expected, ok);
        Assert.Equal(chunk, actual);
    }

    [Fact]
    public void Later_chunks_keep_larger_minimum()
    {
        var buffer = new System.Text.StringBuilder("Jasně.");
        Assert.False(VoiceSpeechChunkPolicy.TryTake(buffer, firstChunk: false, out _));
    }

    [Fact]
    public void First_chunk_can_soft_break_earlier_than_later_chunks()
    {
        string text = "Tohle je první část odpovědi, a tady pokračuje další vysvětlení které ještě není celé.";
        var first = new System.Text.StringBuilder(text);
        var later = new System.Text.StringBuilder(text);

        Assert.True(VoiceSpeechChunkPolicy.TryTake(first, firstChunk: true, out string firstChunk));
        Assert.True(VoiceSpeechChunkPolicy.TryTake(later, firstChunk: false, out string laterChunk));
        Assert.True(firstChunk.Length < laterChunk.Length);
    }
}
