using Windows.Media.SpeechRecognition;

namespace SpankyhoOptimizer.Services;

internal static class VoiceRecognitionResultAccessor
{
    internal static string GetText(SpeechContinuousRecognitionResultGeneratedEventArgs args) =>
        args.Result?.Text ?? string.Empty;
}
