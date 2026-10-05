using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class ChatGptPlanConversationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<PersistedMessage> History = new();
    private const int MaxHistoryMessages = 36;
    private static bool _historyLoaded;
    private static string? _cachedModel;
    private static DateTime _modelCachedAtUtc;
    private static int _modelRefreshActive;
    public static long LastResponseHeadersLatencyMs { get; private set; }
    public static long LastFirstDeltaLatencyMs { get; private set; }

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "voice-plan-history.json");

    private const string Instructions =
        "Jsi Spanky, chytrý hlasový AI asistent ve Windows aplikaci Spankyho Optimizer. " +
        "Mluv přirozenou hovorovou češtinou, stručně, lidsky a přímo, podobně jako běžný ChatGPT v hlasovém režimu. " +
        "Navazuj na předchozí konverzaci, chápej slang, nedokončené věty, krátké follow-upy a drobné chyby hlasového přepisu. " +
        "Krátké věty jako 'a zítra?', 'a GPU?', 'a teplota?' vždy vztahuj k poslednímu relevantnímu tématu. " +
        "Pro aktuální počasí, čas, datum nebo stav PC používej dostupné funkce místo odhadu. " +
        "Když potřebuješ funkci, zavolej ji bez předběžného komentáře a až potom odpověz výsledkem. " +
        "Nevymýšlej si aktuální živé údaje. Když je živý údaj už obsažený v historii nebo kontextu, normálně s ním pracuj. " +
        "Když aktuální údaj k dispozici není, řekni jen stručně, že ho teď nemůžeš ověřit. " +
        "Nikdy uživateli nepopisuj interní mechanismy ani názvy interních funkcí. " +
        "Odpovídej většinou jednou až třemi větami, pokud uživatel nechce podrobnosti.";

    private static readonly object[] ToolDefinitions =
    {
        Tool("get_time", "Vrátí aktuální lokální čas počítače.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_date", "Vrátí aktuální lokální datum počítače.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_current_weather", "Vrátí živé aktuální počasí. location může být město; bez location použije aktuální oblast.", new
        {
            type = "object",
            properties = new { location = new { type = "string", description = "Město nebo místo, např. Brno. Vynech pro aktuální oblast." } },
            additionalProperties = false
        }),
        Tool("get_weather_forecast", "Vrátí živou předpověď počasí pro dnešek nebo další dny.", new
        {
            type = "object",
            properties = new
            {
                location = new { type = "string", description = "Město nebo místo. Vynech pro aktuální oblast." },
                day_offset = new { type = "integer", minimum = 0, maximum = 6, description = "0 dnes, 1 zítra, až 6." }
            },
            required = new[] { "day_offset" },
            additionalProperties = false
        }),
        Tool("get_cpu_telemetry", "Vrátí živou teplotu, vytížení, takt a příkon CPU.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_gpu_telemetry", "Vrátí živou teplotu a vytížení GPU.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_ram_usage", "Vrátí živé využití fyzické RAM.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_disk_usage", "Vrátí využití systémového disku a volné místo.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_top_cpu_processes", "Vrátí procesy, které právě nejvíc zatěžují CPU.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_network_latency", "Změří aktuální síťovou odezvu/ping.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_dns_status", "Vrátí DNS servery aktivních síťových rozhraní.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_windows_version", "Vrátí aktuální Windows verzi.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_active_power_plan", "Vrátí aktivní Windows power plan.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_firefox_status", "Zjistí, zda Firefox právě běží.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("diagnose_firefox", "Provede rychlou diagnostiku Firefoxu z CPU, RAM a síťové odezvy.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_system_health", "Zkontroluje živé CPU, GPU, teploty, RAM a ping.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("get_voice_health", "Vrátí stav voice pipeline, mikrofonu, STT/TTS a poslední latency.", new { type = "object", properties = new { }, additionalProperties = false }),
        Tool("is_application_running", "Zjistí, zda zadaná aplikace nebo proces běží.", new
        {
            type = "object",
            properties = new { name = new { type = "string", description = "Název aplikace nebo procesu, např. Firefox nebo Diablo Immortal." } },
            required = new[] { "name" },
            additionalProperties = false
        }),
        Tool("get_fan_status", "Vrátí aktuální profil ventilátorů a cílové otáčky.", new { type = "object", properties = new { }, additionalProperties = false })
    };

    public static Task<string> AskAsync(string userText, CancellationToken token = default) =>
        RouteAsync(userText, null, token);

    public static Task<string> AskStreamingAsync(
        string userText,
        Func<string, CancellationToken, Task> onSpeechChunk,
        CancellationToken token = default) =>
        RouteAsync(userText, onSpeechChunk, token);

    private static Task<string> RouteAsync(
        string userText,
        Func<string, CancellationToken, Task>? onSpeechChunk,
        CancellationToken token)
    {
        bool toolFree = ChatGptPlanToolRoutingPolicy.CanUseToolFreeFastPath(userText);
        LogService.Info(
            "voice",
            toolFree ? "VOICE_PLAN route=tool_free" : "VOICE_PLAN route=native_tools");

        return toolFree
            ? AskInternalAsync(userText, onSpeechChunk, token)
            : AskWithNativeToolsAsync(userText, onSpeechChunk, token);
    }

    private static object Tool(string name, string description, object parameters) =>
        new { type = "function", name, description, parameters };

    private static async Task<string> AskWithNativeToolsAsync(
        string userText,
        Func<string, CancellationToken, Task>? onSpeechChunk,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return string.Empty;

        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            LoadHistoryOnce();
            string? accessToken = await ChatGptPlanAuthService.GetValidAccessTokenAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
                return "ChatGPT přihlášení vypršelo. Přihlas se znovu v AI nastavení.";

            string model = await GetModelAsync(accessToken, token).ConfigureAwait(false);
            var input = new List<object>();
            AddRecentHistory(input);
            input.Add(new { role = "user", content = userText.Trim() });

            for (int round = 0; round < 3; round++)
            {
                PlanRoundResult result = await RunNativeRoundAsync(
                    accessToken, model, input, onSpeechChunk, token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(result.Error))
                    return result.Error;

                if (result.ToolCalls.Count == 0)
                {
                    string answer = result.Text.Trim();
                    if (!result.Completed || answer.Length == 0)
                        return "ChatGPT stream skončil bez kompletní odpovědi.";

                    AppendExchange(userText, answer);
                    return answer;
                }

                foreach (PlanToolCall call in result.ToolCalls)
                {
                    input.Add(new
                    {
                        type = "function_call",
                        call_id = call.CallId,
                        name = call.Name,
                        arguments = call.Arguments
                    });

                    string output = await ExecuteNativeToolAsync(call, token).ConfigureAwait(false);
                    input.Add(new
                    {
                        type = "function_call_output",
                        call_id = call.CallId,
                        output
                    });
                }
            }

            return "Nepodařilo se dokončit živý dotaz.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "ChatGPT plan native tool conversation failed", ex);
            return "ChatGPT plán teď není dostupný.";
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<PlanRoundResult> RunNativeRoundAsync(
        string accessToken,
        string model,
        List<object> input,
        Func<string, CancellationToken, Task>? onSpeechChunk,
        CancellationToken token)
    {
        var payload = new
        {
            model,
            instructions = Instructions,
            input,
            tools = ToolDefinitions,
            tool_choice = "auto",
            store = false,
            stream = true
        };

        var streamLatency = System.Diagnostics.Stopwatch.StartNew();
        bool firstDeltaLogged = false;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

        LastResponseHeadersLatencyMs = streamLatency.ElapsedMilliseconds;
        LogService.Info("voice", $"VOICE_LATENCY chatgpt_headers_ms={LastResponseHeadersLatencyMs}");

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (ChatGptModelFailurePolicy.ShouldInvalidate(response.StatusCode, error))
            {
                Reset();
                LogService.Info("voice",
                    $"CHATGPT_PLAN model_cache_invalidated status={(int)response.StatusCode}");
            }
            LogService.Info("voice", $"ChatGPT plan tool response HTTP {(int)response.StatusCode}: {TrimForLog(error)}");
            string message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "ChatGPT přihlášení je potřeba obnovit."
                : "ChatGPT plán teď nevrátil odpověď.";
            return new PlanRoundResult(false, string.Empty, Array.Empty<PlanToolCall>(), message);
        }

        using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var answerBuilder = new StringBuilder();
        var speechBuffer = new StringBuilder();
        var toolCalls = new List<PlanToolCall>();
        Channel<string>? speechChannel = null;
        Task speechConsumer = Task.CompletedTask;
        bool firstSpeechChunk = true;

        if (onSpeechChunk != null)
        {
            speechChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            speechConsumer = ConsumeSpeechAsync(speechChannel.Reader, onSpeechChunk, token);
        }

        bool completed = false;
        string? terminalError = null;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == null)
                    break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string data = line[5..].Trim();
                if (data.Length == 0 || data == "[DONE]")
                    continue;

                using JsonDocument evt = JsonDocument.Parse(data);
                JsonElement root = evt.RootElement;
                string type = root.TryGetProperty("type", out JsonElement t)
                    ? t.GetString() ?? string.Empty
                    : string.Empty;

                if (type == "response.output_text.delta" &&
                    root.TryGetProperty("delta", out JsonElement delta))
                {
                    string piece = delta.GetString() ?? string.Empty;
                    answerBuilder.Append(piece);

                    if (piece.Length > 0 && !firstDeltaLogged)
                    {
                        firstDeltaLogged = true;
                        LastFirstDeltaLatencyMs = streamLatency.ElapsedMilliseconds;
                        LogService.Info("voice", $"VOICE_LATENCY chatgpt_first_delta_ms={LastFirstDeltaLatencyMs}");
                    }

                    if (speechChannel != null && piece.Length > 0)
                    {
                        speechBuffer.Append(piece);
                        while (VoiceSpeechChunkPolicy.TryTake(
                            speechBuffer,
                            firstSpeechChunk,
                            out string speechChunk))
                        {
                            if (firstSpeechChunk)
                            {
                                long firstSpeechChunkMs = streamLatency.ElapsedMilliseconds;
                                long afterFirstDeltaMs = Math.Max(
                                    0,
                                    firstSpeechChunkMs - LastFirstDeltaLatencyMs);
                                LogService.Info(
                                    "voice",
                                    $"VOICE_LATENCY chatgpt_first_speech_chunk_ms={firstSpeechChunkMs} after_first_delta_ms={afterFirstDeltaMs}");
                            }

                            firstSpeechChunk = false;
                            speechChannel.Writer.TryWrite(speechChunk);
                        }
                    }
                }
                else if (type == "response.output_item.done" &&
                         root.TryGetProperty("item", out JsonElement item) &&
                         item.TryGetProperty("type", out JsonElement itemType) &&
                         string.Equals(itemType.GetString(), "function_call", StringComparison.Ordinal))
                {
                    string callId = item.TryGetProperty("call_id", out JsonElement ci)
                        ? ci.GetString() ?? string.Empty
                        : string.Empty;
                    string name = item.TryGetProperty("name", out JsonElement fn)
                        ? fn.GetString() ?? string.Empty
                        : string.Empty;
                    string arguments = item.TryGetProperty("arguments", out JsonElement ar)
                        ? ar.GetString() ?? "{}"
                        : "{}";

                    if (callId.Length > 0 && name.Length > 0)
                        toolCalls.Add(new PlanToolCall(callId, name, arguments));
                }
                else if (type == "response.completed")
                {
                    completed = true;
                    break;
                }
                else if (type == "response.failed")
                {
                    string code = "unknown_error";
                    if (root.TryGetProperty("response", out JsonElement re) &&
                        re.TryGetProperty("error", out JsonElement er) &&
                        er.ValueKind == JsonValueKind.Object &&
                        er.TryGetProperty("code", out JsonElement ec))
                    {
                        code = ec.GetString() ?? code;
                    }

                    terminalError = code.Contains("subscription_sharing_usage_limit", StringComparison.OrdinalIgnoreCase)
                        ? "Došel limit použití ChatGPT plánu pro připojené aplikace."
                        : "ChatGPT odpověď selhala.";
                    break;
                }
            }
        }
        finally
        {
            if (speechChannel != null)
            {
                if (completed && toolCalls.Count == 0)
                {
                    string remainder = speechBuffer.ToString().Trim();
                    if (remainder.Length > 0)
                        speechChannel.Writer.TryWrite(remainder);
                }

                speechChannel.Writer.TryComplete();
            }
        }

        if (speechChannel != null)
        {
            try
            {
                await speechConsumer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Error("voice", "Native tool streaming TTS consumer failed", ex);
            }
        }

        return new PlanRoundResult(
            completed,
            answerBuilder.ToString(),
            toolCalls,
            terminalError);
    }

    internal static Task<string> ExecuteNativeToolForTestAsync(
        string name,
        string arguments = "{}",
        CancellationToken token = default) =>
        ExecuteNativeToolAsync(new PlanToolCall("test-call", name, arguments), token);

    private static async Task<string> ExecuteNativeToolAsync(PlanToolCall call, CancellationToken token)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        LogService.Info("voice", $"VOICE_TOOL_CALL name={call.Name}");

        try
        {
            using JsonDocument args = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            JsonElement root = args.RootElement;
            var tools = new SpankyVoiceKernelTools();

            string result = call.Name switch
            {
                "get_time" => tools.GetTime(),
                "get_date" => tools.GetDate(),
                "get_current_weather" => await SpankyVoiceToolService.GetWeatherAsync(
                    GetOptionalString(root, "location"), token).ConfigureAwait(false),
                "get_weather_forecast" => await SpankyVoiceToolService.GetWeatherForecastAsync(
                    GetOptionalString(root, "location"),
                    Math.Clamp(GetOptionalInt(root, "day_offset", 0), 0, 6),
                    token).ConfigureAwait(false),
                "get_cpu_telemetry" => tools.GetCpuTelemetry(),
                "get_gpu_telemetry" => tools.GetGpuTelemetry(),
                "get_ram_usage" => tools.GetRamUsage(),
                "get_disk_usage" => tools.GetDiskUsage(),
                "get_top_cpu_processes" => await tools.GetTopCpuProcessesAsync(token).ConfigureAwait(false),
                "get_network_latency" => await tools.GetNetworkLatencyAsync(token).ConfigureAwait(false),
                "get_dns_status" => tools.GetDnsStatus(),
                "get_windows_version" => await tools.GetWindowsVersionAsync(token).ConfigureAwait(false),
                "get_active_power_plan" => await tools.GetActivePowerPlanAsync(token).ConfigureAwait(false),
                "get_firefox_status" => tools.GetFirefoxStatus(),
                "diagnose_firefox" => await tools.DiagnoseFirefoxAsync(token).ConfigureAwait(false),
                "get_system_health" => await tools.GetSystemHealthAsync(token).ConfigureAwait(false),
                "get_voice_health" => tools.GetVoiceHealth(),
                "is_application_running" => BuildApplicationStatus(
                    GetOptionalString(root, "name")),
                "get_fan_status" => tools.GetFanStatus(),
                _ => "Tato funkce není dostupná."
            };

            LogService.Info("voice", $"VOICE_TOOL_RESULT name={call.Name} ms={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("voice", $"Native tool failed: {call.Name}", ex);
            return "Živá data se teď nepodařilo načíst.";
        }
    }

    private static string BuildApplicationStatus(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Chybí název aplikace.";

        string safeName = name.Trim();
        return SpankyVoiceToolService.IsAnyProcessRunning(safeName)
            ? $"{safeName} běží."
            : $"{safeName} neběží.";
    }

    private static string? GetOptionalString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetOptionalInt(JsonElement root, string name, int fallback) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : fallback;

    private sealed record PlanToolCall(string CallId, string Name, string Arguments);

    private sealed record PlanRoundResult(
        bool Completed,
        string Text,
        IReadOnlyList<PlanToolCall> ToolCalls,
        string? Error);

    private static async Task<string> AskInternalAsync(
        string userText,
        Func<string, CancellationToken, Task>? onSpeechChunk,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return string.Empty;

        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            LoadHistoryOnce();
            string? accessToken = await ChatGptPlanAuthService.GetValidAccessTokenAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
                return "ChatGPT přihlášení vypršelo. Přihlas se znovu v AI nastavení.";

            string model = await GetModelAsync(accessToken, token).ConfigureAwait(false);
            var input = new List<object>();
            AddRecentHistory(input);
            input.Add(new { role = "user", content = userText.Trim() });

            var payload = new
            {
                model,
                instructions = Instructions,
                input,
                store = false,
                stream = true
            };

            var streamLatency = System.Diagnostics.Stopwatch.StartNew();
        bool firstDeltaLogged = false;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            LastResponseHeadersLatencyMs = streamLatency.ElapsedMilliseconds;
            LogService.Info("voice", $"VOICE_LATENCY chatgpt_headers_ms={LastResponseHeadersLatencyMs}");

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (ChatGptModelFailurePolicy.ShouldInvalidate(response.StatusCode, error))
                {
                    Reset();
                    LogService.Info("voice",
                        $"CHATGPT_PLAN model_cache_invalidated status={(int)response.StatusCode}");
                }
                LogService.Info("voice", $"ChatGPT plan response HTTP {(int)response.StatusCode}: {TrimForLog(error)}");
                return response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "ChatGPT přihlášení je potřeba obnovit."
                    : "ChatGPT plán teď nevrátil odpověď.";
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var answerBuilder = new StringBuilder();
            var speechBuffer = new StringBuilder();
            Channel<string>? speechChannel = null;
            Task speechConsumer = Task.CompletedTask;
            bool firstSpeechChunk = true;

            if (onSpeechChunk != null)
            {
                speechChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
                speechConsumer = ConsumeSpeechAsync(speechChannel.Reader, onSpeechChunk, token);
            }

            bool completed = false;
            string? terminalError = null;

            try
            {
                while (true)
            {
                token.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == null)
                    break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string data = line[5..].Trim();
                if (data.Length == 0 || data == "[DONE]")
                    continue;

                using JsonDocument evt = JsonDocument.Parse(data);
                JsonElement root = evt.RootElement;
                string type = root.TryGetProperty("type", out JsonElement t)
                    ? t.GetString() ?? string.Empty
                    : string.Empty;

                if (type == "response.output_text.delta" &&
                    root.TryGetProperty("delta", out JsonElement delta))
                {
                    string piece = delta.GetString() ?? string.Empty;
                    answerBuilder.Append(piece);

                    if (piece.Length > 0 && !firstDeltaLogged)
                    {
                        firstDeltaLogged = true;
                        LastFirstDeltaLatencyMs = streamLatency.ElapsedMilliseconds;
                        LogService.Info("voice", $"VOICE_LATENCY chatgpt_first_delta_ms={LastFirstDeltaLatencyMs}");
                    }

                    if (speechChannel != null && piece.Length > 0)
                    {
                        speechBuffer.Append(piece);
                        while (VoiceSpeechChunkPolicy.TryTake(
                            speechBuffer,
                            firstSpeechChunk,
                            out string speechChunk))
                        {
                            if (firstSpeechChunk)
                            {
                                long firstSpeechChunkMs = streamLatency.ElapsedMilliseconds;
                                long afterFirstDeltaMs = Math.Max(
                                    0,
                                    firstSpeechChunkMs - LastFirstDeltaLatencyMs);
                                LogService.Info(
                                    "voice",
                                    $"VOICE_LATENCY chatgpt_first_speech_chunk_ms={firstSpeechChunkMs} after_first_delta_ms={afterFirstDeltaMs}");
                            }

                            firstSpeechChunk = false;
                            speechChannel.Writer.TryWrite(speechChunk);
                        }
                    }
                }
                else if (type == "response.completed")
                {
                    completed = true;
                    break;
                }
                else if (type == "response.failed")
                {
                    string code = "unknown_error";
                    if (root.TryGetProperty("response", out JsonElement re) &&
                        re.TryGetProperty("error", out JsonElement er) &&
                        er.ValueKind == JsonValueKind.Object &&
                        er.TryGetProperty("code", out JsonElement ec))
                    {
                        code = ec.GetString() ?? code;
                    }

                    terminalError = code.Contains("subscription_sharing_usage_limit", StringComparison.OrdinalIgnoreCase)
                        ? "Došel limit použití ChatGPT plánu pro připojené aplikace."
                        : "ChatGPT odpověď selhala.";
                    break;
                }
                }
            }
            finally
            {
                if (speechChannel != null)
                {
                    if (completed)
                    {
                        string remainder = speechBuffer.ToString().Trim();
                        if (remainder.Length > 0)
                            speechChannel.Writer.TryWrite(remainder);
                    }

                    speechChannel.Writer.TryComplete();
                }
            }

            if (speechChannel != null)
            {
                try
                {
                    await speechConsumer.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.Error("voice", "Streaming TTS consumer failed", ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(terminalError))
                return terminalError;

            string answer = answerBuilder.ToString().Trim();
            if (!completed || answer.Length == 0)
                return "ChatGPT stream skončil bez kompletní odpovědi.";

            AppendExchange(userText, answer);
            return answer;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "ChatGPT plan conversation failed", ex);
            return "ChatGPT plán teď není dostupný.";
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ConsumeSpeechAsync(
        ChannelReader<string> reader,
        Func<string, CancellationToken, Task> onSpeechChunk,
        CancellationToken token)
    {
        await foreach (string chunk in reader.ReadAllAsync(token))
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(chunk))
                await onSpeechChunk(chunk, token).ConfigureAwait(false);
        }
    }

    public static async Task RememberExchangeAsync(
        string userText,
        string assistantText,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(userText) || string.IsNullOrWhiteSpace(assistantText))
            return;

        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            LoadHistoryOnce();
            AppendExchange(userText, assistantText);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Reset()
    {
        _cachedModel = null;
        _modelCachedAtUtc = DateTime.MinValue;
    }

    private static void AppendExchange(string userText, string assistantText)
    {
        History.Add(new PersistedMessage("user", userText.Trim()));
        History.Add(new PersistedMessage("assistant", assistantText.Trim()));
        while (History.Count > MaxHistoryMessages)
            History.RemoveAt(0);
        SaveHistory();
    }

    private static async Task<string> GetModelAsync(string accessToken, CancellationToken token)
    {
        bool hasCached = !string.IsNullOrWhiteSpace(_cachedModel);
        TimeSpan age = hasCached
            ? DateTime.UtcNow - _modelCachedAtUtc
            : TimeSpan.Zero;
        ChatGptModelCacheDecision decision = ChatGptModelCachePolicy.Decide(hasCached, age);

        if (decision.UseCachedImmediately)
        {
            if (decision.RefreshInBackground &&
                Interlocked.CompareExchange(ref _modelRefreshActive, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FetchModelAsync(accessToken, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("voice", "ChatGPT model background refresh failed", ex);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _modelRefreshActive, 0);
                    }
                }, CancellationToken.None);
            }

            return _cachedModel!;
        }

        return await FetchModelAsync(accessToken, token).ConfigureAwait(false);
    }

    public static async Task WarmAsync(CancellationToken token = default)
    {
        if (!ChatGptPlanAuthService.IsConnected)
            return;

        string? accessToken = await ChatGptPlanAuthService.GetValidAccessTokenAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        _ = await GetModelAsync(accessToken, token).ConfigureAwait(false);
    }

    private static async Task<string> FetchModelAsync(string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(body);
        var models = new List<(string Slug, string Display)>();

        if (json.RootElement.TryGetProperty("models", out JsonElement array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                string visibility = item.TryGetProperty("visibility", out JsonElement v)
                    ? v.GetString() ?? string.Empty
                    : string.Empty;
                if (!string.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase))
                    continue;

                string slug = item.TryGetProperty("slug", out JsonElement s)
                    ? s.GetString() ?? string.Empty
                    : string.Empty;
                string display = item.TryGetProperty("display_name", out JsonElement dn)
                    ? dn.GetString() ?? slug
                    : slug;

                if (slug.Length > 0)
                    models.Add((slug, display));
            }
        }

        if (models.Count == 0)
            throw new InvalidOperationException("ChatGPT účet nevrátil žádný dostupný model.");

        _cachedModel =
            models.FirstOrDefault(x => x.Slug.Contains("sol", StringComparison.OrdinalIgnoreCase)).Slug
            ?? models.FirstOrDefault(x => x.Slug.Contains("gpt-6", StringComparison.OrdinalIgnoreCase)).Slug
            ?? models[0].Slug;

        _modelCachedAtUtc = DateTime.UtcNow;
        return _cachedModel;
    }

    private static void AddRecentHistory(List<object> input)
    {
        int[] lengths = History.Select(x => x.Content.Length).ToArray();
        int start = VoiceHistoryWindowPolicy.GetStartIndex(lengths);
        int chars = 0;

        for (int i = start; i < History.Count; i++)
        {
            PersistedMessage item = History[i];
            chars += item.Content.Length;
            input.Add(new { role = item.Role, content = item.Content });
        }

        LogService.Info(
            "voice",
            $"VOICE_LATENCY chatgpt_history_messages={History.Count - start} history_chars={chars}");
    }

    private static void LoadHistoryOnce()
    {
        if (_historyLoaded)
            return;

        _historyLoaded = true;
        try
        {
            if (!File.Exists(HistoryPath))
                return;

            string stored = File.ReadAllText(HistoryPath).Trim();
            bool legacyPlaintext = !stored.StartsWith("dpapi:", StringComparison.Ordinal);
            string json = legacyPlaintext
                ? stored
                : Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(
                        Convert.FromBase64String(stored[6..]),
                        null,
                        DataProtectionScope.CurrentUser));

            PersistedMessage[]? items =
                JsonSerializer.Deserialize<PersistedMessage[]>(json);
            if (items == null)
                return;

            History.AddRange(items.Where(x =>
                (x.Role == "user" || x.Role == "assistant") &&
                !string.IsNullOrWhiteSpace(x.Content)));

            while (History.Count > MaxHistoryMessages)
                History.RemoveAt(0);

            if (legacyPlaintext)
                SaveHistory();
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "ChatGPT plan history load failed", ex);
        }
    }

    private static void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(History));
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            string temp = HistoryPath + ".tmp";
            File.WriteAllText(temp, "dpapi:" + Convert.ToBase64String(encrypted), new UTF8Encoding(false));
            File.Move(temp, HistoryPath, true);
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "ChatGPT plan history save failed", ex);
        }
    }

    private static string TrimForLog(string value) =>
        value.Length <= 300 ? value : value[..300];

    private sealed record PersistedMessage(string Role, string Content);
}
