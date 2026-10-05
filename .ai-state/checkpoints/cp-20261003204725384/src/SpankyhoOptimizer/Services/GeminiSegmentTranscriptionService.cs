using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SpankyhoOptimizer.Services;

public sealed class GeminiSegmentTranscriptionService : IAsyncDisposable
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://generativelanguage.googleapis.com/"),
        Timeout = TimeSpan.FromSeconds(18)
    };

    private readonly object _sync = new();
    private readonly Queue<byte[]> _preRoll = new();
    private readonly SemaphoreSlim _transcribeGate = new(1, 1);
    private readonly List<byte[]> _speechChunks = new();
    private CancellationTokenSource? _activeTranscriptionCts;
    private int _transcriptionGeneration;
    private DateTime _lastRateLimitSkipLogUtc = DateTime.MinValue;

    private WaveInEvent? _waveIn;
    private CancellationTokenSource? _cts;
    private string _apiKey = string.Empty;
    private Action<string>? _onTranscript;
    private Func<bool>? _isTtsActive;

    private bool _speechActive;
    private int _silenceChunks;
    private int _speechChunkCount;
    private int _speechEvidenceChunks;
    private double _noiseFloor = 0.003;

    private const int SampleRate = 16000;
    private const int PreRollChunks = 5;
    private const int MinSpeechChunks = 3;
    private const int MaxSpeechChunks = 300;

    public bool IsRunning { get; private set; }
    public static long LastSttLatencyMs { get; private set; }

    internal static int RequiredEndSilenceChunks(int speechChunkCount) =>
        GeminiCaptureAccuracyPolicy.EndSilenceChunks(speechChunkCount);

    internal static bool ShouldEndSpeech(int silenceChunks, int speechChunkCount) =>
        silenceChunks >= RequiredEndSilenceChunks(speechChunkCount) &&
        speechChunkCount >= MinSpeechChunks;

    internal static bool ShouldAppendCurrentAfterPreRoll(bool currentIncludedInPreRoll) =>
        !currentIncludedInPreRoll;

    public Task StartAsync(string apiKey, Action<string> onFinalTranscript, CancellationToken token, Func<bool>? isTtsActive = null)
    {
        if (IsRunning)
            return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Gemini API key is not configured.");

        _apiKey = apiKey.Trim();
        _onTranscript = onFinalTranscript;
        _isTtsActive = isTtsActive;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = -1,
            BufferMilliseconds = 100,
            NumberOfBuffers = 4,
            WaveFormat = new WaveFormat(SampleRate, 16, 1)
        };

        _waveIn.DataAvailable += OnAudioAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();

        IsRunning = true;
        LogService.Info("voice", "VOICE_STT gemini-segment ready model=gemini-3.5-transcribe language=cs-CZ rate=16000");
        return Task.CompletedTask;
    }

    private void OnAudioAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRunning || e.BytesRecorded <= 0)
            return;

        byte[] chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        double rms = CalculateRms16(chunk);

        lock (_sync)
        {
            bool ttsActive = _isTtsActive?.Invoke() == true;
            double normalStartThreshold = GeminiCaptureAccuracyPolicy.StartThreshold(_noiseFloor);
            double startThreshold = GeminiBargeInPolicy.GetStartThreshold(normalStartThreshold, ttsActive);
            double continueThreshold = GeminiCaptureAccuracyPolicy.ContinueThreshold(_noiseFloor);

            if (!_speechActive)
            {
                _noiseFloor = GeminiCaptureAccuracyPolicy.UpdateNoiseFloor(_noiseFloor, rms);

                _preRoll.Enqueue(chunk);
                while (_preRoll.Count > PreRollChunks)
                    _preRoll.Dequeue();

                GeminiSpeechStartDecision startDecision = GeminiSpeechStartPolicy.Next(
                    _speechEvidenceChunks,
                    rms >= startThreshold,
                    GeminiBargeInPolicy.RequiredEvidenceChunks(ttsActive));
                _speechEvidenceChunks = startDecision.EvidenceChunks;

                if (startDecision.ShouldStart)
                {
                    _speechActive = true;
                    _speechEvidenceChunks = 0;
                    _silenceChunks = 0;
                    _speechChunkCount = 0;
                    _speechChunks.Clear();

                    foreach (byte[] pre in _preRoll)
                        _speechChunks.Add(pre);
                    bool currentIncludedInPreRoll = _preRoll.Count > 0;
                    _preRoll.Clear();

                    if (ShouldAppendCurrentAfterPreRoll(currentIncludedInPreRoll))
                        _speechChunks.Add(chunk);
                    _speechChunkCount++;
                }

                return;
            }

            _speechChunks.Add(chunk);
            _speechChunkCount++;

            if (rms < continueThreshold)
                _silenceChunks++;
            else
                _silenceChunks = 0;

            bool reachedEnd =
                ShouldEndSpeech(_silenceChunks, _speechChunkCount) ||
                _speechChunkCount >= MaxSpeechChunks;

            if (!reachedEnd)
                return;

            int retainedChunks = GeminiTrimPolicy.GetRetainedChunkCount(
                _speechChunks.Count,
                _silenceChunks);
            byte[] pcm = CombineChunks(_speechChunks, retainedChunks);
            ResetSpeechState();

            int generation = Interlocked.Increment(ref _transcriptionGeneration);
            CancellationToken lifetimeToken = _cts?.Token ?? CancellationToken.None;
            var requestCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            CancellationTokenSource? previous = Interlocked.Exchange(
                ref _activeTranscriptionCts,
                requestCts);
            try { previous?.Cancel(); } catch { }

            _ = Task.Run(
                () => TranscribeAndDispatchAsync(pcm, generation, requestCts),
                CancellationToken.None);
        }
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

    private async Task TranscribeAndDispatchAsync(
        byte[] pcm,
        int generation,
        CancellationTokenSource requestCts)
    {
        using (requestCts)
        {
            CancellationToken token = requestCts.Token;
            if (pcm.Length < SampleRate * 2 / 4)
                return;

            bool gateHeld = false;
            try
            {
                await _transcribeGate.WaitAsync(token).ConfigureAwait(false);
                gateHeld = true;

                var latency = Stopwatch.StartNew();
                pcm = GeminiCaptureAccuracyPolicy.NormalizeQuietPcm(pcm, out double gain);
                LogService.Info("voice", $"VOICE_CAPTURE audio_ms={pcm.Length / 32} gain={gain:0.00} end_silence_ms={RequiredEndSilenceChunks(pcm.Length / 3200) * 100} pre_roll_ms={PreRollChunks * 100}");
                byte[] wav = WrapWav(pcm);
                string transcript = await TranscribeAsync(wav, token).ConfigureAwait(false);
                transcript = transcript.Trim();

                if (transcript.Length == 0 ||
                    !GeminiLatestUtterancePolicy.ShouldDispatch(
                        generation,
                        Volatile.Read(ref _transcriptionGeneration)))
                {
                    return;
                }

                LastSttLatencyMs = latency.ElapsedMilliseconds;
                LogService.Info("voice", $"VOICE_LATENCY gemini_stt_ms={LastSttLatencyMs} audio_ms={pcm.Length / 32}");
                LogService.Info("voice", "VOICE_STT_FINAL " + transcript);
                _onTranscript?.Invoke(transcript);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                LogService.Info("voice", "VOICE_STT superseded");
            }
            catch (Exception ex)
            {
                LogService.Error("voice", "Gemini segment transcription failed", ex);
            }
            finally
            {
                _ = Interlocked.CompareExchange(
                    ref _activeTranscriptionCts,
                    null,
                    requestCts);

                if (gateHeld)
                    _transcribeGate.Release();
            }
        }
    }

    private async Task<string> TranscribeAsync(byte[] wav, CancellationToken token)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (GeminiSttRateLimitStateService.TryGetActive(
                _apiKey,
                nowUtc,
                out VoiceCloudRateLimitState? cooldown))
        {
            if (nowUtc - _lastRateLimitSkipLogUtc >= TimeSpan.FromMinutes(1))
            {
                _lastRateLimitSkipLogUtc = nowUtc;
                LogService.Info(
                    "voice",
                    $"VOICE_STT rate_limited_skip until={cooldown!.UntilUtc:O}");
            }

            return await TranscribeFallbackAsync(wav, token).ConfigureAwait(false);
        }

        var serializeLatency = Stopwatch.StartNew();
        string base64 = Convert.ToBase64String(wav);

        string json = JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new
                        {
                            inlineData = new
                            {
                                mimeType = "audio/wav",
                                data = base64
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                audioTranscriptionConfig = new
                {
                    languageCodes = new[] { "cs-CZ" },
                    customVocabulary = VoiceTranscriptionContext.Keywords
                }
            }
        });
        serializeLatency.Stop();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "v1beta/models/gemini-3.5-transcribe:generateContent");
        request.Headers.Add("x-goog-api-key", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var primaryCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        primaryCts.CancelAfter(GeminiPrimarySttPolicy.PrimaryTimeout);

        var requestLatency = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    primaryCts.Token)
                .ConfigureAwait(false);

            long headersMs = requestLatency.ElapsedMilliseconds;
            string body = await response.Content
                .ReadAsStringAsync(primaryCts.Token)
                .ConfigureAwait(false);
            long totalMs = requestLatency.ElapsedMilliseconds;
            long bodyMs = Math.Max(0, totalMs - headersMs);

            LogService.Info(
                "voice",
                $"VOICE_LATENCY gemini_http serialize_ms={serializeLatency.ElapsedMilliseconds} headers_ms={headersMs} body_ms={bodyMs} total_ms={totalMs}");

            if (!response.IsSuccessStatusCode)
            {
                string message = $"HTTP {(int)response.StatusCode}";
                try
                {
                    using JsonDocument errorDoc = JsonDocument.Parse(body);
                    if (errorDoc.RootElement.TryGetProperty("error", out JsonElement error) &&
                        error.TryGetProperty("message", out JsonElement errorMessage))
                        message += ": " + errorMessage.GetString();
                }
                catch { }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    RecordRateLimit(response, body);
                    return await TranscribeFallbackAsync(wav, token).ConfigureAwait(false);
                }

                if (GeminiPrimarySttPolicy.ShouldFallbackAfterStatus(response.StatusCode))
                {
                    if (GeminiPrimarySttPolicy.FallbackDelay > TimeSpan.Zero)
                        await Task.Delay(GeminiPrimarySttPolicy.FallbackDelay, token).ConfigureAwait(false);

                    return await TranscribeFallbackAsync(wav, token).ConfigureAwait(false);
                }

                throw new HttpRequestException(message, null, response.StatusCode);
            }

            return ExtractText(body);
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested &&
                  GeminiPrimarySttPolicy.ShouldFallbackAfterTimeout(primaryCts.IsCancellationRequested))
        {
            LogService.Info(
                "voice",
                $"VOICE_LATENCY gemini_primary_timeout_ms={requestLatency.ElapsedMilliseconds}");
            return await TranscribeFallbackAsync(wav, token).ConfigureAwait(false);
        }
    }

    private async Task<string> TranscribeFallbackAsync(byte[] wav, CancellationToken token)
    {
        if (GeminiSttRateLimitStateService.TryGetActive(_apiKey, DateTime.UtcNow, out _, fallback: true))
            return string.Empty;
        var serializeLatency = Stopwatch.StartNew();
        string base64 = Convert.ToBase64String(wav);
        string json = JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = GeminiFallbackPrompt.Build() },
                        new
                        {
                            inlineData = new
                            {
                                mimeType = "audio/wav",
                                data = base64
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0,
                maxOutputTokens = 512,
                thinkingConfig = new { thinkingLevel = "low" }
            }
        });
        serializeLatency.Stop();

        foreach (string model in new[] { "gemini-3.6-flash", "gemini-3.5-flash" })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"v1beta/models/{model}:generateContent");
            request.Headers.Add("x-goog-api-key", _apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            modelCts.CancelAfter(GeminiPrimarySttPolicy.FallbackModelTimeout);
            var latency = Stopwatch.StartNew();

            try
            {
                using HttpResponseMessage response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        modelCts.Token)
                    .ConfigureAwait(false);

                long headersMs = latency.ElapsedMilliseconds;
                string body = await response.Content
                    .ReadAsStringAsync(modelCts.Token)
                    .ConfigureAwait(false);
                long totalMs = latency.ElapsedMilliseconds;
                long bodyMs = Math.Max(0, totalMs - headersMs);

                LogService.Info(
                    "voice",
                    $"VOICE_LATENCY gemini_fallback model={model} serialize_ms={serializeLatency.ElapsedMilliseconds} headers_ms={headersMs} body_ms={bodyMs} total_ms={totalMs}");

                if (response.IsSuccessStatusCode)
                    return ExtractText(body);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    RecordRateLimit(response, body, fallback: true);
                    break;
                }

                if (!VoiceCloudRateLimitPolicy.ShouldTryNextModel(response.StatusCode))
                    break;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                LogService.Info(
                    "voice",
                    $"VOICE_LATENCY gemini_fallback_timeout model={model} timeout_ms={latency.ElapsedMilliseconds}");
            }
        }

        return string.Empty;
    }

    private void RecordRateLimit(HttpResponseMessage response, string body, bool fallback = false)
    {
        TimeSpan? retryAfter = SpankyConversationService.ResolveRetryAfter(
            response,
            DateTimeOffset.UtcNow);
        TimeSpan cooldown = VoiceCloudRateLimitPolicy.ResolveCooldown(
            body,
            retryAfter);
        DateTime untilUtc = DateTime.UtcNow.Add(cooldown);

        try
        {
            GeminiSttRateLimitStateService.Set(
                _apiKey,
                untilUtc,
                (int)response.StatusCode,
                fallback ? "Gemini fallback STT HTTP 429" : "Gemini primary STT HTTP 429",
                fallback);
        }
        catch { }

        LogService.Info(
            "voice",
            $"VOICE_STT rate_limited cooldown_sec={cooldown.TotalSeconds:0}");
    }

    private static string ExtractText(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.GetArrayLength() == 0)
            return string.Empty;

        if (!candidates[0].TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (JsonElement part in parts.EnumerateArray())
        {
            JsonElement text;
            if (part.TryGetProperty("audioTranscription", out JsonElement transcription) &&
                transcription.ValueKind == JsonValueKind.Object &&
                transcription.TryGetProperty("text", out text) &&
                text.ValueKind == JsonValueKind.String)
            {
                // Dedicated Transcribe returns text inside audioTranscription.
            }
            else if (!part.TryGetProperty("text", out text) || text.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? value = text.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(value);
        }

        return sb.ToString().Trim();
    }

    private static double CalculateRms16(byte[] data)
    {
        if (data.Length < 2)
            return 0;

        double sum = 0;
        int samples = data.Length / 2;

        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            short sample = (short)(data[i] | (data[i + 1] << 8));
            double normalized = sample / 32768.0;
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / samples);
    }

    private static byte[] CombineChunks(List<byte[]> chunks, int count)
    {
        int boundedCount = Math.Clamp(count, 0, chunks.Count);
        int total = 0;
        for (int i = 0; i < boundedCount; i++)
            total += chunks[i].Length;

        byte[] combined = new byte[total];
        int offset = 0;
        for (int i = 0; i < boundedCount; i++)
        {
            byte[] chunk = chunks[i];
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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            LogService.Error("voice", "Gemini segment microphone capture stopped", e.Exception);
    }

    public ValueTask DisposeAsync()
    {
        IsRunning = false;

        lock (_sync)
            ResetSpeechState();

        try
        {
            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnAudioAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }
        }
        catch { }

        CancellationTokenSource? active = Interlocked.Exchange(
            ref _activeTranscriptionCts,
            null);
        try { active?.Cancel(); } catch { }

        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;

        return ValueTask.CompletedTask;
    }
}
