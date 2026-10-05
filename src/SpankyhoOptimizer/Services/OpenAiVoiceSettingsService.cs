using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record OpenAiVoiceSettings(
    string Provider,
    string BrainModel,
    string TranscribeModel,
    string EncryptedApiKey,
    bool Enabled);

public static class OpenAiVoiceSettingsService
{
    private const string DefaultProvider = "OpenAI";
    private const string DefaultBrainModel = "gpt-6-sol";
    private const string DefaultTranscribeModel = "gpt-live-transcribe";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "voice-openai.json");

    public static OpenAiVoiceSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<OpenAiVoiceSettings>(File.ReadAllText(SettingsPath));
                if (loaded != null)
                    return loaded with
                    {
                        Provider = DefaultProvider,
                        BrainModel = string.IsNullOrWhiteSpace(loaded.BrainModel) ? DefaultBrainModel : loaded.BrainModel,
                        TranscribeModel = string.IsNullOrWhiteSpace(loaded.TranscribeModel) ? DefaultTranscribeModel : loaded.TranscribeModel
                    };
            }
        }
        catch { }

        return new OpenAiVoiceSettings(
            DefaultProvider,
            DefaultBrainModel,
            DefaultTranscribeModel,
            string.Empty,
            true);
    }

    public static void SaveApiKey(string apiKey, bool enabled = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string encrypted = string.Empty;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            byte[] raw = Encoding.UTF8.GetBytes(apiKey.Trim());
            byte[] protectedBytes = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            encrypted = Convert.ToBase64String(protectedBytes);
        }

        OpenAiVoiceSettings current = Load();
        var settings = new OpenAiVoiceSettings(
            DefaultProvider,
            string.IsNullOrWhiteSpace(current.BrainModel) ? DefaultBrainModel : current.BrainModel,
            string.IsNullOrWhiteSpace(current.TranscribeModel) ? DefaultTranscribeModel : current.TranscribeModel,
            encrypted,
            enabled);

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static string GetApiKey()
    {
        try
        {
            OpenAiVoiceSettings settings = Load();
            if (string.IsNullOrWhiteSpace(settings.EncryptedApiKey))
                return string.Empty;

            byte[] protectedBytes = Convert.FromBase64String(settings.EncryptedApiKey);
            byte[] raw = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());
}
