namespace AdVoid;

/// <summary>
/// AdVoid's own page scripts, for hosts to run in every document before the page's scripts (WebView2:
/// AddScriptToExecuteOnDocumentCreatedAsync). Each script checks the page's hostname itself, so a host can register
/// them all once instead of per site.
/// </summary>
public static class Scriptlets
{
    /// <summary>Removes YouTube's video ads (they're scheduled inside the video's own data, which request blocking can't reach).</summary>
    public static string YouTube => Load("youtube.js");

    /// <summary>Every built-in script, joined: what a host registers to run in each document.</summary>
    public static string All => YouTube;

    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    private static string Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var text)) return text;
            using var stream = typeof(Scriptlets).Assembly.GetManifestResourceStream("AdVoid.Scriptlets." + name)
                ?? throw new InvalidOperationException($"AdVoid script {name} isn't embedded");
            return Cache[name] = new StreamReader(stream).ReadToEnd();
        }
    }
}
