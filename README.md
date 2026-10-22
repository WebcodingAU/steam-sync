# Gaming Enjoyer (Steam Sync)

Windows tray app for gamingenjoyer.com. It reports which games are installed on this PC, so the
library's **Installed** flags keep themselves up to date, and opens the site's Library in a lightweight
window built to sit next to a running game. (The exe and data folder are still called SteamSync.)

Install: run `GamingEnjoyer-win-Setup.exe` from
[gaming-enjoyer-releases](https://github.com/WebcodingAU/gaming-enjoyer-releases/releases/latest).
It installs per user (no admin) to `%LocalAppData%\GamingEnjoyer`, adds a Start menu entry, installs the
WebView2 runtime if the PC lacks it, and updates itself.

## Library window

Tray → **Open Library** (or the Start menu entry, or double-click the tray icon) opens gamingenjoyer.com
in a single-site window on the WebView2 (Edge) runtime that ships with Windows:

- Its own profile under `%LocalAppData%\SteamSync\WebView2`; sign in once.
- Play links launch games directly; other sites open in the default browser. **F11** is fullscreen.
- GPU on: software rendering measured ~100 MB lighter but made scrolling laggy, so it stays on.
- Not the active window → every process in Windows Efficiency mode, low-memory target.
- Minimized → page frozen; minimized for 2 minutes → the engine shuts down (back to ~30 MB) and the page
  reloads on restore. Closed → no browser processes at all.

Measured on the Library page (2026-10-14): ~310 MB open, ~30 MB unloaded, ~0% CPU when idle.

## Releasing

`./release.ps1` builds, packs (Velopack) and publishes `<Version>` from the csproj to the releases repo;
installed copies download it in the background and install it on their next start (or via
**Restart to update** in the tray menu). `./release.ps1 -NoUpload` only builds the installer locally.
With `GAMING_ENJOYER_SIGN_THUMBPRINT` set to a code signing certificate's thumbprint, the release is
signed, and the script stops before uploading if the signature isn't valid.

## How it works

- Finds Steam via `HKCU\Software\Valve\Steam\SteamPath`, reads every library from
  `steamapps\libraryfolders.vdf`, and every installed game from
  `<library>\steamapps\appmanifest_<appid>.acf`. No drive crawling.
- Watches each library's `steamapps` folder; syncs ~10 s after a change (max 60 s
  during long downloads), plus every 30 minutes as a safety net.
- Sends the complete list to the `sync_device` Postgres function, which updates
  `steam_games.installed` (site owner) or `user_games.installed` (everyone else).
  Schema: `gamingenjoyer/sql/2026-10-14_steam_sync_devices.sql`.
- Holds only the Supabase **publishable** key (built in from `appsettings.json`) and
  its own device token, encrypted with DPAPI in `%LOCALAPPDATA%\SteamSync\device.bin`.

## Non-Steam games

Each sync also sends the non-Steam games on this PC as `p_nonsteam`. The site uses them to
keep the owner's `non_steam_games.installed` flags right, and lists installed games that
aren't in the library as suggestions on **Your PCs** (Add or Hide). Nothing is added automatically.

- **Steam shortcuts**: `userdata\<account>\config\shortcuts.vdf` (binary KeyValues). A shortcut
  is installed if its exe exists.
- **Xbox / Game Pass**: each drive's `.GamingRoot` names the Xbox folders; each game's
  `Content\MicrosoftGame.config` gives the package. A game counts as installed only once the
  package is registered for the current user, so half-finished downloads don't count.
- **Programs list** (Uninstall registry), allowlist only: Battle.net (`--uid=`), Ubisoft
  (`Uplay Install <id>`), EA (`__Installer\installerdata.xml`) and Battlestate games. Steam
  copies are skipped.
- Detections in the same folder become one game. A shortcut joins the launcher game whose
  folder holds its exe, or, if its exe is gone, an installed game with the same name.
- If any detector fails, that sync sends `p_nonsteam = null` and the server leaves non-Steam
  data alone. A partial list would untick games. The app tries once more about 10 s later, then
  waits for a watched file to change or for the 30-minute sync.
- `shortcuts.vdf` and the Xbox folders are watched. Registry-only installs are picked up by
  the 30-minute sync.
- If the server predates `p_nonsteam` (PostgREST `PGRST202`), the app retries without it and
  syncs Steam games only, checking again every hour.

## Pairing

1. On gamingenjoyer.com, sign in with Steam → **Your PCs** → **Get a pairing code**.
2. Tray icon → **Pair this PC…** → enter the code.

Pairing turns on **Start with Windows** (`HKCU\...\Run`); toggle it from the tray menu.

## Build

```
dotnet build src/SteamSync
dotnet publish src/SteamSync -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

`SteamSync.exe --dump scan.json` writes the local scan (Steam and non-Steam) to a file without
touching the network.

Logs: `%LOCALAPPDATA%\SteamSync\logs\steamsync.log`.
To point at another Supabase project without rebuilding, drop an `appsettings.json`
in `%LOCALAPPDATA%\SteamSync\`.

## Privacy

Nothing leaves the PC until you pair it. After pairing, each sync sends gamingenjoyer.com:

- the device name you chose when pairing (defaults to the PC's name),
- the signed-in Steam account ID and the app version,
- the installed Steam games (app IDs) and the detected non-Steam games (name, launcher, launch ID
  and install folder).

The app also checks the [releases repo](https://github.com/WebcodingAU/gaming-enjoyer-releases) on
GitHub for updates, and the Library window loads gamingenjoyer.com. Nothing else is sent anywhere.
To stop syncing, choose **Unpair this PC** in the tray menu or remove the PC on **Your PCs**.

## Code signing policy

Releases are built from this repository's source, and only the maintainer below can approve them.

- Committers, reviewers and approvers: [Joshua Noeth](mailto:dev@gamingenjoyer.com)

Report a security problem to [dev@gamingenjoyer.com](mailto:dev@gamingenjoyer.com).

## License

[MIT](LICENSE).
