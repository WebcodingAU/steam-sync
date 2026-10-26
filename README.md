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

**Beta channel.** `./release.ps1 -Channel beta` publishes `<Version>-beta.N` as a GitHub pre-release for the
`beta` channel only (`releases.beta.json`); copies on stable never see it. A copy follows beta when it was
installed from a beta (`GamingEnjoyer-beta-Setup.exe` on the pre-release) or when **Beta updates** is ticked in
the tray menu; unticking it keeps the current version until a newer stable one is out. Test a beta on your own
PC, then run `./release.ps1` (no `-Channel`) to release that version to everyone. The GitHub Actions build can
pack either channel (Run workflow → channel).

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

## Play button for non-Steam games

The app registers `gamingenjoyer://` for the current user (`HKCU\Software\Classes\gamingenjoyer`, refreshed at
every start, removed on uninstall). The site's Play button links to `gamingenjoyer://play/<entity key>` for
non-Steam games that aren't in Steam, and Windows runs `SteamSync.exe --play <link>` (`GameLauncher.cs`):

- The PC is scanned again and the key must belong to an installed game that scan found. Nothing from the
  link is run or passed on, so a link on another site can at most start one of your installed games.
- Started the way its launcher starts it: Battle.net, Ubisoft and EA through their own links; Xbox and Game
  Pass through the game's `Content\gamelaunchhelper.exe` (else `shell:AppsFolder\<package family>!<app id>`);
  Battlestate by opening its launcher (the games
  need it to sign in); otherwise its Steam shortcut, if it has one.
- Problems (not installed here, no known way to start it) are shown in a message box.

## Play time and "Playing now"

`PlayTracker.cs` checks the running processes every second (only new processes are looked at). A game is running when a process's exe sits
inside its install folder (launchers and `gamelaunchhelper.exe` hand over to the real exe, so the launched
process isn't timed); a process whose path is hidden (some anti-cheat-protected games) is matched by exe name
against the exes in each game folder. Crash reporters, updaters, installers and anti-cheat services don't count.

- A session is reported as running as soon as it's seen, and ends 2 seconds after the last process closes (back within a minute = the same session);
  runs under a minute don't count as play (one already reported is sent back with no length); sleep ends it.
- Starts and ends are sent at once, otherwise about once a minute, to `record_play_sessions`
  (`sql/2026-10-23_play_sessions.sql` in the website repo). Unsent ones wait in `play_sessions.json`.
- Steam games are reported too, but the site only uses them for "Playing now": Steam counts their time.
- Nothing is recorded before the PC is paired.

## PC load (beta builds only)

While a game runs, a beta build (a version with `-beta` in it) sends the PC's load once a second (`PcLoad.cs`,
`SystemStats.cs`): CPU and its speed, GPU (busiest engine) and its clock (NVIDIA only, via nvml.dll), memory load and use,
an approximate responsiveness and the game's own CPU,
the numbers Process Lasso graphs. It goes through `report_live_ping` (the live ping table) as a sample with a
`system` object and `method` `pc_load`, and `null` when the game stops. Stable builds never start it.
`SteamSync.exe --system-stats out.jsonl [seconds] [pid...]` writes the samples without sending them.

## Old ping helper

Live ping (0.4.0-beta.6 to beta.17) has been removed. Its ping helper was a Windows service in
`C:\Program Files\Gaming Enjoyer\`, which uninstalling the app doesn't remove. On a PC that still has it, the tray
menu shows **Remove old ping helper…** (`OldPingHelper.cs`): it runs the helper's own `uninstall` from a temporary
copy, with one UAC prompt.

## Pairing

1. On gamingenjoyer.com, sign in with Steam → **Settings** → **Your PCs** → **Get a pairing code**.
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

While a game runs, it also sends the game and when it started and stopped (play sessions). Beta builds also send,
once a second while a game runs, how busy the PC is (CPU, graphics card and memory use).

The app also checks the [releases repo](https://github.com/WebcodingAU/gaming-enjoyer-releases) on
GitHub for updates, and the Library window loads gamingenjoyer.com. Nothing else is sent anywhere.
To stop syncing, choose **Unpair this PC** in the tray menu or remove the PC under **Settings** → **Your PCs**.

## Code signing policy

Releases are built from this repository's source, and only the maintainer below can approve them.

- Committers, reviewers and approvers: [Joshua Noeth](mailto:dev@gamingenjoyer.com)

Report a security problem to [dev@gamingenjoyer.com](mailto:dev@gamingenjoyer.com).

## License

[MIT](LICENSE).
