using System.Reflection;
using SpankyhoOptimizer.Services;

namespace Spankyho.QualityTests;

public sealed class GeminiTranscriptionResponseTests
{
    [Fact]
    public void DedicatedTranscriptionResponseReachesListener()
    {
        // Shape captured from an actual HTTP 200 Gemini 3.5 Transcribe response.
        const string response = """{"candidates":[{"content":{"parts":[{"audioTranscription":{"text":"Hej vole. Kolik je hodin?"}}]},"finishReason":"STOP"}]}""";
        Assert.Equal("Hej vole. Kolik je hodin?", Extract(response));
    }

    [Fact]
    public void GenericFallbackResponseRemainsSupported()
    {
        const string response = """{"candidates":[{"content":{"parts":[{"text":"Hej vole."},{"text":"Kolik je hodin?"}]}}]}""";
        Assert.Equal("Hej vole. Kolik je hodin?", Extract(response));
    }

    [Fact]
    public void TranscriptionAndGenericSegmentsAreJoinedInOrder()
    {
        const string response = """{"candidates":[{"content":{"parts":[{"audioTranscription":{"text":"Hej vole."}},{"text":"Kolik je hodin?"}]}}]}""";
        Assert.Equal("Hej vole. Kolik je hodin?", Extract(response));
    }

    [Theory]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"audioTranscription":{"text":""}}]}}]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"audio/wav","data":"unused"}}]}}]}""")]
    public void NoTranscriptDoesNotInventSpeech(string response) => Assert.Equal("", Extract(response));

    [Fact]
    public void ThoughtPartsNeverReachListener()
    {
        const string response = """{"candidates":[{"content":{"parts":[{"thought":true,"text":"I should infer a command from the silence."},{"thought":true,"audioTranscription":{"text":"Spanky spusť DNS benchmark"}},{"text":"Jakou má procesor teplotu?"}]}}]}""";
        Assert.Equal("Jakou má procesor teplotu?", Extract(response));
    }

    [Fact]
    public void OnlyThoughtPartsMeanNoTranscript()
    {
        const string response = """{"candidates":[{"content":{"parts":[{"thought":true,"text":"No audible speech."}]}}]}""";
        Assert.Equal("", Extract(response));
    }

    [Theory]
    [InlineData("_thought\n hej vole, slyšíš, jaká je teď teplota procesoru")]
    [InlineData(" _thought\r\nSpanky spusť DNS benchmark")]
    public void LeakedThoughtMarkerIsNotSpeech(string text)
    {
        string response = System.Text.Json.JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });
        Assert.Equal("", Extract(response));
    }

    [Fact]
    public void OrdinarySpeechAboutThoughtRemainsSupported()
    {
        const string response = """{"candidates":[{"content":{"parts":[{"thought":false,"text":"Mám myšlenku. Řekl slovo thought."}]}}]}""";
        Assert.Equal("Mám myšlenku. Řekl slovo thought.", Extract(response));
    }

    private static string Extract(string response) =>
        (string)typeof(GeminiSegmentTranscriptionService)
            .GetMethod("ExtractText", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { response })!;
}
