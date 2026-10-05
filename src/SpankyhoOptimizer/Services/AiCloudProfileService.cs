using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record AiCloudProfileStatus(
    bool Available,
    string Provider,
    string Location,
    DateTime? LastSyncUtc,
    int ItemCount,
    string LastError);

internal sealed record AiCloudProfileEnvelope(
    int SchemaVersion,
    DateTime UpdatedUtc,
    AiSystemRuntimeConfig Config,
    AiSystemRuntimeItemState[] Items);

internal sealed record AiCloudLocalStatus(
    DateTime? LastSyncUtc,
    int ItemCount,
    string LastError);

public static class AiCloudProfileService
{
    private const int SchemaVersion = 2;
    private const string RootFolderName = "Spankyho Optimizer";
    private const string AiFolderName = "AI";
    private const string FileName = "ai-maintenance-profile.json";
    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static string _lastSnapshotFingerprint = string.Empty;
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer");
    private static readonly string StatusPath = Path.Combine(StateDir, "ai-cloud-status.json");
    private static readonly object StatusGate = new();
    private static AiCloudLocalStatus? CachedLocalStatus;
    private static DateTime CachedLocalStatusWriteUtc = DateTime.MinValue;

    public static AiCloudProfileStatus ReadStatus()
    {
        GoogleDriveAuthStatus auth = GoogleDriveOAuthService.ReadStatus();
        AiCloudLocalStatus local = ReadLocalStatus();
        return new(
            auth.SignedIn,
            "Google Drive",
            auth.SignedIn ? "Google Drive / Spankyho Optimizer / AI" : "Nepřihlášeno",
            local.LastSyncUtc,
            local.ItemCount,
            string.IsNullOrWhiteSpace(auth.LastError) ? local.LastError : auth.LastError);
    }

    public static async Task<bool> SyncNowAsync(
        AiSystemRuntimeConfig config,
        IEnumerable<AiSystemRuntimeItemState> items,
        CancellationToken token = default)
    {
        if (!config.CloudSyncEnabled) return false;
        string? accessToken = await GoogleDriveOAuthService.GetAccessTokenAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            WriteLocalStatus(null, 0, "Google účet není přihlášený.");
            return false;
        }
        AiSystemRuntimeConfig cloudConfig = AiCloudProfilePolicy.SanitizeConfig(config);
        AiSystemRuntimeItemState[] rows = AiCloudProfilePolicy.SanitizeItems(items);
        var envelope = new AiCloudProfileEnvelope(
            SchemaVersion,
            DateTime.UtcNow,
            cloudConfig,
            rows);
        string json = JsonSerializer.Serialize(envelope);
        string fingerprintPayload = JsonSerializer.Serialize(new { config = cloudConfig, rows });
        string fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload)));

        await SyncGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (string.Equals(_lastSnapshotFingerprint, fingerprint, StringComparison.Ordinal))
                return true;
            string? fileId = await FindFileIdAsync(accessToken, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(fileId))
                fileId = await CreateFileAsync(accessToken, token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(fileId))
            {
                WriteLocalStatus(null, rows.Length, "Google Drive nevytvořil cloud profil.");
                return false;
            }

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=media");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = content;
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                WriteLocalStatus(null, rows.Length, "Google Drive sync: " + response.StatusCode);
                return false;
            }

            _lastSnapshotFingerprint = fingerprint;
            WriteLocalStatus(DateTime.UtcNow, rows.Length, string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            WriteLocalStatus(null, rows.Length, ex.GetBaseException().Message);
            return false;
        }
        finally
        {
            SyncGate.Release();
        }
    }
    public static void SyncBestEffort(
        AiSystemRuntimeConfig config,
        IEnumerable<AiSystemRuntimeItemState> items)
    {
        if (!config.CloudSyncEnabled || !GoogleDriveOAuthService.ReadStatus().SignedIn)
            return;

        AiSystemRuntimeItemState[] snapshot = items.ToArray();
        _ = Task.Run(async () =>
        {
            try { await SyncNowAsync(config, snapshot, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        });
    }

    public static async Task<int> RestoreNowAsync(CancellationToken token = default)
    {
        string? accessToken = await GoogleDriveOAuthService.GetAccessTokenAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            return 0;

        string? fileId = await FindFileIdAsync(accessToken, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(fileId))
            return 0;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return 0;

        string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        AiCloudProfileEnvelope? profile = JsonSerializer.Deserialize<AiCloudProfileEnvelope>(json);
        if (profile == null || profile.SchemaVersion > SchemaVersion)
            return 0;
        AiSystemRuntimeConfig safeConfig = AiCloudProfilePolicy.SanitizeConfig(profile.Config);
        AiSystemRuntimeItemState[] safeItems = AiCloudProfilePolicy.SanitizeItems(profile.Items);
        AiSystemResourceManagerService.SaveConfig(safeConfig);
        int imported = AiSystemResourceManagerService.ImportCloudMemory(safeItems);
        WriteLocalStatus(DateTime.UtcNow, safeItems.Length, string.Empty);
        return imported;
    }

    private static async Task<string?> FindFileIdAsync(string accessToken, CancellationToken token)
    {
        string? rootId = await FindFolderIdAsync(accessToken, RootFolderName, null, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rootId)) return null;

        string? aiId = await FindFolderIdAsync(accessToken, AiFolderName, rootId, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(aiId)) return null;

        string escapedName = FileName.Replace("'", "\'");
        string query = Uri.EscapeDataString(
            $"name='{escapedName}' and '{aiId}' in parents and trashed=false");
        string url = "https://www.googleapis.com/drive/v3/files?fields=files(id,name)&q=" + query;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        JsonElement files = doc.RootElement.GetProperty("files");
        return files.GetArrayLength() == 0 ? null : files[0].GetProperty("id").GetString();
    }

    private static async Task<string?> CreateFileAsync(string accessToken, CancellationToken token)
    {
        string? rootId = await EnsureFolderAsync(accessToken, RootFolderName, null, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rootId)) return null;

        string? aiId = await EnsureFolderAsync(accessToken, AiFolderName, rootId, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(aiId)) return null;

        using var content = new StringContent(
            JsonSerializer.Serialize(new { name = FileName, parents = new[] { aiId } }),
            Encoding.UTF8,
            "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = content;
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        return doc.RootElement.GetProperty("id").GetString();
    }

    private static async Task<string?> EnsureFolderAsync(
        string accessToken,
        string name,
        string? parentId,
        CancellationToken token)
    {
        string? existing = await FindFolderIdAsync(accessToken, name, parentId, token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        object metadata = string.IsNullOrWhiteSpace(parentId)
            ? new { name, mimeType = "application/vnd.google-apps.folder" }
            : new { name, mimeType = "application/vnd.google-apps.folder", parents = new[] { parentId } };

        using var content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = content;
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        return doc.RootElement.GetProperty("id").GetString();
    }

    private static async Task<string?> FindFolderIdAsync(
        string accessToken,
        string name,
        string? parentId,
        CancellationToken token)
    {
        string escapedName = name.Replace("'", "\'");
        string parentFilter = string.IsNullOrWhiteSpace(parentId) ? string.Empty : $" and '{parentId}' in parents";
        string query = Uri.EscapeDataString(
            $"name='{escapedName}' and mimeType='application/vnd.google-apps.folder' and trashed=false{parentFilter}");
        string url = "https://www.googleapis.com/drive/v3/files?fields=files(id,name)&q=" + query;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        JsonElement files = doc.RootElement.GetProperty("files");
        return files.GetArrayLength() == 0 ? null : files[0].GetProperty("id").GetString();
    }

    private static AiCloudLocalStatus ReadLocalStatus()
    {
        lock (StatusGate)
        {
            try
            {
                DateTime writeUtc = File.Exists(StatusPath)
                    ? File.GetLastWriteTimeUtc(StatusPath)
                    : DateTime.MinValue;
                if (CachedLocalStatus != null && writeUtc == CachedLocalStatusWriteUtc)
                    return CachedLocalStatus;

                CachedLocalStatus = writeUtc != DateTime.MinValue
                    ? JsonSerializer.Deserialize<AiCloudLocalStatus>(File.ReadAllText(StatusPath))
                      ?? new(null, 0, string.Empty)
                    : new(null, 0, string.Empty);
                CachedLocalStatusWriteUtc = writeUtc;
                return CachedLocalStatus;
            }
            catch
            {
                return CachedLocalStatus ?? new(null, 0, string.Empty);
            }
        }
    }

    private static void WriteLocalStatus(DateTime? syncUtc, int itemCount, string error)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var status = new AiCloudLocalStatus(syncUtc, itemCount, error);
            File.WriteAllText(StatusPath, JsonSerializer.Serialize(status));
            lock (StatusGate)
            {
                CachedLocalStatus = status;
                CachedLocalStatusWriteUtc = File.GetLastWriteTimeUtc(StatusPath);
            }
        }
        catch { }
    }
}
