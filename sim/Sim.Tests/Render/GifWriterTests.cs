using System;
using System.Collections.Generic;
using System.IO;
using Urbe.Sim.Runner.Render;
using Xunit;

namespace Urbe.Sim.Tests.Render
{
    public class GifWriterTests
    {
        private static byte[] Encode(int w, int h, byte[][] frames, int delay = 10)
        {
            var ms = new MemoryStream();
            using (var g = new GifWriter(ms, w, h, FixedPalette.Build(), delay, leaveOpen: true))
            {
                foreach (byte[] f in frames) g.AddFrame(f);
                Assert.Equal(frames.Length, g.FrameCount);
            }
            return ms.ToArray();
        }

        private static byte[] Pattern(int w, int h, int seed, bool noisy)
        {
            var f = new byte[w * h];
            var rng = new Urbe.Sim.Random.Pcg32((ulong)seed, 5);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    f[y * w + x] = noisy ? (byte)rng.NextInt(256) : (byte)(((x / 7) + (y / 5) + seed) % 40);
            return f;
        }

        /// <summary>Decodificador GIF minimo (solo lo que escribe GifWriter) para comprobar el viaje de ida y vuelta.</summary>
        private static List<byte[]> Decode(byte[] gif, out int width, out int height, out int delayCs, out bool loops)
        {
            int p = 6;
            width = gif[p] | (gif[p + 1] << 8);
            height = gif[p + 2] | (gif[p + 3] << 8);
            p += 7 + 768;
            loops = false;
            delayCs = -1;
            var frames = new List<byte[]>();
            while (gif[p] != 0x3B)
            {
                if (gif[p] == 0x21)
                {
                    byte label = gif[p + 1];
                    if (label == 0xFF) loops = System.Text.Encoding.ASCII.GetString(gif, p + 3, 11) == "NETSCAPE2.0";
                    if (label == 0xF9) delayCs = gif[p + 4] | (gif[p + 5] << 8);
                    p += 2;
                    while (gif[p] != 0) p += gif[p] + 1;
                    p++;
                }
                else if (gif[p] == 0x2C)
                {
                    p += 10;
                    int minCode = gif[p++];
                    var data = new List<byte>();
                    while (gif[p] != 0)
                    {
                        for (int i = 0; i < gif[p]; i++) data.Add(gif[p + 1 + i]);
                        p += gif[p] + 1;
                    }
                    p++;
                    frames.Add(Lzw(data.ToArray(), minCode, width * height));
                }
                else throw new InvalidDataException("bloque inesperado " + gif[p]);
            }
            return frames;
        }

        private static byte[] Lzw(byte[] data, int minCode, int expected)
        {
            int clear = 1 << minCode, eoi = clear + 1;
            var prefix = new int[4096];
            var suffix = new byte[4096];
            var stack = new byte[4097];
            var output = new List<byte>(expected);
            int size = minCode + 1, next = eoi + 1, prev = -1;
            byte first = 0;
            uint buf = 0;
            int bits = 0, pos = 0;
            while (true)
            {
                while (bits < size && pos < data.Length) { buf |= (uint)data[pos++] << bits; bits += 8; }
                if (bits < size) break;
                int code = (int)(buf & ((1u << size) - 1));
                buf >>= size;
                bits -= size;
                if (code == clear) { size = minCode + 1; next = eoi + 1; prev = -1; continue; }
                if (code == eoi) break;
                int sp = 0;
                int cur = code;
                if (prev == -1) { output.Add((byte)code); prev = code; first = (byte)code; continue; }
                if (code >= next) { stack[sp++] = first; cur = prev; }
                while (cur >= clear) { stack[sp++] = suffix[cur]; cur = prefix[cur]; }
                first = (byte)cur;
                stack[sp++] = first;
                while (sp > 0) output.Add(stack[--sp]);
                if (next < 4096)
                {
                    prefix[next] = prev;
                    suffix[next] = first;
                    next++;
                    if (next == (1 << size) && size < 12) size++;
                }
                prev = code;
            }
            Assert.Equal(expected, output.Count);
            return output.ToArray();
        }

        [Fact]
        public void HeaderTrailerAndFrameCount()
        {
            byte[] gif = Encode(32, 24, new[] { Pattern(32, 24, 1, false), Pattern(32, 24, 2, false), Pattern(32, 24, 3, false) });
            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(gif, 0, 6));
            Assert.Equal(0x3B, gif[gif.Length - 1]);
            List<byte[]> frames = Decode(gif, out int w, out int h, out int delay, out bool loops);
            Assert.Equal(3, frames.Count);
            Assert.Equal(32, w);
            Assert.Equal(24, h);
            Assert.Equal(10, delay);
            Assert.True(loops);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RoundTripIsLossless(bool noisy)
        {
            // 200x150 con ruido supera los 4096 codigos y fuerza reinicios de la tabla LZW.
            byte[][] src = { Pattern(200, 150, 4, noisy), Pattern(200, 150, 9, noisy) };
            List<byte[]> dec = Decode(Encode(200, 150, src), out _, out _, out _, out _);
            Assert.Equal(2, dec.Count);
            for (int i = 0; i < src.Length; i++) Assert.Equal(src[i], dec[i]);
        }

        [Fact]
        public void SolidFrameRoundTrips()
        {
            var f = new byte[64 * 64];
            Array.Fill(f, (byte)200);
            List<byte[]> dec = Decode(Encode(64, 64, new[] { f }), out _, out _, out _, out _);
            Assert.Equal(f, dec[0]);
        }

        [Fact]
        public void WrongFrameSizeThrows()
        {
            using var g = new GifWriter(new MemoryStream(), 4, 4, FixedPalette.Build(), 10);
            Assert.Throws<ArgumentException>(() => g.AddFrame(new byte[15]));
        }

        [Fact]
        public void PaletteQuantizerFindsClosestColors()
        {
            byte[] pal = FixedPalette.Build();
            foreach ((byte r, byte g, byte b) in new[] { ((byte)0, (byte)0, (byte)0), ((byte)255, (byte)255, (byte)255), ((byte)255, (byte)0, (byte)0), ((byte)24, (byte)28, (byte)34) })
            {
                int i = FixedPalette.Index(r, g, b) * 3;
                int err = Math.Abs(pal[i] - r) + Math.Abs(pal[i + 1] - g) + Math.Abs(pal[i + 2] - b);
                Assert.True(err <= 40, "error de cuantizacion " + err);
            }
        }
    }
}
