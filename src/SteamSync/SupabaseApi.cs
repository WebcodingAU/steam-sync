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

    public Task<int> SyncAsync(string token, SteamSnapshot snapshot, CancellationToken ct = default) =>
        CallAsync<int>("sync_device", new
        {
            p_token = token,
            p_steam_id = snapshot.SteamId,
            p_app_version = AppInfo.Version,
            p_games = snapshot.Games,
        }, ct);

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
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    public void Dispose() => _http.Dispose();
}
