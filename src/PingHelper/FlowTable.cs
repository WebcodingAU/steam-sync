using System.Buffers;
using System.Net;
using System.Text.Json;

namespace PingHelper;

/// <summary>One conversation: a process, TCP or UDP, a remote address and port, and the local port.
/// The address is IPv6 (IPv4 as ::ffff:a.b.c.d) in two halves, so the key never allocates.</summary>
internal readonly record struct FlowKey(uint Pid, bool Udp, ulong AddrHi, ulong AddrLo, ushort RemotePort, ushort LocalPort);

/// <summary>
/// Packet and byte counts per flow for the current second. The ETW thread adds to it; once a second the pipe server
/// takes the counts (swapping in an empty table) and turns them into one JSON line.
/// </summary>
internal sealed class FlowTable
{
    private sealed class Counts { public uint PacketsOut, PacketsIn; public ulong BytesOut, BytesIn; }

    private const int MaxFlows = 20_000; // a runaway (port scan, torrent) can't grow the table without bound
    private readonly object _gate = new();
    private Dictionary<FlowKey, Counts> _current = [];
    private Dictionary<FlowKey, Counts> _spare = [];
    private long _dropped;

    public void Add(FlowKey key, bool send, uint size)
    {
        lock (_gate)
        {
            if (!_current.TryGetValue(key, out var c))
            {
                if (_current.Count >= MaxFlows) { _dropped++; return; }
                _current[key] = c = new Counts();
            }
            if (send) { c.PacketsOut++; c.BytesOut += size; }
            else { c.PacketsIn++; c.BytesIn += size; }
        }
    }

    /// <summary>The last second's flows as one line of JSON (no newline), and a fresh table for the next second:
    /// {"v":1,"helper":"1.0.0","t":unix ms,"dropped":n,"flows":[{"pid","p":"udp"|"tcp","ip","rp","lp","po","pi","bo","bi"}]}</summary>
    public byte[] TakeSnapshot(string version)
    {
        Dictionary<FlowKey, Counts> taken;
        long dropped;
        lock (_gate)
        {
            taken = _current;
            _current = _spare;
            _spare = taken;
            dropped = _dropped;
            _dropped = 0;
        }

        var buffer = new ArrayBufferWriter<byte>(4096 + taken.Count * 120);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", 1);
            json.WriteString("helper", version);
            json.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            json.WriteNumber("dropped", dropped);
            json.WriteStartArray("flows");
            foreach (var (k, c) in taken)
            {
                json.WriteStartObject();
                json.WriteNumber("pid", k.Pid);
                json.WriteString("p", k.Udp ? "udp" : "tcp");
                json.WriteString("ip", FormatAddress(k.AddrHi, k.AddrLo));
                json.WriteNumber("rp", k.RemotePort);
                json.WriteNumber("lp", k.LocalPort);
                json.WriteNumber("po", c.PacketsOut);
                json.WriteNumber("pi", c.PacketsIn);
                json.WriteNumber("bo", c.BytesOut);
                json.WriteNumber("bi", c.BytesIn);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        // Only touched by this (the pipe server's) thread from here on, until the next swap hands it back to Add.
        lock (_gate) taken.Clear();
        return buffer.WrittenSpan.ToArray();
    }

    private static string FormatAddress(ulong hi, ulong lo)
    {
        if (hi == 0 && (lo >> 32) == 0xFFFF)
        {
            var v4 = (uint)lo;
            return $"{v4 >> 24}.{(v4 >> 16) & 0xFF}.{(v4 >> 8) & 0xFF}.{v4 & 0xFF}";
        }
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < 8; i++)
        {
            bytes[i] = (byte)(hi >> (56 - i * 8));
            bytes[8 + i] = (byte)(lo >> (56 - i * 8));
        }
        return new IPAddress(bytes).ToString();
    }
}
