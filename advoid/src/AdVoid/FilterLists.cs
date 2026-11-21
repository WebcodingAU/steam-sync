using System.Net.Http;

namespace AdVoid;

/// <summary>A filter list to download: a name for its cached file and where it comes from.</summary>
public sealed record FilterList(string Name, string Url);

/// <summary>
/// Keeps filter lists in a folder: downloads each one when it's missing or older than its refresh age, and reads
/// them all back for FilterEngine.Build. The lists are fetched from their publishers at run time and never shipped
/// with AdVoid itself.
/// </summary>
public sealed class FilterListStore(string folder, IReadOnlyList<FilterList> lists)
{
    /// <summary>EasyList (ads) and EasyPrivacy (trackers), the lists most blockers start from.</summary>
    public static readonly IReadOnlyList<FilterList> Default =
    [
        new("easylist", "https://easylist.to/easylist/easylist.txt"),
        new("easyprivacy", "https://easylist.to/easylist/easyprivacy.txt"),
    ];

    public string Folder { get; } = folder;
    public IReadOnlyList<FilterList> Lists { get; } = lists;

    private string PathOf(FilterList list) => Path.Combine(Folder, list.Name + ".txt");

    /// <summary>Whether any list is missing or older than maxAge.</summary>
    public bool NeedsUpdate(TimeSpan maxAge) =>
        Lists.Any(l => !File.Exists(PathOf(l)) || DateTime.UtcNow - File.GetLastWriteTimeUtc(PathOf(l)) > maxAge);

    /// <summary>Downloads the lists that are missing or older than maxAge. A failed download keeps the old copy. Returns
    /// how many lists changed.</summary>
    public async Task<int> UpdateAsync(HttpClient http, TimeSpan maxAge, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(Folder);
        var changed = 0;
        foreach (var list in Lists)
        {
            var path = PathOf(list);
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= maxAge) continue;
            try
            {
                var text = await http.GetStringAsync(list.Url, cancel);
                // A real list starts with a header or a comment; anything else is an error page, not a list.
                if (!text.StartsWith("[Adblock", StringComparison.OrdinalIgnoreCase) && !text.TrimStart().StartsWith('!')) continue;
                var tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, text, cancel);
                File.Move(tmp, path, overwrite: true);
                changed++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { }
        }
        return changed;
    }

    /// <summary>Every cached list's lines, in list order.</summary>
    public IEnumerable<string> ReadLines()
    {
        foreach (var list in Lists)
        {
            var path = PathOf(list);
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path)) yield return line;
        }
    }
}
