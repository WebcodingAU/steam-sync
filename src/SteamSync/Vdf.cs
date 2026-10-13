using System.Text;

namespace SteamSync;

/// <summary>A node in a Valve KeyValues (VDF/ACF) text document. Keys are case-insensitive.</summary>
internal sealed class VdfObject : Dictionary<string, object>
{
    public VdfObject() : base(StringComparer.OrdinalIgnoreCase) { }

    public string? GetString(string key) => TryGetValue(key, out var v) ? v as string : null;

    public VdfObject? GetObject(string key) => TryGetValue(key, out var v) ? v as VdfObject : null;

    public long? GetLong(string key) => long.TryParse(GetString(key), out var n) ? n : null;
}

/// <summary>Minimal parser for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf).</summary>
internal static class Vdf
{
    public static VdfObject Parse(string text)
    {
        var pos = 0;
        return ParseObject(text, ref pos, topLevel: true);
    }

    public static VdfObject ParseFile(string path)
    {
        // Steam may be writing the file; share access and retry once on a transient lock.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return Parse(reader.ReadToEnd());
            }
            catch (IOException) when (attempt == 0)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static VdfObject ParseObject(string s, ref int pos, bool topLevel)
    {
        var obj = new VdfObject();
        while (true)
        {
            var key = NextToken(s, ref pos, out var keyQuoted);
            if (key is null)
            {
                if (topLevel) return obj;
                throw new FormatException("Unexpected end of VDF input.");
            }
            if (!keyQuoted && key == "}")
            {
                if (topLevel) throw new FormatException("Unexpected '}' in VDF input.");
                return obj;
            }

            var value = NextToken(s, ref pos, out var valueQuoted)
                ?? throw new FormatException($"Missing value for key '{key}'.");

            if (!valueQuoted && value == "{")
                obj[key] = ParseObject(s, ref pos, topLevel: false);
            else
                obj[key] = value;
        }
    }

    /// <summary>Returns the next token, skipping whitespace, // comments and [$PLATFORM] conditionals.</summary>
    private static string? NextToken(string s, ref int pos, out bool quoted)
    {
        quoted = false;
        while (pos < s.Length)
        {
            var c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            if (c == '[')
            {
                while (pos < s.Length && s[pos] != ']') pos++;
                pos++;
                continue;
            }
            if (c is '{' or '}')
            {
                pos++;
                return c.ToString();
            }
            if (c == '"')
            {
                quoted = true;
                pos++;
                var sb = new StringBuilder();
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length)
                    {
                        pos++;
                        sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', _ => s[pos] });
                    }
                    else
                    {
                        sb.Append(s[pos]);
                    }
                    pos++;
                }
                pos++; // closing quote
                return sb.ToString();
            }

            var start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}' or '"')) pos++;
            return s[start..pos];
        }
        return null;
    }
}
