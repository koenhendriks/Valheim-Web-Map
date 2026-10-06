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

        /// <summary>
        /// The game writes the author as "&lt;Platform&gt;_&lt;id&gt;" (Steam_7656..., PlayFab_...). Server-side
        /// mods that write pins put their own plugin id there instead. Data from before the author
        /// field existed has an empty author and counts as a player's.
        /// </summary>
        public static bool IsPlayerAuthor(string author)
        {
            if (string.IsNullOrEmpty(author)) return true;
            int underscore = author.IndexOf('_');
            if (underscore <= 0 || underscore == author.Length - 1) return false;
            for (int i = 0; i < underscore; i++)
                if (!char.IsLetter(author[i])) return false;
            for (int i = underscore + 1; i < author.Length; i++)
                if (!char.IsLetterOrDigit(author[i]) && author[i] != '-') return false;
            return true;
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

        /// <summary>
        /// Generated rather than typed: either written by a mod, or labelled with a localisation
        /// token such as "$location_sunkenCrypt" or "$enemy_eikthyr", which the pin dialog never
        /// produces but mods and the game's own vegvisir discoveries do.
        /// </summary>
        public bool System => Automated || (Name != null && Name.StartsWith("$"));
    }

    /// <summary>
    /// Picks the pins to show when players mark spots that a generated pin already marks: within
    /// the merge distance, the generated pin wins and the hand-placed ones are dropped. Pins that
    /// are not displayable never win and never claim, so a hidden mod pin leaves a player's copy alone.
    /// </summary>
    internal static class PinMerger
    {
        public static HashSet<string> Select(List<MergedPin> pins, Func<MergedPin, bool> displayable, float distance)
        {
            var shown = new HashSet<string>();
            var candidates = new List<MergedPin>();
            foreach (MergedPin pin in pins)
                if (displayable(pin)) candidates.Add(pin);
            if (distance <= 0f)
            {
                foreach (MergedPin pin in candidates) shown.Add(pin.Id);
                return shown;
            }

            var buckets = new Dictionary<long, List<MergedPin>>();
            foreach (MergedPin pin in candidates)
            {
                long key = BucketKey(pin.X, pin.Z, distance);
                List<MergedPin> list;
                if (!buckets.TryGetValue(key, out list)) buckets[key] = list = new List<MergedPin>();
                list.Add(pin);
            }

            var systems = new List<MergedPin>();
            foreach (MergedPin pin in candidates)
                if (pin.System) systems.Add(pin);
            systems.Sort((a, b) =>
            {
                if (a.Automated != b.Automated) return a.Automated ? -1 : 1;
                return string.CompareOrdinal(a.Id, b.Id);
            });

            var claimed = new HashSet<string>();
            float d2 = distance * distance;
            foreach (MergedPin winner in systems)
            {
                if (claimed.Contains(winner.Id)) continue;
                shown.Add(winner.Id);
                claimed.Add(winner.Id);
                int bx = (int)Math.Floor(winner.X / distance), bz = (int)Math.Floor(winner.Z / distance);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        List<MergedPin> list;
                        if (!buckets.TryGetValue(Pack(bx + dx, bz + dz), out list)) continue;
                        foreach (MergedPin other in list)
                        {
                            if (claimed.Contains(other.Id)) continue;
                            float ox = other.X - winner.X, oz = other.Z - winner.Z;
                            if (ox * ox + oz * oz <= d2) claimed.Add(other.Id);
                        }
                    }
            }

            foreach (MergedPin pin in candidates)
                if (!claimed.Contains(pin.Id)) shown.Add(pin.Id);
            return shown;
        }

        private static long BucketKey(float x, float z, float size)
        {
            return Pack((int)Math.Floor(x / size), (int)Math.Floor(z / size));
        }

        private static long Pack(int bx, int bz)
        {
            return ((long)bx << 32) ^ (uint)bz;
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
