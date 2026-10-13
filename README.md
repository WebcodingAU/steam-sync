# Steam Sync

Windows tray app that reports which Steam games are installed on this PC to
gamingenjoyer.com, so the library's **Installed** flags keep themselves up to date.

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
