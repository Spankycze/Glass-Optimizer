using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record GoogleDriveAuthStatus(
    bool Configured,
    bool SignedIn,
    string Account,
    string LastError);

internal sealed record GoogleTokenState(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresUtc);

public static class GoogleDriveOAuthService
{
    private const string Scope = "https://www.googleapis.com/auth/drive.file openid email";
    private static readonly HttpClient Http = new();
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer");
    private static readonly string ClientIdPath = Path.Combine(StateDir, "google-oauth-client-id.txt");
    private static readonly string TokenPath = Path.Combine(StateDir, "google-oauth-token.bin");
    private static readonly object CacheGate = new();
    private static string? CachedClientId;
    private static GoogleTokenState? CachedToken;
    private static DateTime CachedClientIdWriteUtc = DateTime.MinValue;
    private static DateTime CachedTokenWriteUtc = DateTime.MinValue;
    private static string _lastError = string.Empty;
    private static long _tokenEpoch;

    // Read-only fence for asynchronous cloud operations. SignOut advances this
    // epoch after durable token deletion, invalidating in-flight cloud work.
    internal static long ReadSessionEpoch()
    {
        lock (CacheGate) return _tokenEpoch;
    }

    // Keep durable token mutation under the same lock as logout. A refresh
    // started before logout must never re-create a token after sign-out.
    private static bool TryCommitTokenMutation(long? expectedEpoch, Action commit)
    {
        lock (CacheGate)
        {
            if (expectedEpoch.HasValue && expectedEpoch.Value != _tokenEpoch)
                return false;
            commit();
            return true;
        }
    }

    private static void DeleteTokenAndClearCache(string path, Action clear)
    {
        // A sharing violation/ACL error means sign-out did NOT succeed.
        // Never clear in-memory state while leaving a usable token on disk.
        // File.Exists can hide access errors by returning false, therefore
        // attempt deletion directly and trust only its completion.
        File.Delete(path);
        clear();
    }

    internal static string RunSignOutPersistenceSafetySmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-google-signout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "synthetic-token.dat");
            File.WriteAllText(path, "fake-token-for-test-only");
            bool cleared = false;
            bool deletionRejected = false;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { DeleteTokenAndClearCache(path, () => cleared = true); }
                catch (IOException) { deletionRejected = true; }
            }
            if (!deletionRejected || cleared || !File.Exists(path))
                throw new InvalidOperationException(
                    "Google sign-out falsely reported success despite a locked token file.");

            DeleteTokenAndClearCache(path, () => cleared = true);
            if (!cleared || File.Exists(path))
                throw new InvalidOperationException("Google sign-out did not delete the token first.");

            long before;
            lock (CacheGate) before = _tokenEpoch;
            bool saved = false;
            if (!TryCommitTokenMutation(before, () => saved = true) || !saved)
                throw new InvalidOperationException("Current Google token generation cannot persist.");
            lock (CacheGate) _tokenEpoch++;
            saved = false;
            if (TryCommitTokenMutation(before, () => saved = true) || saved)
                throw new InvalidOperationException("A late token refresh resurrected a signed-out session.");

            return "GOOGLE_SIGNOUT_PERSISTENCE_SMOKE_OK lockedTokenPreserved=true " +
                "successfulDeletion=true staleRefreshRejected=true liveCredentialsTouched=0";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public static string LoadClientId()
    {
        lock (CacheGate)
        {
            try
            {
                DateTime writeUtc = File.Exists(ClientIdPath)
                    ? File.GetLastWriteTimeUtc(ClientIdPath)
                    : DateTime.MinValue;
                if (CachedClientId != null && writeUtc == CachedClientIdWriteUtc)
                    return CachedClientId;

                CachedClientId = writeUtc != DateTime.MinValue
                    ? File.ReadAllText(ClientIdPath).Trim()
                    : string.Empty;
                CachedClientIdWriteUtc = writeUtc;
                return CachedClientId;
            }
            catch
            {
                return CachedClientId ?? string.Empty;
            }
        }
    }

    public static void SaveClientId(string clientId)
    {
        Directory.CreateDirectory(StateDir);
        string normalized = clientId.Trim();
        File.WriteAllText(ClientIdPath, normalized);
        lock (CacheGate)
        {
            CachedClientId = normalized;
            CachedClientIdWriteUtc = File.GetLastWriteTimeUtc(ClientIdPath);
        }
    }

    public static bool SignOut()
    {
        lock (CacheGate)
        {
            try
            {
                DeleteTokenAndClearCache(TokenPath, () =>
                {
                    CachedToken = null;
                    CachedTokenWriteUtc = DateTime.MinValue;
                    _tokenEpoch++;
                });
                _lastError = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _lastError = "Odhlášení Google Drive se nezdařilo; uložený token je stále aktivní.";
                LogService.Error("google-drive", _lastError, ex);
                return false;
            }
        }
    }

    public static GoogleDriveAuthStatus ReadStatus()
    {
        string clientId = LoadClientId();
        GoogleTokenState? token = LoadToken();
        return new(!string.IsNullOrWhiteSpace(clientId), token != null, string.Empty, _lastError);
    }
    public static async Task<bool> SignInAsync(CancellationToken token)
    {
        long signInEpoch;
        lock (CacheGate) signInEpoch = _tokenEpoch;
        string clientId = LoadClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _lastError = "Chybí Google OAuth Client ID.";
            return false;
        }

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(24));
        int port = GetFreeLoopbackPort();
        string redirectUri = $"http://127.0.0.1:{port}/";
        string authUrl =
            "https://accounts.google.com/o/oauth2/v2/auth" +
            "?client_id=" + Uri.EscapeDataString(clientId) +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&response_type=code" +
            "&scope=" + Uri.EscapeDataString(Scope) +
            "&code_challenge=" + Uri.EscapeDataString(challenge) +
            "&code_challenge_method=S256" +
            "&access_type=offline&prompt=consent" +
            "&state=" + Uri.EscapeDataString(state);
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        if (!ProcessRunner.StartDetached(authUrl))
            throw new InvalidOperationException("Nepodařilo se otevřít Google přihlášení v prohlížeči.");

        HttpListenerContext context = await listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false);
        string? code = context.Request.QueryString["code"];
        string? returnedState = context.Request.QueryString["state"];

        byte[] responseBytes = Encoding.UTF8.GetBytes(
            "<html><body style='font-family:Segoe UI;background:#111;color:#fff;padding:40px'>" +
            "<h2>Přihlášení dokončeno</h2><p>Můžeš zavřít toto okno a vrátit se do Spankyho Optimizeru.</p></body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes, token).ConfigureAwait(false);
        context.Response.Close();

        if (string.IsNullOrWhiteSpace(code) || !string.Equals(state, returnedState, StringComparison.Ordinal))
        {
            _lastError = "Google OAuth nevrátil platný autorizační kód.";
            return false;
        }
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        };

        using HttpResponseMessage response = await Http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(form),
            token).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _lastError = "Google token error: " + response.StatusCode;
            return false;
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        string access = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        string refresh = doc.RootElement.TryGetProperty("refresh_token", out JsonElement r)
            ? r.GetString() ?? string.Empty
            : string.Empty;
        int expires = doc.RootElement.TryGetProperty("expires_in", out JsonElement e) ? e.GetInt32() : 3600;
        if (string.IsNullOrWhiteSpace(access))
            return false;

        if (!SaveToken(
                new GoogleTokenState(access, refresh, DateTime.UtcNow.AddSeconds(expires - 60)),
                signInEpoch))
        {
            _lastError = "Google přihlášení bylo přerušeno odhlášením.";
            return false;
        }
        _lastError = string.Empty;
        return true;
    }
    public static async Task<string?> GetAccessTokenAsync(CancellationToken token)
    {
        GoogleTokenState? saved;
        long tokenEpoch;
        lock (CacheGate)
        {
            saved = LoadToken();
            tokenEpoch = _tokenEpoch;
        }
        if (saved == null) return null;
        if (saved.ExpiresUtc > DateTime.UtcNow.AddMinutes(1))
        {
            lock (CacheGate)
                return tokenEpoch == _tokenEpoch ? saved.AccessToken : null;
        }
        if (string.IsNullOrWhiteSpace(saved.RefreshToken))
            return null;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = LoadClientId(),
            ["refresh_token"] = saved.RefreshToken,
            ["grant_type"] = "refresh_token"
        };
        using HttpResponseMessage response = await Http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(form),
            token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        string access = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        int expires = doc.RootElement.TryGetProperty("expires_in", out JsonElement e) ? e.GetInt32() : 3600;
        if (string.IsNullOrWhiteSpace(access)) return null;
        return SaveToken(
            saved with { AccessToken = access, ExpiresUtc = DateTime.UtcNow.AddSeconds(expires - 60) },
            tokenEpoch) ? access : null;
    }
    private static int GetFreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool SaveToken(GoogleTokenState token, long? expectedEpoch = null)
    {
        return TryCommitTokenMutation(expectedEpoch, () =>
        {
            Directory.CreateDirectory(StateDir);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(token));
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            AtomicFileService.WriteAllBytesAtomic(TokenPath, protectedBytes);
            CachedToken = token;
            CachedTokenWriteUtc = File.GetLastWriteTimeUtc(TokenPath);
        });
    }

    private static GoogleTokenState? LoadToken()
    {
        lock (CacheGate)
        {
            try
            {
                DateTime writeUtc = File.Exists(TokenPath)
                    ? File.GetLastWriteTimeUtc(TokenPath)
                    : DateTime.MinValue;
                if (writeUtc == DateTime.MinValue)
                {
                    CachedToken = null;
                    CachedTokenWriteUtc = DateTime.MinValue;
                    return null;
                }
                if (CachedToken != null && writeUtc == CachedTokenWriteUtc)
                    return CachedToken;

                byte[] plain = ProtectedData.Unprotect(
                    File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
                CachedToken = JsonSerializer.Deserialize<GoogleTokenState>(Encoding.UTF8.GetString(plain));
                CachedTokenWriteUtc = writeUtc;
                return CachedToken;
            }
            catch
            {
                return CachedToken;
            }
        }
    }
}
