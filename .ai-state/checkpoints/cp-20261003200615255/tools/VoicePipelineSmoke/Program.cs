using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SpankyhoOptimizer.Services;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
string mp3 = Path.Combine(Path.GetTempPath(), "voice-pipeline-" + Guid.NewGuid().ToString("N") + ".mp3");
string wav = Path.ChangeExtension(mp3, ".wav");
try
{
    using (var devices = new MMDeviceEnumerator())
    using (var mic = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
        if (mic.AudioEndpointVolume.Mute) throw new InvalidOperationException("Microphone muted; audible test withheld.");

    await NeuralTtsWorkerService.GenerateAsync("Hej vole.", mp3, timeout.Token);
    using (var reader = new MediaFoundationReader(mp3))
    using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)))
        WaveFileWriter.CreateWaveFile(wav, resampler);

    await using var transcriber = new GeminiSegmentTranscriptionService();
    typeof(GeminiSegmentTranscriptionService).GetField("_apiKey", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(transcriber, VoiceCloudAiSettingsService.GetApiKey());
    var task = (Task<string>)typeof(GeminiSegmentTranscriptionService)
        .GetMethod("TranscribeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(transcriber, new object[] { File.ReadAllBytes(wav), timeout.Token })!;
    string transcript = await task.WaitAsync(timeout.Token);
    if (string.IsNullOrWhiteSpace(transcript)) throw new InvalidOperationException("Actual cloud transcription returned no parsed text.");
    Console.WriteLine($"VOICE_LIVE_TRANSCRIPT_OK chars={transcript.Length}");

    await using var listener = new SpankyVoiceListenerService();
    typeof(SpankyVoiceListenerService).GetMethod("HandleRecognizedText", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(listener, new object[] { transcript });
    var active = typeof(SpankyVoiceListenerService).GetField("_ttsActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
    if ((int)active.GetValue(listener)! != 1) throw new InvalidOperationException("Actual transcribed wake phrase did not start speech.");
    while ((int)active.GetValue(listener)! != 0)
        await Task.Delay(50, timeout.Token);
    if (NeuralTtsService.LastFirstAudioLatencyMs <= 0) throw new InvalidOperationException("No first audio playback was recorded.");
    Console.WriteLine($"VOICE_PIPELINE_PLAYBACK_OK firstAudioMs={NeuralTtsService.LastFirstAudioLatencyMs} status={SpankyVoiceListenerService.LastStatus}");
}
finally
{
    await NeuralTtsWorkerService.StopAsync();
    try { File.Delete(mp3); } catch { }
    try { File.Delete(wav); } catch { }
}
