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
        private readonly PlayerHistory _history;
        private readonly CartographyTables _tables;
        private readonly string _epoch = DateTime.UtcNow.Ticks.ToString("x");
        private volatile string _pinsJson = EmptyPins;
        private int _pinsVersion;
        private int _pinsBuiltFor = -1;
        private int _deathsBuiltFor = -1;
        private ulong _onlineBuiltFor;
        private ulong _fogBuiltFor;
        public const string EmptyPins = "{\"version\":0,\"tables\":0,\"owners\":[],\"pins\":[],\"deaths\":[]}";
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

            _tiles = new TileService(_mask, HalfSize, cfg.MaxZoom.Value, _epoch);
            if (cfg.RevealGeneratedZones.Value) RevealGeneratedZones();

            if (cfg.TrackSessions.Value)
            {
                var options = new HistoryOptions
                {
                    TrackDeaths = cfg.TrackDeaths.Value,
                    RetentionDays = cfg.HistoryRetentionDays.Value,
                    ShowSessionCount = cfg.ShowSessionCount.Value,
                    ShowPlayTime = cfg.ShowPlayTime.Value,
                    ShowDeaths = cfg.ShowDeathCounts.Value,
                    ShowLastSeen = cfg.ShowLastSeen.Value,
                    RecentSessions = cfg.RecentSessions.Value,
                };
                string historyPath = Path.Combine(_dataDir, "history.json");
                try
                {
                    _history = new PlayerHistory(historyPath, options, DateTime.UtcNow);
                    if (_history.PlayerCount > 0) _log.LogInfo("Loaded play history for " + _history.PlayerCount + " player(s)");
                }
                catch (Exception e)
                {
                    _log.LogWarning("Could not read " + historyPath + ": " + e.Message + ". Starting a new history.");
                    _history = new PlayerHistory(historyPath + ".new", options, DateTime.UtcNow);
                }
            }

            if (cfg.ShowPins.Value || cfg.RevealFromCartographyTable.Value)
            {
                _tables = new CartographyTables(log, _mask, cfg.RevealFromCartographyTable.Value, cfg.ShowPins.Value,
                    cfg.CartographyScanInterval.Value);
            }

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
            _tables?.Tick(dt);
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
                // Players without a spawned character report the origin; the game does not reveal there either.
                if (!p.HasCharacter || p.Dead || p.Position.sqrMagnitude < 1f) continue;
                _mask.Reveal(p.Position.x, p.Position.z, radius);
            }
            if (_history != null)
            {
                EnvMan env = EnvMan.instance;
                _history.Update(_players, DateTime.UtcNow, env != null ? env.GetDay() : 0);
            }
            RefreshPins();
            _stateJson = BuildState();
        }

        // Rebuilds the pins document only when table pins, death positions or the set of online
        // characters changed; the last one matters because owner names and online flags come from it.
        private void RefreshPins()
        {
            PinSnapshot snapshot = _tables != null ? _tables.Snapshot : PinSnapshot.Empty;
            int deaths = _history != null ? _history.DeathsVersion : 0;
            ulong online = 0;
            foreach (PlayerEntry p in _players)
                if (p.PlayerId != 0) online ^= Hash.Fnv1a64(p.PlayerId + ":" + p.Name);
            // Exploration keeps growing while the pins stay the same, so track which pins the fog lets through.
            ulong fog = 0;
            if (_cfg.HidePinsInFog.Value)
                foreach (MergedPin pin in snapshot.Pins)
                    if (InExploredArea(pin)) fog ^= Hash.Fnv1a64(pin.Id);
            if (snapshot.Version == _pinsBuiltFor && deaths == _deathsBuiltFor && online == _onlineBuiltFor && fog == _fogBuiltFor) return;
            _pinsBuiltFor = snapshot.Version;
            _deathsBuiltFor = deaths;
            _onlineBuiltFor = online;
            _fogBuiltFor = fog;
            _pinsVersion++;
            _pinsJson = BuildPins(snapshot);
        }

        private sealed class OwnerInfo
        {
            public string Name;
            public bool Online;
            public int Pins;
        }

        private string BuildPins(PinSnapshot snapshot)
        {
            bool showPins = _cfg.ShowPins.Value;
            bool showChecked = _cfg.ShowCheckedPins.Value;
            bool showAutomated = _cfg.ShowAutomatedPins.Value;
            bool hideInFog = _cfg.HidePinsInFog.Value;
            bool showDeaths = _cfg.ShowDeathMarkers.Value && _cfg.TrackDeaths.Value && _history != null;

            var owners = new Dictionary<string, OwnerInfo>();
            var order = new List<string>();
            var j = new JsonWriter(1024 + snapshot.Pins.Count * 160);
            j.BeginObject();
            j.Prop("version", _pinsVersion);
            j.Prop("tables", snapshot.TableCount);

            j.Key("pins").BeginArray();
            if (showPins)
            {
                Func<MergedPin, bool> displayable = pin =>
                    (!pin.Checked || showChecked) && (!pin.Automated || showAutomated) && (!hideInFog || InExploredArea(pin));
                HashSet<string> shown = PinMerger.Select(snapshot.Pins, displayable, _cfg.PinMergeDistance.Value);
                foreach (MergedPin pin in snapshot.Pins)
                {
                    if (!shown.Contains(pin.Id)) continue;
                    // Automated pins group under the mod that wrote them; their owner ids are not player ids.
                    string ownerKey = pin.Automated ? "mod:" + pin.Author : pin.OwnerId.ToString();
                    OwnerInfo owner;
                    if (!owners.TryGetValue(ownerKey, out owner))
                    {
                        owner = new OwnerInfo();
                        if (pin.Automated) owner.Name = "Automated: " + pin.Author;
                        else ResolveOwner(pin.OwnerId, pin.Author, owner);
                        owners[ownerKey] = owner;
                        order.Add(ownerKey);
                    }
                    owner.Pins++;
                    j.BeginObject();
                    j.Prop("id", pin.Id);
                    j.Prop("owner", ownerKey);
                    j.Prop("type", SharedMapData.KindName(pin.Type));
                    j.Prop("name", pin.Name);
                    j.Prop("x", pin.X, 1);
                    j.Prop("z", pin.Z, 1);
                    j.Prop("checked", pin.Checked);
                    j.EndObject();
                }
            }
            j.EndArray();

            j.Key("owners").BeginArray();
            foreach (string id in order)
            {
                OwnerInfo owner = owners[id];
                j.BeginObject();
                j.Prop("id", id);
                j.Prop("name", owner.Name);
                j.Prop("online", owner.Online);
                j.Prop("pins", owner.Pins);
                j.EndObject();
            }
            j.EndArray();

            j.Key("deaths").BeginArray();
            if (showDeaths) _history.WriteDeaths(j, _cfg.DeathMarkersPerPlayer.Value);
            j.EndArray();
            j.EndObject();
            return j.ToString();
        }

        private bool InExploredArea(MergedPin pin)
        {
            return _mask.Sample(pin.X, pin.Z) >= 0.5f;
        }

        private void ResolveOwner(long playerId, string author, OwnerInfo into)
        {
            foreach (PlayerEntry p in _players)
            {
                if (p.PlayerId == playerId && playerId != 0)
                {
                    into.Name = p.Name;
                    into.Online = true;
                    return;
                }
            }
            string name;
            if (_history != null && (_history.TryResolveCharacter(playerId, out name) || _history.TryResolveAuthor(author, out name)))
            {
                into.Name = name;
                return;
            }
            into.Name = null;
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
            j.Prop("epoch", _epoch);
            j.Prop("mapId", atlas != null ? atlas.Id : 0);
            j.Prop("exploreVersion", _mask.Version);
            j.Prop("pinsVersion", _pinsVersion);
            if (_cfg.ShowExploredPercent.Value) j.Prop("exploredPercent", _mask.ExploredPercent, 2);
            EnvMan env = EnvMan.instance;
            if (env != null && _cfg.ShowDayAndTime.Value)
            {
                j.Prop("day", env.GetDay());
                j.Prop("timeOfDay", env.GetDayFraction(), 4);
            }
            j.Key("players").BeginArray();
            foreach (PlayerEntry p in _players)
            {
                if (!p.Visible && !_cfg.ShowHiddenPlayers.Value) continue;
                j.BeginObject();
                j.Prop("id", p.Id);
                j.Prop("name", p.Name);
                j.Prop("visible", p.Visible);
                if (_cfg.ShowDeadStatus.Value) j.Prop("dead", p.Dead);
                DateTime since;
                int sessionDeaths, totalDeaths;
                if (_history != null && _history.TryGetLive(p.Id, out since, out sessionDeaths, out totalDeaths))
                {
                    if (_cfg.ShowTimeOnline.Value) j.Prop("since", PlayerHistory.Iso(since));
                    if (_cfg.ShowDeathCounts.Value && _cfg.TrackDeaths.Value)
                    {
                        j.Prop("sessionDeaths", sessionDeaths);
                        j.Prop("deaths", totalDeaths);
                    }
                }
                if (p.Visible)
                {
                    j.Prop("x", p.Position.x, 1);
                    j.Prop("z", p.Position.z, 1);
                    j.Prop("y", p.Position.y, 1);
                    if (_cfg.ShowHeading.Value) j.Prop("yaw", p.Yaw, 0);
                    if (_cfg.ShowBiome.Value) j.Prop("biome", p.Biome);
                }
                j.EndObject();
            }
            j.EndArray();
            j.EndObject();
            return j.ToString();
        }

        public string StateJson => _stateJson;
        public string HistoryJson => _history != null && _cfg.ShowHistory.Value ? _history.Json : "{\"players\":[]}";
        public string PinsJson => _pinsJson;

        public void SaveIfDirty()
        {
            if (_mask.Dirty)
            {
                try
                {
                    _mask.Save(_exploredPath);
                }
                catch (Exception e)
                {
                    _log.LogWarning("Could not save exploration data: " + e.Message);
                }
            }
            if (_history != null && _history.Dirty)
            {
                try
                {
                    _history.Save();
                }
                catch (Exception e)
                {
                    _log.LogWarning("Could not save play history: " + e.Message);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _tables?.Dispose();
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
