

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;

namespace SpankyhoOptimizer.Services;

public sealed class SpankyVoiceListenerService : IAsyncDisposable
{
    private SpeechRecognizer? _recognizer;
    private OpenAiRealtimeTranscriptionService? _openAiRealtimeTranscriber;
    private OpenAiSegmentTranscriptionService? _openAiTranscriber;
    private GeminiSegmentTranscriptionService? _geminiSegmentTranscriber;
    private readonly SemaphoreSlim _ttsGate = new(1, 1);
    private readonly object _conversationSync = new();
    private readonly object _speechSync = new();
    private MediaPlayer? _activePlayer;
    private TaskCompletionSource? _activeSpeechCompletion;
    private CancellationTokenSource? _activeTtsCancellation;
    private string? _pendingConversationText;
    private CancellationTokenSource? _activeConversationCts;
    private CancellationToken _lifetimeToken;
    private long _conversationRequestId;
    private string _lastSpokenText = string.Empty;
    private DateTime _speechStartedUtc = DateTime.MinValue;
    private DateTime _lastSpeechEndedUtc = DateTime.MinValue;
    private int _started;
    private int _ttsActive;
    private int _conversationActive;
    private int _sttFailoverActive;
    private DateTime _ignoreRecognitionUntilUtc = DateTime.MinValue;
    private string _lastAcceptedText = string.Empty;
    private DateTime _lastAcceptedUtc = DateTime.MinValue;
    private bool _authorized;
    private DateTime _authorizedUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan ConversationSessionLifetime = TimeSpan.FromSeconds(60);
    private const string AuthorizationPhrase = "hej vole";

    public static bool IsListening { get; private set; }
    public static string LastStatus { get; private set; } = "Starting";
    public static string LastCommand { get; private set; } = string.Empty;
    public event Action<SpankyVoiceDecision>? CommandRecognized;

    public async Task StartAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _lifetimeToken = token;

        try
        {
            LastStatus = "Preparing voice runtime";
            await VoiceRuntimeBootstrapService.EnsureAsync(s => LastStatus = s, token).ConfigureAwait(false);

            OpenWakeWordGateService.Start((model, score) =>
            {
                _authorized = true;
                _authorizedUntilUtc = DateTime.UtcNow + ConversationSessionLifetime;
                LastStatus = "Authorized (openWakeWord)";
                LogService.Info("voice", $"VOICE_AUTHORIZED_OPENWAKEWORD model={model} score={score:0.000}");
            });
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "openWakeWord startup failed, text wake-word fallback remains active", ex);
        }

        _ = WarmChatGptPlanAsync(token);

        string openAiKey = OpenAiVoiceSettingsService.GetApiKey();
        string apiKey = VoiceCloudAiSettingsService.GetApiKey();
        VoiceSttRoute route = VoiceSttRoutingPolicy.Select(
            !string.IsNullOrWhiteSpace(openAiKey),
            !string.IsNullOrWhiteSpace(apiKey));

        if (route == VoiceSttRoute.OpenAiCloud)
        {
            OpenAiVoiceSettings openAiSettings = OpenAiVoiceSettingsService.Load();
            bool realtimeCooling = OpenAiSttProviderHealthStateService.IsCoolingDown(
                openAiKey, "realtime", DateTime.UtcNow, out DateTime realtimeUntilUtc);
            if (realtimeCooling)
                LogService.Info("voice", $"VOICE_STT skip=openai-realtime cooldown_until={realtimeUntilUtc:O}");

            if (VoiceSttFailoverPolicy.ShouldTryOpenAiRealtime(
                    !string.IsNullOrWhiteSpace(openAiKey),
                    string.Equals(openAiSettings.TranscribeModel, "gpt-live-transcribe", StringComparison.OrdinalIgnoreCase),
                    realtimeCooling))
            {
                try
                {
                    _openAiRealtimeTranscriber = new OpenAiRealtimeTranscriptionService();
                    await _openAiRealtimeTranscriber
                        .StartAsync(
                            openAiKey,
                            HandleRecognizedText,
                            token,
                            ex => _ = Task.Run(
                                () => HandleRealtimeFatalFailureAsync(openAiKey, apiKey, token, ex),
                                CancellationToken.None))
                        .ConfigureAwait(false);

                    OpenAiSttProviderHealthStateService.RecordSuccess(openAiKey, "realtime");
                    IsListening = true;
                    LastStatus = "Listening (OpenAI Realtime STT)";
                    LogService.Info("voice", "VOICE_ENGINE openai-realtime-stt ready language=cs rate=24000");
                    return;
                }
                catch (Exception ex)
                {
                    OpenAiSttProviderHealthStateService.RecordFailure(
                        openAiKey, "realtime", ex.GetBaseException().Message);
                    LogService.Error("voice", "OpenAI realtime STT unavailable, using OpenAI segment fallback", ex);
                    if (_openAiRealtimeTranscriber != null)
                    {
                        try { await _openAiRealtimeTranscriber.DisposeAsync().ConfigureAwait(false); } catch { }
                        _openAiRealtimeTranscriber = null;
                    }
                }
            }

            bool segmentCooling = OpenAiSttProviderHealthStateService.IsCoolingDown(
                openAiKey, "segment", DateTime.UtcNow, out DateTime segmentUntilUtc);
            if (segmentCooling)
                LogService.Info("voice", $"VOICE_STT skip=openai-segment cooldown_until={segmentUntilUtc:O}");

            if (VoiceSttFailoverPolicy.ShouldTryOpenAiSegment(
                    !string.IsNullOrWhiteSpace(openAiKey),
                    segmentCooling))
            {
                try
                {
                    _openAiTranscriber = new OpenAiSegmentTranscriptionService();
                    await _openAiTranscriber
                        .StartAsync(openAiKey, HandleRecognizedText, token)
                        .ConfigureAwait(false);

                    OpenAiSttProviderHealthStateService.RecordSuccess(openAiKey, "segment");
                    IsListening = true;
                    LastStatus = "Listening (OpenAI Cloud STT fallback)";
                    LogService.Info("voice", "VOICE_ENGINE openai-segment-stt fallback ready language=cs");
                    return;
                }
                catch (Exception ex)
                {
                    OpenAiSttProviderHealthStateService.RecordFailure(
                        openAiKey, "segment", ex.GetBaseException().Message);
                    LogService.Error("voice", "OpenAI cloud STT unavailable, switching to Gemini cloud fallback", ex);
                    if (_openAiTranscriber != null)
                    {
                        try { await _openAiTranscriber.DisposeAsync().ConfigureAwait(false); } catch { }
                        _openAiTranscriber = null;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            bool geminiPrimaryCooling = GeminiSttRateLimitStateService.TryGetActive(
                apiKey, DateTime.UtcNow, out VoiceCloudRateLimitState? geminiPrimaryState);
            bool geminiFallbackCooling = GeminiSttRateLimitStateService.TryGetActive(
                apiKey, DateTime.UtcNow, out VoiceCloudRateLimitState? geminiFallbackState, fallback: true);
            bool geminiFullyCooling = geminiPrimaryCooling && geminiFallbackCooling;

            if (geminiFullyCooling)
            {
                DateTime until = geminiPrimaryState!.UntilUtc > geminiFallbackState!.UntilUtc
                    ? geminiPrimaryState.UntilUtc
                    : geminiFallbackState.UntilUtc;
                LogService.Info("voice", $"VOICE_STT skip=gemini cooldown_until={until:O}");
            }

            if (VoiceSttFailoverPolicy.ShouldTryGemini(
                    !string.IsNullOrWhiteSpace(apiKey),
                    geminiPrimaryCooling,
                    geminiFallbackCooling))
            {
                try
                {
                    _geminiSegmentTranscriber = new GeminiSegmentTranscriptionService();
                    await _geminiSegmentTranscriber
                        .StartAsync(
                            apiKey,
                            HandleRecognizedText,
                            token,
                            () => Volatile.Read(ref _ttsActive) != 0)
                        .ConfigureAwait(false);

                    IsListening = true;
                    LastStatus = "Listening (Gemini Transcribe)";
                    LogService.Info("voice", "VOICE_ENGINE gemini-segment-transcribe ready language=cs-CZ");
                    return;
                }
                catch (Exception ex)
                {
                    LogService.Error("voice", "Gemini cloud STT unavailable", ex);
                    if (_geminiSegmentTranscriber != null)
                    {
                        try { await _geminiSegmentTranscriber.DisposeAsync().ConfigureAwait(false); } catch { }
                        _geminiSegmentTranscriber = null;
                    }
                }
            }
        }

        IsListening = false;
        LastStatus = "Cloud STT unavailable";
        Interlocked.Exchange(ref _started, 0);
        throw new InvalidOperationException("Cloud STT není dostupné. Nastav OpenAI nebo Gemini cloud přepis.");
    }

    private static async Task WarmChatGptPlanAsync(CancellationToken token)
    {
        try
        {
            await ChatGptPlanConversationService.WarmAsync(token).ConfigureAwait(false);
            if (ChatGptPlanAuthService.IsConnected)
                LogService.Info("voice", "CHATGPT_PLAN_PREWARM_OK");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "ChatGPT plan prewarm failed", ex);
        }
    }

    private async Task HandleRealtimeFatalFailureAsync(
        string openAiKey,
        string geminiKey,
        CancellationToken token,
        Exception failure)
    {
        if (Interlocked.Exchange(ref _sttFailoverActive, 1) != 0)
            return;

        try
        {
            if (token.IsCancellationRequested)
                return;

            IsListening = false;
            LastStatus = "Realtime STT reconnect failed";
            OpenAiSttProviderHealthStateService.RecordFailure(
                openAiKey, "realtime", failure.GetBaseException().Message);
            LogService.Error("voice", "VOICE_REALTIME failover_start", failure);

            OpenAiRealtimeTranscriptionService? realtime = _openAiRealtimeTranscriber;
            _openAiRealtimeTranscriber = null;
            if (realtime != null)
            {
                try { await realtime.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            if (token.IsCancellationRequested)
                return;

            bool segmentCooling = OpenAiSttProviderHealthStateService.IsCoolingDown(
                openAiKey, "segment", DateTime.UtcNow, out DateTime segmentUntilUtc);
            if (segmentCooling)
                LogService.Info("voice",
                    $"VOICE_REALTIME failover=openai-segment-skipped cooldown_until={segmentUntilUtc:O}");

            if (VoiceSttFailoverPolicy.ShouldTryOpenAiSegment(
                    !string.IsNullOrWhiteSpace(openAiKey),
                    segmentCooling))
            {
                try
                {
                    _openAiTranscriber = new OpenAiSegmentTranscriptionService();
                    await _openAiTranscriber
                        .StartAsync(openAiKey, HandleRecognizedText, token)
                        .ConfigureAwait(false);

                    OpenAiSttProviderHealthStateService.RecordSuccess(openAiKey, "segment");
                    IsListening = true;
                    LastStatus = "Listening (OpenAI Cloud STT fallback)";
                    LogService.Info("voice", "VOICE_REALTIME failover=openai-segment");
                    return;
                }
                catch (Exception ex)
                {
                    OpenAiSttProviderHealthStateService.RecordFailure(
                        openAiKey, "segment", ex.GetBaseException().Message);
                    LogService.Error("voice", "OpenAI segment fallback failed", ex);
                    if (_openAiTranscriber != null)
                    {
                        try { await _openAiTranscriber.DisposeAsync().ConfigureAwait(false); } catch { }
                        _openAiTranscriber = null;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(geminiKey))
            {
                bool primaryCooling = GeminiSttRateLimitStateService.TryGetActive(
                    geminiKey, DateTime.UtcNow, out VoiceCloudRateLimitState? primaryState);
                bool fallbackCooling = GeminiSttRateLimitStateService.TryGetActive(
                    geminiKey, DateTime.UtcNow, out VoiceCloudRateLimitState? fallbackState, fallback: true);

                if (!VoiceSttFailoverPolicy.ShouldTryGemini(
                        !string.IsNullOrWhiteSpace(geminiKey),
                        primaryCooling,
                        fallbackCooling))
                {
                    DateTime until = primaryState!.UntilUtc > fallbackState!.UntilUtc
                        ? primaryState.UntilUtc
                        : fallbackState.UntilUtc;
                    LogService.Info("voice",
                        $"VOICE_REALTIME failover=gemini_skipped cooldown_until={until:O}");
                }
                else
                {
                    try
                    {
                        _geminiSegmentTranscriber = new GeminiSegmentTranscriptionService();
                        await _geminiSegmentTranscriber
                            .StartAsync(
                                geminiKey,
                                HandleRecognizedText,
                                token,
                                () => Volatile.Read(ref _ttsActive) != 0)
                            .ConfigureAwait(false);

                        IsListening = true;
                        LastStatus = "Listening (Gemini Cloud STT fallback)";
                        LogService.Info("voice", "VOICE_REALTIME failover=gemini");
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("voice", "Gemini fallback after realtime failure failed", ex);
                    }
                }
            }

            IsListening = false;
            LastStatus = "Cloud STT unavailable";
        }
        finally
        {
            Interlocked.Exchange(ref _sttFailoverActive, 0);
        }
    }

    private async Task StartWindowsSpeechAsync()
    {
        _recognizer = new SpeechRecognizer();
        _recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(
            SpeechRecognitionScenario.Dictation, "SpankyVoiceAI"));

        SpeechRecognitionCompilationResult compile =
            await _recognizer.CompileConstraintsAsync();

        if (compile.Status != SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException(
                "Speech recognition constraints failed: " + compile.Status);

        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
        await _recognizer.ContinuousRecognitionSession.StartAsync();
    }

    private void OnResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        HandleRecognizedText(VoiceRecognitionResultAccessor.GetText(args));
    }

    private bool IsLikelyValidUtterance(string heard)
    {
        string text = (heard ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        if (text.Contains('\uFFFD')) return false;
        if (VoiceTranscriptCleanupPolicy.HasExcessiveCharacterRun(text)) return false;
        if (text.StartsWith("*", StringComparison.Ordinal) && text.EndsWith("*", StringComparison.Ordinal)) return false;
        if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal)) return false;

        string normalized = Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        string[] words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;

        if (string.Equals(text, _lastAcceptedText, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - _lastAcceptedUtc < TimeSpan.FromSeconds(4))
            return false;

        if (words.Length >= 4)
        {
            var uniqueWords = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string word in words)
                uniqueWords.Add(word);
            double ratio = uniqueWords.Count / (double)words.Length;
            if (ratio < 0.45)
                return false;
        }

        _lastAcceptedText = text;
        _lastAcceptedUtc = DateTime.UtcNow;
        return true;
    }

    private static string NormalizeRecognizedText(string heard)
    {
        string text = VoiceTranscriptCleanupPolicy.Normalize(heard);
        if (text.Length == 0) return string.Empty;

        text = Regex.Replace(text, @"\b(?:spenky|spanki|spankyho)\b", "Spanky", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(?:diablo immortal|diablo imortal|diablo immortal)\b", "Diablo Immortal", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(?:fire fox|firefoks)\b", "Firefox", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(?:open ai|openaj)\b", "OpenAI", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    private bool IsLikelySpeechEcho(string heard)
    {
        if (string.IsNullOrWhiteSpace(_lastSpokenText))
            return false;
        bool speechActive = Volatile.Read(ref _ttsActive) != 0;
        if (!speechActive && DateTime.UtcNow - _lastSpeechEndedUtc > TimeSpan.FromSeconds(7))
            return false;

        return VoiceEchoPolicy.IsLikelyEcho(heard, _lastSpokenText);
    }

    private void StopActiveSpeech()
    {
        lock (_speechSync)
        {
            try { _activeTtsCancellation?.Cancel(); } catch { }
            try { _activePlayer?.Pause(); } catch { }
            try { _activePlayer?.Source = null; } catch { }
            _activeSpeechCompletion?.TrySetResult();
        }
    }

    private void HandleRecognizedText(string heard)
    {
        heard = NormalizeRecognizedText(heard);
        if (heard.Length == 0) return;
        if (IsMicrophoneMuted())
        {
            LastStatus = "Muted";
            return;
        }
        if (IsLikelySpeechEcho(heard))
            return;

        if (Volatile.Read(ref _ttsActive) != 0)
        {
            string interruptProbe = Regex.Replace(heard.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
            interruptProbe = Regex.Replace(interruptProbe, @"\s+", " ").Trim();
            bool activeSession = _authorized && DateTime.UtcNow <= _authorizedUntilUtc;
            bool wakeInterrupt = interruptProbe.Contains(AuthorizationPhrase, StringComparison.Ordinal) ||
                                 SpankyVoiceAiService.HasWakeWord(interruptProbe);
            bool naturalBargeIn = activeSession && interruptProbe.Length >= 2;
            if (!wakeInterrupt && !naturalBargeIn) return;

            StopActiveSpeech();
            _ignoreRecognitionUntilUtc = DateTime.MinValue;
            LastStatus = "Listening";
            LogService.Info("voice", $"VOICE_BARGE_IN heard={heard}");
        }

        if (Volatile.Read(ref _conversationActive) != 0)
        {
            lock (_conversationSync)
            {
                _pendingConversationText = heard;
                try { _activeConversationCts?.Cancel(); } catch { }
            }
            LastStatus = "Queued";
            LogService.Info("voice", "VOICE_REQUEST superseded_by_new_utterance");
            return;
        }
        if (DateTime.UtcNow < _ignoreRecognitionUntilUtc) return;

        heard = heard.Trim();
        if (!IsLikelyValidUtterance(heard)) return;

        LogService.Info("voice", $"VOICE_HEARD {heard}");

        string normalizedHeard = Regex.Replace(heard.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        normalizedHeard = Regex.Replace(normalizedHeard, @"\s+", " ").Trim();
        bool hasWakeLead =
            normalizedHeard.Contains("hej", StringComparison.Ordinal) ||
            normalizedHeard.Contains("hey", StringComparison.Ordinal) ||
            normalizedHeard.StartsWith("he ", StringComparison.Ordinal) ||
            normalizedHeard.StartsWith("jej ", StringComparison.Ordinal) ||
            normalizedHeard.StartsWith("je ", StringComparison.Ordinal) ||
            normalizedHeard.EndsWith(" he", StringComparison.Ordinal);

        bool hasWakeTail =
            normalizedHeard.Contains("vole", StringComparison.Ordinal) ||
            normalizedHeard.Contains("vola", StringComparison.Ordinal) ||
            normalizedHeard.Contains("volá", StringComparison.Ordinal) ||
            normalizedHeard.Contains("volé", StringComparison.Ordinal) ||
            normalizedHeard.Contains(" vol", StringComparison.Ordinal);

        bool spankyWake = SpankyVoiceAiService.HasWakeWord(normalizedHeard);
        bool authorizationHeard =
            normalizedHeard.Contains(AuthorizationPhrase, StringComparison.Ordinal) ||
            (hasWakeLead && hasWakeTail) ||
            spankyWake;

        if (authorizationHeard)
        {
            _authorized = true;
            _authorizedUntilUtc = DateTime.UtcNow + ConversationSessionLifetime;
            LastCommand = heard;
            LastStatus = "Authorized";
            LogService.Info("voice", $"VOICE_AUTHORIZED heard={heard}");

            string remainder = Regex.Replace(
                heard,
                @"(?i)\b(?:hej|hey)\s+vole\b|\b(?:spanky|spanki|spenky)\b",
                " ").Trim(' ', ',', '.', '!', '?', ':', ';');

            if (remainder.Length == 0)
            {
                _ = SpeakAsync("Jo. Poslouchám.");
                return;
            }

            heard = remainder;
        }

        if (_authorized && DateTime.UtcNow > _authorizedUntilUtc)
            _authorized = false;

        if (!_authorized)
        {
            LastStatus = "Locked";
            return;
        }

        _authorizedUntilUtc = DateTime.UtcNow + ConversationSessionLifetime;
        SpankyVoiceDecision decision = SpankyVoiceAiService.Understand(heard);
        if (decision.Intent == SpankyVoiceIntent.Unknown || decision.Confidence < .70)
        {
            LastCommand = heard;
            _ = HandleConversationAsync(heard);
            return;
        }

        bool executed = SpankyVoiceAiService.Execute(decision);
        LastCommand = heard;
        CommandRecognized?.Invoke(decision);

        _ = SpeakAsync(
            executed
                ? decision.Reply
                : "Příkaz jsem pochopil, ale nepodařilo se mi ho provést.");
    }

    private async Task HandleConversationAsync(string heard)
    {
        if (Interlocked.Exchange(ref _conversationActive, 1) != 0)
        {
            lock (_conversationSync)
            {
                _pendingConversationText = heard;
                try { _activeConversationCts?.Cancel(); } catch { }
            }
            StopActiveSpeech();
            LastStatus = "Queued";
            LogService.Info("voice", "VOICE_REQUEST superseded_by_new_utterance");
            return;
        }
        long requestId = Interlocked.Increment(ref _conversationRequestId);
        using var conversationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        conversationCts.CancelAfter(TimeSpan.FromSeconds(20));
        lock (_conversationSync)
            _activeConversationCts = conversationCts;

        CancellationToken token = conversationCts.Token;
        var latency = Stopwatch.StartNew();
        LastStatus = "Thinking";
        LogService.Info("voice", $"VOICE_REQUEST start id={requestId}");

        try
        {
            string? localToolReply = await SpankyVoiceToolService
                .TryHandleAsync(heard, token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(localToolReply))
            {
                long toolMs = latency.ElapsedMilliseconds;
                conversationCts.CancelAfter(Timeout.InfiniteTimeSpan);
                LogService.Info("voice", $"VOICE_LATENCY id={requestId} path=tool tool_ms={toolMs}");
                await VoiceLocalToolFastPathService.DeliverAsync(
                    heard,
                    localToolReply,
                    SpeakAsync,
                    ChatGptPlanAuthService.IsConnected
                        ? ChatGptPlanConversationService.RememberExchangeAsync
                        : null,
                    token).ConfigureAwait(false);
                LogService.Info("voice", $"VOICE_LATENCY id={requestId} total_ms={latency.ElapsedMilliseconds}");
                return;
            }

            bool streamedSpeech = false;
            string reply;

            if (ChatGptPlanAuthService.IsConnected)
            {
                reply = await ChatGptPlanConversationService.AskStreamingAsync(
                    heard,
                    async (chunk, streamToken) =>
                    {
                        if (!streamedSpeech)
                        {
                            streamedSpeech = true;
                            conversationCts.CancelAfter(TimeSpan.FromSeconds(60));
                            LogService.Info(
                                "voice",
                                $"VOICE_LATENCY id={requestId} first_speech_chunk_ms={latency.ElapsedMilliseconds}");
                        }

                        await SpeakAsync(chunk, streamToken).ConfigureAwait(false);
                    },
                    token).ConfigureAwait(false);

                if (VoiceBrainFallbackPolicy.ShouldFallback(reply, streamedSpeech))
                {
                    string planFailure = reply;
                    string fallback = await AskSecondaryBrainAsync(heard, token).ConfigureAwait(false);
                    if (!VoiceBrainFallbackPolicy.IsFailureReply(fallback))
                    {
                        reply = fallback;
                        LogService.Info("voice",
                            $"VOICE_BRAIN fallback=secondary id={requestId} plan_failure={TrimVoiceLog(planFailure)}");
                    }
                }
            }
            else
            {
                reply = await AskSecondaryBrainAsync(heard, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(reply))
                return;

            long llmMs = latency.ElapsedMilliseconds;
            conversationCts.CancelAfter(Timeout.InfiniteTimeSpan);
            LogService.Info("voice", $"VOICE_LATENCY id={requestId} path=llm llm_ms={llmMs}");

            if (!streamedSpeech)
                await SpeakAsync(reply, token).ConfigureAwait(false);

            LogService.Info("voice", $"VOICE_LATENCY id={requestId} total_ms={latency.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            LogService.Info("voice", $"VOICE_REQUEST canceled id={requestId} elapsed_ms={latency.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "Voice conversation failed", ex);
            await SpeakAsync("Konverzační model teď není dostupný.").ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _conversationActive, 0);
            string? pending = null;
            lock (_conversationSync)
            {
                if (ReferenceEquals(_activeConversationCts, conversationCts))
                    _activeConversationCts = null;
                pending = _pendingConversationText;
                _pendingConversationText = null;
            }
            if (!string.IsNullOrWhiteSpace(pending))
                _ = HandleConversationAsync(pending);
        }
    }

    private static async Task<string> AskSecondaryBrainAsync(
        string heard,
        CancellationToken token)
    {
        string lastFailure = string.Empty;

        if (OpenAiVoiceSettingsService.HasApiKey)
        {
            string openAi = await OpenAiConversationService.AskAsync(heard, token).ConfigureAwait(false);
            if (!VoiceBrainFallbackPolicy.IsFailureReply(openAi))
            {
                LogService.Info("voice", "VOICE_BRAIN route=openai_api");
                return openAi;
            }
            lastFailure = openAi;
            LogService.Info("voice", "VOICE_BRAIN openai_api_failed");
        }

        if (VoiceCloudAiSettingsService.HasApiKey)
        {
            string gemini = await SpankyConversationService.AskAsync(heard, token).ConfigureAwait(false);
            if (!VoiceBrainFallbackPolicy.IsFailureReply(gemini))
            {
                LogService.Info("voice", "VOICE_BRAIN route=gemini");
                return gemini;
            }
            lastFailure = gemini;
            LogService.Info("voice", "VOICE_BRAIN gemini_failed");
        }

        return string.IsNullOrWhiteSpace(lastFailure)
            ? "Konverzační AI není připojená."
            : lastFailure;
    }

    private static string TrimVoiceLog(string value)
    {
        string compact = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 120 ? compact : compact[..120];
    }

    public static string GetHealthSummary()
    {
        string mic = IsMicrophoneMuted() ? "muted" : "ready";
        string brain = ChatGptPlanAuthService.IsConnected
            ? "ChatGPT plan + fallback"
            : OpenAiVoiceSettingsService.HasApiKey
                ? (VoiceCloudAiSettingsService.HasApiKey ? "OpenAI API + Gemini fallback" : "OpenAI API")
                : VoiceCloudAiSettingsService.HasApiKey
                    ? "Gemini"
                    : "konverzační AI není připojená";
        string tts = NeuralTtsService.IsAvailable
            ? "Antonin Neural"
            : PiperTtsService.IsAvailable
                ? "Piper fallback"
                : "Windows fallback";
        string stt = OpenAiRealtimeTranscriptionService.LastFinalLatencyMs > 0
            ? $"OpenAI Realtime STT, první delta {OpenAiRealtimeTranscriptionService.LastFirstDeltaLatencyMs} ms, final {OpenAiRealtimeTranscriptionService.LastFinalLatencyMs} ms"
            : !OpenAiVoiceSettingsService.HasApiKey && VoiceCloudAiSettingsService.HasApiKey && GeminiSegmentTranscriptionService.LastSttLatencyMs > 0
                ? $"Gemini cloud STT, poslední přepis {GeminiSegmentTranscriptionService.LastSttLatencyMs} ms"
                : OpenAiSegmentTranscriptionService.LastSttLatencyMs > 0
                    ? $"{OpenAiSegmentTranscriptionService.LastSttEngine}, poslední přepis {OpenAiSegmentTranscriptionService.LastSttLatencyMs} ms"
                    : "zatím bez změřeného přepisu";
        string cloudStt = OpenAiVoiceSettingsService.HasApiKey
            ? "OpenAI cloud"
            : VoiceCloudAiSettingsService.HasApiKey
                ? "Gemini cloud"
                : "cloud STT není nastavené";
        string ttsLatency = NeuralTtsService.LastFirstAudioLatencyMs > 0
            ? $"první audio {NeuralTtsService.LastFirstAudioLatencyMs} ms"
            : "TTS latency zatím nezměřená";

        return $"Voice {(IsListening ? "běží" : "je vypnutý")}, stav {LastStatus}. Mikrofon {mic}. Mozek {brain}. STT {stt}, režim {cloudStt}. TTS {tts}, {ttsLatency}.";
    }

    private static bool IsMicrophoneMuted()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return device.AudioEndpointVolume.Mute;
        }
        catch
        {
            return false;
        }
    }

    private async Task WatchMicrophoneMuteAsync(CancellationTokenSource ttsCts)
    {
        try
        {
            while (!ttsCts.IsCancellationRequested)
            {
                if (IsMicrophoneMuted())
                {
                    LogService.Info("voice", "TTS_STOP_MIC_MUTED");
                    StopActiveSpeech();
                    ttsCts.Cancel();
                    return;
                }

                await Task.Delay(120, ttsCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SpeakAsync(string text, CancellationToken externalToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || IsMicrophoneMuted())
        {
            LogService.Info("voice", "TTS_SKIPPED_MIC_MUTED");
            return;
        }
        string spokenChunk = text.Trim();
        DateTime nowUtc = DateTime.UtcNow;
        bool continuingStream =
            Volatile.Read(ref _ttsActive) != 0 ||
            nowUtc - _lastSpeechEndedUtc <= TimeSpan.FromSeconds(2);

        _lastSpokenText = continuingStream && _lastSpokenText.Length > 0
            ? _lastSpokenText + " " + spokenChunk
            : spokenChunk;

        if (_lastSpokenText.Length > 900)
            _lastSpokenText = _lastSpokenText[^900..];

        _speechStartedUtc = nowUtc;
        await _ttsGate.WaitAsync(externalToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _ttsActive, 1);
        LastStatus = "Speaking";

        using var ttsCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        lock (_speechSync)
            _activeTtsCancellation = ttsCts;
        _ = WatchMicrophoneMuteAsync(ttsCts);

        try
        {
            try
            {
                LogService.Info("voice", "TTS_NEURAL_START voice=cs-CZ-AntoninNeural");
                await NeuralTtsService.SpeakAsync(text, ttsCts.Token).ConfigureAwait(false);
                LogService.Info("voice", "TTS_NEURAL_OK");
                return;
            }
            catch (OperationCanceledException) when (ttsCts.IsCancellationRequested)
            {
                LogService.Info("voice", "TTS_INTERRUPTED");
                return;
            }
            catch (Exception neuralEx)
            {
                LogService.Error("voice", "Primary neural TTS failed, using Piper fallback", neuralEx);
            }

            try
            {
                LogService.Info("voice", "TTS_PIPER_START");
                await PiperTtsService.SpeakAsync(text, ttsCts.Token).ConfigureAwait(false);
                LogService.Info("voice", "TTS_PIPER_OK");
                return;
            }
            catch (OperationCanceledException) when (ttsCts.IsCancellationRequested)
            {
                LogService.Info("voice", "TTS_INTERRUPTED");
                return;
            }
            catch (Exception piperEx)
            {
                LogService.Error("voice", "Piper TTS failed, using Windows fallback", piperEx);
            }

            try
            {
                using var synthesizer = new SpeechSynthesizer();
                Windows.Media.SpeechSynthesis.VoiceInformation? selectedVoice = null;
                foreach (var voice in SpeechSynthesizer.AllVoices)
                {
                    selectedVoice ??= voice;
                    if (string.Equals(voice.Language, "cs-CZ", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedVoice = voice;
                        break;
                    }
                }
                if (selectedVoice != null)
                    synthesizer.Voice = selectedVoice;
                synthesizer.Options.SpeakingRate = 1.08;
                synthesizer.Options.AudioVolume = 1.0;

                using SpeechSynthesisStream stream =
                    await synthesizer.SynthesizeTextToStreamAsync(text);

                using var player = new MediaPlayer();
                var completed =
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_speechSync)
                {
                    _activePlayer = player;
                    _activeSpeechCompletion = completed;
                }

                bool playbackFailed = false;
                player.MediaEnded += (_, _) => completed.TrySetResult();
                player.MediaFailed += (_, _) =>
                {
                    playbackFailed = true;
                    completed.TrySetResult();
                };
                player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
                player.Play();

                double timeoutSeconds = Math.Clamp(10 + (text.Length / 11d), 14, 45);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ttsCts.Token);
                if (playbackFailed)
                    throw new InvalidOperationException("Windows MediaPlayer TTS playback failed.");
                LogService.Info("voice", "TTS_WINDOWS_OK");
            }
            catch (OperationCanceledException) when (ttsCts.IsCancellationRequested)
            {
                LogService.Info("voice", "TTS_INTERRUPTED");
            }
            catch (Exception ex)
            {
                LogService.Error("voice", "Windows TTS failed, using SAPI fallback", ex);
                try
                {
                    await SpeakWithSapiFallbackAsync(text).ConfigureAwait(false);
                    LogService.Info("voice", "TTS_SAPI_OK");
                }
                catch (Exception fallbackEx)
                {
                    LogService.Error("voice", "SAPI speech fallback failed", fallbackEx);
                }
            }
        }
        finally
        {
            lock (_speechSync)
            {
                _activeTtsCancellation = null;
                _activePlayer = null;
                _activeSpeechCompletion = null;
            }
            _lastSpeechEndedUtc = DateTime.UtcNow;
            _ignoreRecognitionUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
            Volatile.Write(ref _ttsActive, 0);
            LastStatus = IsListening
                ? (_openAiRealtimeTranscriber?.IsRunning == true
                    ? "Listening (OpenAI Realtime STT)"
                    : (_openAiTranscriber?.IsRunning == true
                        ? "Listening (OpenAI Cloud STT fallback)"
                        : (_geminiSegmentTranscriber?.IsRunning == true
                            ? "Listening (Gemini Cloud STT)"
                            : "Listening")))
                : "Off";
            _ttsGate.Release();
        }
    }

    private static Task SpeakWithSapiFallbackAsync(string text)
    {
        return Task.Run(() =>
        {
            Type? voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (voiceType == null)
                throw new InvalidOperationException("Windows SAPI.SpVoice is unavailable.");

            object? voice = Activator.CreateInstance(voiceType);
            if (voice == null)
                throw new InvalidOperationException("Windows SAPI voice could not be created.");

            try
            {
                voiceType.InvokeMember(
                    "Speak",
                    System.Reflection.BindingFlags.InvokeMethod,
                    binder: null,
                    target: voice,
                    args: new object[] { text, 0 },
                    culture: System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                if (System.Runtime.InteropServices.Marshal.IsComObject(voice))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _started, 0);
        IsListening = false;
        LastStatus = "Off";

        lock (_conversationSync)
        {
            try { _activeConversationCts?.Cancel(); } catch { }
            _activeConversationCts = null;
            _pendingConversationText = null;
        }
        StopActiveSpeech();

        try { await NeuralTtsWorkerService.StopAsync().ConfigureAwait(false); } catch { }
        try { await OpenWakeWordGateService.StopAsync().ConfigureAwait(false); } catch { }

        OpenAiRealtimeTranscriptionService? openAiRealtimeTranscriber = _openAiRealtimeTranscriber;
        _openAiRealtimeTranscriber = null;
        if (openAiRealtimeTranscriber != null)
        {
            try { await openAiRealtimeTranscriber.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        OpenAiSegmentTranscriptionService? openAiTranscriber = _openAiTranscriber;
        _openAiTranscriber = null;
        if (openAiTranscriber != null)
        {
            try { await openAiTranscriber.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        GeminiSegmentTranscriptionService? geminiSegmentTranscriber = _geminiSegmentTranscriber;
        _geminiSegmentTranscriber = null;
        if (geminiSegmentTranscriber != null)
        {
            try { await geminiSegmentTranscriber.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        SpeechRecognizer? recognizer = _recognizer;
        _recognizer = null;
        if (recognizer == null) return;

        try
        {
            await recognizer.ContinuousRecognitionSession.StopAsync();
        }
        catch { }

        recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
        recognizer.Dispose();
    }
}

