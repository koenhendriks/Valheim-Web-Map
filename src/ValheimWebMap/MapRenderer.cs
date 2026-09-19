using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ValheimWebMap
{
    /// <summary>
    /// Renders the unmodified world (biomes, terrain height, forests, water) from WorldGenerator,
    /// the same source the in-game minimap uses. Player terraforming and buildings are deliberately
    /// not part of the picture. Safe to run off the main thread: the game's own heightmap builder
    /// calls WorldGenerator from a worker thread too.
    /// </summary>
    internal static class MapRenderer
    {
        /// <summary>Bump when the look changes so cached renders are redone.</summary>
        public const int FormatVersion = 1;

        private const float WaterLevel = 30f;
        private const float WaterEdgeSqr = WorldGenerator.waterEdgeSqr;

        private static readonly Vector3 SunDirection = new Vector3(-1f, 1.6f, 1f).normalized;
        private static readonly float FlatShade = 0.5f + 0.6f * SunDirection.y;

        public static byte[] Render(int size, float halfSize, int threads, Action<float> progress, Func<bool> cancelled)
        {
            WorldGenerator wg = WorldGenerator.instance;
            if (wg == null) throw new InvalidOperationException("WorldGenerator is not initialised");

            int n = size * size;
            var height = new float[n];
            var biome = new byte[n];
            var forest = new byte[n];
            var lava = new byte[n];
            float mpp = halfSize * 2f / size;

            RunRows(size, threads, cancelled, row => SampleRow(wg, row, size, halfSize, mpp, height, biome, forest, lava),
                done => progress(done * 0.92f));
            if (cancelled()) return null;

            var rgb = new byte[n * 3];
            RunRows(size, threads, cancelled, row => ComposeRow(row, size, mpp, height, biome, forest, lava, rgb),
                done => progress(0.92f + done * 0.08f));
            if (cancelled()) return null;

            progress(1f);
            return rgb;
        }

        private static void RunRows(int rows, int threads, Func<bool> cancelled, Action<int> work, Action<float> progress)
        {
            threads = Math.Max(1, threads);
            int nextRow = -1;
            int done = 0;
            Exception failure = null;
            var workers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                workers[t] = new Thread(() =>
                {
                    try
                    {
                        while (true)
                        {
                            int row = Interlocked.Increment(ref nextRow);
                            if (row >= rows || cancelled() || Volatile.Read(ref failure) != null) break;
                            work(row);
                            int d = Interlocked.Increment(ref done);
                            if ((d & 15) == 0) progress(d / (float)rows);
                        }
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref failure, e, null);
                    }
                })
                {
                    IsBackground = true,
                    Name = "WebMap render " + t,
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                };
                workers[t].Start();
            }
            foreach (Thread w in workers) w.Join();
            if (failure != null) throw new Exception("Map render failed: " + failure.Message, failure);
        }

        private static void SampleRow(WorldGenerator wg, int y, int size, float halfSize, float mpp,
            float[] height, byte[] biome, byte[] forest, byte[] lava)
        {
            float wz = halfSize - (y + 0.5f) * mpp;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                float wx = -halfSize + (x + 0.5f) * mpp;
                int i = row + x;
                if (wx * wx + wz * wz > WaterEdgeSqr)
                {
                    height[i] = -400f;
                    biome[i] = BiomeId.Ocean;
                    continue;
                }

                Heightmap.Biome b = wg.GetBiome(wx, wz);
                Color mask;
                float h = wg.GetBiomeHeight(b, wx, wz, out mask);
                height[i] = h;
                biome[i] = BiomeId.From(b);
                if (h < WaterLevel) continue;

                float f = 0f;
                switch (b)
                {
                    case Heightmap.Biome.Meadows:
                        // The game draws meadows forest where the factor is below 1.15.
                        f = Mathf.Clamp01((1.15f - WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz))) / 0.25f);
                        break;
                    case Heightmap.Biome.Plains:
                        f = Mathf.Clamp01((0.8f - WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz))) / 0.2f);
                        break;
                    case Heightmap.Biome.BlackForest:
                        f = 0.4f + 0.6f * Mathf.Clamp01((1.2f - WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz))) / 0.5f);
                        break;
                    case Heightmap.Biome.Mistlands:
                        f = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.1f, 1.3f, WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz))));
                        break;
                    case Heightmap.Biome.AshLands:
                        lava[i] = (byte)(Mathf.Clamp01(mask.a) * 255f);
                        break;
                }
                forest[i] = (byte)(f * 255f);
            }
        }

        private static void ComposeRow(int y, int size, float mpp, float[] height, byte[] biome, byte[] forest, byte[] lava, byte[] rgb)
        {
            int row = y * size;
            int rowN = Math.Max(y - 1, 0) * size;
            int rowS = Math.Min(y + 1, size - 1) * size;
            float inv2 = 1f / (2f * mpp);
            for (int x = 0; x < size; x++)
            {
                int i = row + x;
                float h = height[i];
                byte b = biome[i];
                float f = forest[i] / 255f;

                int xw = Math.Max(x - 1, 0);
                int xe = Math.Min(x + 1, size - 1);
                float dx = (height[row + xe] - height[row + xw]) * inv2;
                float dz = (height[rowN + x] - height[rowS + x]) * inv2;
                float slope = Mathf.Sqrt(dx * dx + dz * dz);

                Rgb c;
                switch (b)
                {
                    case BiomeId.Meadows: c = Rgb.Lerp(Palette.Meadows, Palette.MeadowsForest, f); break;
                    case BiomeId.BlackForest: c = Rgb.Lerp(Palette.BlackForest, Palette.BlackForestDense, f); break;
                    case BiomeId.Swamp: c = Palette.Swamp; break;
                    case BiomeId.Mountain: c = Rgb.Lerp(Palette.MountainSnow, Palette.MountainRock, Mathf.Clamp01((slope - 0.7f) / 0.9f)); break;
                    case BiomeId.Plains: c = Rgb.Lerp(Palette.Plains, Palette.PlainsForest, f); break;
                    case BiomeId.Mistlands: c = Rgb.Lerp(Palette.Mistlands, Palette.MistlandsForest, f); break;
                    case BiomeId.AshLands: c = Rgb.Lerp(Palette.Ashlands, Palette.Lava, Mathf.Sqrt(lava[i] / 255f)); break;
                    case BiomeId.DeepNorth: c = Rgb.Lerp(Palette.DeepNorth, Palette.MountainRock, Mathf.Clamp01((slope - 0.9f) / 1.0f) * 0.6f); break;
                    case BiomeId.Ocean: c = Palette.WaterDeep; break;
                    default: c = Palette.Unknown; break;
                }

                if (h < WaterLevel)
                {
                    float depth = WaterLevel - h;
                    float t = Mathf.Sqrt(Mathf.Clamp01(depth / 50f));
                    Rgb water = Rgb.Lerp(Palette.WaterShallow, Palette.WaterDeep, t);
                    float seeThrough = Mathf.Clamp01(1f - depth / 4f) * 0.5f;
                    c = Rgb.Lerp(water, c, seeThrough);
                    dx *= 0.3f;
                    dz *= 0.3f;
                }
                else if (h < WaterLevel + 1.5f && (b == BiomeId.Meadows || b == BiomeId.BlackForest || b == BiomeId.Plains || b == BiomeId.Mistlands))
                {
                    c = Rgb.Lerp(c, Palette.Sand, Mathf.Clamp01((WaterLevel + 1.5f - h) / 1.5f) * 0.7f);
                }

                // Hillshade: the surface normal of z = f(x, y) is (-dx, 1, -dz).
                float nx = -dx, ny = 1f, nz = -dz;
                float inv = 1f / Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                float light = (nx * SunDirection.x + ny * SunDirection.y + nz * SunDirection.z) * inv;
                float shade = (0.5f + 0.6f * Mathf.Clamp01(light)) / FlatShade;
                c = c.Scale(shade);

                int o = i * 3;
                rgb[o] = c.R;
                rgb[o + 1] = c.G;
                rgb[o + 2] = c.B;
            }
        }

        // Cached render: magic, format version, size, seed, world generator version, game version, zlib payload.
        private const string CacheMagic = "VWMB";

        public static void SaveCache(string path, byte[] rgb, int size, World world)
        {
            string tmp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(CacheMagic);
                w.Write(FormatVersion);
                w.Write(size);
                w.Write(world.m_seed);
                w.Write(world.m_worldGenVersion);
                w.Write(Version.CurrentVersion.ToString());
                byte[] payload = PngEncoder.Zlib(rgb);
                w.Write(payload.Length);
                w.Write(payload);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static byte[] TryLoadCache(string path, int size, World world)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var r = new BinaryReader(fs))
                {
                    if (r.ReadString() != CacheMagic) return null;
                    if (r.ReadInt32() != FormatVersion) return null;
                    if (r.ReadInt32() != size) return null;
                    if (r.ReadInt32() != world.m_seed) return null;
                    if (r.ReadInt32() != world.m_worldGenVersion) return null;
                    if (r.ReadString() != Version.CurrentVersion.ToString()) return null;
                    int len = r.ReadInt32();
                    byte[] payload = r.ReadBytes(len);
                    return PngEncoder.Unzlib(payload, size * size * 3);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
