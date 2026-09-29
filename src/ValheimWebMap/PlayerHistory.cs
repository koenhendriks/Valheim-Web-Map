using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ValheimWebMap
{
    internal sealed class SessionRecord
    {
        public string Character;
        public DateTime Start;
        public DateTime End;
        public int Deaths;

        [JsonIgnore]
        public double Seconds => Math.Max(0, (End - Start).TotalSeconds);
    }

    internal sealed class DeathRecord
    {
        public DateTime Time;
        public int Day;
        public string Character;
    }

    internal sealed class PlayerRecord
    {
        public string Id;
        public string Name;
        public List<SessionRecord> Sessions = new List<SessionRecord>();
        public List<DeathRecord> Deaths = new List<DeathRecord>();

        [JsonIgnore]
        public double TotalSeconds
        {
            get
            {
                double s = 0;
                foreach (SessionRecord r in Sessions) s += r.Seconds;
                return s;
            }
        }

        [JsonIgnore]
        public DateTime LastSeen => Sessions.Count > 0 ? Sessions[Sessions.Count - 1].End : DateTime.MinValue;
    }

    internal sealed class HistoryFile
    {
        public int Version = 1;
        public List<PlayerRecord> Players = new List<PlayerRecord>();
    }

    /// <summary>What gets recorded and what the history API hands out. Plain values so this file has no BepInEx dependency.</summary>
    internal sealed class HistoryOptions
    {
        public bool TrackDeaths = true;
        public int RetentionDays;
        public bool ShowSessionCount = true;
        public bool ShowPlayTime = true;
        public bool ShowDeaths = true;
        public bool ShowLastSeen = true;
        public int RecentSessions = 30;
    }

    /// <summary>
    /// Per-player play sessions and deaths, derived from the peer list each tick. A session runs from
    /// the moment a connection has a player name until it disappears, so the respawn gap after a death
    /// (character id goes to None for ten seconds) does not split it. Main thread only.
    /// </summary>
    internal sealed class PlayerHistory
    {
        private sealed class Live
        {
            public PlayerRecord Player;
            public SessionRecord Session;
            public string Character = "";
            public bool DeathCounted;
        }

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            Formatting = Formatting.Indented,
        };

        private readonly string _path;
        private readonly HistoryOptions _options;
        private readonly HistoryFile _file;
        private readonly Dictionary<string, PlayerRecord> _byId = new Dictionary<string, PlayerRecord>();
        private readonly Dictionary<long, Live> _live = new Dictionary<long, Live>();
        private readonly List<long> _gone = new List<long>();
        private volatile string _json;
        private bool _dirty;

        public PlayerHistory(string path) : this(path, new HistoryOptions(), DateTime.UtcNow)
        {
        }

        public PlayerHistory(string path, HistoryOptions options, DateTime now)
        {
            _path = path;
            _options = options;
            _file = Load(path) ?? new HistoryFile();
            if (options.RetentionDays > 0) Prune(now.AddDays(-options.RetentionDays));
            foreach (PlayerRecord p in _file.Players)
                if (!string.IsNullOrEmpty(p.Id)) _byId[p.Id] = p;
            _json = BuildJson();
        }

        private void Prune(DateTime cutoff)
        {
            int before = 0, after = 0;
            foreach (PlayerRecord p in _file.Players)
            {
                before += p.Sessions.Count + p.Deaths.Count;
                p.Sessions.RemoveAll(s => s.End < cutoff);
                p.Deaths.RemoveAll(d => d.Time < cutoff);
                after += p.Sessions.Count + p.Deaths.Count;
            }
            _file.Players.RemoveAll(p => p.Sessions.Count == 0 && p.Deaths.Count == 0);
            if (after != before) _dirty = true;
        }

        public string Json => _json;
        public bool Dirty => _dirty;
        public int PlayerCount => _file.Players.Count;

        public bool TryGetLive(long peerId, out DateTime sessionStart, out int sessionDeaths, out int totalDeaths)
        {
            Live live;
            if (_live.TryGetValue(peerId, out live))
            {
                sessionStart = live.Session.Start;
                sessionDeaths = live.Session.Deaths;
                totalDeaths = live.Player.Deaths.Count;
                return true;
            }
            sessionStart = DateTime.MinValue;
            sessionDeaths = 0;
            totalDeaths = 0;
            return false;
        }

        public void Update(List<PlayerEntry> players, DateTime now, int day)
        {
            bool changed = false;
            foreach (PlayerEntry p in players)
            {
                Live live;
                if (!_live.TryGetValue(p.Id, out live))
                {
                    live = new Live { Player = RecordFor(p.Key, p.Name) };
                    live.Session = new SessionRecord { Character = p.Name, Start = now, End = now };
                    live.Player.Sessions.Add(live.Session);
                    live.Player.Name = p.Name;
                    _live[p.Id] = live;
                    changed = true;
                }
                live.Session.End = now;

                if (p.HasCharacter && p.CharacterKey != live.Character)
                {
                    // A new character while still connected means the old one was destroyed, which
                    // outside a death only happens on logout, and logout drops the connection.
                    if (live.Character.Length > 0 && !live.DeathCounted) { RecordDeath(live, now, day); changed = true; }
                    live.Character = p.CharacterKey;
                    live.DeathCounted = false;
                }
                if (p.Dead && !live.DeathCounted)
                {
                    RecordDeath(live, now, day);
                    changed = true;
                }
            }

            _gone.Clear();
            foreach (KeyValuePair<long, Live> kv in _live)
            {
                bool present = false;
                foreach (PlayerEntry p in players)
                    if (p.Id == kv.Key) { present = true; break; }
                if (!present) _gone.Add(kv.Key);
            }
            foreach (long id in _gone)
            {
                _live[id].Session.End = now;
                _live.Remove(id);
                changed = true;
            }

            if (changed || _live.Count > 0)
            {
                _dirty = true;
                _json = BuildJson();
            }
        }

        private void RecordDeath(Live live, DateTime now, int day)
        {
            live.DeathCounted = true;
            if (!_options.TrackDeaths) return;
            live.Session.Deaths++;
            live.Player.Deaths.Add(new DeathRecord { Time = now, Day = day, Character = live.Session.Character });
        }

        private PlayerRecord RecordFor(string key, string name)
        {
            PlayerRecord record;
            if (_byId.TryGetValue(key, out record)) return record;
            record = new PlayerRecord { Id = key, Name = name };
            _byId[key] = record;
            _file.Players.Add(record);
            return record;
        }

        private string BuildJson()
        {
            var sorted = new List<PlayerRecord>(_file.Players);
            sorted.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));

            var j = new JsonWriter(512 + sorted.Count * 400);
            j.BeginObject();
            j.Key("players").BeginArray();
            foreach (PlayerRecord p in sorted)
            {
                bool online = false;
                foreach (Live live in _live.Values)
                    if (live.Player == p) { online = true; break; }

                j.BeginObject();
                j.Prop("name", p.Name);
                j.Prop("online", online);
                if (_options.ShowSessionCount) j.Prop("sessions", p.Sessions.Count);
                if (_options.ShowPlayTime) j.Prop("playSeconds", (long)p.TotalSeconds);
                if (_options.ShowDeaths && _options.TrackDeaths) j.Prop("deaths", p.Deaths.Count);
                if (_options.ShowLastSeen) j.Prop("lastSeen", p.LastSeen == DateTime.MinValue ? null : Iso(p.LastSeen));
                if (_options.RecentSessions > 0)
                {
                    j.Key("recent").BeginArray();
                    for (int i = p.Sessions.Count - 1, n = 0; i >= 0 && n < _options.RecentSessions; i--, n++)
                    {
                        SessionRecord s = p.Sessions[i];
                        j.BeginObject();
                        j.Prop("character", s.Character);
                        j.Prop("start", Iso(s.Start));
                        j.Prop("end", Iso(s.End));
                        if (_options.ShowPlayTime) j.Prop("seconds", (long)s.Seconds);
                        if (_options.ShowDeaths && _options.TrackDeaths) j.Prop("deaths", s.Deaths);
                        j.EndObject();
                    }
                    j.EndArray();
                }
                j.EndObject();
            }
            j.EndArray();
            j.EndObject();
            return j.ToString();
        }

        public static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        public void Save()
        {
            string tmp = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(tmp, JsonConvert.SerializeObject(_file, Settings));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
            _dirty = false;
        }

        private static HistoryFile Load(string path)
        {
            if (!File.Exists(path)) return null;
            HistoryFile file = JsonConvert.DeserializeObject<HistoryFile>(File.ReadAllText(path), Settings);
            if (file == null) return null;
            if (file.Players == null) file.Players = new List<PlayerRecord>();
            foreach (PlayerRecord p in file.Players)
            {
                if (p.Sessions == null) p.Sessions = new List<SessionRecord>();
                if (p.Deaths == null) p.Deaths = new List<DeathRecord>();
            }
            return file;
        }
    }
}
