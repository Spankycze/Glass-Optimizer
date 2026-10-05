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

    private static string Extract(string response) =>
        (string)typeof(GeminiSegmentTranscriptionService)
            .GetMethod("ExtractText", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { response })!;
}
