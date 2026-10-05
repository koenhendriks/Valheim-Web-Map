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
        /// <summary>Where the player died. Only recorded when they were sharing their position at the time.</summary>
        public float? X;
        public float? Z;
    }

    /// <summary>A character seen on this account, by the persistent id that cartography table pins carry as owner.</summary>
    internal sealed class CharacterRecord
    {
        public long PlayerId;
        public string Name;
        public DateTime LastSeen;
    }

    internal sealed class PlayerRecord
    {
        public string Id;
        public string Name;
        public List<SessionRecord> Sessions = new List<SessionRecord>();
        public List<DeathRecord> Deaths = new List<DeathRecord>();
        public List<CharacterRecord> Characters = new List<CharacterRecord>();

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
        private readonly Dictionary<long, CharacterRecord> _characters = new Dictionary<long, CharacterRecord>();
        private readonly Dictionary<long, Live> _live = new Dictionary<long, Live>();
        private readonly List<long> _gone = new List<long>();
        private volatile string _json;
        private bool _dirty;
        private int _deathsVersion;

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
            {
                if (!string.IsNullOrEmpty(p.Id)) _byId[p.Id] = p;
                foreach (CharacterRecord c in p.Characters)
                    if (c.PlayerId != 0) _characters[c.PlayerId] = c;
            }
            _json = BuildJson();
        }

        /// <summary>Changes whenever a death with a position is recorded.</summary>
        public int DeathsVersion => _deathsVersion;

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
                if (p.HasCharacter && p.PlayerId != 0 && RememberCharacter(live.Player, p.PlayerId, p.Name, now)) changed = true;

                if (p.HasCharacter && p.CharacterKey != live.Character)
                {
                    // A new character while still connected means the old one was destroyed, which
                    // outside a death only happens on logout, and logout drops the connection. The
                    // new character already stands at the spawn point, so there is no death position.
                    if (live.Character.Length > 0 && !live.DeathCounted) { RecordDeath(live, now, day, null); changed = true; }
                    live.Character = p.CharacterKey;
                    live.DeathCounted = false;
                }
                if (p.Dead && !live.DeathCounted)
                {
                    RecordDeath(live, now, day, p.Visible ? p : (PlayerEntry?)null);
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

        private void RecordDeath(Live live, DateTime now, int day, PlayerEntry? at)
        {
            live.DeathCounted = true;
            if (!_options.TrackDeaths) return;
            live.Session.Deaths++;
            var death = new DeathRecord { Time = now, Day = day, Character = live.Session.Character };
            if (at.HasValue)
            {
                death.X = at.Value.Position.x;
                death.Z = at.Value.Position.z;
                _deathsVersion++;
            }
            live.Player.Deaths.Add(death);
        }

        private bool RememberCharacter(PlayerRecord player, long playerId, string name, DateTime now)
        {
            CharacterRecord c;
            if (_characters.TryGetValue(playerId, out c))
            {
                if (c.Name == name) return false;
                c.Name = name;
                c.LastSeen = now;
                return true;
            }
            c = new CharacterRecord { PlayerId = playerId, Name = name, LastSeen = now };
            _characters[playerId] = c;
            player.Characters.Add(c);
            return true;
        }

        public bool TryResolveCharacter(long playerId, out string name)
        {
            CharacterRecord c;
            if (playerId != 0 && _characters.TryGetValue(playerId, out c))
            {
                name = c.Name;
                return true;
            }
            name = null;
            return false;
        }

        /// <summary>
        /// Matches a pin author such as "Steam_7656..." to an account key, which is the bare platform id,
        /// and answers with the account's last used character name.
        /// </summary>
        public bool TryResolveAuthor(string author, out string name)
        {
            if (!string.IsNullOrEmpty(author))
            {
                foreach (PlayerRecord p in _file.Players)
                {
                    if (!string.IsNullOrEmpty(p.Id) && !p.Id.StartsWith("name:") && author.EndsWith("_" + p.Id, StringComparison.Ordinal))
                    {
                        name = p.Name;
                        return true;
                    }
                }
            }
            name = null;
            return false;
        }

        /// <summary>Writes deaths that have a position, most recent first per player, at most perPlayer each (0 = all).</summary>
        public void WriteDeaths(JsonWriter j, int perPlayer)
        {
            foreach (PlayerRecord p in _file.Players)
            {
                int written = 0;
                for (int i = p.Deaths.Count - 1; i >= 0 && (perPlayer <= 0 || written < perPlayer); i--)
                {
                    DeathRecord d = p.Deaths[i];
                    if (!d.X.HasValue || !d.Z.HasValue) continue;
                    j.BeginObject();
                    j.Prop("name", d.Character ?? p.Name);
                    j.Prop("day", d.Day);
                    j.Prop("time", Iso(d.Time));
                    j.Prop("x", d.X.Value, 1);
                    j.Prop("z", d.Z.Value, 1);
                    j.EndObject();
                    written++;
                }
            }
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
                if (p.Characters == null) p.Characters = new List<CharacterRecord>();
            }
            return file;
        }
    }
}
