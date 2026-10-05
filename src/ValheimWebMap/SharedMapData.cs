using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ValheimWebMap
{
    internal struct TablePin
    {
        public long OwnerId;
        public string Name;
        public float X, Y, Z;
        public int Type;
        public bool Checked;
        public string Author;
    }

    internal struct RectF
    {
        public float MinX, MinZ, MaxX, MaxZ;
    }

    /// <summary>
    /// The map a cartography table holds, decoded from the same stream the game client writes
    /// (Minimap.GetSharedMapData): the shared explored bitmap plus every shared pin. No game types,
    /// so it can be parsed on a worker thread and tested outside the game.
    /// </summary>
    internal sealed class SharedMapData
    {
        /// <summary>The in-game map covers this many metres from the centre to its edge.</summary>
        public const float MapHalfSize = 12288f;
        private const int PinsVersion = 2;
        private const int PinsAuthorVersion = 3;
        private const int DeathPinType = 4;

        public int Version;
        public int TextureSize;
        public float PixelSize;
        /// <summary>TextureSize² bytes, 0 or 1, row = z, column = x.</summary>
        public byte[] Explored;
        public List<TablePin> Pins = new List<TablePin>();

        public static SharedMapData Parse(byte[] data, int maxTextureSize = 4096, int maxPins = 100000)
        {
            if (data == null || data.Length < 8) throw new InvalidDataException("shared map data too short");
            try
            {
                using (var ms = new MemoryStream(data, false))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var result = new SharedMapData();
                    result.Version = r.ReadInt32();
                    int length = r.ReadInt32();
                    int size = (int)Math.Round(Math.Sqrt(length));
                    if (length <= 0 || size * size != length || size > maxTextureSize)
                        throw new InvalidDataException("explored bitmap has an unexpected size: " + length);
                    result.TextureSize = size;
                    result.PixelSize = MapHalfSize * 2f / size;
                    result.Explored = r.ReadBytes(length);
                    if (result.Explored.Length != length) throw new EndOfStreamException();

                    if (result.Version >= PinsVersion && ms.Position < ms.Length)
                    {
                        int count = r.ReadInt32();
                        if (count < 0 || count > maxPins) throw new InvalidDataException("unexpected pin count: " + count);
                        for (int i = 0; i < count; i++)
                        {
                            var pin = new TablePin
                            {
                                OwnerId = r.ReadInt64(),
                                Name = r.ReadString(),
                                X = r.ReadSingle(),
                                Y = r.ReadSingle(),
                                Z = r.ReadSingle(),
                                Type = r.ReadInt32(),
                                Checked = r.ReadBoolean(),
                                Author = result.Version >= PinsAuthorVersion ? r.ReadString() : "",
                            };
                            // The game never shares death pins; drop any that a modified client sends.
                            if (pin.Type == DeathPinType) continue;
                            result.Pins.Add(pin);
                        }
                    }
                    return result;
                }
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("truncated shared map data");
            }
        }

        public static string KindName(int pinType)
        {
            switch (pinType)
            {
                case 0: return "fire";
                case 1: return "house";
                case 2: return "hammer";
                case 3: return "dot";
                case 5: return "bed";
                case 6: return "portal";
                case 9: return "boss";
                case 14:
                case 15:
                case 16: return "hildir";
                case 17: return "memorial";
                default: return "other";
            }
        }
    }

    internal static class ExploredRuns
    {
        /// <summary>
        /// Converts the set pixels of a shared explored bitmap into world rectangles, one per horizontal
        /// run, clipped to ±half. A pixel at column j covers x from (j - size/2 - 0.5) to (j - size/2 + 0.5)
        /// pixel widths, because the game maps a position with RoundToInt(x / pixelSize + size / 2).
        /// </summary>
        public static List<RectF> FromBitmap(byte[] explored, int size, float pixelSize, float half)
        {
            var rects = new List<RectF>();
            float origin = -(size / 2f) * pixelSize - pixelSize / 2f;
            for (int row = 0; row < size; row++)
            {
                float minZ = origin + row * pixelSize;
                float maxZ = minZ + pixelSize;
                if (maxZ <= -half || minZ >= half) continue;
                int rowStart = row * size;
                int runStart = -1;
                for (int col = 0; col <= size; col++)
                {
                    bool set = col < size && explored[rowStart + col] != 0;
                    if (set && runStart < 0) runStart = col;
                    if (set || runStart < 0) continue;

                    float minX = origin + runStart * pixelSize;
                    float maxX = origin + col * pixelSize;
                    runStart = -1;
                    if (maxX <= -half || minX >= half) continue;
                    rects.Add(new RectF
                    {
                        MinX = Math.Max(minX, -half),
                        MinZ = Math.Max(minZ, -half),
                        MaxX = Math.Min(maxX, half),
                        MaxZ = Math.Min(maxZ, half),
                    });
                }
            }
            return rects;
        }
    }

    internal static class Hash
    {
        public static ulong Fnv1a64(byte[] data)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in data) h = (h ^ b) * 1099511628211UL;
            return h;
        }

        public static ulong Fnv1a64(string text)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in text) h = (h ^ c) * 1099511628211UL;
            return h;
        }
    }
}
