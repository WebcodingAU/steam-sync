using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamSync;

internal sealed class SupabaseException(string message, string? code, HttpStatusCode status) : Exception(message)
{
    public string? Code { get; } = code;
    public HttpStatusCode Status { get; } = status;

    /// <summary>The server no longer recognises this device's token (e.g. removed on the website).</summary>
    public bool IsUnknownDevice => Code == "28000";

    /// <summary>PostgREST found no function with these argument names (e.g. a migration that isn't applied yet).</summary>
    public bool IsUnknownSignature => Code == "PGRST202";
}

/// <summary>Calls the Postgres RPC functions from supabase/migrations via PostgREST.</summary>
internal sealed class SupabaseApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly HttpClient _http;
    // Set when the server predates p_nonsteam; checked again after an hour so a migration applied later is picked up.
    private DateTimeOffset _skipNonSteamUntil = DateTimeOffset.MinValue;

    public SupabaseApi(string supabaseUrl, string anonKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(supabaseUrl.TrimEnd('/') + "/rest/v1/rpc/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Add("apikey", anonKey);
        _http.DefaultRequestHeaders.Authorization = new("Bearer", anonKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"SteamSync/{AppInfo.Version}");
    }

    public Task<string> ClaimPairingCodeAsync(string code, string deviceName, CancellationToken ct = default) =>
        CallAsync<string>("claim_pairing_code", new { p_code = code, p_device_name = deviceName }, ct);

    /// <summary>
    /// Always sends p_nonsteam; null (detection failed) tells the server to leave non-Steam data alone. A server
    /// without the p_nonsteam migration answers PGRST202: retry without it, and skip it for the next hour.
    /// </summary>
    public async Task<int> SyncAsync(string token, SteamSnapshot snapshot, IReadOnlyList<NonSteamGame>? nonSteam, CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow >= _skipNonSteamUntil)
        {
            try
            {
                return await CallAsync<int>("sync_device", new
                {
                    p_token = token,
                    p_steam_id = snapshot.SteamId,
                    p_app_version = AppInfo.Version,
                    p_games = snapshot.Games,
                    p_nonsteam = nonSteam,
                }, ct);
            }
            catch (SupabaseException ex) when (ex.IsUnknownSignature)
            {
                Log.Info("Server does not accept p_nonsteam yet (PGRST202); syncing Steam games only, checking again in an hour");
                _skipNonSteamUntil = DateTimeOffset.UtcNow.AddHours(1);
            }
        }

        return await CallAsync<int>("sync_device", new
        {
            p_token = token,
            p_steam_id = snapshot.SteamId,
            p_app_version = AppInfo.Version,
            p_games = snapshot.Games,
        }, ct);
    }

    /// <summary>Play sessions (PlayTracker): finished ones not sent yet and every running one. PGRST202 means the
    /// play_sessions migration isn't applied yet.</summary>
    public Task<int> RecordPlaySessionsAsync(string token, IReadOnlyList<PlaySession> sessions, CancellationToken ct = default) =>
        CallAsync<int>("record_play_sessions", new { p_token = token, p_sessions = sessions }, ct);

    private async Task<T> CallAsync<T>(string function, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(function, body, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            string? message = null, code = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
                code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            }
            catch (JsonException) { }
            throw new SupabaseException(message ?? $"HTTP {(int)response.StatusCode}: {text}", code, response.StatusCode);
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return default!; // a function returning void
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    public void Dispose() => _http.Dispose();
}
