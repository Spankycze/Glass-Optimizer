using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record VoiceCloudAiSettings(
    string Provider,
    string Model,
    string EncryptedApiKey,
    bool Enabled);

public static class VoiceCloudAiSettingsService
{
    private const string DefaultProvider = "Google Gemini";
    private const string DefaultModel = "gemini-3.8-flash";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "voice-cloud-ai.json");

    public static VoiceCloudAiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<VoiceCloudAiSettings>(File.ReadAllText(SettingsPath));
                if (loaded != null)
                    return loaded with
                    {
                        Provider = DefaultProvider,
                        Model = DefaultModel
                    };
            }
        }
        catch { }

        return new VoiceCloudAiSettings(DefaultProvider, DefaultModel, string.Empty, true);
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

        var settings = new VoiceCloudAiSettings(DefaultProvider, DefaultModel, encrypted, enabled);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static string GetApiKey()
    {
        try
        {
            VoiceCloudAiSettings settings = Load();
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
