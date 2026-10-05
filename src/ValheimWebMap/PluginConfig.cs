using BepInEx.Configuration;

namespace ValheimWebMap
{
    internal sealed class PluginConfig
    {
        public readonly ConfigEntry<int> Port;
        public readonly ConfigEntry<string> Host;
        public readonly ConfigEntry<int> Resolution;
        public readonly ConfigEntry<int> RenderThreads;
        public readonly ConfigEntry<int> MaxZoom;
        public readonly ConfigEntry<float> ExploreRadius;
        public readonly ConfigEntry<bool> RevealGeneratedZones;
        public readonly ConfigEntry<int> RevealGeneratedZonesMargin;
        public readonly ConfigEntry<float> UpdateInterval;
        public readonly ConfigEntry<string> DataDirectory;
        public readonly ConfigEntry<float> SaveInterval;

        public readonly ConfigEntry<bool> TrackSessions;
        public readonly ConfigEntry<bool> TrackDeaths;
        public readonly ConfigEntry<int> HistoryRetentionDays;

        public readonly ConfigEntry<bool> ShowHiddenPlayers;
        public readonly ConfigEntry<bool> ShowBiome;
        public readonly ConfigEntry<bool> ShowCoordinates;
        public readonly ConfigEntry<bool> ShowHeading;
        public readonly ConfigEntry<bool> ShowTimeOnline;
        public readonly ConfigEntry<bool> ShowDeadStatus;
        public readonly ConfigEntry<bool> ShowDeathCounts;
        public readonly ConfigEntry<bool> ShowDayAndTime;
        public readonly ConfigEntry<bool> ShowExploredPercent;
        public readonly ConfigEntry<bool> ShowHistory;
        public readonly ConfigEntry<bool> ShowSessionCount;
        public readonly ConfigEntry<bool> ShowPlayTime;
        public readonly ConfigEntry<bool> ShowLastSeen;
        public readonly ConfigEntry<int> RecentSessions;

        public readonly ConfigEntry<bool> RevealFromCartographyTable;
        public readonly ConfigEntry<float> CartographyScanInterval;
        public readonly ConfigEntry<bool> ShowPins;
        public readonly ConfigEntry<bool> ShowCheckedPins;
        public readonly ConfigEntry<bool> ShowAutomatedPins;
        public readonly ConfigEntry<bool> HidePinsInFog;
        public readonly ConfigEntry<bool> ShowDeathMarkers;
        public readonly ConfigEntry<int> DeathMarkersPerPlayer;

        public PluginConfig(ConfigFile file)
        {
            Port = file.Bind("Web", "Port", 3000,
                "TCP port the built-in web server listens on.");
            Host = file.Bind("Web", "Host", "*",
                "Address to bind. '*' binds every interface. On Windows a non-administrator process may need " +
                "a specific address (for example 'localhost' or '127.0.0.1') or a URL reservation via netsh.");

            Resolution = file.Bind("Map", "Resolution", 4096,
                new ConfigDescription(
                    "Pixels across the rendered world map. 4096 is 5 m per pixel (the in-game map is 12 m per pixel). " +
                    "Doubling it quadruples render time and memory.",
                    new AcceptableValueList<int>(1024, 2048, 4096, 8192)));
            RenderThreads = file.Bind("Map", "RenderThreads", 0,
                "Worker threads for rendering the world map on startup. 0 picks half the CPU cores, at least one.");
            MaxZoom = file.Bind("Map", "MaxZoom", 7,
                new ConfigDescription("Deepest zoom level offered in the browser; levels past the native resolution are upscaled.",
                    new AcceptableValueRange<int>(3, 9)));

            ExploreRadius = file.Bind("Exploration", "ExploreRadius", 100f,
                "Metres revealed around a player, matching the in-game map's reveal radius.");
            RevealGeneratedZones = file.Bind("Exploration", "RevealGeneratedZones", true,
                "On startup, reveal every zone the world save has already generated. Zones are only generated near " +
                "players, so this recovers exploration from before the mod was installed.");
            RevealGeneratedZonesMargin = file.Bind("Exploration", "RevealGeneratedZonesMargin", 1,
                new ConfigDescription(
                    "The game generates zones a little further out than the map reveals. A zone is revealed only when " +
                    "all zones within this many zones of it are generated too, which trims that extra ring.",
                    new AcceptableValueRange<int>(0, 3)));

            RevealFromCartographyTable = file.Bind("Exploration", "RevealFromCartographyTable", true,
                "Lift the fog wherever the map shared on an in-world cartography table has been explored. " +
                "Players who never share their position still contribute once they write to a table.");

            CartographyScanInterval = file.Bind("CartographyTables", "ScanInterval", 30f,
                new ConfigDescription(
                    "Seconds between scans of the world for cartography tables. A scan is spread over several " +
                    "frames and only tables whose contents changed are read again.",
                    new AcceptableValueRange<float>(5f, 600f)));

            UpdateInterval = file.Bind("Players", "UpdateInterval", 1f,
                new ConfigDescription("Seconds between player position samples.", new AcceptableValueRange<float>(0.25f, 10f)));

            DataDirectory = file.Bind("Storage", "DataDirectory", "",
                "Where the rendered map and exploration data are kept. Empty uses BepInEx/config/ValheimWebMap/<world>.");
            SaveInterval = file.Bind("Storage", "SaveInterval", 60f,
                new ConfigDescription("Seconds between writes of the exploration data when it changed.", new AcceptableValueRange<float>(5f, 3600f)));

            TrackSessions = file.Bind("History", "TrackSessions", true,
                "Record a play session (start, end, character) for every connection in history.json.");
            TrackDeaths = file.Bind("History", "TrackDeaths", true,
                "Record player deaths in history.json. Requires TrackSessions.");
            HistoryRetentionDays = file.Bind("History", "RetentionDays", 0,
                new ConfigDescription("Drop sessions and deaths older than this many days when the world loads. 0 keeps everything.",
                    new AcceptableValueRange<int>(0, 3650)));

            ShowHiddenPlayers = file.Bind("Display", "ShowHiddenPlayers", true,
                "List online players who do not share their position (name only, no location).");
            ShowBiome = file.Bind("Display", "ShowBiome", true,
                "Show the biome a visible player is in.");
            ShowCoordinates = file.Bind("Display", "ShowCoordinates", true,
                "Show a visible player's world coordinates as text in the player list.");
            ShowHeading = file.Bind("Display", "ShowHeading", true,
                "Show which way a visible player is facing.");
            ShowTimeOnline = file.Bind("Display", "ShowTimeOnline", true,
                "Show how long each online player has been connected. Requires TrackSessions.");
            ShowDeadStatus = file.Bind("Display", "ShowDeadStatus", true,
                "Mark players who are currently dead.");
            ShowDeathCounts = file.Bind("Display", "ShowDeathCounts", true,
                "Show death counts for online players and in the history. Requires TrackDeaths.");
            ShowDayAndTime = file.Bind("Display", "ShowDayAndTime", true,
                "Show the in-game day and time of day.");
            ShowExploredPercent = file.Bind("Display", "ShowExploredPercent", true,
                "Show how much of the world has been explored.");
            ShowHistory = file.Bind("Display", "ShowHistory", true,
                "Offer the History tab and the /api/history endpoint. Requires TrackSessions.");
            ShowSessionCount = file.Bind("Display", "ShowSessionCount", true,
                "History: show how many sessions each player has had.");
            ShowPlayTime = file.Bind("Display", "ShowPlayTime", true,
                "History: show each player's total play time.");
            ShowLastSeen = file.Bind("Display", "ShowLastSeen", true,
                "History: show when each player was last online.");
            RecentSessions = file.Bind("Display", "RecentSessions", 30,
                new ConfigDescription("History: how many recent sessions to list per player. 0 hides the per-session list.",
                    new AcceptableValueRange<int>(0, 365)));

            ShowPins = file.Bind("Display", "ShowPins", true,
                "Show map markers shared on cartography tables, with a Markers tab to toggle them per player.");
            ShowCheckedPins = file.Bind("Display", "ShowCheckedPins", true,
                "Include markers that were crossed out on the table. The page has its own switch to hide them.");
            ShowAutomatedPins = file.Bind("Display", "ShowAutomatedPins", false,
                "Also show markers that server-side mods (for example AutoMapTables) wrote to the table rather than " +
                "players. They are grouped under the mod's name in the Markers tab.");
            HidePinsInFog = file.Bind("Display", "HidePinsInFog", true,
                "Hide markers that lie in unexplored territory, so the map still only shows where players have been.");
            ShowDeathMarkers = file.Bind("Display", "ShowDeathMarkers", true,
                "Mark where players died, from the server's own death records. Only deaths of players who were " +
                "sharing their position at the time are placed. Requires TrackDeaths.");
            DeathMarkersPerPlayer = file.Bind("Display", "DeathMarkersPerPlayer", 5,
                new ConfigDescription("How many of each player's most recent deaths to mark. 0 marks all of them.",
                    new AcceptableValueRange<int>(0, 100)));
        }
    }
}
