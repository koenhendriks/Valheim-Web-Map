using System;

namespace ValheimWebMap
{
    /// <summary>The rendered base map as a mip chain of raw RGB buffers, level 0 being the full resolution.</summary>
    internal sealed class MapAtlas
    {
        public const int TileSize = 256;

        public int Id { get; }
        public int Size { get; }
        public float HalfSize { get; }
        public int NativeZoom { get; }
        public byte[][] Levels { get; }

        public MapAtlas(int id, int size, float halfSize, byte[] level0)
        {
            if (size < TileSize || (size & (size - 1)) != 0)
                throw new ArgumentException("Atlas size must be a power of two and at least " + TileSize, nameof(size));
            if (level0.Length != size * size * 3)
                throw new ArgumentException("Pixel buffer does not match size", nameof(level0));

            Id = id;
            Size = size;
            HalfSize = halfSize;
            NativeZoom = Log2(size / TileSize);
            Levels = new byte[NativeZoom + 1][];
            Levels[0] = level0;
            for (int level = 1; level <= NativeZoom; level++)
            {
                Levels[level] = Downsample(Levels[level - 1], size >> (level - 1));
            }
        }

        public int LevelSize(int level) => Size >> level;

        public static int Log2(int v)
        {
            int r = 0;
            while (v > 1)
            {
                v >>= 1;
                r++;
            }
            return r;
        }

        private static byte[] Downsample(byte[] src, int srcSize)
        {
            int dstSize = srcSize / 2;
            var dst = new byte[dstSize * dstSize * 3];
            for (int y = 0; y < dstSize; y++)
            {
                int r0 = (y * 2) * srcSize * 3;
                int r1 = r0 + srcSize * 3;
                int d = y * dstSize * 3;
                for (int x = 0; x < dstSize; x++)
                {
                    int s = x * 6;
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = src[r0 + s + c] + src[r0 + s + 3 + c] + src[r1 + s + c] + src[r1 + s + 3 + c];
                        dst[d + x * 3 + c] = (byte)((sum + 2) >> 2);
                    }
                }
            }
            return dst;
        }
    }
}
