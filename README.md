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

## Pairing

1. On gamingenjoyer.com, sign in with Steam → **Your PCs** → **Get a pairing code**.
2. Tray icon → **Pair this PC…** → enter the code.

Pairing turns on **Start with Windows** (`HKCU\...\Run`); toggle it from the tray menu.

## Build

```
dotnet build src/SteamSync
dotnet publish src/SteamSync -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

`SteamSync.exe --dump scan.json` writes the local scan to a file without touching the network.

Logs: `%LOCALAPPDATA%\SteamSync\logs\steamsync.log`.
To point at another Supabase project without rebuilding, drop an `appsettings.json`
in `%LOCALAPPDATA%\SteamSync\`.
