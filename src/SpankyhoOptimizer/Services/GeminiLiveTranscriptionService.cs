using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SpankyhoOptimizer.Services;

public sealed class GeminiLiveTranscriptionService : IAsyncDisposable
{
    private const string Model = "gemini-3.5-transcribe-live";
    private const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    private readonly Channel<byte[]> _audioQueue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(24)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private ClientWebSocket? _socket;
    private WaveIn? _waveIn;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _sendLoop;
    private Task? _receiveLoop;
    private readonly TaskCompletionSource _setupReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsRunning { get; private set; }

    public async Task StartAsync(
        string apiKey,
        Action<string> onFinalTranscript,
        CancellationToken token)
    {
        if (IsRunning)
            return;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Gemini API key is not configured.");

        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken lifetimeToken = _lifetimeCts.Token;

        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        var uri = new Uri(Endpoint + "?key=" + Uri.EscapeDataString(apiKey.Trim()));
        await _socket.ConnectAsync(uri, lifetimeToken).ConfigureAwait(false);

        string setupJson = JsonSerializer.Serialize(new
        {
            setup = new
            {
                model = "models/" + Model,
                generationConfig = new
                {
                    responseModalities = new[] { "TEXT" }
                },
                inputAudioTranscription = new
                {
                    languageCodes = new[] { "cs-CZ" }
                }
            }
        });

        await SendTextAsync(setupJson, lifetimeToken).ConfigureAwait(false);

        _receiveLoop = Task.Run(
            () => ReceiveLoopAsync(onFinalTranscript, lifetimeToken),
            lifetimeToken);

        await _setupReady.Task
            .WaitAsync(TimeSpan.FromSeconds(12), lifetimeToken)
            .ConfigureAwait(false);

        _sendLoop = Task.Run(() => SendLoopAsync(lifetimeToken), lifetimeToken);

        _waveIn = new WaveIn
        {
            DeviceNumber = 0,
            BufferMilliseconds = 100,
            NumberOfBuffers = 4,
            WaveFormat = new WaveFormat(16000, 16, 1)
        };

        _waveIn.DataAvailable += OnAudioAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();

        IsRunning = true;
        LogService.Info("voice", "VOICE_STT gemini-live ready model=" + Model + " language=cs-CZ rate=16000");
    }

    private void OnAudioAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRunning && _waveIn == null)
            return;
        if (e.BytesRecorded <= 0)
            return;

        byte[] chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        _audioQueue.Writer.TryWrite(chunk);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            LogService.Error("voice", "Gemini Live microphone capture stopped", e.Exception);
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        await foreach (byte[] chunk in _audioQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            string json = JsonSerializer.Serialize(new
            {
                realtimeInput = new
                {
                    audio = new
                    {
                        data = Convert.ToBase64String(chunk),
                        mimeType = "audio/pcm;rate=16000"
                    }
                }
            });

            await SendTextAsync(json, token).ConfigureAwait(false);
        }
    }

    private async Task SendTextAsync(string text, CancellationToken token)
    {
        ClientWebSocket socket = _socket
            ?? throw new InvalidOperationException("Gemini Live socket is not initialized.");

        byte[] payload = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: token).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(Action<string> onFinalTranscript, CancellationToken token)
    {
        ClientWebSocket socket = _socket
            ?? throw new InvalidOperationException("Gemini Live socket is not initialized.");

        byte[] rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!token.IsCancellationRequested &&
                   socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var ms = new System.IO.MemoryStream();

                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(rented),
                        token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        string closeReason = socket.CloseStatusDescription ?? "no reason";
                        string closeStatus = socket.CloseStatus?.ToString() ?? "unknown";
                        var closeException = new InvalidOperationException(
                            $"Gemini Live closed during setup/session: status={closeStatus}, reason={closeReason}");
                        _setupReady.TrySetException(closeException);
                        LogService.Error("voice", "Gemini Live socket closed", closeException);

                        if (socket.State == WebSocketState.CloseReceived)
                        {
                            await socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "closing",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        return;
                    }

                    ms.Write(rented, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, checked((int)ms.Length));
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("setupComplete", out _))
                {
                    _setupReady.TrySetResult();
                    continue;
                }

                if (!root.TryGetProperty("serverContent", out JsonElement serverContent))
                    continue;

                if (serverContent.TryGetProperty("inputTranscription", out JsonElement transcription) &&
                    transcription.TryGetProperty("text", out JsonElement textElement))
                {
                    string finalText = textElement.GetString()?.Trim() ?? string.Empty;
                    if (finalText.Length > 0)
                    {
                        LogService.Info("voice", "VOICE_STT_FINAL " + finalText);
                        onFinalTranscript(finalText);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _setupReady.TrySetException(ex);
            LogService.Error("voice", "Gemini Live receive loop failed", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;

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

        _audioQueue.Writer.TryComplete();

        CancellationTokenSource? cts = _lifetimeCts;
        _lifetimeCts = null;
        try { cts?.Cancel(); } catch { }

        ClientWebSocket? socket = _socket;
        _socket = null;
        if (socket != null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "shutdown",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { }

            socket.Dispose();
        }

        try
        {
            if (_sendLoop != null)
                await _sendLoop.ConfigureAwait(false);
        }
        catch { }

        try
        {
            if (_receiveLoop != null)
                await _receiveLoop.ConfigureAwait(false);
        }
        catch { }

        cts?.Dispose();
    }
}
