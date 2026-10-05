# Valheim Web Map

A server-side [BepInEx](https://github.com/BepInEx/BepInEx) plugin for Valheim 1.0 that serves a
live, interactive web map of your world. Players install nothing.

- **Default world only.** The map is rendered from the game's world generator: biomes, terrain,
  rivers, forests and water, exactly as a fresh world looks. Buildings and terraforming are not shown.
- **Fog of war.** Only ground that players have actually walked is visible. Exploration is tracked
  on the server and saved between restarts; unexplored terrain never leaves the server.
- **Live players.** Players who turned on *Visible to other players* on their in-game map are drawn
  with their name, facing direction and biome, and updated every second. Everyone else is listed as
  online with their position hidden.
- **Familiar controls.** Scroll or pinch to zoom, drag to pan, double-click to zoom in, click a
  player to follow them. Works on phones. The URL keeps the view (`#x,z,zoom`) so it can be shared.
- **Cartography tables.** Markers that players share on an in-world cartography table appear on
  the web map, grouped by the player who placed them so each player's markers can be switched on
  and off. The explored area shared on the table lifts the fog as well. Deaths of players who were
  sharing their position are marked where they happened.
- **Play history.** Every connection is recorded as a session with its length and the deaths
  that happened in it, so the History tab shows how often, how long and how deadly each player's
  time on the server has been. Nothing is needed from the clients.
- **Existing worlds work.** Zones in the save file are only generated near players, so on first start
  the plugin reveals everything anyone had already visited before the mod was installed.
- **No authentication.** Meant to sit behind your own reverse proxy. The web server only starts
  when the process is the game server, so the plugin is inert if it ends up on a client.

Built and tested against Valheim 1.0.16 (dedicated server, Linux) with BepInExPack Valheim 5.4.2351.

## Screenshots

Fog of war over a world where a few areas have been visited, with two players sharing their
position and one who is not:

![Overview with fog of war and online players](https://github.com/koenhendriks/Valheim-Web-Map/raw/main/docs/screenshots/overview.jpg)

Zoomed in: terrain, rivers, forests and mountains come from the world generator at 5 m per pixel.
Players show their facing direction, biome, time online and deaths:

![Zoomed in with a player marker](https://github.com/koenhendriks/Valheim-Web-Map/raw/main/docs/screenshots/players.jpg)

The History tab, with one player's recent sessions expanded:

![History tab with sessions, play time and deaths](https://github.com/koenhendriks/Valheim-Web-Map/raw/main/docs/screenshots/history.jpg)

On a phone the player panel folds away behind a button:

![Phone layout](https://github.com/koenhendriks/Valheim-Web-Map/raw/main/docs/screenshots/phone.jpg)

## Install

With a mod manager (r2modman, Gale or the Thunderstore app), pick the **Valheim Dedicated Server**
profile and install **ValheimWebMap**; BepInExPack Valheim comes along as a dependency. Then skip
to step 3.

By hand:

1. Install [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
   on the **server**.
2. Download the release zip and copy `plugins/ValheimWebMap/ValheimWebMap.dll` to
   `<server>/BepInEx/plugins/ValheimWebMap/ValheimWebMap.dll`.
3. Start the server once. The config is written to `BepInEx/config/com.valheimwebmap.cfg`.
4. Open `http://<server-ip>:3000/`.

Startup log lines to expect:

```
[Info   :Valheim Web Map] Web map listening on http://*:3000/
[Info   :Valheim Web Map] Revealed 412 previously visited zones (3.1% of the world explored)
[Info   :Valheim Web Map] Tracking world 'Midgard'
[Info   :Valheim Web Map] Rendered preview world map (1024 px) in 0.4s; rendering 4096 px map with 4 thread(s)
[Info   :Valheim Web Map] Rendered world map (4096 px) in 25.3s
```

The map is usable right away at preview quality; the full-resolution render takes from a few
seconds to a couple of minutes depending on the CPU and is then cached on disk, so later starts are
instant. Map data lives in `BepInEx/config/ValheimWebMap/<world name>/`:

| File | Purpose |
| --- | --- |
| `basemap_<resolution>.bin` | Cached world render. Redone automatically after a game update. |
| `explored.bin` | Fog-of-war state. Delete it to start over with a black map. |
| `history.json` | Play sessions, deaths (with position when shared) and character ids per player (keyed by Steam or PlayFab id). Human readable. |

### Docker (lloesche/valheim-server)

With `BEPINEX=true`, BepInEx lives at `/opt/valheim/bepinex/BepInEx` on the data volume. Put the
plugin in `/opt/valheim/bepinex/BepInEx/plugins/ValheimWebMap/` and publish the port
(`-p 3000:3000/tcp`). The config and map data end up under `/config/bepinex/`.

### Reverse proxy

The page uses relative URLs, so it can be served from a sub-path as long as the path ends in a
slash (`https://example.com/valheim/` → `http://server:3000/`). Example for nginx:

```nginx
location /valheim/ {
    proxy_pass http://127.0.0.1:3000/;
    proxy_set_header Host $host;
}
```

Add whatever authentication you want at the proxy; the plugin has none.

## Configuration

`BepInEx/config/com.valheimwebmap.cfg`, read at startup.

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| Web | `Port` | `3000` | Listening port. |
| Web | `Host` | `*` | Bind address. On Windows a non-admin process may need `localhost` or a specific IP, or a `netsh http add urlacl` reservation. |
| Map | `Resolution` | `4096` | Pixels across the world render (`1024`, `2048`, `4096` or `8192`). 4096 is 5 m per pixel; the in-game map is 12 m per pixel. |
| Map | `RenderThreads` | `0` | Threads for the startup render; `0` uses half the cores. |
| Map | `MaxZoom` | `7` | Deepest browser zoom level. Levels beyond the native resolution are upscaled. |
| Exploration | `ExploreRadius` | `100` | Metres revealed around each player, same as the in-game map. |
| Exploration | `RevealGeneratedZones` | `true` | Reveal zones already generated in the save at startup. |
| Exploration | `RevealGeneratedZonesMargin` | `1` | The game generates zones a bit further out than the map reveals; a zone counts only if all zones within this many zones are generated too. |
| Exploration | `RevealFromCartographyTable` | `true` | Lift the fog wherever the map shared on a cartography table has been explored. |
| CartographyTables | `ScanInterval` | `30` | Seconds between scans for cartography tables. Only tables whose contents changed are read again. |
| Players | `UpdateInterval` | `1` | Seconds between position samples. |
| Storage | `DataDirectory` | *(empty)* | Where map data is stored. Empty means `BepInEx/config/ValheimWebMap/<world>`. |
| Storage | `SaveInterval` | `60` | Seconds between saves of the exploration data when it changed. |
| History | `TrackSessions` | `true` | Record play sessions in `history.json`. Off disables all session and death tracking. |
| History | `TrackDeaths` | `true` | Record deaths. |
| History | `RetentionDays` | `0` | Drop sessions and deaths older than this many days when the world loads. `0` keeps everything. |

### What the page shows

Each item can be switched off on its own. A disabled item is left out of the API responses, so it
is hidden from everyone, not just from the page.

| Key | Default | Meaning |
| --- | --- | --- |
| `ShowHiddenPlayers` | `true` | List online players who do not share their position (name only). |
| `ShowBiome` | `true` | Biome of a visible player. |
| `ShowCoordinates` | `true` | World coordinates of a visible player in the list. The marker itself is unaffected. |
| `ShowHeading` | `true` | The facing arrow on player markers. |
| `ShowTimeOnline` | `true` | How long each online player has been connected. |
| `ShowDeadStatus` | `true` | Mark players who are currently dead. |
| `ShowDeathCounts` | `true` | Death counts for online players and in the history. |
| `ShowDayAndTime` | `true` | In-game day and clock in the status bar. |
| `ShowExploredPercent` | `true` | Explored percentage in the panel. |
| `ShowHistory` | `true` | The History tab and `/api/history`. |
| `ShowSessionCount` | `true` | History: number of sessions per player. |
| `ShowPlayTime` | `true` | History: total play time and per-session length. |
| `ShowLastSeen` | `true` | History: when a player was last online. |
| `RecentSessions` | `30` | History: how many recent sessions to list per player. `0` hides the list. |
| `ShowPins` | `true` | Markers shared on cartography tables, with the Markers tab to toggle them per player. |
| `ShowCheckedPins` | `true` | Include markers that were crossed out on the table. The page has its own switch too. |
| `ShowDeathMarkers` | `true` | Mark where players died. Only deaths of players who were sharing their position at the time. |
| `DeathMarkersPerPlayer` | `5` | How many of each player's most recent deaths to mark. `0` marks all. |

All of these live in the `[Display]` section.

Exploration is recorded for every connected player, whether or not they share their position; only
the live marker respects the in-game setting.

### Sessions and deaths

A session starts when a connection has a player name and ends when that connection goes away, so
the respawn pause after dying does not split it. Deaths are detected from the `dead` flag the game
sets on the player's character for the ten seconds before the body is removed, with a respawn
(new character id on the same connection) as a fallback. A player who dies and logs out before
respawning is still counted. The place of death is stored only when the player was sharing their
position at that moment, so a player who hides their position also hides where they died.

### Cartography tables

Writing to a cartography table stores the shared map in the table itself, which the server holds.
The plugin scans the world for tables every `ScanInterval` seconds (spread over several frames),
decodes any table whose contents changed on a worker thread, lifts the fog where the shared map is
explored, and merges the pins of all tables (pins within a metre of each other count once, as in
the game). Pins carry the id of the character that placed them; names are resolved from players
seen online, so a marker from a character that has not connected since the plugin was installed
shows as "Unknown owner" until they do. Death pins are never shared by the game, which is why
death markers come from the server's own records instead.

## HTTP API

| Path | Content |
| --- | --- |
| `GET /` | The map page. |
| `GET /api/info` | World name, map extent, zoom limits. |
| `GET /api/state` | Render progress, exploration version, in-game day and time, online players with session start and death counts. |
| `GET /api/history` | Per player: session count, total play time, deaths, last seen and the 30 most recent sessions. |
| `GET /api/pins` | Cartography table markers (`pins`, `owners`) and death markers (`deaths`). `pinsVersion` in `/api/state` changes when this does. |
| `GET /tiles/{z}/{x}/{y}.png` | 256 px map tiles with fog applied. |

The world seed is deliberately not exposed.

## Building

### GitHub Actions

Every push to `main` and every pull request runs the **Build** workflow, which downloads the
Valheim dedicated server through SteamCMD (anonymous login), compiles against its assemblies and
uploads `ValheimWebMap.dll` plus the zip as a workflow artifact.

To release, bump `<Version>` in `src/ValheimWebMap/ValheimWebMap.csproj` and push to `main`. When
the version has no `v<version>` tag yet, the workflow tags the commit and publishes a GitHub
release with the zip, a `SHA256SUMS` file and release notes listing every commit since the
previous release, grouped by conventional-commit type (`.github/changelog.sh`).

### Thunderstore

`thunderstore.toml` describes the Thunderstore package; `build.sh` runs the
[Thunderstore CLI](https://github.com/thunderstore-io/thunderstore-cli) (`dotnet tool install -g tcli`)
to produce `dist/thunderstore/koenhendriks-ValheimWebMap-<version>.zip` with `manifest.json`,
`icon.png`, this README and a `CHANGELOG.md` made from the release notes. The workflow attaches
that zip to every GitHub release. When a `THUNDERSTORE_TOKEN` secret is present in the `Default`
environment (a service account token for the `koenhendriks` team), any released version that is
missing from the Thunderstore listing is uploaded on the next push to `main`; without the secret,
upload it by hand at <https://thunderstore.io/package/create/>.

### Locally

Requires the .NET SDK (any recent version; the plugin targets .NET Framework 4.6.2, which is what
the game's Mono runtime runs) and a Valheim install to reference the game assemblies.

```sh
./build.sh                                   # auto-detects a Steam install in the usual places
./build.sh -p:ValheimInstall=/path/to/valheim_server   # or point at a client/server folder
```

Output: `dist/ValheimWebMap-<version>.zip`. To change the page, edit `src/ValheimWebMap/web/`; the
files are embedded in the DLL. A `web/` folder next to the DLL on the server overrides the embedded
files, handy for tweaking without rebuilding.

### How it works

- No Harmony patches. The plugin polls `ZNet` from a `MonoBehaviour.Update` and starts once the
  server has loaded a world.
- The world render calls `WorldGenerator.GetBiome` / `GetBiomeHeight` from worker threads, like the
  game's own heightmap builder does. Hillshade, water depth, shoreline and forest density are
  derived from the same functions the in-game minimap uses.
- Fog is a 2048×2048 grid (10 m cells) revealed around player positions and persisted with
  zlib. Tiles are composited with the fog on the server, so unexplored terrain is never sent.
- Players come from `ZNet.GetPeers()`; the position is read from the player's ZDO and the
  *Visible to other players* flag from `ZNetPeer.m_publicRefPos`.
- Sessions and deaths are derived from the same per-second peer snapshot and saved with
  Newtonsoft.Json, which the game ships.
- Cartography tables are found with `ZDOMan.GetAllZDOsWithPrefabIterative` and read from the
  `data` entry of their ZDO, the gzip stream the client writes with `Minimap.GetSharedMapData`.

## Credits

Inspired by [valheim-webmap](https://github.com/f00d4tehg0dz/valheim-webmap) by kylepaulsen and
f00d4tehg0dz. Map rendering in the browser by [Leaflet](https://leafletjs.com/) (BSD-2-Clause).

## License

MIT, see [LICENSE](LICENSE).
