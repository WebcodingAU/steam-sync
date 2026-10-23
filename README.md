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

## Live ping (testing)

While a game runs, `LivePing.cs` measures the ping to the server it's talking to and sends one sample a second to
`report_live_ping` (`sql/2026-10-24_live_ping.sql` in the website repo), shown on the site's `ping.php`. When the
game stops it sends `null`, which clears the PC's live row.

- **Which server:** the busiest internet address the game's processes exchanged packets with in the last second,
  leaving out web, DNS and LAN traffic. Without the ping helper only the game's TCP connections are visible.
- **TCP servers:** Windows' own RTT for that connection (`TcpStats.cs`, `GetPerTcpConnectionEStats` FineRtt).
  It needs no admin rights because Windows already collects it.
- **UDP servers:** an ICMP ping.
- **Servers that drop ping** (AWS blocks ICMP by default): after 3 missed pings, for a minute before ping is tried
  again, the first of these that works. `probe` in the sample says which one, and `estimate` holds the server's AWS
  region whenever it's known (from AWS's `ip-ranges.json`, fetched on first need and cached for a week):
  1. `steam_query` (Steam games): an A2S_INFO query to the game port, the next port or 27015, answered by the game
     server itself, so real latency (`SteamQuery.cs`). Gives up on a silent server for 10 minutes.
  2. `hop_estimate` with `hop.same_network`: the last router that answers on the route (tracert style,
     `HopProber.cs`), when it's in the server's own AWS region.
  3. `beacon_estimate`: for a region-less server (AWS GLOBAL, like Hunger Playtest's), the nearest AWS GameLift region
     beacon (UDP echo on port 7770) that the game itself pinged before joining (`GameLiftBeacons.cs`). Hunger from
     Australia: Sydney, ~20 ms.
  4. `cloud_estimate`: the TCP handshake time to the public endpoint of the server's AWS region (`CloudRegions.cs`).
  5. `hop_estimate` with any other usable hop: AWS's edge (`hop.edge`) or a router far from home. That's a lower bound;
     from Australia, a far hop came in 25–35 ms under the region estimate.
  Anything left shows as lost, with a note saying why.
- **ExitLag:** if the game connects to a port ExitLag listens on (its local proxy), or the game has no traffic of
  its own while ExitLag's relay is busy, ExitLag is routing it. With helper 1.1.0+ the sample is the real game
  server, pinged by the normal route (`exitlag_server`), with ExitLag's relay alongside. Otherwise it's the relay
  (`exitlag_relay`), which is only the first leg.
- `SteamSync.exe --live-ping out.jsonl [seconds]` writes the samples it would send, without sending them.
  `SteamSync.exe --cloud-region <ip> out.txt` shows the region an address is in and times its endpoint.
  `SteamSync.exe --probe <ip> <port> <steam|other>[-noping] out.txt [seconds]` runs the measuring of one server.

### Ping helper (`src/PingHelper`)

Windows only shows which server a UDP socket talks to through ETW (Microsoft-Windows-Kernel-Network), and only
SYSTEM, Administrators and LOCAL SERVICE may turn that provider on. So UDP games need `GamingEnjoyerPing.exe`, a
small Windows service:

- Ships inside the package next to `SteamSync.exe` (`release.ps1` publishes it as one trimmed, self-contained exe).
  The tray menu's **Install ping helper** runs it elevated with `install` (one UAC prompt). That copies it to
  `C:\Program Files\Gaming Enjoyer\` and registers the `GamingEnjoyerPing` service there.
- Runs as `NT AUTHORITY\LocalService` with only `SeChangeNotifyPrivilege`. Its exe must stay in Program Files: a
  service exe in a folder the user can write to could be swapped by any program the user runs.
- Once a second it writes the last second's flows (process, TCP/UDP, remote address and port, packet and byte counts)
  as one JSON line on `\\.\pipe\GamingEnjoyer.Ping`. Only people signed in at the PC can read the pipe, never the
  network. It sees addresses and counts, never packet contents.
- Has its own `<Version>` in `PingHelper.csproj`. Bump it whenever the helper changes, so the tray offers
  **Update ping helper** (it compares the installed exe with the bundled one).
- As administrator: `GamingEnjoyerPing.exe install | uninstall | console` (`console` runs it in the window for
  testing). Errors go to the Windows Application event log under `GamingEnjoyerPing`.
- Uninstalling the app doesn't remove the service (that would need a UAC prompt during uninstall). Use **Remove ping
  helper** in the tray menu first, or `uninstall` as administrator.

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

The ping helper builds on its own: `dotnet publish src/PingHelper -c Release -o <folder>` (`release.ps1` puts it in
the package).

Logs: `%LOCALAPPDATA%\SteamSync\logs\steamsync.log`.
To point at another Supabase project without rebuilding, drop an `appsettings.json`
in `%LOCALAPPDATA%\SteamSync\`.

## Privacy

Nothing leaves the PC until you pair it. After pairing, each sync sends gamingenjoyer.com:

- the device name you chose when pairing (defaults to the PC's name),
- the signed-in Steam account ID and the app version,
- the installed Steam games (app IDs) and the detected non-Steam games (name, launcher, launch ID
  and install folder).

While a game runs, it also sends the game, when it started and stopped (play sessions), and once a second a live
ping sample: the game, the ping, the address and port of the server it measured, packet rates, and whether ExitLag
is routing the game. To measure that ping, it pings the game server. For servers that don't answer, it may also:
- send them a Steam server query,
- ping the routers on the way to them,
- echo the AWS GameLift beacons the game itself pinged,
- download AWS's public address list (ip-ranges.amazonaws.com) and connect to that AWS region's public endpoint,
  without sending anything over the connection.

The app also checks the [releases repo](https://github.com/WebcodingAU/gaming-enjoyer-releases) on
GitHub for updates, and the Library window loads gamingenjoyer.com. Nothing else is sent anywhere.
To stop syncing, choose **Unpair this PC** in the tray menu or remove the PC under **Settings** → **Your PCs**.

## Code signing policy

Releases are built from this repository's source, and only the maintainer below can approve them.

- Committers, reviewers and approvers: [Joshua Noeth](mailto:dev@gamingenjoyer.com)

Report a security problem to [dev@gamingenjoyer.com](mailto:dev@gamingenjoyer.com).

## License

[MIT](LICENSE).
