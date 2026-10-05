using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class SpankyConversationService
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://generativelanguage.googleapis.com/"),
        Timeout = TimeSpan.FromSeconds(35)
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<(string Role, string Content)> History = new();
    private const int MaxHistoryMessages = 18;
    private static readonly string[] Models =
    {
        "gemini-3.8-flash",
        "gemini-3.6-flash",
        "gemini-3.5-flash"
    };
    private static string _activeModel = Models[0];

    private const string SystemPrompt =
        "Jsi Spanky, hlasový AI asistent ve Windows aplikaci Spankyho Optimizer. " +
        "Mluv přirozenou hovorovou češtinou, stručně a přímo. " +
        "Uživatel nechce robotický ani formální styl. " +
        "Odpovídej typicky jednou až třemi větami, pokud si výslovně neřekne o detail. " +
        "Navazuj na předchozí konverzaci. " +
        "Když je přepis z mikrofonu zjevně zkomolený nebo nedává smysl, nevymýšlej si význam a stručně se zeptej, co tím myslel. " +
        "Nikdy netvrď, že jsi provedl systémovou akci; ty provádí oddělený lokální parser. " +
        "Neopakuj otázku a nepopisuj interní uvažování.";

    public static async Task WarmupAsync(CancellationToken token = default)
    {
        if (!VoiceCloudAiSettingsService.HasApiKey)
        {
            LogService.Info("voice", "VOICE_CLOUD waiting_for_api_key provider=Gemini model=" + _activeModel);
            return;
        }

        string apiKey = VoiceCloudAiSettingsService.GetApiKey();
        if (VoiceCloudRateLimitStateService.TryGetActive(
                apiKey,
                DateTime.UtcNow,
                out VoiceCloudRateLimitState? cooldown))
        {
            LogService.Info(
                "voice",
                $"VOICE_CLOUD warmup_skipped rate_limited until={cooldown!.UntilUtc:O}");
            return;
        }

        try
        {
            string reply = await SendAsync("Odpověz pouze OK.", Array.Empty<(string Role, string Content)>(), token)
                .ConfigureAwait(false);
            LogService.Info("voice", "VOICE_CLOUD warm provider=Gemini model=" + _activeModel + " ok=" + (!string.IsNullOrWhiteSpace(reply)));
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "Gemini Voice AI warmup failed", ex);
        }
    }

    public static async Task<(bool Ok, string Message)> TestConnectionAsync(string apiKey, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "API klíč chybí.");

        try
        {
            string reply = await SendAsync("Odpověz pouze slovem OK.", Array.Empty<(string Role, string Content)>(), token, apiKey.Trim())
                .ConfigureAwait(false);
            return (!string.IsNullOrWhiteSpace(reply), string.IsNullOrWhiteSpace(reply) ? "Gemini nevrátil odpověď." : $"Gemini je připojený přes {_activeModel}.");
        }
        catch (HttpRequestException ex)
        {
            return (false, "Gemini API: " + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "Test selhal: " + ex.Message);
        }
    }

    public static async Task<string> AskAsync(string userText, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return string.Empty;

        string apiKey = VoiceCloudAiSettingsService.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return "V AI nastavení chybí Gemini API klíč.";

        if (VoiceCloudRateLimitStateService.TryGetActive(
                apiKey,
                DateTime.UtcNow,
                out _))
        {
            return "Došel bezplatný Gemini limit. Zkus to později.";
        }

        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string reply = await SendAsync(userText.Trim(), History, token, apiKey).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply))
                return "Gemini nevrátil odpověď.";

            History.Add(("user", userText.Trim()));
            History.Add(("model", reply));
            while (History.Count > MaxHistoryMessages)
                History.RemoveAt(0);

            return reply;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return string.Empty;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "Došel bezplatný Gemini limit. Zkus to později.";
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "Gemini conversation failed", ex);
            return "Gemini teď není dostupný.";
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> SendAsync(
        string userText,
        IReadOnlyList<(string Role, string Content)> history,
        CancellationToken token,
        string? explicitApiKey = null)
    {
        string apiKey = explicitApiKey ?? VoiceCloudAiSettingsService.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Gemini API key is not configured.");

        if (VoiceCloudRateLimitStateService.TryGetActive(
                apiKey,
                DateTime.UtcNow,
                out VoiceCloudRateLimitState? cooldown))
        {
            throw new HttpRequestException(
                $"HTTP 429: Gemini cooldown do {cooldown!.UntilUtc:O}",
                null,
                HttpStatusCode.TooManyRequests);
        }

        var contents = new List<object>();
        foreach ((string role, string content) in history)
        {
            contents.Add(new
            {
                role = role == "assistant" ? "model" : role,
                parts = new[] { new { text = content } }
            });
        }

        contents.Add(new
        {
            role = "user",
            parts = new[] { new { text = userText } }
        });

        string json = JsonSerializer.Serialize(new
        {
            system_instruction = new
            {
                parts = new[] { new { text = SystemPrompt } }
            },
            contents,
            generationConfig = new
            {
                temperature = 0.35,
                maxOutputTokens = 220,
                thinkingConfig = new
                {
                    thinkingLevel = "low"
                }
            }
        });

        string? body = null;
        HttpStatusCode? lastStatus = null;
        string? lastError = null;

        var orderedModels = new List<string> { _activeModel };
        foreach (string model in Models)
        {
            if (!orderedModels.Contains(model))
                orderedModels.Add(model);
        }

        foreach (string model in orderedModels)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"v1beta/models/{model}:generateContent");
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                if (!string.Equals(_activeModel, model, StringComparison.Ordinal))
                    LogService.Info("voice", $"VOICE_CLOUD failover model={model}");
                _activeModel = model;
                break;
            }

            lastStatus = response.StatusCode;
            string message = $"HTTP {(int)response.StatusCode}";
            try
            {
                using JsonDocument errorDoc = JsonDocument.Parse(body);
                if (errorDoc.RootElement.TryGetProperty("error", out JsonElement error) &&
                    error.TryGetProperty("message", out JsonElement errorMessage))
                    message += ": " + errorMessage.GetString();
            }
            catch { }

            lastError = message;
            LogService.Info("voice", $"VOICE_CLOUD model_failed model={model} status={(int)response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = ResolveRetryAfter(response, DateTimeOffset.UtcNow);
                TimeSpan cooldownDuration = VoiceCloudRateLimitPolicy.ResolveCooldown(body, retryAfter);
                DateTime untilUtc = DateTime.UtcNow.Add(cooldownDuration);
                try
                {
                    VoiceCloudRateLimitStateService.Set(
                        apiKey,
                        untilUtc,
                        (int)response.StatusCode,
                        "Gemini HTTP 429");
                }
                catch { }

                LogService.Info(
                    "voice",
                    $"VOICE_CLOUD rate_limited cooldown_sec={cooldownDuration.TotalSeconds:0}");
                throw new HttpRequestException(message, null, response.StatusCode);
            }

            if (!VoiceCloudRateLimitPolicy.ShouldTryNextModel(response.StatusCode))
                throw new HttpRequestException(message, null, response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body) || lastStatus.HasValue && lastStatus.Value != HttpStatusCode.OK &&
            !body.Contains("\"candidates\"", StringComparison.Ordinal))
        {
            throw new HttpRequestException(
                lastError ?? "Gemini models unavailable.",
                null,
                lastStatus);
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.GetArrayLength() == 0)
            return string.Empty;

        JsonElement parts = candidates[0].GetProperty("content").GetProperty("parts");
        var sb = new StringBuilder();
        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out JsonElement text))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(text.GetString());
            }
        }

        return sb.ToString().Trim();
    }

    internal static TimeSpan? ResolveRetryAfter(
        HttpResponseMessage response,
        DateTimeOffset nowUtc)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            return delta;

        if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan remaining = date - nowUtc;
            return remaining > TimeSpan.Zero ? remaining : null;
        }

        return null;
    }

    public static void Reset() => History.Clear();
}
