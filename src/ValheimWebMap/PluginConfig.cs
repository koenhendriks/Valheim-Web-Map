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

            UpdateInterval = file.Bind("Players", "UpdateInterval", 1f,
                new ConfigDescription("Seconds between player position samples.", new AcceptableValueRange<float>(0.25f, 10f)));

            DataDirectory = file.Bind("Storage", "DataDirectory", "",
                "Where the rendered map and exploration data are kept. Empty uses BepInEx/config/ValheimWebMap/<world>.");
            SaveInterval = file.Bind("Storage", "SaveInterval", 60f,
                new ConfigDescription("Seconds between writes of the exploration data when it changed.", new AcceptableValueRange<float>(5f, 3600f)));
        }
    }
}
