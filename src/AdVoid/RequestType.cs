namespace AdVoid;

/// <summary>What a request is for, as filter options name it ($script, $image, ...). A filter's types are a mask of these.</summary>
[Flags]
public enum RequestType
{
    None = 0,
    Document = 1 << 0,       // a top-level page (only $document filters apply)
    Subdocument = 1 << 1,    // an iframe
    Script = 1 << 2,
    Stylesheet = 1 << 3,
    Image = 1 << 4,
    Media = 1 << 5,
    Font = 1 << 6,
    Xhr = 1 << 7,            // fetch and XMLHttpRequest
    WebSocket = 1 << 8,
    Ping = 1 << 9,           // beacons and <a ping>
    Object = 1 << 10,
    Other = 1 << 11,

    /// <summary>What a filter without type options covers: everything but whole pages.</summary>
    AllButDocument = Subdocument | Script | Stylesheet | Image | Media | Font | Xhr | WebSocket | Ping | Object | Other,
}

/// <summary>One request to decide on: its URL, the page it's made from (for third-party and $domain=) and its type.</summary>
public readonly record struct Request(string Url, string? PageUrl, RequestType Type)
{
    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
}

/// <summary>The engine's answer: blocked or not, and the filter line that decided it (for logs and a future logger UI).</summary>
public readonly record struct Decision(bool Blocked, string? Filter)
{
    public static readonly Decision Allow = new(false, null);
}
