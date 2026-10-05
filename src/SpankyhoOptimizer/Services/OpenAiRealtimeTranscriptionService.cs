using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SpankyhoOptimizer.Services;

public sealed class OpenAiRealtimeTranscriptionService : IAsyncDisposable
{
    private static readonly Uri Endpoint =
        new("wss://api.openai.com/v1/realtime?model=gpt-live-transcribe");

    private readonly object _sync = new();
    private readonly Queue<byte[]> _preRoll = new();
    private readonly Channel<string> _outgoing =
        Channel.CreateBounded<string>(new BoundedChannelOptions(96)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private ClientWebSocket? _socket;
    private WaveIn? _waveIn;
    private CancellationTokenSource? _cts;
    private Task? _connectionTask;
    private Action<string>? _onFinalTranscript;
    private Action<Exception>? _onFatalFailure;
    private string _apiKey = string.Empty;
    private TaskCompletionSource _initialSessionReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _attemptSessionReady;

    private bool _speechActive;
    private int _silenceChunks;
    private int _speechChunkCount;
    private double _noiseFloor = 0.0045;
    private long _turnStartedTimestamp;
    private int _firstDeltaLogged;

    private const int SampleRate = 24000;
    private const int PreRollChunks = 4;
    private const int MinSpeechChunks = 4;
    private const int MaxSpeechChunks = 180;

    public bool IsRunning { get; private set; }
    public static long LastFirstDeltaLatencyMs { get; private set; }
    public static long LastFinalLatencyMs { get; private set; }

    public async Task StartAsync(
        string apiKey,
        Action<string> onFinalTranscript,
        CancellationToken token,
        Action<Exception>? onFatalFailure = null)
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI realtime STT API key is not configured.");

        _onFinalTranscript = onFinalTranscript;
        _onFatalFailure = onFatalFailure;
        _apiKey = apiKey.Trim();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _initialSessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _connectionTask = Task.Run(() => ConnectionSupervisorAsync(_cts.Token), _cts.Token);

        await _initialSessionReady.Task
            .WaitAsync(TimeSpan.FromSeconds(6), _cts.Token)
            .ConfigureAwait(false);

        _waveIn = new WaveIn
        {
            DeviceNumber = 0,
            BufferMilliseconds = 80,
            NumberOfBuffers = 4,
            WaveFormat = new WaveFormat(SampleRate, 16, 1)
        };
        _waveIn.DataAvailable += OnAudioAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();

        IsRunning = true;
        LogService.Info("voice", "VOICE_STT openai-realtime ready model=gpt-live-transcribe rate=24000 delay=low");
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
            double startThreshold = Math.Max(0.011, _noiseFloor * 3.0);
            double continueThreshold = Math.Max(0.0075, _noiseFloor * 1.9);

            if (!_speechActive)
            {
                if (rms < 0.04)
                    _noiseFloor = (_noiseFloor * 0.96) + (rms * 0.04);

                _preRoll.Enqueue(chunk);
                while (_preRoll.Count > PreRollChunks)
                    _preRoll.Dequeue();

                if (rms >= startThreshold)
                {
                    _speechActive = true;
                    _silenceChunks = 0;
                    _speechChunkCount = 0;
                    _turnStartedTimestamp = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _firstDeltaLogged, 0);

                    foreach (byte[] pre in _preRoll)
                        QueueAudio(pre);
                    _preRoll.Clear();

                    _speechChunkCount++;
                }

                return;
            }

            QueueAudio(chunk);
            _speechChunkCount++;

            _silenceChunks = RealtimeVoiceVadPolicy.NextSilenceCount(
                _silenceChunks,
                rms >= continueThreshold);

            int requiredSilence = RealtimeVoiceVadPolicy.RequiredEndSilenceChunks(_speechChunkCount);
            bool end =
                (_silenceChunks >= requiredSilence && _speechChunkCount >= MinSpeechChunks) ||
                _speechChunkCount >= MaxSpeechChunks;

            if (!end)
                return;

            QueueCommit();
            _speechActive = false;
            _silenceChunks = 0;
            _speechChunkCount = 0;
            _preRoll.Clear();
        }
    }

    private void QueueAudio(byte[] pcm)
    {
        string json = OpenAiRealtimeTranscriptionProtocol.CreateAudioAppend(pcm);
        if (!_outgoing.Writer.TryWrite(json))
            LogService.Info("voice", "VOICE_REALTIME outgoing queue full; audio chunk dropped");
    }

    private void QueueCommit()
    {
        if (!_outgoing.Writer.TryWrite(OpenAiRealtimeTranscriptionProtocol.CreateCommit()))
            LogService.Info("voice", "VOICE_REALTIME outgoing queue full; commit dropped");
    }

    private ValueTask QueueJsonAsync(string json, CancellationToken token) =>
        _outgoing.Writer.WriteAsync(json, token);

    private async Task ConnectionSupervisorAsync(CancellationToken token)
    {
        Exception? lastError = null;

        for (int attempt = 0; !token.IsCancellationRequested; attempt++)
        {
            if (attempt > 0)
            {
                int delay = RealtimeReconnectPolicy.GetDelayMs(attempt - 1);
                LogService.Info("voice", $"VOICE_REALTIME reconnect_attempt={attempt} delay_ms={delay}");
                await Task.Delay(delay, token).ConfigureAwait(false);
            }

            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            ClientWebSocket? socket = null;
            try
            {
                _attemptSessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

                socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + _apiKey);
                await socket.ConnectAsync(Endpoint, connectionCts.Token).ConfigureAwait(false);
                _socket = socket;

                Task sender = Task.Run(() => SenderLoopAsync(connectionCts.Token), connectionCts.Token);
                Task receiver = Task.Run(() => ReceiverLoopAsync(connectionCts.Token), connectionCts.Token);

                await QueueJsonAsync(
                    OpenAiRealtimeTranscriptionProtocol.CreateSessionUpdate(
                        VoiceTranscriptionContext.Prompt,
                        VoiceTranscriptionContext.Keywords),
                    connectionCts.Token).ConfigureAwait(false);

                await _attemptSessionReady.Task
                    .WaitAsync(TimeSpan.FromSeconds(5), connectionCts.Token)
                    .ConfigureAwait(false);

                _initialSessionReady.TrySetResult();

                if (attempt > 0)
                    LogService.Info("voice", $"VOICE_REALTIME reconnected attempt={attempt}");

                Task finished = await Task.WhenAny(sender, receiver).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;

                await finished.ConfigureAwait(false);
                throw new InvalidOperationException("OpenAI realtime connection ended unexpectedly.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                LogService.Error("voice", $"VOICE_REALTIME connection_failed attempt={attempt}", ex);
                if (!RealtimeReconnectPolicy.ShouldRetry(attempt))
                    break;
            }
            finally
            {
                try { connectionCts.Cancel(); } catch { }
                if (ReferenceEquals(_socket, socket))
                    _socket = null;
                if (socket != null)
                {
                    try { socket.Dispose(); } catch { }
                }
            }
        }

        if (!token.IsCancellationRequested)
        {
            IsRunning = false;
            Exception failure = lastError ??
                new InvalidOperationException("OpenAI realtime connection failed.");
            _initialSessionReady.TrySetException(failure);
            _onFatalFailure?.Invoke(failure);
        }
    }

    private async Task SenderLoopAsync(CancellationToken token)
    {
        ClientWebSocket socket = _socket
            ?? throw new InvalidOperationException("Realtime socket is not connected.");

        await foreach (string json in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                token).ConfigureAwait(false);
        }
    }

    private async Task ReceiverLoopAsync(CancellationToken token)
    {
        ClientWebSocket socket = _socket
            ?? throw new InvalidOperationException("Realtime socket is not connected.");
        byte[] buffer = new byte[64 * 1024];

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new System.IO.MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (result.Count > 0)
                    message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            string json = Encoding.UTF8.GetString(message.ToArray());
            HandleServerEvent(json);
        }
    }

    private void HandleServerEvent(string json)
    {
        string? eventType = OpenAiRealtimeTranscriptionProtocol.GetEventType(json);
        if (string.Equals(eventType, "session.updated", StringComparison.Ordinal))
        {
            _attemptSessionReady?.TrySetResult();
            return;
        }

        if (string.Equals(eventType, "error", StringComparison.Ordinal))
        {
            string message = OpenAiRealtimeTranscriptionProtocol.TryParseErrorMessage(json)
                ?? "OpenAI realtime transcription error.";
            _attemptSessionReady?.TrySetException(new InvalidOperationException(message));
            LogService.Error("voice", "VOICE_REALTIME " + message);
            return;
        }

        string? transcript = OpenAiRealtimeTranscriptionProtocol.TryParseTranscript(json);
        if (string.IsNullOrEmpty(transcript))
            return;

        if (OpenAiRealtimeTranscriptionProtocol.IsCompletedTranscriptEvent(json))
        {
            LastFinalLatencyMs = _turnStartedTimestamp == 0
                ? 0
                : (long)Stopwatch.GetElapsedTime(_turnStartedTimestamp).TotalMilliseconds;
            LogService.Info("voice",
                $"VOICE_LATENCY realtime_final_ms={LastFinalLatencyMs} chars={transcript.Length}");
            _onFinalTranscript?.Invoke(transcript.Trim());
            return;
        }

        if (Interlocked.Exchange(ref _firstDeltaLogged, 1) == 0)
        {
            LastFirstDeltaLatencyMs = _turnStartedTimestamp == 0
                ? 0
                : (long)Stopwatch.GetElapsedTime(_turnStartedTimestamp).TotalMilliseconds;
            LogService.Info("voice",
                $"VOICE_LATENCY realtime_first_delta_ms={LastFirstDeltaLatencyMs}");
        }
    }

    private static double CalculateRms16(byte[] data)
    {
        if (data.Length < 2) return 0;
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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            LogService.Error("voice", "OpenAI realtime microphone capture stopped", e.Exception);
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }

        WaveIn? waveIn = _waveIn;
        _waveIn = null;
        if (waveIn != null)
        {
            try { waveIn.DataAvailable -= OnAudioAvailable; } catch { }
            try { waveIn.RecordingStopped -= OnRecordingStopped; } catch { }
            try { waveIn.StopRecording(); } catch { }
            try { waveIn.Dispose(); } catch { }
        }

        _outgoing.Writer.TryComplete();

        Task? connection = _connectionTask;
        _connectionTask = null;
        if (connection != null)
        {
            try { await connection.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogService.Error("voice", "Realtime connection shutdown failed", ex); }
        }

        ClientWebSocket? socket = _socket;
        _socket = null;
        if (socket != null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { }
            socket.Dispose();
        }

        _cts?.Dispose();
        _cts = null;
    }
}
