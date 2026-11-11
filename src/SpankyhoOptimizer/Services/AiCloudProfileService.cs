using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private sealed record PendingCloudUpload(
        AiSystemRuntimeConfig Config, AiSystemRuntimeItemState[] Items, long AuthEpoch);
    private static readonly LatestSnapshotBackgroundQueue<PendingCloudUpload> BackgroundUploads =
        new(async pending =>
        {
            await SyncCoreAsync(pending.Config, pending.Items, CancellationToken.None, pending.AuthEpoch)
                .ConfigureAwait(false);
        });
    private static string _lastSnapshotFingerprint = string.Empty;
    private static string _lastCredentialFingerprint = string.Empty;
    private static DateTime _lastSuccessfulUploadUtc = DateTime.MinValue;
    private static readonly TimeSpan UploadDedupWindow = TimeSpan.FromMinutes(3);
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer");
    private static readonly string StatusPath = Path.Combine(StateDir, "ai-cloud-status.json");
    private static readonly object StatusGate = new();
    private static AiCloudLocalStatus? CachedLocalStatus;
    private static DateTime CachedLocalStatusWriteUtc = DateTime.MinValue;

    public static AiCloudProfileStatus ReadStatus() =>
        ReadStatus(GoogleDriveOAuthService.ReadStatus());

    // The Runtime page already has a Google auth snapshot. Reuse the exact
    // same result for the cloud badge, avoiding a second token/cache read and
    // preventing two different login states from appearing in one render.
    public static AiCloudProfileStatus ReadStatus(GoogleDriveAuthStatus auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        AiCloudLocalStatus local = ReadLocalStatus();
        return new(
            auth.SignedIn,
            "Google Drive",
            auth.SignedIn ? "Google Drive / Spankyho Optimizer / AI" : "Nepřihlášeno",
            local.LastSyncUtc,
            local.ItemCount,
            string.IsNullOrWhiteSpace(auth.LastError) ? local.LastError : auth.LastError);
    }

    public static Task<bool> SyncNowAsync(
        AiSystemRuntimeConfig config,
        IEnumerable<AiSystemRuntimeItemState> items,
        CancellationToken token = default) =>
        SyncCoreAsync(config, items, token, GoogleDriveOAuthService.ReadSessionEpoch());

    private static async Task<bool> SyncCoreAsync(
        AiSystemRuntimeConfig config,
        IEnumerable<AiSystemRuntimeItemState> items,
        CancellationToken token,
        long authEpoch)
    {
        if (!config.CloudSyncEnabled ||
            !IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
            return false;
        string? accessToken = await GoogleDriveOAuthService.GetAccessTokenAsync(token).ConfigureAwait(false);
        if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
            return false;
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
        // The SHA-256 input stays byte-identical to the old UTF-8 JSON,
        // without allocating a second UTF-16 JSON string and re-encoding it.
        string fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { config = cloudConfig, rows })));

        string credentialFingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));

        await SyncGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
                return false;
            if (CanReuseRecentUpload(_lastSnapshotFingerprint, fingerprint,
                    _lastCredentialFingerprint, credentialFingerprint, _lastSuccessfulUploadUtc, DateTime.UtcNow))
                return true;
            string? fileId = await FindFileIdAsync(accessToken, token).ConfigureAwait(false);
            if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
                return false;
            if (string.IsNullOrWhiteSpace(fileId))
                fileId = await CreateFileAsync(accessToken, token).ConfigureAwait(false);
            if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
                return false;

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
            if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
                return false;
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
                return false;
            if (!response.IsSuccessStatusCode)
            {
                WriteLocalStatus(null, rows.Length, "Google Drive sync: " + response.StatusCode);
                return false;
            }

            _lastSnapshotFingerprint = fingerprint;
            _lastCredentialFingerprint = credentialFingerprint;
            _lastSuccessfulUploadUtc = DateTime.UtcNow;
            WriteLocalStatus(_lastSuccessfulUploadUtc, rows.Length, string.Empty);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
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
    // Fingerprints represent SHA-256 digests; actual credentials are never persisted in this cache.
    internal static bool CanReuseRecentUpload(
        string lastPayload, string payload, string lastCredential, string credential,
        DateTime lastUploadUtc, DateTime nowUtc)
    {
        TimeSpan age = nowUtc - lastUploadUtc;
        return age >= TimeSpan.Zero && age < UploadDedupWindow &&
               string.Equals(lastPayload, payload, StringComparison.Ordinal) &&
               string.Equals(lastCredential, credential, StringComparison.Ordinal);
    }
    public static void SyncBestEffort(
        AiSystemRuntimeConfig config,
        IEnumerable<AiSystemRuntimeItemState> items)
    {
        if (!config.CloudSyncEnabled || !GoogleDriveOAuthService.ReadStatus().SignedIn)
            return;

        // Keep the newest immutable snapshot instead of queueing an unbounded
        // number of Task.Run uploads while Google Drive is slow or offline.
        BackgroundUploads.Enqueue(new PendingCloudUpload(
            config, items.ToArray(), GoogleDriveOAuthService.ReadSessionEpoch()));
    }

    // A cloud request can outlive the Google account it was authorized for.
    // Every async boundary before local restore or remote upload must verify
    // that logout did not invalidate the original authorization generation.
    private static bool IsCloudAuthEpochCurrent(long captured, long current) =>
        captured == current;

    internal static async Task<string> RunCloudAuthEpochSmokeAsync()
    {
        if (!IsCloudAuthEpochCurrent(7, 7))
            throw new InvalidOperationException("Current Google session was rejected.");
        if (IsCloudAuthEpochCurrent(7, 8))
            throw new InvalidOperationException(
                "An in-flight cloud restore could import into local settings after Google sign-out.");
        if (IsCloudAuthEpochCurrent(8, 7))
            throw new InvalidOperationException(
                "An older cloud upload might be accepted by a different Google auth epoch.");

        // Drive returns only after the user has signed out. No Google account,
        // credential, local state file or HTTP connection is used by this test.
        var networkWait = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int epoch = 7;
        int localCommits = 0;
        int cloudUploads = 0;
        async Task SimulatedRestore()
        {
            int captured = epoch;
            await networkWait.Task.ConfigureAwait(false);
            if (IsCloudAuthEpochCurrent(captured, epoch))
                Interlocked.Increment(ref localCommits);
        }
        Task staleRestore = SimulatedRestore();
        int pendingUploadEpoch = epoch;
        epoch = 8; // Synthetic logout, NOT GoogleDriveOAuthService.SignOut().
        networkWait.TrySetResult(true);
        await staleRestore.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (IsCloudAuthEpochCurrent(pendingUploadEpoch, epoch))
            Interlocked.Increment(ref cloudUploads);

        if (localCommits != 0 || cloudUploads != 0)
            throw new InvalidOperationException("Stale Google session committed restored or queued data.");
        return "AI_CLOUD_AUTH_EPOCH_SMOKE_OK sameSessionAllowed=true " +
               "staleRestoreRejected=true staleUploadRejected=true " +
               "asyncLogoutFence=true networkCalls=0 userFilesTouched=0";
    }

    public static async Task<int> RestoreNowAsync(CancellationToken token = default)
    {
        long authEpoch = GoogleDriveOAuthService.ReadSessionEpoch();
        string? accessToken = await GoogleDriveOAuthService.GetAccessTokenAsync(token).ConfigureAwait(false);
        if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()) ||
            string.IsNullOrWhiteSpace(accessToken))
            return 0;

        string? fileId = await FindFileIdAsync(accessToken, token).ConfigureAwait(false);
        if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()) ||
            string.IsNullOrWhiteSpace(fileId))
            return 0;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
            return 0;
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            !IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
            return 0;

        string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        AiCloudProfileEnvelope? profile = JsonSerializer.Deserialize<AiCloudProfileEnvelope>(json);
        if (profile == null || profile.SchemaVersion > SchemaVersion)
            return 0;
        AiSystemRuntimeConfig safeConfig = AiCloudProfilePolicy.SanitizeConfig(profile.Config);
        AiSystemRuntimeItemState[] safeItems = AiCloudProfilePolicy.SanitizeItems(profile.Items);
        // Never import cloud preferences after a completed Google sign-out.
        if (!IsCloudAuthEpochCurrent(authEpoch, GoogleDriveOAuthService.ReadSessionEpoch()))
            return 0;
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

        using HttpContent content = JsonContent.Create(new { name = FileName, parents = new[] { aiId } });
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

        using HttpContent content = JsonContent.Create(metadata);
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

                // On a changed local status, reuse the established UTF-8
                // snapshot decoder (including legacy Unicode BOM support)
                // without materializing an intermediate JSON string.
                CachedLocalStatus = writeUtc != DateTime.MinValue
                    ? ObservedJsonSnapshotCache<AiCloudLocalStatus>.ReadSnapshotFromFile(StatusPath)
                      ?? new(null, 0, string.Empty)
                    : new(null, 0, string.Empty);
                CachedLocalStatusWriteUtc = writeUtc;
                return CachedLocalStatus;
            }
            catch (IOException)
            {
                return CachedLocalStatus ?? new(null, 0, string.Empty);
            }
            catch (UnauthorizedAccessException)
            {
                return CachedLocalStatus ?? new(null, 0, string.Empty);
            }
            catch (JsonException)
            {
                return CachedLocalStatus ?? new(null, 0, string.Empty);
            }
            catch (System.Security.SecurityException)
            {
                return CachedLocalStatus ?? new(null, 0, string.Empty);
            }
        }
    }

    private static void WriteLocalStatus(DateTime? syncUtc, int itemCount, string error)
    {
        lock (StatusGate)
        {
            try
            {
                Directory.CreateDirectory(StateDir);
                var status = new AiCloudLocalStatus(syncUtc, itemCount, error);
                AtomicFileService.WriteText(StatusPath, JsonSerializer.Serialize(status));
                CachedLocalStatus = status;
                CachedLocalStatusWriteUtc = File.GetLastWriteTimeUtc(StatusPath);
            }
            catch (IOException ex)
            {
                LogService.Error("ai-cloud", "Cloud status persistence failed", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogService.Error("ai-cloud", "Cloud status persistence denied", ex);
            }
            catch (System.Security.SecurityException ex)
            {
                LogService.Error("ai-cloud", "Cloud status persistence denied", ex);
            }
        }
    }
}
