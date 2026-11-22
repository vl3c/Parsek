using System;
using System.IO;
using System.IO.Compression;

namespace Parsek.TestCommands
{
    /// <summary>
    /// A minimal RGBA8 PNG writer for the part parity sweep (design-part-ghost-parity.md
    /// sections 4.4, 11): zlib over <see cref="DeflateStream"/>, filter 0 on every row, no
    /// <c>ImageConversionModule</c> reference (<c>EncodeToPNG</c> would need a thirteenth
    /// Unity module reference). Pure: the P0 spike's proven writer, moved out of the spike.
    /// </summary>
    internal static class PartParityPng
    {
        internal static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>The PNG / zlib CRC-32 of <paramref name="count"/> bytes from <paramref name="offset"/>.</summary>
        internal static uint Crc32(byte[] data, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        /// <summary>The zlib Adler-32 of the whole array.</summary>
        internal static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521u;
                b = (b + a) % 65521u;
            }
            return (b << 16) | a;
        }

        /// <summary>Encodes RGBA8 pixels, rows top-down, as a colour-type-6 PNG.</summary>
        internal static byte[] EncodeRgba(int width, int height, byte[] rgbaTopDown)
        {
            if (width <= 0 || height <= 0 || rgbaTopDown == null || rgbaTopDown.Length != (long)width * height * 4)
                throw new ArgumentException("bad image size");
            int stride = width * 4;
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                raw[y * (stride + 1)] = 0;
                Buffer.BlockCopy(rgbaTopDown, y * stride, raw, y * (stride + 1) + 1, stride);
            }
            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(raw, 0, raw.Length);
                deflated = ms.ToArray();
            }
            var zlib = new byte[deflated.Length + 6];
            zlib[0] = 0x78;
            zlib[1] = 0x9C;
            Buffer.BlockCopy(deflated, 0, zlib, 2, deflated.Length);
            WriteBE(zlib, zlib.Length - 4, Adler32(raw));

            using (var png = new MemoryStream())
            {
                png.Write(Signature, 0, Signature.Length);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)width);
                WriteBE(ihdr, 4, (uint)height);
                ihdr[8] = 8;   // bit depth
                ihdr[9] = 6;   // colour type RGBA
                ihdr[10] = 0;  // deflate
                ihdr[11] = 0;  // adaptive filtering
                ihdr[12] = 0;  // no interlace
                WriteChunk(png, "IHDR", ihdr);
                WriteChunk(png, "IDAT", zlib);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void WriteBE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            var body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            s.Write(body, 0, body.Length);
            var crc = new byte[4];
            WriteBE(crc, 0, Crc32(body, 0, body.Length));
            s.Write(crc, 0, 4);
        }
    }

    /// <summary>
    /// Pure RGBA8 image operations of the sweep's files: the atlas (rows real / ghost,
    /// columns the views) and the grid thumbnail. Arrays are row-major, 4 bytes per pixel.
    /// </summary>
    internal static class PartParityImage
    {
        /// <summary>Thumbnail tile size, px (design 5.1: a 2 x 128 px pair).</summary>
        internal const int ThumbTile = 128;

        /// <summary>
        /// The atlas: <c>rows.Length</c> rows by <c>cols</c> columns of square tiles, each
        /// <paramref name="tile"/> px. <c>rows[r][c]</c> is one tile (top-down RGBA8); a null
        /// tile leaves its cell transparent.
        /// </summary>
        internal static byte[] BuildAtlas(byte[][][] rows, int cols, int tile, out int width, out int height)
        {
            if (rows == null || rows.Length == 0 || cols <= 0 || tile <= 0)
                throw new ArgumentException("empty atlas");
            width = cols * tile;
            height = rows.Length * tile;
            var atlas = new byte[(long)width * height * 4];
            int tileStride = tile * 4, atlasStride = width * 4;
            for (int r = 0; r < rows.Length; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    byte[] t = rows[r] != null && c < rows[r].Length ? rows[r][c] : null;
                    if (t == null) continue;
                    if (t.Length != tile * tile * 4) throw new ArgumentException("tile size mismatch");
                    for (int y = 0; y < tile; y++)
                    {
                        Buffer.BlockCopy(t, y * tileStride, atlas,
                            (r * tile + y) * atlasStride + c * tileStride, tileStride);
                    }
                }
            }
            return atlas;
        }

        /// <summary>
        /// Box-filter downsample with alpha weighting (colour averaged premultiplied, then
        /// divided back), so a transparent background never darkens the edge colour. Each
        /// destination pixel covers source columns <c>[x * sw / dw, (x + 1) * sw / dw)</c>,
        /// at least one.
        /// </summary>
        internal static byte[] Downsample(byte[] src, int sw, int sh, int dw, int dh)
        {
            if (src == null || src.Length != (long)sw * sh * 4 || dw <= 0 || dh <= 0)
                throw new ArgumentException("bad image size");
            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    AverageCell(src, sw, x0, x1, y0, y1, dst, (y * dw + x) * 4);
                }
            }
            return dst;
        }

        private static void AverageCell(byte[] src, int sw, int x0, int x1, int y0, int y1, byte[] dst, int o)
        {
            long r = 0, g = 0, b = 0, a = 0;
            int n = 0;
            for (int yy = y0; yy < y1; yy++)
            {
                for (int xx = x0; xx < x1; xx++)
                {
                    int i = (yy * sw + xx) * 4;
                    int pa = src[i + 3];
                    r += src[i] * pa;
                    g += src[i + 1] * pa;
                    b += src[i + 2] * pa;
                    a += pa;
                    n++;
                }
            }
            if (a > 0)
            {
                dst[o] = (byte)((r + a / 2) / a);
                dst[o + 1] = (byte)((g + a / 2) / a);
                dst[o + 2] = (byte)((b + a / 2) / a);
            }
            dst[o + 3] = (byte)((a + n / 2) / n);
        }

        /// <summary>The grid thumbnail: the real and ghost tiles of one view, each
        /// downsampled to <see cref="ThumbTile"/> px, side by side (real left).</summary>
        internal static byte[] BuildThumbnail(byte[] real, byte[] ghost, int tile, out int width, out int height)
        {
            byte[] r = real != null ? Downsample(real, tile, tile, ThumbTile, ThumbTile) : null;
            byte[] g = ghost != null ? Downsample(ghost, tile, tile, ThumbTile, ThumbTile) : null;
            return BuildAtlas(new[] { new[] { r, g } }, 2, ThumbTile, out width, out height);
        }
    }
}
