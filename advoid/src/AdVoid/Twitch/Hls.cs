using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdVoid.Twitch;

/// <summary>One quality in a Twitch master playlist: its media playlist's URL and what it is.</summary>
/// <param name="Name">Twitch's name for it ("1080p60 (source)", "720p60", "audio_only").</param>
/// <param name="Group">The quality's stable id ("chunked" or "1080p60" for the source, "720p60"): the same quality has the same id in every copy.</param>
public sealed record Variant(string Url, string Name, string Group, int Height, double FrameRate, long Bandwidth);

/// <summary>
/// The small part of HLS (the playlist format behind Twitch's streams) AdVoid needs: reading a master playlist's
/// qualities, spotting the ads Twitch splices into a media playlist, and cutting them out.
/// </summary>
/// <remarks>
/// Twitch marks a stitched ad two ways: an <c>#EXT-X-DATERANGE</c> with <c>CLASS="twitch-stitched-ad"</c>, and each ad
/// segment's <c>#EXTINF</c> title, which is <c>live</c> for the stream and the ad's source (<c>Amazon|…</c>) for an ad.
/// </remarks>
public static partial class Hls
{
    /// <summary>The qualities in a master playlist, in its order (best first on Twitch).</summary>
    public static List<Variant> ParseMaster(string text)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal); // VIDEO group id -> NAME
        var variants = new List<Variant>();
        string? streamInf = null;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var group = Attribute(line, "GROUP-ID");
                var name = Attribute(line, "NAME");
                if (group is not null && name is not null) names[group] = name;
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) streamInf = line;
            else if (streamInf is not null && line.Length > 0 && !line.StartsWith('#'))
            {
                // Older masters name qualities with VIDEO groups and #EXT-X-MEDIA; newer ones (2026) with STABLE-VARIANT-ID and IVS-NAME.
                var group = Attribute(streamInf, "VIDEO") ?? Attribute(streamInf, "STABLE-VARIANT-ID") ?? "";
                var resolution = Attribute(streamInf, "RESOLUTION");
                var height = resolution?.Split('x') is [_, var h] && int.TryParse(h, out var hv) ? hv : 0;
                double.TryParse(Attribute(streamInf, "FRAME-RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps);
                long.TryParse(Attribute(streamInf, "BANDWIDTH"), out var bandwidth);
                var name = names.GetValueOrDefault(group) ?? Attribute(streamInf, "IVS-NAME") ?? group;
                variants.Add(new Variant(line, name, group, height, fps, bandwidth));
                streamInf = null;
            }
        }
        return variants;
    }

    /// <summary>The variant in <paramref name="choices"/> closest to <paramref name="wanted"/>: the same quality, else the
    /// tallest no taller than it, else the shortest. Null when there are no video qualities.</summary>
    public static Variant? Closest(IReadOnlyList<Variant> choices, Variant? wanted)
    {
        var video = choices.Where(v => v.Height > 0).ToList();
        if (video.Count == 0) return null;
        if (wanted is null) return video.MaxBy(v => (v.Height, v.FrameRate));
        return video.FirstOrDefault(v => v.Group == wanted.Group)
            ?? video.FirstOrDefault(v => v.Height == wanted.Height && Math.Abs(v.FrameRate - wanted.FrameRate) < 1)
            ?? video.Where(v => v.Height <= wanted.Height).MaxBy(v => (v.Height, v.FrameRate))
            ?? video.MinBy(v => v.Height);
    }

    /// <summary>True when a media playlist carries a stitched ad (marked break, or any segment that isn't the stream).</summary>
    public static bool HasAds(string mediaPlaylist)
    {
        if (mediaPlaylist.Contains("twitch-stitched-ad", StringComparison.Ordinal)) return true;
        foreach (var line in Lines(mediaPlaylist))
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal) && IsAdTitle(line)) return true;
        return false;
    }

    /// <summary>True when a media playlist has at least one segment of the stream itself.</summary>
    public static bool HasLive(string mediaPlaylist)
    {
        foreach (var line in Lines(mediaPlaylist))
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal) && !IsAdTitle(line)) return true;
        return false;
    }

    /// <summary>
    /// The playlist with its ad segments and ad markers taken out. The media sequence number is moved on by the
    /// segments removed ahead of the first one kept, so the player still sees later segments as new. With nothing but
    /// ads, what's left is an empty playlist: the player waits (a short freeze) instead of showing the ad.
    /// </summary>
    public static string StripAds(string mediaPlaylist)
    {
        var header = new List<string>();
        var body = new List<string>();
        var pending = new List<string>();  // the current segment's tags, until its URI says whether it's kept
        bool inSegments = false, segmentIsAd = false, keptAny = false;
        int removedBeforeFirstKept = 0;
        foreach (var raw in Lines(mediaPlaylist))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (IsAdMarker(line)) continue;
            if (line.StartsWith("#EXT-X-TWITCH-PREFETCH:", StringComparison.Ordinal)) continue; // may be the ad's next segment
            if (line.StartsWith('#') && !IsSegmentTag(line))
            {
                (inSegments ? body : header).Add(line);
                continue;
            }
            inSegments = true;
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal)) segmentIsAd = IsAdTitle(line);
                pending.Add(line);
                continue;
            }
            // A segment URI: keep it with its tags, or drop them together.
            if (segmentIsAd) { if (!keptAny) removedBeforeFirstKept++; }
            else { body.AddRange(pending); body.Add(line); keptAny = true; }
            pending.Clear();
            segmentIsAd = false;
        }

        var output = new StringBuilder();
        foreach (var line in header)
        {
            if (removedBeforeFirstKept > 0 && line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal)
                && long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], out var sequence))
                output.Append("#EXT-X-MEDIA-SEQUENCE:").Append(sequence + removedBeforeFirstKept).Append('\n');
            else output.Append(line).Append('\n');
        }
        foreach (var line in body) output.Append(line).Append('\n');
        return output.ToString();
    }

    // "#EXTINF:2.000,live" is the stream; "#EXTINF:2.000,Amazon|123" is an ad. An empty title counts as the stream.
    private static bool IsAdTitle(string extinf)
    {
        var comma = extinf.IndexOf(',');
        if (comma < 0) return false;
        var title = extinf[(comma + 1)..].Trim();
        return title.Length > 0 && !title.Equals("live", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdMarker(string line) =>
        line.StartsWith("#EXT-X-DATERANGE:", StringComparison.Ordinal)
        && (line.Contains("twitch-stitched-ad", StringComparison.Ordinal) || line.Contains("twitch-ad-quartile", StringComparison.Ordinal));

    private static bool IsSegmentTag(string line) =>
        line.StartsWith("#EXTINF:", StringComparison.Ordinal)
        || line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal)
        || line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal)
        || line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal)
        || line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal)
        || line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal);

    private static string? Attribute(string line, string name)
    {
        var match = AttributeRegex().Matches(line).FirstOrDefault(m => m.Groups[1].Value == name);
        if (match is null) return null;
        var value = match.Groups[2].Value;
        return value.Length >= 2 && value[0] == '"' ? value[1..^1] : value;
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r'));

    [GeneratedRegex("""(?:^|[:,])([A-Z0-9-]+)=("[^"]*"|[^,]*)""")]
    private static partial Regex AttributeRegex();
}
