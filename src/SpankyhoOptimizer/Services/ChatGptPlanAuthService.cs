using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record ChatGptPlanCredential(
    string Email,
    string Subject,
    string ClientId,
    string ExtAgentHostId,
    string IdToken,
    string AccessToken,
    string RefreshToken,
    string TokenType,
    int ExpiresIn,
    string Scope,
    DateTime SavedAtUtc);

public static class ChatGptPlanAuthService
{
    private const string AuthorizeUrl = "https://auth.openai.com/api/accounts/authorize";
    private const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";
    private const string Resource = "https://api.openai.com/v1";
    private const string InitialClientId = "dynamic_agent_client";
    private const string AgentName = "Spankyho Optimizer";
    private const string RequiredScope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer");
    private static readonly string CredentialPath = Path.Combine(Root, "chatgpt-plan.json");
    private static readonly string HostIdPath = Path.Combine(Root, "chatgpt-host-id.txt");

    public static bool IsConnected
    {
        get
        {
            ChatGptPlanCredential? c = Load();
            return c != null && HasPlanScope(c.Scope);
        }
    }

    public static ChatGptPlanCredential? Load()
    {
        try
        {
            if (!File.Exists(CredentialPath)) return null;
            string stored = File.ReadAllText(CredentialPath).Trim();
            byte[] encrypted = Convert.FromBase64String(stored);
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<ChatGptPlanCredential>(Encoding.UTF8.GetString(plain));
        }
        catch { return null; }
    }

    public static async Task<(bool Ok, string Message)> SignInAsync(CancellationToken token = default)
    {
        string hostId = GetOrCreateHostId();
        ChatGptPlanCredential? existing = Load();
        string requestedClientId = string.IsNullOrWhiteSpace(existing?.ClientId) ? InitialClientId : existing.ClientId;
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(24));
        string nonce = Base64Url(RandomNumberGenerator.GetBytes(24));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirectUri = $"http://127.0.0.1:{port}/auth/callback";

        var q = new Dictionary<string, string>
        {
            ["client_id"] = requestedClientId,
            ["ext_agent_host_id"] = hostId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = RequiredScope,
            ["resource"] = Resource,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge
        };
        if (requestedClientId == InitialClientId)
            q["agent_name_hint"] = AgentName;
        else
        {
            if (!string.IsNullOrWhiteSpace(existing?.IdToken)) q["id_token_hint"] = existing.IdToken;
            if (!string.IsNullOrWhiteSpace(existing?.Email)) q["login_hint"] = existing.Email;
        }

        string authorize = AuthorizeUrl + "?" + BuildQuery(q);
        if (!ProcessRunner.StartDetached(authorize))
            return (false, "Nepodařilo se otevřít ChatGPT přihlášení v prohlížeči.");

        using TcpClient client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        string request = await ReadHttpRequestAsync(stream, token).ConfigureAwait(false);
        string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
        string target = firstLine.Split(' ').Length >= 2 ? firstLine.Split(' ')[1] : "/";
        var callback = ParseQuery(target);

        await WriteBrowserReplyAsync(stream, "Přihlášení dokončeno. Toto okno můžeš zavřít.", token).ConfigureAwait(false);

        if (!callback.TryGetValue("state", out string? returnedState) || !FixedEquals(state, returnedState))
            return (false, "ChatGPT přihlášení selhalo: neplatný state.");
        if (callback.TryGetValue("error", out string? oauthError))
            return (false, "ChatGPT přihlášení: " + oauthError);
        if (!callback.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
            return (false, "ChatGPT nepředal autorizační kód.");

        string issuedClientId = requestedClientId;
        if (requestedClientId == InitialClientId)
        {
            if (!callback.TryGetValue("client_id", out issuedClientId) || string.IsNullOrWhiteSpace(issuedClientId))
                return (false, "ChatGPT registrace nevrátila client ID.");
        }
        else if (callback.TryGetValue("client_id", out string? callbackClientId) &&
                 !string.IsNullOrWhiteSpace(callbackClientId) &&
                 !string.Equals(callbackClientId, requestedClientId, StringComparison.Ordinal))
        {
            return (false, "ChatGPT vrátil jiné client ID než očekávané.");
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = issuedClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["resource"] = Resource
        });
        using HttpResponseMessage response = await Http.PostAsync(TokenUrl, form, token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (false, "Token exchange selhal: " + response.StatusCode);

        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        string access = root.GetProperty("access_token").GetString() ?? string.Empty;
        string refresh = root.TryGetProperty("refresh_token", out JsonElement r) ? r.GetString() ?? string.Empty : string.Empty;
        string idToken = root.TryGetProperty("id_token", out JsonElement i) ? i.GetString() ?? string.Empty : string.Empty;
        string scope = root.TryGetProperty("scope", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
        int expires = root.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt32(out int seconds) ? seconds : 3600;
        if (!HasPlanScope(scope))
            return (false, "Přihlášení proběhlo, ale nebylo povoleno použití ChatGPT plánu.");

        (bool valid, string email, string subject, string error) = await ValidateIdTokenAsync(idToken, issuedClientId, nonce, token).ConfigureAwait(false);
        if (!valid)
            return (false, "ID token není platný: " + error);
        if (existing != null && !string.IsNullOrWhiteSpace(existing.Subject) &&
            !string.Equals(existing.Subject, subject, StringComparison.Ordinal))
            return (false, "Přihlášení vrátilo jiný ChatGPT účet než uložené připojení.");

        var credential = new ChatGptPlanCredential(
            email, subject, issuedClientId, hostId, idToken, access, refresh,
            "Bearer", expires, scope, DateTime.UtcNow);
        Save(credential);
        return (true, string.IsNullOrWhiteSpace(email) ? "ChatGPT plán je připojený." : $"ChatGPT plán je připojený: {email}");
    }

    public static async Task<string?> GetValidAccessTokenAsync(CancellationToken token = default)
    {
        ChatGptPlanCredential? c = Load();
        if (c == null || !HasPlanScope(c.Scope)) return null;
        if (DateTime.UtcNow < c.SavedAtUtc.AddSeconds(Math.Max(60, c.ExpiresIn - 120)))
            return c.AccessToken;
        if (string.IsNullOrWhiteSpace(c.RefreshToken)) return null;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = c.ClientId,
            ["refresh_token"] = c.RefreshToken,
            ["resource"] = Resource
        });
        using HttpResponseMessage response = await Http.PostAsync(TokenUrl, form, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        string access = root.GetProperty("access_token").GetString() ?? string.Empty;
        string refresh = root.TryGetProperty("refresh_token", out JsonElement r) ? r.GetString() ?? c.RefreshToken : c.RefreshToken;
        string idToken = c.IdToken;
        string scope = root.TryGetProperty("scope", out JsonElement s) ? s.GetString() ?? c.Scope : c.Scope;
        int expires = root.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt32(out int seconds) ? seconds : 3600;
        if (!HasPlanScope(scope)) return null;
        c = c with { AccessToken = access, RefreshToken = refresh, IdToken = idToken, Scope = scope, ExpiresIn = expires, SavedAtUtc = DateTime.UtcNow };
        Save(c);
        return access;
    }

    public static void SignOut()
    {
        try { if (File.Exists(CredentialPath)) File.Delete(CredentialPath); } catch { }
    }

    private static void Save(ChatGptPlanCredential credential)
    {
        Directory.CreateDirectory(Root);
        byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credential));
        byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        string temp = CredentialPath + ".tmp";
        File.WriteAllText(temp, Convert.ToBase64String(encrypted));
        File.Move(temp, CredentialPath, true);
    }

    private static string GetOrCreateHostId()
    {
        Directory.CreateDirectory(Root);
        try
        {
            if (File.Exists(HostIdPath))
            {
                string existing = File.ReadAllText(HostIdPath).Trim();
                if (existing.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) return existing;
            }
        }
        catch { }
        string created = "urn:uuid:" + Guid.NewGuid().ToString();
        File.WriteAllText(HostIdPath, created);
        return created;
    }

    private static bool HasPlanScope(string scope) =>
        scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal);

    private static async Task<(bool Valid, string Email, string Subject, string Error)> ValidateIdTokenAsync(
        string tokenValue, string clientId, string expectedNonce, CancellationToken token)
    {
        try
        {
            string[] parts = tokenValue.Split('.');
            if (parts.Length != 3) return (false, "", "", "JWT má neplatný formát.");
            using JsonDocument header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            using JsonDocument payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            string kid = header.RootElement.GetProperty("kid").GetString() ?? string.Empty;
            string alg = header.RootElement.GetProperty("alg").GetString() ?? string.Empty;
            if (alg != "RS256") return (false, "", "", "Nepodporovaný podpis " + alg);

            string discovery = await Http.GetStringAsync("https://auth.openai.com/.well-known/openid-configuration", token).ConfigureAwait(false);
            using JsonDocument disc = JsonDocument.Parse(discovery);
            string issuer = disc.RootElement.GetProperty("issuer").GetString() ?? string.Empty;
            string jwksUri = disc.RootElement.GetProperty("jwks_uri").GetString() ?? string.Empty;
            string jwks = await Http.GetStringAsync(jwksUri, token).ConfigureAwait(false);
            using JsonDocument keys = JsonDocument.Parse(jwks);
            JsonElement jwk = default;
            bool found = false;
            foreach (JsonElement key in keys.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (string.Equals(key.GetProperty("kid").GetString(), kid, StringComparison.Ordinal))
                {
                    jwk = key;
                    found = true;
                    break;
                }
            }
            if (!found) return (false, "", "", "Podpisový klíč nebyl nalezen.");

            using RSA rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64UrlDecode(jwk.GetProperty("n").GetString() ?? string.Empty),
                Exponent = Base64UrlDecode(jwk.GetProperty("e").GetString() ?? string.Empty)
            });
            byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            byte[] signature = Base64UrlDecode(parts[2]);
            if (!rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return (false, "", "", "Podpis ID tokenu nesouhlasí.");

            JsonElement p = payload.RootElement;
            if (!string.Equals(p.GetProperty("iss").GetString(), issuer, StringComparison.Ordinal))
                return (false, "", "", "Issuer nesouhlasí.");
            if (!AudienceMatches(p.GetProperty("aud"), clientId))
                return (false, "", "", "Audience nesouhlasí.");
            long exp = p.GetProperty("exp").GetInt64();
            if (DateTimeOffset.FromUnixTimeSeconds(exp) <= DateTimeOffset.UtcNow)
                return (false, "", "", "ID token vypršel.");
            string nonce = p.TryGetProperty("nonce", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
            if (!FixedEquals(expectedNonce, nonce))
                return (false, "", "", "Nonce nesouhlasí.");

            string sub = p.GetProperty("sub").GetString() ?? string.Empty;
            string email = p.TryGetProperty("email", out JsonElement em) ? em.GetString() ?? string.Empty : string.Empty;
            return (!string.IsNullOrWhiteSpace(sub), email, sub, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, "", "", ex.Message);
        }
    }

    private static bool AudienceMatches(JsonElement aud, string clientId)
    {
        if (aud.ValueKind == JsonValueKind.String)
            return string.Equals(aud.GetString(), clientId, StringComparison.Ordinal);
        if (aud.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in aud.EnumerateArray())
                if (string.Equals(item.GetString(), clientId, StringComparison.Ordinal)) return true;
        return false;
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static async Task WriteBrowserReplyAsync(NetworkStream stream, string message, CancellationToken token)
    {
        string html = $"<html><body style='font-family:Segoe UI;background:#111;color:#eee;padding:40px'><h2>{WebUtility.HtmlEncode(message)}</h2><p>Můžeš se vrátit do Spankyho Optimizeru.</p></body></html>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        string headers = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int q = target.IndexOf('?');
        if (q < 0 || q == target.Length - 1) return result;
        foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = WebUtility.UrlDecode(parts[0]);
            string value = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
            result[key] = value;
        }
        return result;
    }

    private static string BuildQuery(Dictionary<string, string> values) =>
        string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string s = value.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }

    private static bool FixedEquals(string a, string b)
    {
        byte[] aa = Encoding.UTF8.GetBytes(a ?? string.Empty);
        byte[] bb = Encoding.UTF8.GetBytes(b ?? string.Empty);
        return aa.Length == bb.Length && CryptographicOperations.FixedTimeEquals(aa, bb);
    }
}
