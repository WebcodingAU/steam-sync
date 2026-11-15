using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SteamSync;

/// <summary>
/// Signing in to Spotify for the Spotify player (SpotifyWindow): Spotify's authorization code flow with PKCE, which
/// needs only the app's public Client ID (SpotifyClientId in appsettings.json; no client secret ships with the app).
/// <para>
/// Sign-in happens in the default browser, where the user may already be signed in to Spotify. Spotify sends it back to
/// a one-off listener on http://127.0.0.1:43819/spotify/callback, which must be the Spotify app's redirect URI exactly
/// (Spotify's docs allow a loopback URI without a port, but its dashboard rejects one as "not secure"). The refresh token is
/// kept encrypted with DPAPI (only this Windows user can read it) in spotify.bin; access tokens stay in memory and are
/// refreshed a minute before they expire. Spotify may hand out a new refresh token with each refresh, which replaces
/// the stored one.
/// </para>
/// </summary>
internal sealed class SpotifyAuth
{
    public const string Scopes = "streaming user-read-email user-read-private user-read-playback-state user-modify-playback-state "
        + "user-read-currently-playing playlist-read-private playlist-read-collaborative user-library-read user-library-modify "
        + "user-read-recently-played user-top-read";

    public const int CallbackPort = 43819;
    public static readonly string RedirectUri = $"http://127.0.0.1:{CallbackPort}/spotify/callback";
    private static readonly string TokenFile = Path.Combine(AppInfo.DataDir, "spotify.bin");
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _clientId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expires;

    public SpotifyAuth(string clientId) => _clientId = clientId;

    /// <summary>A refresh token is stored: the user has signed in (it may still have been revoked since).</summary>
    public static bool HasStoredSignIn => File.Exists(TokenFile);

    /// <summary>A valid access token, refreshed when it's within a minute of expiring, with its expiry. Null when the
    /// user isn't signed in or Spotify refused the refresh (then the stored sign-in is forgotten).</summary>
    public async Task<(string Token, DateTimeOffset Expires)?> GetTokenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expires - TimeSpan.FromMinutes(1)) return (_accessToken, _expires);
            if (LoadRefreshToken() is not { } refresh) return null;
            var reply = await PostTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = _clientId });
            if (reply is null) return null;
            return Accept(reply);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Signs in through the default browser. Null when it worked, else what went wrong.</summary>
    public async Task<string?> SignInAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            Log.Error($"Spotify sign-in: port {CallbackPort} is in use", ex);
            return $"Spotify sign-in needs port {CallbackPort} on this PC, and another program is using it. Close it and try again.";
        }
        try
        {
            var redirect = RedirectUri;
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));
            var url = "https://accounts.spotify.com/authorize?" + Query(new()
            {
                ["client_id"] = _clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = redirect,
                ["code_challenge_method"] = "S256",
                ["code_challenge"] = challenge,
                ["state"] = state,
                ["scope"] = Scopes,
            });
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = new CancellationTokenSource(SignInTimeout);
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(timeout.Token); }
                catch (OperationCanceledException) { return "Spotify sign-in timed out. Try again."; }

                using (client)
                {
                    var (path, query) = await ReadRequestAsync(client, timeout.Token);
                    if (path != "/spotify/callback")
                    {
                        await RespondAsync(client, 404, "Not found");
                        continue; // e.g. the browser asking for a favicon
                    }
                    if (query.GetValueOrDefault("state") != state)
                    {
                        await RespondAsync(client, 400, "This sign-in link doesn't belong to the current Spotify sign-in. Go back to Gaming Enjoyer and try again.");
                        continue;
                    }
                    if (query.GetValueOrDefault("error") is { } error)
                    {
                        await RespondAsync(client, 200, "Spotify sign-in was cancelled. You can close this tab.");
                        return error == "access_denied" ? "" : $"Spotify said: {error}";
                    }
                    if (query.GetValueOrDefault("code") is not { } code)
                    {
                        await RespondAsync(client, 400, "Spotify didn't send a sign-in code.");
                        return "Spotify didn't send a sign-in code.";
                    }

                    var reply = await PostTokenAsync(new()
                    {
                        ["grant_type"] = "authorization_code",
                        ["code"] = code,
                        ["redirect_uri"] = redirect,
                        ["client_id"] = _clientId,
                        ["code_verifier"] = verifier,
                    });
                    if (reply is null || (string?)reply["refresh_token"] is null)
                    {
                        await RespondAsync(client, 500, "Spotify sign-in didn't finish. Go back to Gaming Enjoyer and try again.");
                        return $"Spotify didn't accept the sign-in. Check that the Spotify app's redirect URI is {RedirectUri}.";
                    }
                    await _gate.WaitAsync();
                    try { Accept(reply); } finally { _gate.Release(); }
                    await RespondAsync(client, 200, "Signed in to Spotify. You can close this tab and go back to Gaming Enjoyer.");
                    Log.Info("Spotify: signed in");
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Spotify sign-in failed", ex);
            return $"Spotify sign-in failed: {ex.Message}";
        }
        finally { listener.Stop(); }
    }

    public void SignOut()
    {
        _accessToken = null;
        try { File.Delete(TokenFile); } catch (IOException) { }
        Log.Info("Spotify: signed out");
    }

    private (string, DateTimeOffset) Accept(JsonObject reply)
    {
        _accessToken = (string)reply["access_token"]!;
        _expires = DateTimeOffset.UtcNow + TimeSpan.FromSeconds((int?)reply["expires_in"] ?? 3600);
        if ((string?)reply["refresh_token"] is { } refresh) SaveRefreshToken(refresh);
        return (_accessToken, _expires);
    }

    /// <summary>Spotify's token endpoint. Null when it refused (a revoked or expired sign-in is then forgotten) or
    /// couldn't be reached.</summary>
    private async Task<JsonObject?> PostTokenAsync(Dictionary<string, string> form)
    {
        try
        {
            using var response = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) return JsonNode.Parse(body) as JsonObject;
            var error = (JsonNode.Parse(body) as JsonObject)?["error"]?.ToString();
            Log.Info($"Spotify: token request refused ({(int)response.StatusCode} {error})");
            if (error == "invalid_grant" && form["grant_type"] == "refresh_token") SignOut();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Error("Spotify: token request failed", ex);
            return null;
        }
    }

    private static string? LoadRefreshToken()
    {
        try
        {
            return File.Exists(TokenFile)
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            Log.Error("Spotify: the stored sign-in couldn't be read", ex);
            return null;
        }
    }

    private static void SaveRefreshToken(string token) =>
        File.WriteAllBytes(TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    /// <summary>Reads the browser's request line ("GET /spotify/callback?code=... HTTP/1.1") and returns its path and
    /// query. Headers aren't needed.</summary>
    private static async Task<(string Path, Dictionary<string, string> Query)> ReadRequestAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n")) break;
        }
        var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0];
        var target = line.Split(' ') is [_, var t, ..] ? t : "/";
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (question >= 0)
        {
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }
        return (path, query);
    }

    private static async Task RespondAsync(TcpClient client, int status, string message)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>Gaming Enjoyer</title>"
            + "<body style=\"background:#0a0c11;color:#e8eaf0;font:16px system-ui;display:grid;place-items:center;height:90vh\">"
            + $"<p>{WebUtility.HtmlEncode(message)}</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    private static string Query(Dictionary<string, string> values) =>
        string.Join('&', values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
