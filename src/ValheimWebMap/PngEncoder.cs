using System;
using System.IO;
using System.IO.Compression;

namespace ValheimWebMap
{
    /// <summary>
    /// Minimal PNG writer (8-bit RGB, no filtering). Unity's ImageConversion needs the
    /// main thread and a Texture2D; this runs anywhere and keeps HTTP work off the game loop.
    /// </summary>
    internal static class PngEncoder
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static byte[] EncodeRgb(byte[] rgb, int width, int height)
        {
            int stride = width * 3;
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int o = y * (stride + 1);
                raw[o] = 0;
                Buffer.BlockCopy(rgb, y * stride, raw, o + 1, stride);
            }

            byte[] idat = Zlib(raw);
            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, width);
            WriteBigEndian(ihdr, 4, height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // colour type: truecolour
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace

            using (var ms = new MemoryStream(idat.Length + 128))
            {
                ms.Write(Signature, 0, Signature.Length);
                WriteChunk(ms, "IHDR", ihdr);
                WriteChunk(ms, "IDAT", idat);
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        public static byte[] Zlib(byte[] data)
        {
            using (var ms = new MemoryStream(data.Length / 2 + 64))
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                {
                    ds.Write(data, 0, data.Length);
                }
                uint adler = Adler32(data);
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        public static byte[] Unzlib(byte[] data, int expectedLength)
        {
            if (data.Length < 6) throw new InvalidDataException("zlib stream too short");
            var result = new byte[expectedLength];
            using (var ms = new MemoryStream(data, 2, data.Length - 6))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < expectedLength)
                {
                    int n = ds.Read(result, read, expectedLength - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != expectedLength) throw new InvalidDataException("zlib stream shorter than expected");
            }
            return result;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBigEndian(len, 0, data.Length);
            s.Write(len, 0, 4);
            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(typeBytes, 0, 4, 0xFFFFFFFF);
            crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFF;
            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, (int)crc);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteBigEndian(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] buf, int offset, int count, uint crc)
        {
            for (int i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                // 5552 is the largest block that cannot overflow the 32-bit sums.
                int end = Math.Min(i + 5552, data.Length);
                for (; i < end; i++)
                {
                    a += data[i];
                    b += a;
                }
                a %= 65521;
                b %= 65521;
            }
            return (b << 16) | a;
        }
    }
}
