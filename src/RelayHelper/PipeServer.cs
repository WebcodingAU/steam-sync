using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace RelayHelper;

/// <summary>
/// \\.\pipe\GamingEnjoyer.Relay: the tray app sends one line of JSON per request and gets one line back.
/// <list type="bullet">
/// <item><c>{"cmd":"status"}</c></item>
/// <item><c>{"cmd":"up","ranges":["66.40.178.0/24", ...]}</c>: route exactly these ranges through the relay</item>
/// <item><c>{"cmd":"down"}</c></item>
/// </list>
/// Every reply: ok, error, version, up, routes, handshake_age_s, rx, tx, relay, endpoint. Open to people signed in at
/// this PC (INTERACTIVE; the tray app runs as the signed-in user), closed to the network. Ranges are checked here:
/// public IPv4 only, /8 or narrower, never the relay's own address (that would loop the tunnel into itself).
/// </summary>
internal sealed class PipeServer : IDisposable
{
    public const string PipeName = "GamingEnjoyer.Relay";
    private const int MaxClients = 4;

    private readonly Tunnel _tunnel;
    private readonly RelayConfig _config;
    private readonly string _version;
    private readonly CancellationTokenSource _stop = new();
    private int _clients;

    public PipeServer(Tunnel tunnel, string version)
    {
        _tunnel = tunnel;
        _config = RelayConfig.Parse(File.ReadAllText(Program.ConfigFile));
        _version = version;
    }

    public void Start() => _ = Task.Run(() => AcceptLoop(_stop.Token));

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        // Named pipes are reachable over SMB too: never from another machine.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                if (Volatile.Read(ref _clients) >= MaxClients)
                {
                    await Task.Delay(500, ct);
                    continue;
                }
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, Security());
                await pipe.WaitForConnectionAsync(ct);
                Interlocked.Increment(ref _clients);
                var client = pipe;
                pipe = null;
                _ = Task.Run(() => Serve(client, ct));
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Write($"Pipe accept failed: {ex.Message}");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            {
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length > 256 * 1024) break;
                    await writer.WriteLineAsync(Handle(line).ToJsonString());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { Interlocked.Decrement(ref _clients); }
    }

    private JsonObject Handle(string line)
    {
        string? error = null;
        try
        {
            var request = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("not a JSON object");
            switch ((string?)request["cmd"])
            {
                case "status":
                    break;
                case "up":
                    var ranges = (request["ranges"] as JsonArray ?? []).Select(r => (string?)r ?? "").ToList();
                    if (ranges.Count > Tunnel.MaxRanges) throw new FormatException($"too many ranges (most {Tunnel.MaxRanges})");
                    var bad = ranges.Where(r => !IsAllowed(r)).ToList();
                    if (bad.Count > 0) throw new FormatException($"not allowed: {string.Join(", ", bad.Take(5))}");
                    error = _tunnel.Up(ranges.Select(Normalise).Distinct().ToList());
                    break;
                case "down":
                    _tunnel.Down();
                    break;
                default:
                    throw new FormatException("unknown cmd");
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            error = ex.Message;
        }

        var (up, routes, handshake, rx, tx) = _tunnel.Status();
        return new JsonObject
        {
            ["ok"] = error is null,
            ["error"] = error,
            ["version"] = _version,
            ["up"] = up,
            ["routes"] = routes,
            ["handshake_age_s"] = handshake is { } h ? (long)(DateTimeOffset.UtcNow - h).TotalSeconds : null,
            ["rx"] = rx,
            ["tx"] = tx,
            ["relay"] = _config.RelayAddress.ToString(),
            ["endpoint"] = _config.EndpointText,
        };
    }

    /// <summary>A public IPv4 range, /8 to /32, that doesn't contain the relay's own address.</summary>
    private bool IsAllowed(string range)
    {
        var slash = range.IndexOf('/');
        if (slash <= 0 || !IPAddress.TryParse(range[..slash], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(range[(slash + 1)..], out var bits) || bits is < 8 or > 32) return false;
        var b = ip.GetAddressBytes();
        if (b[0] is 0 or 10 or 127 or >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127)) return false;
        var mask = bits == 32 ? uint.MaxValue : ~(uint.MaxValue >> bits);
        return (ToUInt(_config.Endpoint.Address) & mask) != (ToUInt(ip) & mask);
    }

    /// <summary>The range with its host bits cleared (66.40.178.45/24 → 66.40.178.0/24), as routes need it.</summary>
    private static string Normalise(string range)
    {
        var slash = range.IndexOf('/');
        var bits = int.Parse(range[(slash + 1)..]);
        var mask = bits == 32 ? uint.MaxValue : ~(uint.MaxValue >> bits);
        var n = ToUInt(IPAddress.Parse(range[..slash])) & mask;
        return $"{n >> 24}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}/{bits}";
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public void Dispose() => _stop.Cancel();
}
