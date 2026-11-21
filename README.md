# AdVoid

My own ad blocker: a filter engine in C# that reads the same filter lists as uBlock Origin and Adblock Plus
(EasyList syntax), and its own page scripts for the ads lists can't reach. Gaming Enjoyer's YouTube window is its
first home; the goal is to grow it into one of the best blockers around.

## What it does now (0.1)

- **Network blocking.** `||host^`, wildcards (`*`), separators (`^`), anchors (`|`, `||`), `/regex/` filters,
  `@@` exceptions, `$important`, and the options `third-party`/`first-party`, `domain=`, the request types (`script`,
  `image`, `stylesheet`, `xmlhttprequest`, `subdocument`, `media`, `font`, `websocket`, `ping`, `object`, `other`,
  negated with `~`) and `match-case`. Page-level exceptions: `$document`, `$elemhide`, `$generichide`, `$genericblock`.
- **Element hiding.** `##selector`, `example.com##selector`, `~example.com##selector`, `#@#` exceptions, served as
  one CSS rule per selector so a selector the browser doesn't understand can't break the others.
- **YouTube** (`Scriptlets/youtube.js`). YouTube schedules video ads inside the video's own data, not from ad
  servers, so request blocking can't stop them. The script removes `adPlacements`, `adSlots` and `playerAds` from
  `ytInitialPlayerResponse` and from every JSON response before the player reads them, hides ad tiles and banners,
  and skips any ad that still starts.
- **Lists.** `FilterListStore` downloads EasyList and EasyPrivacy from their publishers and keeps them cached; they're
  never shipped with AdVoid.
- Filters using options it doesn't support yet (`$csp`, `$redirect`, `$removeparam`, ...) are skipped rather than
  half-applied. `Counts.Skipped` says how many.

Measured with EasyList + EasyPrivacy (2026-11-21): about 107,000 blocking and 24,000 hiding filters, built in about 0.2 s,
about 24 µs per request.

## Using it

```csharp
var engine = FilterEngine.Build(File.ReadLines("easylist.txt"));
var decision = engine.Match(new Request("https://ads.example/x.js", "https://news.example/", RequestType.Script));
if (decision.Blocked) { /* cancel the request; decision.Filter says which filter did it */ }
string css = engine.Cosmetics("https://news.example/");   // add as a <style> to the page
string script = Scriptlets.All;                           // run before the page's own scripts
```

The engine is read-only once built, so it can answer from any thread; to change lists, build a new one and swap it in.

## Layout

- `src/AdVoid`: the engine (`FilterEngine`, `NetworkFilter`, `CosmeticFilter`, `Domains`, `FilterListStore`,
  `Scriptlets`).
- `tests/AdVoid.Tests`: `dotnet test AdVoid.slnx`.

Gaming Enjoyer (`C:\Repos\steam-sync`) carries a copy of this repo in its `advoid/` folder as a git subtree, so its
GitHub build has the engine. After committing here, update the copy from steam-sync with
`git subtree pull --prefix=advoid C:/Repos/advoid main`.

## Where it goes next

Roughly in the order that pays off most:

1. **uBlock Origin's own lists** (uBlock filters, Badware, Privacy, Quick fixes) and the **Public Suffix List** in
   place of the short built-in suffix list.
2. **Scriptlets** (`##+js(...)`): `set-constant`, `json-prune`, `abort-on-property-read`, `no-setTimeout-if`,
   `no-fetch-if`, and so on. Most anti-adblock and many YouTube fixes in the lists are scriptlets.
3. **Procedural hiding** (`#?#`, `:has-text()`, `:upward()`, `:remove()`) with a small JS engine on the page.
4. **`$redirect`** to harmless stand-ins (empty scripts, 1×1 GIFs) so pages that need an ad script don't break, and
   **`$removeparam`** for tracking parameters.
5. **Entity domains** (`example.*`) and `$badfilter`.
6. **A logger and element picker**: what was blocked on this page and by which filter, and "hide this" by clicking.
7. **Speed**: a compiled token index (no per-request allocations) and a serialized engine so start-up skips parsing.
8. **A Chrome/Edge extension front end** using the same lists and scripts, for browsing outside Gaming Enjoyer.
