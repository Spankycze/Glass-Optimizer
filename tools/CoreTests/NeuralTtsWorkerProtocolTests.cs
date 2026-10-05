using SpankyhoOptimizer.Services;

public sealed class NeuralTtsWorkerProtocolTests
{
    [Fact]
    public void Request_roundtrips_text_and_output_path()
    {
        string json = NeuralTtsWorkerProtocol.CreateRequest(7, "Ahoj světe.", @"C:\Temp\voice.mp3");
        Assert.Contains("\"id\":7", json);
        Assert.Contains("Ahoj světe.", json);
        Assert.Contains("voice.mp3", json);
    }

    [Fact]
    public void Success_response_is_parsed()
    {
        var result = NeuralTtsWorkerProtocol.ParseResponse(
            "{\"id\":7,\"ok\":true,\"generation_ms\":321}");
        Assert.True(result.Ok);
        Assert.Equal(7, result.Id);
        Assert.Equal(321, result.GenerationMs);
    }
}
