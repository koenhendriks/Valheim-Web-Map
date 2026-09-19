namespace ValheimWebMap
{
    internal struct Rgb
    {
        public byte R, G, B;

        public Rgb(int r, int g, int b)
        {
            R = (byte)r;
            G = (byte)g;
            B = (byte)b;
        }

        public static Rgb Lerp(Rgb a, Rgb b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return new Rgb(
                (int)(a.R + (b.R - a.R) * t + 0.5f),
                (int)(a.G + (b.G - a.G) * t + 0.5f),
                (int)(a.B + (b.B - a.B) * t + 0.5f));
        }

        public Rgb Scale(float f)
        {
            return new Rgb(Clamp(R * f), Clamp(G * f), Clamp(B * f));
        }

        private static int Clamp(float v)
        {
            return v < 0f ? 0 : v > 255f ? 255 : (int)(v + 0.5f);
        }
    }

    /// <summary>Biome indices as stored per pixel while rendering (compact, unlike the game's flag enum).</summary>
    internal static class BiomeId
    {
        public const byte None = 0, Meadows = 1, Swamp = 2, Mountain = 3, BlackForest = 4, Plains = 5,
            AshLands = 6, DeepNorth = 7, Ocean = 8, Mistlands = 9;

        public static byte From(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return Meadows;
                case Heightmap.Biome.Swamp: return Swamp;
                case Heightmap.Biome.Mountain: return Mountain;
                case Heightmap.Biome.BlackForest: return BlackForest;
                case Heightmap.Biome.Plains: return Plains;
                case Heightmap.Biome.AshLands: return AshLands;
                case Heightmap.Biome.DeepNorth: return DeepNorth;
                case Heightmap.Biome.Ocean: return Ocean;
                case Heightmap.Biome.Mistlands: return Mistlands;
                default: return None;
            }
        }

        public static string Name(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return "Meadows";
                case Heightmap.Biome.Swamp: return "Swamp";
                case Heightmap.Biome.Mountain: return "Mountain";
                case Heightmap.Biome.BlackForest: return "Black Forest";
                case Heightmap.Biome.Plains: return "Plains";
                case Heightmap.Biome.AshLands: return "Ashlands";
                case Heightmap.Biome.DeepNorth: return "Deep North";
                case Heightmap.Biome.Ocean: return "Ocean";
                case Heightmap.Biome.Mistlands: return "Mistlands";
                default: return "";
            }
        }
    }

    internal static class Palette
    {
        public static readonly Rgb Meadows = new Rgb(126, 165, 92);
        public static readonly Rgb MeadowsForest = new Rgb(84, 128, 66);
        public static readonly Rgb BlackForest = new Rgb(60, 92, 56);
        public static readonly Rgb BlackForestDense = new Rgb(42, 66, 42);
        public static readonly Rgb Swamp = new Rgb(104, 92, 68);
        public static readonly Rgb MountainSnow = new Rgb(230, 234, 238);
        public static readonly Rgb MountainRock = new Rgb(150, 150, 152);
        public static readonly Rgb Plains = new Rgb(196, 176, 102);
        public static readonly Rgb PlainsForest = new Rgb(150, 142, 76);
        public static readonly Rgb Mistlands = new Rgb(92, 86, 102);
        public static readonly Rgb MistlandsForest = new Rgb(66, 74, 84);
        public static readonly Rgb Ashlands = new Rgb(112, 56, 46);
        public static readonly Rgb Lava = new Rgb(236, 110, 36);
        public static readonly Rgb DeepNorth = new Rgb(214, 226, 238);
        public static readonly Rgb Sand = new Rgb(204, 192, 150);
        public static readonly Rgb WaterShallow = new Rgb(62, 128, 164);
        public static readonly Rgb WaterDeep = new Rgb(22, 50, 92);
        public static readonly Rgb Fog = new Rgb(18, 20, 26);
        public static readonly Rgb Unknown = new Rgb(90, 90, 90);
    }
}
