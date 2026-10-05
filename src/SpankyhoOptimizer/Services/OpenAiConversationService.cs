using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace SpankyhoOptimizer.Services;

public static class OpenAiConversationService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<(string Role, string Content)> History = new();
    private const int MaxHistoryMessages = 40;
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "voice-history.json");
    private static bool _historyLoaded;

    private static Kernel? _kernel;
    private static IChatCompletionService? _chat;
    private static string _kernelFingerprint = string.Empty;
    private static DateTime _cooldownUntilUtc = DateTime.MinValue;

    private const string SystemPrompt =
        "Jsi Spanky, hlasový AI asistent ve Windows aplikaci Spankyho Optimizer. " +
        "Mluv přirozenou hovorovou češtinou, stručně a přímo. " +
        "Navazuj na předchozí konverzaci a chápej běžnou hovorovou češtinu, slang i drobné chyby v hlasovém přepisu. ";
    private const string SystemPrompt2 =
        "Pro živá nebo aktuální fakta nikdy nehádej: použij dostupný nástroj. To platí hlavně pro počasí a předpověď, čas, datum, stav PC, RAM a ventilátory. " +
        "Když se uživatel ptá na počasí pro konkrétní město nebo na zítřek/další dny, použij weather forecast nástroj s místem a dayOffset. " +
        "Systémovou akci považuj za provedenou jen pokud nástroj vrátí úspěch. " +
        "Pokud přepis vypadá jako zkomolená běžná věta, nejdřív z kontextu oprav pravděpodobný význam. Jen když existují dvě rozumné možnosti, stručně se doptáš. " +
        "Pamatuj si návaznost tématu z předchozích zpráv a nevyžaduj opakování údajů, které už v konverzaci jsou. Krátké follow-upy jako 'a zítra?', 'a GPU?', 'a v Brně?' vždy vztahuj k poslednímu relevantnímu tématu. " +
        "Odpovídej typicky jednou až třemi větami a nepopisuj interní uvažování ani názvy interních nástrojů.";

    public static async Task<(bool Ok, string Message)> TestConnectionAsync(
        string apiKey,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "API klíč chybí.");

        try
        {
            string model = OpenAiVoiceSettingsService.Load().BrainModel;
            Kernel kernel = BuildKernel(apiKey.Trim(), model);
            IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory(SystemPrompt + SystemPrompt2);
            history.AddUserMessage("Odpověz pouze slovem OK.");

            ChatMessageContent reply = await chat.GetChatMessageContentAsync(
                history,
                cancellationToken: token).ConfigureAwait(false);

            bool ok = !string.IsNullOrWhiteSpace(reply.Content);
            if (ok)
            {
                _cooldownUntilUtc = DateTime.MinValue;
                OpenAiBrainHealthStateService.RecordSuccess(apiKey.Trim());
            }

            return (ok,
                ok
                    ? $"OpenAI {model} je připojený."
                    : "OpenAI nevrátil odpověď.");
        }        catch (Exception ex)
        {
            return (false, "OpenAI API: " + ex.Message);
        }
    }

    public static async Task<string> AskAsync(string userText, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return string.Empty;

        string apiKey = OpenAiVoiceSettingsService.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return "V AI nastavení chybí OpenAI API klíč.";

        DateTime nowUtc = DateTime.UtcNow;
        if (nowUtc < _cooldownUntilUtc)
            return "OpenAI brain teď není dostupný.";

        if (OpenAiBrainHealthStateService.TryGetActive(
                apiKey,
                nowUtc,
                out OpenAiBrainHealthState? persistedCooldown))
        {
            _cooldownUntilUtc = persistedCooldown!.UntilUtc;
            return "OpenAI brain teď není dostupný.";
        }

        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            LoadHistoryOnce();
            OpenAiVoiceSettings settings = OpenAiVoiceSettingsService.Load();
            EnsureKernel(apiKey, settings.BrainModel);
            if (_kernel == null || _chat == null)
                return "OpenAI brain se nepodařilo inicializovat.";

            var chatHistory = new ChatHistory(SystemPrompt + SystemPrompt2);
            int[] lengths = History.Select(x => x.Content.Length).ToArray();
            int start = VoiceHistoryWindowPolicy.GetStartIndex(lengths);
            for (int i = start; i < History.Count; i++)
            {
                (string role, string content) = History[i];
                if (role == "user") chatHistory.AddUserMessage(content);
                else chatHistory.AddAssistantMessage(content);
            }
            chatHistory.AddUserMessage(userText.Trim());

            var execution = new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };            ChatMessageContent reply = await _chat.GetChatMessageContentAsync(
                chatHistory,
                execution,
                _kernel,
                token).ConfigureAwait(false);

            string text = reply.Content?.Trim() ?? string.Empty;
            if (text.Length == 0)
                return "OpenAI nevrátil odpověď.";

            _cooldownUntilUtc = DateTime.MinValue;
            OpenAiBrainHealthStateService.RecordSuccess(apiKey);
            History.Add(("user", userText.Trim()));
            History.Add(("assistant", text));
            while (History.Count > MaxHistoryMessages)
                History.RemoveAt(0);
            SaveHistory();

            return text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string reason = ex.GetBaseException().Message;
            _cooldownUntilUtc = OpenAiBrainHealthStateService.RecordFailure(
                apiKey,
                reason,
                DateTime.UtcNow);
            LogService.Error("voice", "OpenAI Semantic Kernel conversation failed", ex);
            return "OpenAI brain teď není dostupný.";
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed record PersistedMessage(string Role, string Content);

    private static void LoadHistoryOnce()
    {
        if (_historyLoaded) return;
        _historyLoaded = true;
        try
        {
            if (!File.Exists(HistoryPath)) return;
            string stored = File.ReadAllText(HistoryPath).Trim();
            bool legacyPlaintext = !stored.StartsWith("dpapi:", StringComparison.Ordinal);
            string json = legacyPlaintext
                ? stored
                : Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(
                        Convert.FromBase64String(stored[6..]),
                        null,
                        DataProtectionScope.CurrentUser));

            PersistedMessage[]? items = JsonSerializer.Deserialize<PersistedMessage[]>(json);
            if (items == null) return;
            foreach (PersistedMessage item in items)
            {
                if ((item.Role == "user" || item.Role == "assistant") && !string.IsNullOrWhiteSpace(item.Content))
                    History.Add((item.Role, item.Content.Trim()));
            }
            while (History.Count > MaxHistoryMessages)
                History.RemoveAt(0);

            if (legacyPlaintext)
                SaveHistory();
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "Voice history load failed", ex);
        }
    }

    private static void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var items = new PersistedMessage[History.Count];
            for (int i = 0; i < History.Count; i++)
                items[i] = new PersistedMessage(History[i].Role, History[i].Content);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items));
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            string temp = HistoryPath + ".tmp";
            File.WriteAllText(temp, "dpapi:" + Convert.ToBase64String(encrypted), new UTF8Encoding(false));
            File.Move(temp, HistoryPath, true);
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "Voice history save failed", ex);
        }
    }

    private static void EnsureKernel(string apiKey, string model)
    {
        string fingerprint = model + "|" + apiKey.Length + "|" + (apiKey.Length >= 6 ? apiKey[..6] : apiKey);
        if (_kernel != null && _chat != null && string.Equals(_kernelFingerprint, fingerprint, StringComparison.Ordinal))
            return;

        _kernel = BuildKernel(apiKey, model);
        _chat = _kernel.GetRequiredService<IChatCompletionService>();
        _kernelFingerprint = fingerprint;
    }
    private static Kernel BuildKernel(string apiKey, string model)
    {
        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(model, apiKey);
        Kernel kernel = builder.Build();
        kernel.Plugins.AddFromType<SpankyVoiceKernelTools>("Spanky");
        return kernel;
    }

    public static void Reset()
    {
        _kernel = null;
        _chat = null;
        _kernelFingerprint = string.Empty;
        _cooldownUntilUtc = DateTime.MinValue;
    }

    public static void ClearConversationMemory()
    {
        History.Clear();
        _historyLoaded = true;
        try { if (File.Exists(HistoryPath)) File.Delete(HistoryPath); } catch { }
    }
}
