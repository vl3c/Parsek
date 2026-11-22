using System;
using System.IO;
using System.IO.Compression;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The sweep's PNG writer and image operations (<see cref="PartParityPng"/>,
    /// <see cref="PartParityImage"/>): a decode written here from the PNG spec round-trips
    /// every pixel and checks every chunk CRC and the zlib Adler-32, so the writer is not
    /// tested against itself.
    /// </summary>
    public class PartParityImagingTests
    {
        private static uint ReadBE(byte[] b, int o)
            => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        /// <summary>Decodes a filter-0, colour-type-6, 8-bit PNG; asserts the structure.</summary>
        private static byte[] DecodeRgba(byte[] png, out int width, out int height)
        {
            for (int i = 0; i < 8; i++) Assert.Equal(PartParityPng.Signature[i], png[i]);
            int pos = 8;
            width = height = 0;
            var idat = new MemoryStream();
            bool sawEnd = false;
            while (pos < png.Length)
            {
                int len = (int)ReadBE(png, pos);
                string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                uint crc = ReadBE(png, pos + 8 + len);
                Assert.Equal(PartParityPng.Crc32(png, pos + 4, len + 4), crc);
                if (type == "IHDR")
                {
                    width = (int)ReadBE(png, pos + 8);
                    height = (int)ReadBE(png, pos + 12);
                    Assert.Equal(8, png[pos + 16]);
                    Assert.Equal(6, png[pos + 17]);
                }
                else if (type == "IDAT") idat.Write(png, pos + 8, len);
                else if (type == "IEND") sawEnd = true;
                pos += 12 + len;
            }
            Assert.True(sawEnd);
            byte[] z = idat.ToArray();
            Assert.Equal(0x78, z[0]);
            byte[] raw;
            using (var ms = new MemoryStream(z, 2, z.Length - 6))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var outp = new MemoryStream())
            {
                ds.CopyTo(outp);
                raw = outp.ToArray();
            }
            Assert.Equal(PartParityPng.Adler32(raw), ReadBE(z, z.Length - 4));
            int stride = width * 4;
            Assert.Equal((stride + 1) * height, raw.Length);
            var rgba = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                Assert.Equal(0, raw[y * (stride + 1)]);
                Buffer.BlockCopy(raw, y * (stride + 1) + 1, rgba, y * stride, stride);
            }
            return rgba;
        }

        private static byte[] Pattern(int w, int h, int seed)
        {
            var b = new byte[w * h * 4];
            var rnd = new Random(seed);
            rnd.NextBytes(b);
            return b;
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(7, 3)]
        [InlineData(64, 64)]
        [InlineData(256, 128)]
        public void Png_RoundTripsEveryPixel(int w, int h)
        {
            byte[] src = Pattern(w, h, w * 31 + h);
            byte[] png = PartParityPng.EncodeRgba(w, h, src);
            byte[] back = DecodeRgba(png, out int dw, out int dh);
            Assert.Equal(w, dw);
            Assert.Equal(h, dh);
            Assert.Equal(src, back);
        }

        [Fact]
        public void Png_RejectsAWrongSizedBuffer()
        {
            Assert.Throws<ArgumentException>(() => PartParityPng.EncodeRgba(4, 4, new byte[4 * 4 * 4 - 1]));
            Assert.Throws<ArgumentException>(() => PartParityPng.EncodeRgba(0, 4, new byte[0]));
        }

        [Fact]
        public void Crc32_MatchesTheStandardCheckValue()
        {
            byte[] check = System.Text.Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, PartParityPng.Crc32(check, 0, check.Length));
            Assert.Equal(0x091E01DEu, PartParityPng.Adler32(check));
        }

        private static byte[] Solid(int tile, byte r, byte g, byte b, byte a)
        {
            var t = new byte[tile * tile * 4];
            for (int i = 0; i < t.Length; i += 4) { t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a; }
            return t;
        }

        [Fact]
        public void Atlas_PlacesRowsAndColumns()
        {
            const int tile = 4;
            var rows = new[]
            {
                new[] { Solid(tile, 10, 0, 0, 255), Solid(tile, 20, 0, 0, 255), Solid(tile, 30, 0, 0, 255) },
                new[] { Solid(tile, 0, 40, 0, 255), null, Solid(tile, 0, 60, 0, 255) },
            };
            byte[] atlas = PartParityImage.BuildAtlas(rows, 3, tile, out int w, out int h);
            Assert.Equal(12, w);
            Assert.Equal(8, h);
            Func<int, int, int> at = (x, y) => (y * w + x) * 4;
            Assert.Equal(10, atlas[at(0, 0)]);
            Assert.Equal(20, atlas[at(5, 3)]);
            Assert.Equal(30, atlas[at(11, 0)]);
            Assert.Equal(40, atlas[at(0, 4) + 1]);
            Assert.Equal(0, atlas[at(5, 5) + 3]); // the null tile stays transparent
            Assert.Equal(60, atlas[at(11, 7) + 1]);
        }

        [Fact]
        public void Downsample_WeightsColourByAlpha()
        {
            // 2x1 -> 1x1: one opaque red pixel beside one fully transparent black pixel. A plain
            // average would read dark red; the alpha-weighted one keeps pure red at half alpha.
            byte[] src = { 200, 0, 0, 255, 0, 0, 0, 0 };
            byte[] d = PartParityImage.Downsample(src, 2, 1, 1, 1);
            Assert.Equal(200, d[0]);
            Assert.Equal(0, d[1]);
            Assert.Equal(128, d[3]);
        }

        [Fact]
        public void Downsample_FullyTransparentCellIsTransparentBlack()
        {
            byte[] d = PartParityImage.Downsample(new byte[4 * 4 * 4], 4, 4, 2, 2);
            Assert.All(d, b => Assert.Equal(0, b));
        }

        [Fact]
        public void Thumbnail_IsTwoTilesOfThumbSizeSideBySide()
        {
            const int tile = 256;
            byte[] thumb = PartParityImage.BuildThumbnail(Solid(tile, 255, 0, 0, 255), Solid(tile, 0, 0, 255, 255), tile, out int w, out int h);
            Assert.Equal(2 * PartParityImage.ThumbTile, w);
            Assert.Equal(PartParityImage.ThumbTile, h);
            Assert.Equal(255, thumb[0]);                                 // real on the left
            Assert.Equal(255, thumb[(PartParityImage.ThumbTile) * 4 + 2]); // ghost on the right
        }
    }
}
