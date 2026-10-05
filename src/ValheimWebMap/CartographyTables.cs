using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BepInEx.Logging;

namespace ValheimWebMap
{
    internal sealed class MergedPin
    {
        public string Id;
        public long OwnerId;
        public string Author;
        public string Name;
        public int Type;
        public float X, Z;
        public bool Checked;
        /// <summary>Written by a server-side mod rather than by a player.</summary>
        public bool Automated;
    }

    internal sealed class PinSnapshot
    {
        public static readonly PinSnapshot Empty = new PinSnapshot { Pins = new List<MergedPin>() };
        public int Version;
        public int TableCount;
        public int TablesWithData;
        public List<MergedPin> Pins;
    }

    /// <summary>
    /// Finds every cartography table in the world and keeps up with what players shared on them.
    /// The ZDO scan and all ZDO reads happen on the main thread, spread over frames; decompressing
    /// and decoding the shared map happens on the thread pool; the results are applied on the main
    /// thread again, fog in time-boxed chunks so a big bitmap does not stall a frame.
    /// </summary>
    internal sealed class CartographyTables : IDisposable
    {
        public const string PrefabName = "piece_cartographytable";
        private const int MaxCompressedBytes = 16 * 1024 * 1024;
        private const double FogBudgetMs = 2.0;
        private const int RectsPerChunk = 256;

        private sealed class TableState
        {
            public ulong Hash;
            public int Length;
            public bool Bad;
            public bool HasData;
            public List<TablePin> Pins = new List<TablePin>();
        }

        private sealed class ParsedTable
        {
            public ZDOID Uid;
            public ulong Hash;
            public bool Bad;
            public string Error;
            public List<TablePin> Pins;
            public List<RectF> Rects;
        }

        private readonly ManualLogSource _log;
        private readonly ExploredMask _mask;
        private readonly bool _revealFog;
        private readonly bool _collectPins;
        private readonly float _scanInterval;
        private readonly Dictionary<ZDOID, TableState> _tables = new Dictionary<ZDOID, TableState>();
        private readonly List<ZDO> _scanList = new List<ZDO>();
        private readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();
        private readonly object _resultLock = new object();
        private readonly List<ParsedTable> _results = new List<ParsedTable>();
        private readonly Queue<List<RectF>> _pendingReveals = new Queue<List<RectF>>();
        private int _pendingOffset;
        private int _scanIndex;
        private bool _scanning;
        private bool _scannedOnce;
        private float _sinceScan;
        private bool _pinsDirty;
        private int _lastLoggedCount = -1;
        private volatile bool _stop;
        private volatile PinSnapshot _snapshot = PinSnapshot.Empty;

        public CartographyTables(ManualLogSource log, ExploredMask mask, bool revealFog, bool collectPins, float scanInterval)
        {
            _log = log;
            _mask = mask;
            _revealFog = revealFog;
            _collectPins = collectPins;
            _scanInterval = scanInterval;
            _sinceScan = scanInterval; // first scan right away
        }

        public PinSnapshot Snapshot => _snapshot;

        /// <summary>Main thread, every frame.</summary>
        public void Tick(float dt)
        {
            DrainResults();
            ApplyPendingFog();
            if (_pinsDirty && _pendingReveals.Count == 0)
            {
                _pinsDirty = false;
                PublishSnapshot();
            }

            _sinceScan += dt;
            if (!_scanning && _sinceScan >= _scanInterval)
            {
                _sinceScan = 0f;
                _scanIndex = 0;
                _scanList.Clear();
                _scanning = true;
            }
            if (_scanning) ScanStep();
        }

        private void ScanStep()
        {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null)
            {
                _scanning = false;
                return;
            }
            bool done;
            try
            {
                done = zdoMan.GetAllZDOsWithPrefabIterative(PrefabName, _scanList, ref _scanIndex);
            }
            catch (Exception e)
            {
                _log.LogWarning("Cartography table scan failed: " + e.Message);
                _scanning = false;
                return;
            }
            if (!done) return;
            _scanning = false;
            ProcessScan();
        }

        private void ProcessScan()
        {
            _seen.Clear();
            int withData = 0;
            foreach (ZDO zdo in _scanList)
            {
                if (zdo == null || !_seen.Add(zdo.m_uid)) continue;
                TableState state;
                if (!_tables.TryGetValue(zdo.m_uid, out state))
                {
                    state = new TableState();
                    _tables[zdo.m_uid] = state;
                    _pinsDirty = true;
                }

                byte[] data = zdo.GetByteArray(ZDOVars.s_data, null);
                if (data == null || data.Length == 0)
                {
                    if (state.HasData) { state.HasData = false; state.Pins.Clear(); state.Hash = 0; state.Length = 0; _pinsDirty = true; }
                    continue;
                }
                withData++;
                if (data.Length > MaxCompressedBytes)
                {
                    if (!state.Bad) _log.LogWarning("Cartography table " + zdo.m_uid + " holds " + data.Length + " bytes of map data; ignoring it");
                    state.Bad = true;
                    continue;
                }
                ulong hash = Hash.Fnv1a64(data);
                if (state.Length == data.Length && state.Hash == hash) continue;
                state.Hash = hash;
                state.Length = data.Length;
                state.Bad = false;
                var item = new ParsedTable { Uid = zdo.m_uid, Hash = hash };
                var copy = (byte[])data.Clone();
                ThreadPool.QueueUserWorkItem(_ => ParseWorker(item, copy));
            }

            var gone = new List<ZDOID>();
            foreach (ZDOID uid in _tables.Keys)
                if (!_seen.Contains(uid)) gone.Add(uid);
            foreach (ZDOID uid in gone)
            {
                _tables.Remove(uid);
                _pinsDirty = true;
            }

            if (!_scannedOnce || _lastLoggedCount != _tables.Count)
            {
                _log.LogInfo("Found " + _tables.Count + " cartography table(s), " + withData + " with shared map data");
                _lastLoggedCount = _tables.Count;
            }
            _scannedOnce = true;
            _scanList.Clear();
        }

        private void ParseWorker(ParsedTable item, byte[] compressed)
        {
            try
            {
                byte[] raw = Utils.Decompress(compressed);
                SharedMapData map = SharedMapData.Parse(raw);
                item.Pins = _collectPins ? map.Pins : new List<TablePin>();
                item.Rects = _revealFog
                    ? ExploredRuns.FromBitmap(map.Explored, map.TextureSize, map.PixelSize, MapSession.HalfSize)
                    : null;
            }
            catch (Exception e)
            {
                item.Bad = true;
                item.Error = e.Message;
            }
            if (_stop) return;
            lock (_resultLock) _results.Add(item);
        }

        private void DrainResults()
        {
            List<ParsedTable> batch = null;
            lock (_resultLock)
            {
                if (_results.Count > 0)
                {
                    batch = new List<ParsedTable>(_results);
                    _results.Clear();
                }
            }
            if (batch == null) return;
            foreach (ParsedTable item in batch)
            {
                TableState state;
                if (!_tables.TryGetValue(item.Uid, out state) || state.Hash != item.Hash) continue; // table gone or rewritten since
                if (item.Bad)
                {
                    state.Bad = true;
                    _log.LogWarning("Could not read the map on cartography table " + item.Uid + ": " + item.Error);
                    continue;
                }
                state.HasData = true;
                state.Pins = item.Pins;
                _pinsDirty = true;
                if (item.Rects != null && item.Rects.Count > 0) _pendingReveals.Enqueue(item.Rects);
            }
        }

        private void ApplyPendingFog()
        {
            if (_pendingReveals.Count == 0) return;
            var sw = Stopwatch.StartNew();
            while (_pendingReveals.Count > 0 && sw.Elapsed.TotalMilliseconds < FogBudgetMs)
            {
                List<RectF> rects = _pendingReveals.Peek();
                _mask.RevealRects(rects, _pendingOffset, RectsPerChunk);
                _pendingOffset += RectsPerChunk;
                if (_pendingOffset >= rects.Count)
                {
                    _pendingReveals.Dequeue();
                    _pendingOffset = 0;
                }
            }
        }

        private void PublishSnapshot()
        {
            var merged = new List<MergedPin>();
            var byCell = new HashSet<long>();
            var uids = new List<ZDOID>(_tables.Keys);
            uids.Sort();
            int withData = 0;
            foreach (ZDOID uid in uids)
            {
                TableState state = _tables[uid];
                if (state.HasData) withData++;
                foreach (TablePin pin in state.Pins)
                {
                    // The game treats pins within a metre as the same pin; the lowest table id wins.
                    int qx = (int)Math.Round(pin.X), qz = (int)Math.Round(pin.Z);
                    long cell = ((long)qx << 32) ^ (uint)qz;
                    if (!byCell.Add(cell)) continue;
                    merged.Add(new MergedPin
                    {
                        Id = Hash.Fnv1a64(pin.OwnerId + "|" + pin.Type + "|" + qx + "|" + qz).ToString("x16").Substring(4),
                        OwnerId = pin.OwnerId,
                        Author = pin.Author ?? "",
                        Name = pin.Name ?? "",
                        Type = pin.Type,
                        X = pin.X,
                        Z = pin.Z,
                        Checked = pin.Checked,
                        Automated = !SharedMapData.IsPlayerAuthor(pin.Author),
                    });
                }
            }
            merged.Sort((a, b) =>
            {
                int c = a.OwnerId.CompareTo(b.OwnerId);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            _snapshot = new PinSnapshot
            {
                Version = _snapshot.Version + 1,
                TableCount = _tables.Count,
                TablesWithData = withData,
                Pins = merged,
            };
        }

        public void Dispose()
        {
            _stop = true;
        }
    }
}
