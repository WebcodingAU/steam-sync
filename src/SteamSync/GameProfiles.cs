using System.Net;
using System.Reflection;
using System.Text.Json;

namespace SteamSync;

/// <summary>
/// What a game profile says about some of a game's servers: where they really are (Region, for people) and a host in
/// that region whose TCP handshake LivePing times instead of the server (Target, port 443).
/// </summary>
internal sealed record GameProfile(string Name, string[] Keys, string[] Servers, string Region, string Target, string? Note);

/// <summary>
/// Per-game profiles for servers whose ping can't be measured from here (live ping's profile_estimate). AION 2's are
/// behind Cloudflare: everything sent to them is answered by Cloudflare's nearest edge (CdnNetworks), so only knowing
/// where the servers are gives a real figure.
/// <para>
/// A profile matches a game (keys: its session keys, "steam:3393110") and the server addresses it covers (servers:
/// CIDR prefixes, so one region's servers can have a profile without another's). The built-in profiles
/// (game-profiles.json) can be added to or replaced, without an app update, by
/// %LocalAppData%\SteamSync\game-profiles.json in the same format: its profiles are checked first, and it's reread
/// when it changes.
/// </para>
/// </summary>
internal sealed class GameProfiles
{
    private static readonly string UserFile = Path.Combine(AppInfo.DataDir, "game-profiles.json");
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly List<GameProfile> _builtIn = Load(() => Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamSync.game-profiles.json")!, "built-in");
    private List<GameProfile> _user = [];
    private DateTime _userWrittenAt = DateTime.MinValue;

    /// <summary>The profile for this game's server, or null.</summary>
    public GameProfile? Find(string gameKey, IPAddress server)
    {
        if (server.IsIPv4MappedToIPv6) server = server.MapToIPv4();
        RefreshUser();
        return _user.Concat(_builtIn).FirstOrDefault(p =>
            p.Keys.Contains(gameKey, StringComparer.OrdinalIgnoreCase) && p.Servers.Any(s => InPrefix(server, s)));
    }

    private void RefreshUser()
    {
        var at = File.Exists(UserFile) ? File.GetLastWriteTimeUtc(UserFile) : DateTime.MinValue;
        if (at == _userWrittenAt) return;
        _userWrittenAt = at;
        _user = at == DateTime.MinValue ? [] : Load(() => File.OpenRead(UserFile), UserFile);
    }

    private static List<GameProfile> Load(Func<Stream> open, string from)
    {
        try
        {
            using var stream = open();
            var profiles = JsonSerializer.Deserialize<List<GameProfile>>(stream, Options) ?? [];
            // A profile missing what it needs is skipped rather than failing the rest.
            return profiles.Where(p => p is { Keys.Length: > 0, Servers.Length: > 0 } && !string.IsNullOrWhiteSpace(p.Target)
                                       && !string.IsNullOrWhiteSpace(p.Region)).ToList();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read the game profiles ({from})", ex);
            return [];
        }
    }

    /// <summary>Whether the address is in "a.b.c.d/n" (or is "a.b.c.d").</summary>
    private static bool InPrefix(IPAddress ip, string prefix)
    {
        var slash = prefix.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? prefix : prefix[..slash], out var net)) return false;
        var a = ip.GetAddressBytes();
        var n = net.GetAddressBytes();
        if (a.Length != n.Length) return false;
        var bits = slash < 0 ? a.Length * 8 : int.TryParse(prefix[(slash + 1)..], out var b) ? b : -1;
        if (bits < 0 || bits > a.Length * 8) return false;
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }
}
