using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;

namespace ValheimWebMap
{
    /// <summary>Everything tied to one loaded world: fog state, rendered map, live player snapshot.</summary>
    internal sealed class MapSession : IDisposable
    {
        /// <summary>Half the width of the square the map covers, in metres. The world is a 10 km radius circle.</summary>
        public const float HalfSize = 10240f;
        private const int ProvisionalResolution = 1024;

        private static readonly FieldInfo GeneratedZonesField =
            typeof(ZoneSystem).GetField("m_generatedZones", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly PluginConfig _cfg;
        private readonly ManualLogSource _log;
        private readonly World _world;
        private readonly string _dataDir;
        private readonly string _exploredPath;
        private readonly ExploredMask _mask;
        private readonly TileService _tiles;
        private readonly List<PlayerEntry> _players = new List<PlayerEntry>();
        private readonly Thread _renderThread;
        private volatile bool _stop;
        private volatile string _stateJson;
        private volatile string _renderError;
        private float _renderProgress;
        private int _atlasCounter;
        private float _sinceUpdate;
        private float _sinceSave;

        public string WorldName => _world.m_name;
        public TileService Tiles => _tiles;

        public MapSession(PluginConfig cfg, ManualLogSource log, World world)
        {
            _cfg = cfg;
            _log = log;
            _world = world;

            string root = string.IsNullOrEmpty(cfg.DataDirectory.Value)
                ? Path.Combine(BepInEx.Paths.ConfigPath, "ValheimWebMap")
                : cfg.DataDirectory.Value;
            _dataDir = Path.Combine(root, SafeFileName(world.m_name));
            Directory.CreateDirectory(_dataDir);
            _exploredPath = Path.Combine(_dataDir, "explored.bin");

            _mask = new ExploredMask(HalfSize);
            try
            {
                if (_mask.TryLoad(_exploredPath))
                    _log.LogInfo($"Loaded exploration data ({_mask.ExploredPercent:F1}% of the world explored)");
            }
            catch (Exception e)
            {
                _log.LogWarning("Could not read " + _exploredPath + ": " + e.Message + ". Starting with an empty map.");
            }

            _tiles = new TileService(_mask, HalfSize, cfg.MaxZoom.Value);
            if (cfg.RevealGeneratedZones.Value) RevealGeneratedZones();

            _stateJson = BuildState();
            _renderThread = new Thread(RenderWorker)
            {
                IsBackground = true,
                Name = "WebMap render",
                Priority = System.Threading.ThreadPriority.BelowNormal,
            };
            _renderThread.Start();
        }

        /// <summary>
        /// Zones only get generated close to a player, so the save's list of generated zones is a
        /// record of where people have been, including before this mod existed.
        /// </summary>
        private void RevealGeneratedZones()
        {
            ZoneSystem zs = ZoneSystem.instance;
            var zones = zs != null && GeneratedZonesField != null ? GeneratedZonesField.GetValue(zs) as HashSet<Vector2s> : null;
            if (zones == null)
            {
                _log.LogWarning("Could not read generated zones from ZoneSystem; skipping reveal of visited zones.");
                return;
            }

            int margin = _cfg.RevealGeneratedZonesMargin.Value;
            float zoneSize = zs.m_zoneSize;
            int revealedZones = 0;
            int newCells = 0;
            foreach (Vector2s zone in zones)
            {
                bool inside = true;
                for (int dy = -margin; dy <= margin && inside; dy++)
                    for (int dx = -margin; dx <= margin; dx++)
                        if (!zones.Contains(new Vector2s(zone.x + dx, zone.y + dy)))
                        {
                            inside = false;
                            break;
                        }
                if (!inside) continue;
                float cx = zone.x * zoneSize;
                float cz = zone.y * zoneSize;
                int cells = _mask.RevealRect(cx - zoneSize / 2f, cz - zoneSize / 2f, cx + zoneSize / 2f, cz + zoneSize / 2f);
                if (cells > 0)
                {
                    revealedZones++;
                    newCells += cells;
                }
            }
            if (revealedZones > 0)
                _log.LogInfo($"Revealed {revealedZones} previously visited zones ({_mask.ExploredPercent:F1}% of the world explored)");
        }

        private void RenderWorker()
        {
            try
            {
                int size = _cfg.Resolution.Value;
                int threads = _cfg.RenderThreads.Value > 0 ? _cfg.RenderThreads.Value : Math.Max(1, Environment.ProcessorCount / 2);
                string cachePath = Path.Combine(_dataDir, "basemap_" + size + ".bin");

                byte[] rgb = MapRenderer.TryLoadCache(cachePath, size, _world);
                if (rgb != null)
                {
                    _log.LogInfo("Loaded cached world map (" + size + " px)");
                    PublishAtlas(size, rgb);
                    _renderProgress = 1f;
                    return;
                }

                if (size > ProvisionalResolution)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] quick = MapRenderer.Render(ProvisionalResolution, HalfSize, threads, p => { }, () => _stop);
                    if (quick == null || _stop) return;
                    PublishAtlas(ProvisionalResolution, quick);
                    _log.LogInfo($"Rendered preview world map ({ProvisionalResolution} px) in {sw.Elapsed.TotalSeconds:F1}s; rendering {size} px map with {threads} thread(s)");
                }

                var full = System.Diagnostics.Stopwatch.StartNew();
                rgb = MapRenderer.Render(size, HalfSize, threads, p => _renderProgress = p, () => _stop);
                if (rgb == null || _stop) return;
                PublishAtlas(size, rgb);
                _renderProgress = 1f;
                _log.LogInfo($"Rendered world map ({size} px) in {full.Elapsed.TotalSeconds:F1}s");
                MapRenderer.SaveCache(cachePath, rgb, size, _world);
            }
            catch (Exception e)
            {
                _renderError = e.Message;
                _log.LogError("World map render failed: " + e);
            }
        }

        private void PublishAtlas(int size, byte[] rgb)
        {
            int id = Interlocked.Increment(ref _atlasCounter);
            _tiles.Atlas = new MapAtlas(id, size, HalfSize, rgb);
        }

        /// <summary>Main thread, once per frame.</summary>
        public void Tick(float dt)
        {
            _sinceUpdate += dt;
            _sinceSave += dt;
            if (_sinceUpdate >= _cfg.UpdateInterval.Value)
            {
                _sinceUpdate = 0f;
                UpdatePlayers();
            }
            if (_sinceSave >= _cfg.SaveInterval.Value)
            {
                _sinceSave = 0f;
                SaveIfDirty();
            }
        }

        private void UpdatePlayers()
        {
            ZNet znet = ZNet.instance;
            if (znet == null) return;
            PlayerTracker.Collect(znet, _players);
            float radius = _cfg.ExploreRadius.Value;
            foreach (PlayerEntry p in _players)
            {
                // Dead or not yet spawned players sit at the origin; the game does not reveal there either.
                if (p.Position.sqrMagnitude < 1f) continue;
                _mask.Reveal(p.Position.x, p.Position.z, radius);
            }
            _stateJson = BuildState();
        }

        private string BuildState()
        {
            MapAtlas atlas = _tiles.Atlas;
            var j = new JsonWriter(512 + _players.Count * 160);
            j.BeginObject();
            j.Prop("world", _world.m_name);
            j.Prop("mapReady", atlas != null);
            j.Prop("renderProgress", _renderProgress, 3);
            j.Prop("renderError", _renderError);
            j.Prop("nativeZoom", atlas != null ? atlas.NativeZoom : 0);
            j.Prop("mapId", atlas != null ? atlas.Id : 0);
            j.Prop("exploreVersion", _mask.Version);
            j.Prop("exploredPercent", _mask.ExploredPercent, 2);
            EnvMan env = EnvMan.instance;
            if (env != null)
            {
                j.Prop("day", env.GetDay());
                j.Prop("timeOfDay", env.GetDayFraction(), 4);
            }
            j.Key("players").BeginArray();
            foreach (PlayerEntry p in _players)
            {
                j.BeginObject();
                j.Prop("id", p.Id);
                j.Prop("name", p.Name);
                j.Prop("visible", p.Visible);
                if (p.Visible)
                {
                    j.Prop("x", p.Position.x, 1);
                    j.Prop("z", p.Position.z, 1);
                    j.Prop("y", p.Position.y, 1);
                    j.Prop("yaw", p.Yaw, 0);
                    j.Prop("biome", p.Biome);
                }
                j.EndObject();
            }
            j.EndArray();
            j.EndObject();
            return j.ToString();
        }

        public string StateJson => _stateJson;

        public void SaveIfDirty()
        {
            if (!_mask.Dirty) return;
            try
            {
                _mask.Save(_exploredPath);
            }
            catch (Exception e)
            {
                _log.LogWarning("Could not save exploration data: " + e.Message);
            }
        }

        public void Dispose()
        {
            _stop = true;
            SaveIfDirty();
        }

        private static string SafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            string s = new string(chars).Trim();
            return s.Length == 0 ? "world" : s;
        }
    }
}
