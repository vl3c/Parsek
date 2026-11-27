using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Parsek.TestCommands;

namespace Parsek.Tests
{
    /// <summary>
    /// Synthetic bursts for the X3 tests and fixture (design-part-ghost-parity.md G.3): a burst
    /// is a disc about the source point whose radius follows a per-bin profile and whose
    /// brightness falls to half at its rim, drawn per frame and trial with a stochastic rim from
    /// a fixed integer LCG (no <see cref="Random"/>, whose sequence is not pinned across
    /// runtimes), so every tile and measure is the same on every host. A late, small, shifted,
    /// hue-rotated or missing burst is the same burst with one knob turned.
    /// </summary>
    internal sealed class FxBurst
    {
        internal int Tile = 48;
        internal double SourceX = 24.0, SourceY = 24.0;

        /// <summary>The disc radius per bin, px (0: nothing drawn); the real burst's profile
        /// rises fast to its one peak at bin 4 (so noise cannot move the peak bin) and fades by bin 15.</summary>
        internal double[] Radius = { 0, 0, 14, 15, 16, 15, 15, 15, 15, 14, 13, 12, 10, 7, 4, 0 };

        internal int FramesPerBin = 2;
        internal int Trials = PartParityFxRules.BurstTrials;

        /// <summary>Bins the whole profile starts late by.</summary>
        internal int DelayBins;

        /// <summary>Every radius times this.</summary>
        internal double Scale = 1.0;

        /// <summary>The disc's centre off the source point, px.</summary>
        internal double Dx, Dy;

        internal double R = 240.0, G = 140.0, B = 60.0;

        /// <summary>The alpha written (0 for an additive flame render, the matte's alpha for smoke).</summary>
        internal byte Alpha;

        internal bool RotateHue;

        /// <summary>With <see cref="RotateHue"/>, the one bin left unrotated (-1: none), so a
        /// ghost's colour can differ over the bins and not at the real peak bin.</summary>
        internal int KeepHueBin = -1;

        /// <summary>Draws nothing (a missing burst).</summary>
        internal bool Off;

        /// <summary>One rim pixel in this many is dropped per sample (0: none).</summary>
        internal int RimDropout = 3;

        internal uint Seed = 1;

        internal int Bins => Radius.Length;

        internal FxBurst Clone()
        {
            var b = (FxBurst)MemberwiseClone();
            b.Radius = (double[])Radius.Clone();
            return b;
        }

        internal double RadiusAt(int bin)
        {
            int src = bin - DelayBins;
            if (Off || src < 0 || src >= Radius.Length) return 0.0;
            return Radius[src] * Scale;
        }

        /// <summary>One frame of one trial in one bin, RGBA8 top-down.</summary>
        internal byte[] Sample(int bin, int frame, int trial)
        {
            var px = new byte[Tile * Tile * 4];
            double radius = RadiusAt(bin);
            if (radius <= 0.0) return px;
            uint state = Seed * 2654435761u + (uint)bin * 40503u + (uint)frame * 9973u + (uint)trial * 7919u + 12345u;
            double cx0 = SourceX + Dx, cy0 = SourceY + Dy;
            for (int y = 0; y < Tile; y++)
            {
                for (int x = 0; x < Tile; x++)
                {
                    double dx = x + 0.5 - cx0, dy = y + 0.5 - cy0;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > radius) continue;
                    uint roll = Next(ref state);
                    if (d > radius - 1.5 && RimDropout > 0 && roll % (uint)RimDropout == 0) continue;
                    double f = 1.0 - 0.5 * d / radius;
                    byte r = Clip(R * f), g = Clip(G * f), b = Clip(B * f);
                    if (RotateHue && bin != KeepHueBin) PartParityFxRules.RotateHueControl(ref r, ref g, ref b);
                    int o = (y * Tile + x) * 4;
                    px[o] = r;
                    px[o + 1] = g;
                    px[o + 2] = b;
                    px[o + 3] = Alpha;
                }
            }
            return px;
        }

        /// <summary>The burst's accumulation on one layer: every frame of every trial into its bin.</summary>
        internal PartParityBurstAccumulator Accumulate(string layer)
        {
            var acc = new PartParityBurstAccumulator(layer, Tile, Tile, Bins);
            for (int b = 0; b < Bins; b++)
                for (int t = 0; t < Trials; t++)
                    for (int f = 0; f < FramesPerBin; f++)
                        acc.Add(b, t, Sample(b, f, t));
            return acc;
        }

        internal PartParityBurstSource Source(double diameterPx = 8.0)
            => new PartParityBurstSource
            {
                RealPx = new[] { SourceX, SourceY }, GhostPx = new[] { SourceX, SourceY }, DiameterPx = diameterPx,
            };

        private static uint Next(ref uint s)
        {
            s = s * 1664525u + 1013904223u;
            return s >> 8;
        }

        private static byte Clip(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));
    }

    /// <summary>
    /// The committed synthetic burst fixture (design G.3, piece X3) the harness recompute reads
    /// (X5): one explode case's burst atlas PNG (<see cref="PartParityJson.BurstAtlasRows"/> by
    /// <see cref="PartParityFxRules.BurstViews"/> x 16 bins, 48 px tiles, 8 samples a bin) and its
    /// sidecar, whose <c>burst</c> block carries every bin measure, side summary, comparison,
    /// split half and the schedule. The ghost is late on <c>burst-side</c>, shifted on
    /// <c>burst-top</c> and hue-rotated on <c>burst-far</c> (flame); its smoke is another draw on
    /// the side view, missing on the top view and small on the far view.
    /// </summary>
    internal static class PartParityBurstFixture
    {
        internal const string Part = "burstFixturePart";

        internal static string TargetDir(string repoRoot)
            => Path.Combine(repoRoot, "Source", "Parsek.Tests", "Fixtures", "PartParity", "burst");

        internal static string CaseId()
            => TestCommandPartParitySweep.CaseId(new PartParityCaseKey
            {
                Part = Part, State = PartParityFxRules.StateExplode, Context = PartParityFxRules.ContextBurst,
            });

        /// <summary>The real and ghost bursts of one view and layer.</summary>
        internal static FxBurst[] Bursts(string view, string layer)
        {
            bool smoke = layer == PartParityFxRules.LayerSmoke;
            int v = Array.IndexOf(PartParityFxRules.BurstViews, view);
            var real = new FxBurst { Seed = (uint)v * 7u + (smoke ? 3u : 1u) };
            if (smoke)
            {
                real.R = real.G = real.B = 120.0;
                real.Alpha = 200;
                real.Radius = new double[] { 0, 0, 10, 13, 16, 18, 19, 21, 20, 20, 20, 19, 18, 16, 14, 12 };
            }
            FxBurst ghost = real.Clone();
            ghost.Seed += 100;
            if (!smoke)
            {
                if (view == PartParityFxRules.ViewBurstSide) ghost.DelayBins = 2;
                else if (view == PartParityFxRules.ViewBurstTop) ghost.Dx = 3.0;
                else ghost.RotateHue = true;
            }
            else if (view == PartParityFxRules.ViewBurstTop) ghost.Off = true;
            else if (view == PartParityFxRules.ViewBurstFar) ghost.Scale = 0.6;
            return new[] { real, ghost };
        }

        internal sealed class Built
        {
            internal byte[] Atlas;
            internal int Width, Height;
            internal string Sidecar;
            internal readonly List<PartParityBurstViewResult> Entries = new List<PartParityBurstViewResult>();
            internal List<PartParityBurstBlast> RealSchedule, GhostSchedule;
            internal PartParityBurstScheduleCompare Schedule;
        }

        internal static Built Build()
        {
            var b = new Built();
            const int tile = 48, bins = 16;
            string[] rows = PartParityJson.BurstAtlasRows;
            string[] views = PartParityFxRules.BurstViews;
            var tiles = new byte[rows.Length][][];
            for (int r = 0; r < rows.Length; r++) tiles[r] = new byte[views.Length * bins][];
            int[] samples = null;
            foreach (string view in views)
            {
                int v = Array.IndexOf(views, view);
                foreach (string layer in PartParityFxRules.BurstLayers)
                {
                    FxBurst[] p = Bursts(view, layer);
                    var e = PartParityBurstViewResult.Measure(view, layer, p[0].Source(),
                        p[0].Accumulate(layer), p[1].Accumulate(layer));
                    b.Entries.Add(e);
                    samples = e.RealSamples;
                    int realRow = Array.IndexOf(rows, PartParityFxRules.SideReal + "-" + layer);
                    int ghostRow = Array.IndexOf(rows, PartParityFxRules.SideGhost + "-" + layer);
                    for (int i = 0; i < bins; i++)
                    {
                        tiles[realRow][v * bins + i] = e.RealTiles[i];
                        tiles[ghostRow][v * bins + i] = e.GhostTiles[i];
                    }
                }
            }
            b.Atlas = PartParityImage.BuildAtlas(tiles, views.Length * bins, tile, out b.Width, out b.Height);
            b.RealSchedule = new List<PartParityBurstBlast>
            {
                new PartParityBurstBlast { Frame = 12, T = 0.0, OffsetM = new[] { 0.0, 0.0, 0.0 }, Power = 0.5, Prefab = "fx-explosion-1", Index = 1 },
            };
            b.GhostSchedule = new List<PartParityBurstBlast>
            {
                new PartParityBurstBlast { Frame = 12, T = 0.0, OffsetM = new[] { 0.25, 0.0, -0.1 }, Power = 0.55, Prefab = "fx-explosion-1", Index = 1 },
            };
            b.Schedule = PartParityBurstMetrics.CompareSchedule(b.RealSchedule, b.GhostSchedule);

            string id = CaseId();
            var entries = new List<string>();
            foreach (PartParityBurstViewResult e in b.Entries) entries.Add(e.Json());
            string samplesJson = PartParityJson.IntArray(samples);
            var sidecar = new PartParityCaseSidecar
            {
                CaseId = id, Part = Part, Title = "synthetic burst (X3 fixture)", Source = "synthetic",
                State = PartParityFxRules.StateExplode, Context = PartParityFxRules.ContextBurst,
                Setup = new PartParitySetup { Status = TestCommandPartParitySweep.SetupOk },
                Burst = new Dictionary<string, string>
                {
                    { "family", PartParityJson.Str(PartParityFxRules.FamilyExplode) },
                    { "potential", PartParityJson.Num(PartParityFxRules.DefaultExplosionPotential) },
                    { "windowS", PartParityJson.Num(PartParityFxRules.BurstWindowSeconds) },
                    { "binS", PartParityJson.Num(PartParityFxRules.BurstBinSeconds) },
                    { "bins", PartParityJson.Int(bins) },
                    { "trials", PartParityJson.Int(PartParityFxRules.BurstTrials) },
                    { "samples", PartParityJson.KeyedBlock(PartParityJson.BurstSideKeys, new Dictionary<string, string>
                        { { PartParityFxRules.SideReal, samplesJson }, { PartParityFxRules.SideGhost, samplesJson } }) },
                    { "schedule", PartParityBurstMetrics.ScheduleJson(b.RealSchedule, b.GhostSchedule) },
                    { "scheduleCompare", PartParityBurstMetrics.ScheduleCompareJson(b.Schedule) },
                    { "atlas", PartParityJson.KeyedBlock(PartParityJson.BurstAtlasKeys, new Dictionary<string, string>
                        {
                            { "file", PartParityJson.Str(TestCommandPartParitySweep.BurstAtlasFileName(id)) },
                            { "rows", PartParityJson.StrArray(rows) },
                            { "views", PartParityJson.StrArray(views) },
                            { "bins", PartParityJson.Int(bins) },
                            { "tile", PartParityJson.Int(tile) },
                        }) },
                    { "views", "[" + string.Join(",", entries.ToArray()) + "]" },
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
            return new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { TestCommandPartParitySweep.BurstAtlasFileName(id), PartParityPng.EncodeRgba(built.Width, built.Height, built.Atlas) },
                { TestCommandPartParitySweep.SidecarFileName(id), Encoding.UTF8.GetBytes(built.Sidecar.Replace("\r\n", "\n")) },
            };
        }
    }
}
