using System;
using System.Collections.Generic;

namespace ValheimWebMap
{
    /// <summary>
    /// Serves Leaflet tiles: cuts the atlas, paints fog over unexplored ground and encodes PNG.
    /// Fog is applied here rather than in the browser so unexplored terrain never leaves the server.
    /// </summary>
    internal sealed class TileService
    {
        private const int T = MapAtlas.TileSize;

        private sealed class CachedTile
        {
            public int AtlasId;
            public int RegionVersion;
            public byte[] Png;
            public string ETag;
        }

        private readonly ExploredMask _mask;
        private readonly float _half;
        private readonly int _maxZoom;
        private readonly object _lock = new object();
        private readonly Dictionary<long, CachedTile> _cache = new Dictionary<long, CachedTile>();
        private readonly byte[] _fogPng;
        private volatile MapAtlas _atlas;

        public TileService(ExploredMask mask, float halfSize, int maxZoom)
        {
            _mask = mask;
            _half = halfSize;
            _maxZoom = maxZoom;
            var fog = new byte[T * T * 3];
            for (int i = 0; i < T * T; i++)
            {
                fog[i * 3] = Palette.Fog.R;
                fog[i * 3 + 1] = Palette.Fog.G;
                fog[i * 3 + 2] = Palette.Fog.B;
            }
            _fogPng = PngEncoder.EncodeRgb(fog, T, T);
        }

        public MapAtlas Atlas
        {
            get => _atlas;
            set
            {
                _atlas = value;
                lock (_lock) _cache.Clear();
            }
        }

        public bool TryGetTile(int z, int x, int y, out byte[] png, out string etag)
        {
            png = null;
            etag = null;
            MapAtlas atlas = _atlas;
            if (atlas == null || z < 0 || z > _maxZoom) return false;
            int tilesAcross = 1 << z;
            if (x < 0 || y < 0 || x >= tilesAcross || y >= tilesAcross) return false;

            float mpp = _half * 2f / (T << z);
            float minX = -_half + x * T * mpp;
            float maxX = minX + T * mpp;
            float maxZ = _half - y * T * mpp;
            float minZ = maxZ - T * mpp;
            float bleed = _mask.CellSize * 2f;

            if (!_mask.AnyExplored(minX - bleed, minZ - bleed, maxX + bleed, maxZ + bleed))
            {
                png = _fogPng;
                etag = "\"fog\"";
                return true;
            }

            int regionVersion = _mask.RegionVersion(minX - bleed, minZ - bleed, maxX + bleed, maxZ + bleed);
            long key = ((long)z << 56) | ((long)x << 28) | (long)y;
            lock (_lock)
            {
                CachedTile hit;
                if (_cache.TryGetValue(key, out hit) && hit.AtlasId == atlas.Id && hit.RegionVersion == regionVersion)
                {
                    png = hit.Png;
                    etag = hit.ETag;
                    return true;
                }
            }

            byte[] rgb = Compose(atlas, z, x, y, mpp, minX, maxZ);
            png = PngEncoder.EncodeRgb(rgb, T, T);
            etag = "\"" + atlas.Id + "-" + regionVersion + "\"";
            lock (_lock)
            {
                if (_cache.Count > 4096) _cache.Clear();
                _cache[key] = new CachedTile { AtlasId = atlas.Id, RegionVersion = regionVersion, Png = png, ETag = etag };
            }
            return true;
        }

        private byte[] Compose(MapAtlas atlas, int z, int tx, int ty, float mpp, float minX, float maxZ)
        {
            int gridSize = T << z;
            var rgb = new byte[T * T * 3];
            byte[] src;
            int srcSize;
            int shift; // tile-grid pixel -> source pixel, as a right shift when the atlas is coarser than the grid

            if (atlas.Size >= gridSize)
            {
                int level = MapAtlas.Log2(atlas.Size / gridSize);
                src = atlas.Levels[level];
                srcSize = atlas.LevelSize(level);
                shift = 0;
            }
            else
            {
                src = atlas.Levels[0];
                srcSize = atlas.Size;
                shift = MapAtlas.Log2(gridSize / atlas.Size);
            }

            Rgb fog = Palette.Fog;
            for (int py = 0; py < T; py++)
            {
                int gy = ty * T + py;
                int sy = gy >> shift;
                float wz = maxZ - (py + 0.5f) * mpp;
                for (int px = 0; px < T; px++)
                {
                    int gx = tx * T + px;
                    int sx = gx >> shift;
                    float wx = minX + (px + 0.5f) * mpp;
                    int o = (py * T + px) * 3;
                    float a = _mask.Sample(wx, wz);
                    if (a <= 0.001f)
                    {
                        rgb[o] = fog.R;
                        rgb[o + 1] = fog.G;
                        rgb[o + 2] = fog.B;
                        continue;
                    }
                    int s = (sy * srcSize + sx) * 3;
                    if (a >= 0.999f)
                    {
                        rgb[o] = src[s];
                        rgb[o + 1] = src[s + 1];
                        rgb[o + 2] = src[s + 2];
                        continue;
                    }
                    float ia = 1f - a;
                    rgb[o] = (byte)(src[s] * a + fog.R * ia + 0.5f);
                    rgb[o + 1] = (byte)(src[s + 1] * a + fog.G * ia + 0.5f);
                    rgb[o + 2] = (byte)(src[s + 2] * a + fog.B * ia + 0.5f);
                }
            }
            return rgb;
        }
    }
}
