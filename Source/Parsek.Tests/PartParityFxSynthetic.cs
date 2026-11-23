using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Parsek.TestCommands;

namespace Parsek.Tests
{
    /// <summary>
    /// Synthetic FX samples for the F1b tests and fixture (design-part-ghost-parity.md E.4):
    /// a plume is a band of constant width along an exhaust axis whose brightness falls to half
    /// at its tip, drawn per sample with a stochastic tip and edge from a fixed integer LCG (no
    /// <see cref="Random"/>, whose sequence is not pinned across runtimes), so every tile and
    /// measure is the same on every host.
    /// </summary>
    internal sealed class FxPlume
    {
        internal int Tile = 64;
        internal double OriginX = 8.0, OriginY = 32.0;
        internal double DirX = 1.0, DirY = 0.0;

        /// <summary>Along-axis start and length, across-axis centre and half width, px.</summary>
        internal double Start = 0.0, Length = 40.0, Lateral = 0.0, HalfWidth = 5.0;

        /// <summary>The colour at the start (may exceed 255: clipped per channel, which is
        /// what an additive overlap does).</summary>
        internal double R = 240.0, G = 140.0, B = 60.0;

        /// <summary>The alpha written (0 for an additive flame render, the matte's alpha for smoke).</summary>
        internal byte Alpha = 0;

        /// <summary>Tip jitter (px of length removed, 0 to this - 1) and edge dropout (one
        /// in this many outer-row pixels dropped; 0 for none).</summary>
        internal int TipJitter = 3;
        internal int EdgeDropout = 3;

        /// <summary>A disc instead of a band (the axis view's cross-section): radius px,
        /// centred at the origin plus (DiscDx, DiscDy).</summary>
        internal double DiscRadius = 0.0;
        internal double DiscDx = 0.0, DiscDy = 0.0;

        internal bool RotateHue;
        internal uint Seed = 1;

        internal FxPlume Clone() => (FxPlume)MemberwiseClone();

        /// <summary>The <paramref name="i"/>-th sample, RGBA8 top-down.</summary>
        internal byte[] Sample(int i)
        {
            var px = new byte[Tile * Tile * 4];
            uint state = Seed * 2654435761u + (uint)i * 40503u + 12345u;
            double len = Length - (TipJitter > 0 ? Next(ref state) % (uint)TipJitter : 0);
            double dl = Math.Sqrt(DirX * DirX + DirY * DirY);
            double ux = DirX / dl, uy = DirY / dl;
            for (int y = 0; y < Tile; y++)
            {
                for (int x = 0; x < Tile; x++)
                {
                    double cx = x + 0.5, cy = y + 0.5;
                    double f;
                    bool edge;
                    if (DiscRadius > 0.0)
                    {
                        double dx = cx - (OriginX + DiscDx), dy = cy - (OriginY + DiscDy);
                        double d = Math.Sqrt(dx * dx + dy * dy);
                        if (d > DiscRadius) continue;
                        f = 1.0 - 0.5 * d / DiscRadius;
                        edge = d > DiscRadius - 1.0;
                    }
                    else
                    {
                        double t = (cx - OriginX) * ux + (cy - OriginY) * uy;
                        double s = (cy - OriginY) * ux - (cx - OriginX) * uy;
                        if (t < Start || t > Start + len) continue;
                        double off = Math.Abs(s - Lateral);
                        if (off > HalfWidth) continue;
                        f = 1.0 - 0.5 * (t - Start) / Length;
                        edge = off > HalfWidth - 1.0;
                    }
                    uint roll = Next(ref state);
                    if (edge && EdgeDropout > 0 && roll % (uint)EdgeDropout == 0) continue;
                    byte r = Clip(R * f), g = Clip(G * f), b = Clip(B * f);
                    if (RotateHue) PartParityFxRules.RotateHueControl(ref r, ref g, ref b);
                    int o = (y * Tile + x) * 4;
                    px[o] = r;
                    px[o + 1] = g;
                    px[o + 2] = b;
                    px[o + 3] = Alpha;
                }
            }
            return px;
        }

        /// <summary>The sample set of <paramref name="n"/> samples on one layer.</summary>
        internal PartParityFxSampleSet Samples(string layer, int n)
        {
            var set = new PartParityFxSampleSet(layer, Tile, Tile);
            for (int i = 0; i < n; i++) set.Add(Sample(i));
            return set;
        }

        internal PartParityFxAxis Axis(double? nozzle = 10.0)
            => new PartParityFxAxis
            {
                OriginPx = new[] { OriginX, OriginY },
                DirPx = DiscRadius > 0.0 ? null : new[] { DirX, DirY },
                NozzleDiameterPx = nozzle,
            };

        private static uint Next(ref uint s)
        {
            s = s * 1664525u + 1013904223u;
            return s >> 8;
        }

        private static byte Clip(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));

        /// <summary>An empty sample set (nothing drawn).</summary>
        internal static PartParityFxSampleSet Empty(string layer, int tile, int n)
        {
            var set = new PartParityFxSampleSet(layer, tile, tile);
            for (int i = 0; i < n; i++) set.Add(new byte[tile * tile * 4]);
            return set;
        }
    }

    /// <summary>A test-only reader of the PNGs <see cref="PartParityPng.EncodeRgba"/> writes
    /// (colour type 6, filter 0 on every row): the pixels, not the deflate bytes, are the
    /// contract, since a runtime's deflate output may differ.</summary>
    internal static class FxPngReader
    {
        internal static byte[] DecodeRgba(byte[] png, out int width, out int height)
        {
            width = height = 0;
            for (int i = 0; i < 8; i++)
                if (png[i] != PartParityPng.Signature[i]) throw new InvalidDataException("not a PNG");
            var idat = new MemoryStream();
            int p = 8;
            while (p + 8 <= png.Length)
            {
                int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
                string type = Encoding.ASCII.GetString(png, p + 4, 4);
                if (type == "IHDR")
                {
                    width = (png[p + 8] << 24) | (png[p + 9] << 16) | (png[p + 10] << 8) | png[p + 11];
                    height = (png[p + 12] << 24) | (png[p + 13] << 16) | (png[p + 14] << 8) | png[p + 15];
                    if (png[p + 16] != 8 || png[p + 17] != 6) throw new InvalidDataException("not RGBA8");
                }
                else if (type == "IDAT") idat.Write(png, p + 8, len);
                else if (type == "IEND") break;
                p += 12 + len;
            }
            byte[] z = idat.ToArray();
            var raw = new MemoryStream();
            using (var ds = new DeflateStream(new MemoryStream(z, 2, z.Length - 2), CompressionMode.Decompress))
                ds.CopyTo(raw);
            byte[] r = raw.ToArray();
            int stride = width * 4;
            if (r.Length != (stride + 1) * height) throw new InvalidDataException("bad IDAT size");
            var px = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                if (r[y * (stride + 1)] != 0) throw new InvalidDataException("filter " + r[y * (stride + 1)]);
                Buffer.BlockCopy(r, y * (stride + 1) + 1, px, y * stride, stride);
            }
            return px;
        }

        /// <summary>One tile of an atlas (top-down RGBA8).</summary>
        internal static byte[] Crop(byte[] atlas, int atlasWidth, int row, int col, int tile)
        {
            var t = new byte[tile * tile * 4];
            for (int y = 0; y < tile; y++)
                Buffer.BlockCopy(atlas, ((row * tile + y) * atlasWidth + col * tile) * 4, t, y * tile * 4, tile * 4);
            return t;
        }
    }

    /// <summary>
    /// The committed synthetic FX fixture (design E.4, piece F1b) the harness judge recomputes
    /// (F4): one plume case's FX atlas PNG (<see cref="PartParityJson.FxAtlasRows"/> by
    /// <see cref="PartParityFxRules.Views"/>, 64 px tiles, 16 samples) and its sidecar, whose
    /// <c>fx.views[]</c> carry every side measure, comparison and split half F1b writes. The
    /// event-route ghost is a short plume (length), the start-route ghost a hue-rotated,
    /// shifted plume with its smoke removed (colour, origin, lateral, smoke); the real core
    /// clips near the nozzle (saturation).
    /// </summary>
    internal static class PartParityFxFixture
    {
        internal const int Tile = 64;
        internal const int SampleCount = PartParityFxRules.Samples;
        internal const string Part = "fxFixturePlume";

        internal static string TargetDir(string repoRoot)
            => Path.Combine(repoRoot, "Source", "Parsek.Tests", "Fixtures", "PartParity", "fx");

        internal static string CaseId()
            => TestCommandPartParitySweep.CaseId(new PartParityCaseKey
            {
                Part = Part, State = PartParityFxRules.StateBurnFull, Context = PartParityStateRules.ContextAlone,
            });

        /// <summary>The real and the two ghost plumes of one view and layer.</summary>
        internal static FxPlume[] Plumes(string view, string layer)
        {
            bool smoke = layer == PartParityFxRules.LayerSmoke;
            bool far = view == PartParityFxRules.ViewFxFar;
            bool bottom = view == PartParityFxRules.ViewFxBottom;
            var real = new FxPlume
            {
                Tile = Tile, Seed = (uint)Array.IndexOf(PartParityFxRules.Views, view) * 7u + (smoke ? 3u : 1u),
                Length = far ? 50.0 : 40.0, HalfWidth = smoke ? 7.0 : 5.0,
                R = smoke ? 120.0 : 330.0, G = smoke ? 120.0 : 230.0, B = smoke ? 120.0 : 90.0,
                Alpha = smoke ? (byte)200 : (byte)0,
            };
            if (view == PartParityFxRules.ViewFxSideB) { real.OriginX = 32.0; real.OriginY = 8.0; real.DirX = 0.0; real.DirY = 1.0; }
            if (bottom) { real.OriginX = 32.0; real.OriginY = 32.0; real.DiscRadius = smoke ? 14.0 : 10.0; }
            FxPlume ghost = real.Clone();
            ghost.Seed += 100;
            if (bottom) ghost.DiscRadius = real.DiscRadius * 0.85;
            else ghost.Length = real.Length * 0.6;
            FxPlume start = real.Clone();
            start.Seed += 200;
            start.RotateHue = true;
            if (bottom) { start.DiscDx = 2.0; start.DiscDy = -1.0; }
            else { start.Start = 6.0; start.Lateral = 6.0; }
            return new[] { real, ghost, start };
        }

        internal sealed class Built
        {
            internal byte[] Atlas;
            internal int Width, Height;
            internal string Sidecar;
            internal List<PartParityFxViewResult> Entries = new List<PartParityFxViewResult>();
        }

        internal static Built Build()
        {
            var b = new Built();
            string[] rows = PartParityJson.FxAtlasRowsFor(false);
            string[] cols = PartParityFxRules.Views;
            var tiles = new byte[rows.Length][][];
            for (int r = 0; r < rows.Length; r++) tiles[r] = new byte[cols.Length][];
            for (int c = 0; c < cols.Length; c++)
            {
                foreach (string layer in PartParityFxRules.FxLayers)
                {
                    FxPlume[] p = Plumes(cols[c], layer);
                    PartParityFxSampleSet real = p[0].Samples(layer, SampleCount);
                    PartParityFxSampleSet ghost = p[1].Samples(layer, SampleCount);
                    PartParityFxSampleSet start = layer == PartParityFxRules.LayerSmoke
                        ? FxPlume.Empty(layer, Tile, SampleCount)
                        : p[2].Samples(layer, SampleCount);
                    PartParityFxAxis axis = p[0].Axis(8.0);
                    var ev = PartParityFxViewResult.Measure(cols[c], PartParityStateRules.RouteEvent, layer, axis, real, ghost);
                    var st = PartParityFxViewResult.Measure(cols[c], PartParityStateRules.RouteStart, layer, axis, real, start);
                    b.Entries.Add(ev);
                    b.Entries.Add(st);
                    tiles[Array.IndexOf(rows, PartParityFxMetrics.AtlasRowFor(PartParityFxRules.SideReal, layer))][c] = ev.RealTile;
                    tiles[Array.IndexOf(rows, PartParityFxMetrics.AtlasRowFor(PartParityFxRules.SideGhost, layer))][c] = ev.GhostTile;
                    tiles[Array.IndexOf(rows, PartParityFxMetrics.AtlasRowFor(PartParityFxRules.SideGhostStart, layer))][c] = st.GhostTile;
                }
            }
            b.Atlas = PartParityImage.BuildAtlas(tiles, cols.Length, Tile, out b.Width, out b.Height);

            string id = CaseId();
            var views = new List<string>();
            foreach (PartParityFxViewResult e in b.Entries) views.Add(e.Json());
            var sidecar = new PartParityCaseSidecar
            {
                CaseId = id, Part = Part, Title = "synthetic plume (F1b fixture)", Source = "synthetic",
                State = PartParityFxRules.StateBurnFull, Context = PartParityStateRules.ContextAlone,
                Setup = new PartParitySetup { Status = TestCommandPartParitySweep.SetupOk },
                Fx = new Dictionary<string, string>
                {
                    { "family", PartParityJson.Str(PartParityFxRules.FamilyPlume) },
                    { "samples", PartParityJson.Int(SampleCount) },
                    { "views", "[" + string.Join(",", views.ToArray()) + "]" },
                },
                FxAtlas = new Dictionary<string, string>
                {
                    { "file", PartParityJson.Str(TestCommandPartParitySweep.FxAtlasFileName(id)) },
                    { "rows", PartParityJson.StrArray(rows) },
                    { "cols", PartParityJson.StrArray(cols) },
                    { "tile", PartParityJson.Int(Tile) },
                },
            };
            b.Sidecar = PartParityJson.WriteCase(sidecar);
            return b;
        }

        /// <summary>The fixture's files, name -> bytes (LF text).</summary>
        internal static SortedDictionary<string, byte[]> BuildFiles(out Built built)
        {
            built = Build();
            string id = CaseId();
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { TestCommandPartParitySweep.FxAtlasFileName(id), PartParityPng.EncodeRgba(built.Width, built.Height, built.Atlas) },
                { TestCommandPartParitySweep.SidecarFileName(id), Encoding.UTF8.GetBytes(built.Sidecar.Replace("\r\n", "\n")) },
            };
            return files;
        }
    }
}
