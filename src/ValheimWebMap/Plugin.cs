using System;
using BepInEx;
using UnityEngine;

namespace ValheimWebMap
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin, IMapApi
    {
        private PluginConfig _cfg;
        private WebServer _server;
        private MapSession _session;
        private string _emptyState;

        private void Awake()
        {
            _cfg = new PluginConfig(Config);
            _emptyState = new JsonWriter().BeginObject().Prop("world", (string)null).Prop("mapReady", false)
                .Prop("pinsVersion", 0).Key("players").BeginArray().EndArray().EndObject().ToString();

        }

        private void Update()
        {
            if (_session == null)
            {
                if (WorldIsReady()) StartSession();
                return;
            }

            if (ZNet.instance == null)
            {
                EndSession();
                return;
            }

            _session.Tick(Time.unscaledDeltaTime);
        }

        // Only act as the server: ZNet exists, the world generator is up and the 1.0 biome layout
        // (built synchronously while the world loads) is complete.
        private static bool WorldIsReady()
        {
            ZNet znet = ZNet.instance;
            if (znet == null || !znet.IsServer()) return false;
            if (WorldGenerator.instance == null || ZoneSystem.instance == null) return false;
            World world = ZNet.World;
            return world != null && world.m_biomeData != null && world.m_biomeData.IsReady;
        }

        private void StartSession()
        {
            try
            {
                _session = new MapSession(_cfg, Logger, ZNet.World);
                Logger.LogInfo("Tracking world '" + _session.WorldName + "'");
            }
            catch (Exception e)
            {
                Logger.LogError("Could not start map session: " + e);
                enabled = false;
                return;
            }

            // The listener only exists while this process is the server, so a copy of the plugin
            // that ends up on a client never opens a port.
            if (_server != null) return;
            _server = new WebServer(Logger, this);
            try
            {
                _server.Start(_cfg.Host.Value, _cfg.Port.Value);
            }
            catch (Exception e)
            {
                Logger.LogError("Could not start the web server on port " + _cfg.Port.Value + ": " + e.Message);
                _server.Dispose();
                _server = null;
            }
        }

        private void EndSession()
        {
            if (_session == null) return;
            _session.Dispose();
            _session = null;
            _server?.Dispose();
            _server = null;
            Logger.LogInfo("World closed; map tracking stopped");
        }

        private void OnDestroy()
        {
            EndSession();
        }

        private void OnApplicationQuit()
        {
            EndSession();
        }

        public string InfoJson()
        {
            var j = new JsonWriter();
            j.BeginObject();
            j.Prop("world", _session?.WorldName);
            j.Prop("mapHalfSize", MapSession.HalfSize, 0);
            j.Prop("worldRadius", WorldGenerator.worldSize, 0);
            j.Prop("tileSize", MapAtlas.TileSize);
            j.Prop("maxZoom", _cfg.MaxZoom.Value);
            j.Prop("updateInterval", _cfg.UpdateInterval.Value, 2);
            j.Prop("version", MyPluginInfo.PLUGIN_VERSION);
            // What the page may show; the API already omits the data behind a disabled switch.
            j.Key("features").BeginObject();
            j.Prop("history", _cfg.ShowHistory.Value && _cfg.TrackSessions.Value);
            j.Prop("hiddenPlayers", _cfg.ShowHiddenPlayers.Value);
            j.Prop("biome", _cfg.ShowBiome.Value);
            j.Prop("coordinates", _cfg.ShowCoordinates.Value);
            j.Prop("heading", _cfg.ShowHeading.Value);
            j.Prop("timeOnline", _cfg.ShowTimeOnline.Value && _cfg.TrackSessions.Value);
            j.Prop("deadStatus", _cfg.ShowDeadStatus.Value);
            j.Prop("deathCounts", _cfg.ShowDeathCounts.Value && _cfg.TrackDeaths.Value && _cfg.TrackSessions.Value);
            j.Prop("dayTime", _cfg.ShowDayAndTime.Value);
            j.Prop("explored", _cfg.ShowExploredPercent.Value);
            j.Prop("pins", _cfg.ShowPins.Value);
            j.Prop("checkedPins", _cfg.ShowPins.Value && _cfg.ShowCheckedPins.Value);
            j.Prop("automatedPins", _cfg.ShowPins.Value && _cfg.ShowAutomatedPins.Value);
            j.Prop("pinsInFog", !_cfg.HidePinsInFog.Value);
            j.Prop("deathMarkers", _cfg.ShowDeathMarkers.Value && _cfg.TrackDeaths.Value && _cfg.TrackSessions.Value);
            j.Prop("tableReveal", _cfg.RevealFromCartographyTable.Value);
            j.EndObject();
            j.EndObject();
            return j.ToString();
        }

        public string StateJson()
        {
            MapSession s = _session;
            return s != null ? s.StateJson : _emptyState;
        }

        public string HistoryJson()
        {
            MapSession s = _session;
            return s != null ? s.HistoryJson : "{\"players\":[]}";
        }

        public string PinsJson()
        {
            MapSession s = _session;
            return s != null ? s.PinsJson : MapSession.EmptyPins;
        }

        public bool TryGetTile(int z, int x, int y, out byte[] png, out string etag)
        {
            MapSession s = _session;
            if (s == null)
            {
                png = null;
                etag = null;
                return false;
            }
            return s.Tiles.TryGetTile(z, x, y, out png, out etag);
        }
    }
}
