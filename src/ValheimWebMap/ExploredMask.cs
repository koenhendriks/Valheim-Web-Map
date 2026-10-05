using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ValheimWebMap
{
    /// <summary>
    /// Server-side fog of war. The game keeps each player's explored map in their character file,
    /// which never reaches the server, so the server builds its own record from where players stand.
    /// Written on the main thread, read from HTTP threads; byte reads are atomic and a torn view only
    /// delays a reveal by one request.
    /// </summary>
    internal sealed class ExploredMask
    {
        public const int Size = 2048;
        private const int RegionGrid = 128;
        private const string FileMagic = "VWME";
        private const int FileVersion = 1;

        private readonly float _half;
        private readonly float _cell;
        private readonly float _regionCell;
        private readonly byte[] _cells = new byte[Size * Size];
        private readonly int[] _regionVersion = new int[RegionGrid * RegionGrid];
        private int _version = 1;
        private int _exploredCount;

        public ExploredMask(float halfSize)
        {
            _half = halfSize;
            _cell = halfSize * 2f / Size;
            _regionCell = halfSize * 2f / RegionGrid;
        }

        public int Version => Volatile.Read(ref _version);
        public bool Dirty { get; private set; }
        public float CellSize => _cell;

        public float ExploredPercent
        {
            get
            {
                double worldCells = Math.PI * Math.Pow(WorldGenerator.worldSize / _cell, 2);
                return Math.Min(100f, (float)(100.0 * Volatile.Read(ref _exploredCount) / worldCells));
            }
        }

        public int Reveal(float wx, float wz, float radius)
        {
            int r = (int)Math.Ceiling(radius / _cell);
            float r2 = radius * radius;
            int ci = (int)Math.Floor((wx + _half) / _cell);
            int cj = (int)Math.Floor((wz + _half) / _cell);
            int revealed = 0;
            for (int j = Math.Max(cj - r, 0); j <= Math.Min(cj + r, Size - 1); j++)
            {
                float cz = -_half + (j + 0.5f) * _cell - wz;
                for (int i = Math.Max(ci - r, 0); i <= Math.Min(ci + r, Size - 1); i++)
                {
                    float cx = -_half + (i + 0.5f) * _cell - wx;
                    if (cx * cx + cz * cz > r2) continue;
                    revealed += Set(i, j);
                }
            }
            if (revealed > 0) Touch(wx - radius, wz - radius, wx + radius, wz + radius);
            return revealed;
        }

        public int RevealRect(float minX, float minZ, float maxX, float maxZ)
        {
            int i0 = Clamp((int)Math.Floor((minX + _half) / _cell));
            int i1 = Clamp((int)Math.Ceiling((maxX + _half) / _cell) - 1);
            int j0 = Clamp((int)Math.Floor((minZ + _half) / _cell));
            int j1 = Clamp((int)Math.Ceiling((maxZ + _half) / _cell) - 1);
            int revealed = 0;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    revealed += Set(i, j);
            if (revealed > 0) Touch(minX, minZ, maxX, maxZ);
            return revealed;
        }

        private int Set(int i, int j)
        {
            int idx = j * Size + i;
            if (_cells[idx] != 0) return 0;
            _cells[idx] = 255;
            _exploredCount++;
            return 1;
        }

        /// <summary>
        /// Reveals many rectangles under one version increment. Rectangles outside the mask are
        /// skipped rather than clamped: the in-game map reaches further out than this mask, and
        /// clamping would paint the border cells.
        /// </summary>
        public int RevealRects(List<RectF> rects, int start, int count)
        {
            int revealed = 0;
            int version = 0;
            int end = Math.Min(start + count, rects.Count);
            for (int n = start; n < end; n++)
            {
                RectF r = rects[n];
                if (r.MaxX <= -_half || r.MinX >= _half || r.MaxZ <= -_half || r.MinZ >= _half) continue;
                int i0 = Clamp((int)Math.Floor((r.MinX + _half) / _cell));
                int i1 = Clamp((int)Math.Ceiling((r.MaxX + _half) / _cell) - 1);
                int j0 = Clamp((int)Math.Floor((r.MinZ + _half) / _cell));
                int j1 = Clamp((int)Math.Ceiling((r.MaxZ + _half) / _cell) - 1);
                int here = 0;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                        here += Set(i, j);
                if (here == 0) continue;
                if (version == 0) version = Interlocked.Increment(ref _version);
                Dirty = true;
                StampRegions(r.MinX, r.MinZ, r.MaxX, r.MaxZ, version);
                revealed += here;
            }
            return revealed;
        }

        private void Touch(float minX, float minZ, float maxX, float maxZ)
        {
            int v = Interlocked.Increment(ref _version);
            Dirty = true;
            StampRegions(minX, minZ, maxX, maxZ, v);
        }

        private void StampRegions(float minX, float minZ, float maxX, float maxZ, int v)
        {
            int i0 = ClampRegion((int)Math.Floor((minX + _half) / _regionCell));
            int i1 = ClampRegion((int)Math.Floor((maxX + _half) / _regionCell));
            int j0 = ClampRegion((int)Math.Floor((minZ + _half) / _regionCell));
            int j1 = ClampRegion((int)Math.Floor((maxZ + _half) / _regionCell));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    _regionVersion[j * RegionGrid + i] = v;
        }

        /// <summary>Highest change counter touching a world rectangle; stable while nothing inside it changes.</summary>
        public int RegionVersion(float minX, float minZ, float maxX, float maxZ)
        {
            int i0 = ClampRegion((int)Math.Floor((minX + _half) / _regionCell));
            int i1 = ClampRegion((int)Math.Floor((maxX + _half) / _regionCell));
            int j0 = ClampRegion((int)Math.Floor((minZ + _half) / _regionCell));
            int j1 = ClampRegion((int)Math.Floor((maxZ + _half) / _regionCell));
            int max = 0;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    max = Math.Max(max, Volatile.Read(ref _regionVersion[j * RegionGrid + i]));
            return max;
        }

        /// <summary>
        /// Explored amount in 0..1 at a world position. Four bilinear taps half a cell apart make a
        /// tent filter two cells wide, so the fog fades out smoothly instead of showing the cell grid.
        /// </summary>
        public float Sample(float wx, float wz)
        {
            float o = _cell * 0.5f;
            return (SampleBilinear(wx - o, wz - o) + SampleBilinear(wx + o, wz - o)
                  + SampleBilinear(wx - o, wz + o) + SampleBilinear(wx + o, wz + o)) * 0.25f;
        }

        private float SampleBilinear(float wx, float wz)
        {
            float fx = (wx + _half) / _cell - 0.5f;
            float fz = (wz + _half) / _cell - 0.5f;
            int i0 = (int)Math.Floor(fx);
            int j0 = (int)Math.Floor(fz);
            float tx = fx - i0;
            float tz = fz - j0;
            int i1 = Clamp(i0 + 1);
            int j1 = Clamp(j0 + 1);
            i0 = Clamp(i0);
            j0 = Clamp(j0);
            float a = _cells[j0 * Size + i0] * (1f - tx) + _cells[j0 * Size + i1] * tx;
            float b = _cells[j1 * Size + i0] * (1f - tx) + _cells[j1 * Size + i1] * tx;
            return (a * (1f - tz) + b * tz) / 255f;
        }

        public bool AnyExplored(float minX, float minZ, float maxX, float maxZ)
        {
            int i0 = Clamp((int)Math.Floor((minX + _half) / _cell));
            int i1 = Clamp((int)Math.Ceiling((maxX + _half) / _cell));
            int j0 = Clamp((int)Math.Floor((minZ + _half) / _cell));
            int j1 = Clamp((int)Math.Ceiling((maxZ + _half) / _cell));
            for (int j = j0; j <= j1; j++)
            {
                int row = j * Size;
                for (int i = i0; i <= i1; i++)
                    if (_cells[row + i] != 0) return true;
            }
            return false;
        }

        private static int Clamp(int v) => v < 0 ? 0 : v >= Size ? Size - 1 : v;
        private static int ClampRegion(int v) => v < 0 ? 0 : v >= RegionGrid ? RegionGrid - 1 : v;

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(FileMagic);
                w.Write(FileVersion);
                w.Write(Size);
                w.Write(_exploredCount);
                byte[] payload = PngEncoder.Zlib(_cells);
                w.Write(payload.Length);
                w.Write(payload);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            Dirty = false;
        }

        public bool TryLoad(string path)
        {
            if (!File.Exists(path)) return false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var r = new BinaryReader(fs))
            {
                if (r.ReadString() != FileMagic) throw new InvalidDataException("not an explored-mask file");
                if (r.ReadInt32() != FileVersion) throw new InvalidDataException("unsupported explored-mask version");
                if (r.ReadInt32() != Size) throw new InvalidDataException("explored-mask size mismatch");
                int count = r.ReadInt32();
                int len = r.ReadInt32();
                byte[] cells = PngEncoder.Unzlib(r.ReadBytes(len), Size * Size);
                Buffer.BlockCopy(cells, 0, _cells, 0, cells.Length);
                _exploredCount = count;
            }
            Touch(-_half, -_half, _half, _half);
            Dirty = false;
            return true;
        }
    }
}
