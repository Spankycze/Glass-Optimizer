using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpankyhoOptimizer.Services;

public sealed class OpenAiSegmentTranscriptionService : IAsyncDisposable
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api.openai.com/"),
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly object _sync = new();
    private readonly Queue<byte[]> _preRoll = new();
    private readonly List<byte[]> _speechChunks = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WaveIn? _waveIn;
    private CancellationTokenSource? _cts;
    private Task? _deviceMonitorTask;
    private string _captureEndpointId = string.Empty;
    private Action<string>? _onTranscript;
    private string _apiKey = string.Empty;

    private bool _speechActive;
    private int _silenceChunks;
    private int _speechChunkCount;
    private int _speechEvidenceChunks;
    private double _noiseFloor = 0.0045;

    private const int SampleRate = 16000;
    private const int PreRollChunks = 5;
    private const int MinSpeechChunks = 4;
    private const int MaxSpeechChunks = 180;
    private const int SpeechStartConfirmChunks = 2;

    public bool IsRunning { get; private set; }
    public static long LastSttLatencyMs { get; private set; }
    public static string LastSttEngine { get; private set; } = "not-run";

    internal static string ResolveTranscribeModel(OpenAiVoiceSettings settings)
    {
        string model = string.IsNullOrWhiteSpace(settings.TranscribeModel)
            ? "gpt-transcribe"
            : settings.TranscribeModel.Trim();
        return string.Equals(model, "gpt-live-transcribe", StringComparison.OrdinalIgnoreCase)
            ? "gpt-transcribe"
            : model;
    }

    public Task StartAsync(string apiKey, Action<string> onFinalTranscript, CancellationToken token)
    {
        if (IsRunning)
            return Task.CompletedTask;

        _apiKey = apiKey?.Trim() ?? string.Empty;
        if (_apiKey.Length == 0)
            throw new InvalidOperationException("OpenAI cloud STT API key is not configured.");
        _onTranscript = onFinalTranscript;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        StartRecorder();
        _captureEndpointId = TryGetDefaultCaptureId();
        _deviceMonitorTask = Task.Run(() => MonitorDefaultCaptureAsync(_cts.Token), _cts.Token);

        IsRunning = true;
        LogService.Info("voice", "VOICE_STT openai-cloud ready language=cs rate=16000 vad=adaptive");
        return Task.CompletedTask;
    }

    private void StartRecorder()
    {
        var waveIn = new WaveIn
        {
            DeviceNumber = 0,
            BufferMilliseconds = 80,
            NumberOfBuffers = 4,
            WaveFormat = new WaveFormat(SampleRate, 16, 1)
        };

        waveIn.DataAvailable += OnAudioAvailable;
        waveIn.RecordingStopped += OnRecordingStopped;
        waveIn.StartRecording();
        _waveIn = waveIn;
    }

    private async Task MonitorDefaultCaptureAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, token).ConfigureAwait(false);
                string current = TryGetDefaultCaptureId();
                if (current.Length == 0 || string.Equals(current, _captureEndpointId, StringComparison.Ordinal))
                    continue;

                string previous = _captureEndpointId;
                _captureEndpointId = current;
                RestartRecorder();
                LogService.Info("voice", $"VOICE_MIC_HOTSWAP old={previous} new={current}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogService.Error("voice", "Voice microphone hot-swap monitor failed", ex);
            }
        }
    }

    private void RestartRecorder()
    {
        lock (_sync)
        {
            ResetSpeechState();

            WaveIn? previous = _waveIn;
            _waveIn = null;
            if (previous != null)
            {
                try { previous.DataAvailable -= OnAudioAvailable; } catch { }
                try { previous.RecordingStopped -= OnRecordingStopped; } catch { }
                try { previous.StopRecording(); } catch { }
                try { previous.Dispose(); } catch { }
            }

            if (_cts?.IsCancellationRequested == true)
                return;

            StartRecorder();
        }
    }

    private static string TryGetDefaultCaptureId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return device.ID ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void OnAudioAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRunning || e.BytesRecorded <= 0)
            return;

        byte[] chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        OpenWakeWordGateService.PushPcm(chunk);
        (double rms, double peak, double zeroCrossingRate) = AnalyzePcm16(chunk);

        lock (_sync)
        {
            double startThreshold = Math.Max(0.0085, _noiseFloor * 2.6);
            double continueThreshold = Math.Max(0.0055, _noiseFloor * 1.65);
            double peakStartThreshold = Math.Max(0.045, _noiseFloor * 7.0);
            double peakContinueThreshold = Math.Max(0.030, _noiseFloor * 4.5);

            bool voicedStart =
                rms >= startThreshold ||
                (peak >= peakStartThreshold && zeroCrossingRate >= 0.015);

            bool voicedContinue =
                rms >= continueThreshold ||
                peak >= peakContinueThreshold;

            if (!_speechActive)
            {
                if (rms < Math.Max(0.03, _noiseFloor * 5.0))
                    _noiseFloor = Math.Clamp((_noiseFloor * 0.985) + (rms * 0.015), 0.0015, 0.025);

                _preRoll.Enqueue(chunk);
                while (_preRoll.Count > PreRollChunks)
                    _preRoll.Dequeue();

                _speechEvidenceChunks = voicedStart
                    ? Math.Min(SpeechStartConfirmChunks, _speechEvidenceChunks + 1)
                    : Math.Max(0, _speechEvidenceChunks - 1);

                if (_speechEvidenceChunks >= SpeechStartConfirmChunks)
                {
                    _speechActive = true;
                    _silenceChunks = 0;
                    _speechChunkCount = 0;
                    _speechChunks.Clear();

                    foreach (byte[] pre in _preRoll)
                        _speechChunks.Add(pre);
                    _preRoll.Clear();

                    _speechChunkCount = _speechChunks.Count;
                }

                return;
            }

            _speechChunks.Add(chunk);
            _speechChunkCount++;

            if (voicedContinue)
                _silenceChunks = Math.Max(0, _silenceChunks - 2);
            else
                _silenceChunks++;

            int requiredSilence = VoiceVadPolicy.RequiredEndSilenceChunks(_speechChunkCount);

            bool end =
                (_silenceChunks >= requiredSilence && _speechChunkCount >= MinSpeechChunks) ||
                _speechChunkCount >= MaxSpeechChunks;

            if (!end)
                return;

            byte[] pcm = CombineChunks(_speechChunks);
            ResetSpeechState();

            CancellationToken token = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => TranscribeAndDispatchAsync(pcm, token), token);
        }
    }

    private async Task TranscribeAndDispatchAsync(byte[] pcm, CancellationToken token)
    {
        if (pcm.Length < SampleRate * 2 / 4)
            return;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        var latency = Stopwatch.StartNew();
        try
        {
            byte[] wav = WrapWav(pcm);
            string transcript = await TranscribeAsync(wav, token).ConfigureAwait(false);
            transcript = transcript.Trim();

            if (transcript.Length == 0)
                return;

            LastSttLatencyMs = latency.ElapsedMilliseconds;
            LogService.Info("voice", $"VOICE_LATENCY stt_ms={LastSttLatencyMs} audio_ms={pcm.Length / 32} engine={LastSttEngine}");
            LogService.Info("voice", "VOICE_STT_FINAL " + transcript);
            _onTranscript?.Invoke(transcript);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "OpenAI transcription failed", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<string> TranscribeAsync(byte[] wav, CancellationToken token) =>
        TranscribeOpenAiAsync(wav, token);

    private async Task<string> TranscribeOpenAiAsync(byte[] wav, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(file, "file", "voice.wav");
        form.Add(new StringContent(ResolveTranscribeModel(OpenAiVoiceSettingsService.Load())), "model");
        form.Add(new StringContent("cs"), "languages[]");
        form.Add(new StringContent(VoiceTranscriptionContext.Prompt), "prompt");
        foreach (string keyword in VoiceTranscriptionContext.Keywords)
            form.Add(new StringContent(keyword), "keywords[]");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = form;

        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI STT HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        string result = doc.RootElement.TryGetProperty("text", out JsonElement text)
            ? text.GetString() ?? string.Empty
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(result))
            LastSttEngine = "OpenAI cloud STT";
        return result;
    }

    private static (double Rms, double Peak, double ZeroCrossingRate) AnalyzePcm16(byte[] data)
    {
        if (data.Length < 4)
            return (0, 0, 0);

        double sum = 0;
        double peak = 0;
        int zeroCrossings = 0;
        int samples = data.Length / 2;
        short previous = 0;
        bool hasPrevious = false;

        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            short sample = (short)(data[i] | (data[i + 1] << 8));
            double normalized = sample / 32768.0;
            double absolute = Math.Abs(normalized);
            sum += normalized * normalized;
            if (absolute > peak)
                peak = absolute;

            if (hasPrevious && ((sample >= 0 && previous < 0) || (sample < 0 && previous >= 0)))
                zeroCrossings++;

            previous = sample;
            hasPrevious = true;
        }

        return (
            Math.Sqrt(sum / samples),
            peak,
            zeroCrossings / (double)Math.Max(1, samples - 1));
    }

    private static byte[] CombineChunks(List<byte[]> chunks)
    {
        int total = 0;
        foreach (byte[] chunk in chunks)
            total += chunk.Length;

        byte[] combined = new byte[total];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
            offset += chunk.Length;
        }

        return combined;
    }

    private static byte[] WrapWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, new WaveFormat(SampleRate, 16, 1)))
        {
            writer.Write(pcm, 0, pcm.Length);
            writer.Flush();
        }

        return ms.ToArray();
    }

    private void ResetSpeechState()
    {
        _speechActive = false;
        _silenceChunks = 0;
        _speechChunkCount = 0;
        _speechEvidenceChunks = 0;
        _speechChunks.Clear();
        _preRoll.Clear();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            LogService.Error("voice", "OpenAI microphone capture stopped", e.Exception);
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;

        try { _cts?.Cancel(); } catch { }

        Task? monitor = _deviceMonitorTask;
        _deviceMonitorTask = null;
        if (monitor != null)
        {
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogService.Error("voice", "Voice microphone monitor shutdown failed", ex); }
        }

        lock (_sync)
        {
            ResetSpeechState();

            WaveIn? waveIn = _waveIn;
            _waveIn = null;
            if (waveIn != null)
            {
                try { waveIn.DataAvailable -= OnAudioAvailable; } catch { }
                try { waveIn.RecordingStopped -= OnRecordingStopped; } catch { }
                try { waveIn.StopRecording(); } catch { }
                try { waveIn.Dispose(); } catch { }
            }
        }

        _cts?.Dispose();
        _cts = null;
        _captureEndpointId = string.Empty;
    }
}
