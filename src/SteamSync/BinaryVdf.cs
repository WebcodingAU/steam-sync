using System.Buffers.Binary;
using System.Text;

namespace SteamSync;

/// <summary>
/// Minimal reader for Valve's binary KeyValues format (userdata\&lt;id&gt;\config\shortcuts.vdf).
/// Values come back as string, int, float, ulong, long or a nested <see cref="VdfObject"/>; keys are case-insensitive.
/// </summary>
internal static class BinaryVdf
{
    private const byte TypeMap = 0x00, TypeString = 0x01, TypeInt32 = 0x02, TypeFloat32 = 0x03, TypeUInt64 = 0x07, TypeEnd = 0x08, TypeInt64 = 0x0A;

    // shortcuts.vdf nests only root > shortcuts > entry > tags. Without a cap, a corrupt file (e.g. a run of NULs
    // after an unclean shutdown) recurses once per two bytes and overflows the stack, which no catch can stop.
    private const int MaxDepth = 16;

    public static VdfObject Parse(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        return ReadMap(data, ref pos, depth: 0);
    }

    public static VdfObject ParseFile(string path)
    {
        // Steam rewrites the file whenever a shortcut changes; share access and retry once on a transient lock.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            }
            catch (IOException) when (attempt == 0)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static VdfObject ReadMap(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (depth > MaxDepth) throw new FormatException($"Binary VDF nests deeper than {MaxDepth} levels at offset {pos}.");

        var obj = new VdfObject();
        while (true)
        {
            if (pos >= data.Length)
            {
                if (depth == 0) return obj;
                throw new FormatException("Unexpected end of binary VDF input.");
            }

            var type = data[pos++];
            if (type == TypeEnd) return obj;

            var key = ReadCString(data, ref pos);
            obj[key] = type switch
            {
                TypeMap => (object)ReadMap(data, ref pos, depth + 1),
                TypeString => ReadCString(data, ref pos),
                TypeInt32 => BinaryPrimitives.ReadInt32LittleEndian(Take(data, ref pos, 4)),
                TypeFloat32 => BinaryPrimitives.ReadSingleLittleEndian(Take(data, ref pos, 4)),
                TypeUInt64 => BinaryPrimitives.ReadUInt64LittleEndian(Take(data, ref pos, 8)),
                TypeInt64 => BinaryPrimitives.ReadInt64LittleEndian(Take(data, ref pos, 8)),
                _ => throw new FormatException($"Unknown binary VDF type 0x{type:X2} for key '{key}' at offset {pos}."),
            };
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int pos)
    {
        var length = data[pos..].IndexOf((byte)0);
        if (length < 0) throw new FormatException("Unterminated string in binary VDF input.");
        var s = Encoding.UTF8.GetString(data.Slice(pos, length));
        pos += length + 1;
        return s;
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        if (pos + count > data.Length) throw new FormatException("Unexpected end of binary VDF input.");
        var slice = data.Slice(pos, count);
        pos += count;
        return slice;
    }
}
