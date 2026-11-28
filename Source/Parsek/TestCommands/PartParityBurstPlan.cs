using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>One read of stock's <c>ModuleSurfaceFX</c> raycast (design G.2.2): whether it
    /// hit a surface, which, how far from the thrust transform and the dust scale stock drew.</summary>
    internal sealed class PartParityDustRaycastSample
    {
        internal bool Hit;
        internal string Surface = PartParityFxRules.DustSurfaceNone;
        internal double DistanceM = double.NaN;
        internal double ScaledFx = double.NaN;

        /// <summary>The hit point relative to the thrust transform, slot frame (east, up, north); null on a miss.</summary>
        internal double[] PointM;

        /// <summary>The surface normal, slot frame (east, up, north); null on a miss.</summary>
        internal double[] Normal;
    }

    /// <summary>Stock's raycast over a dust case's samples (<c>dust.raycast</c>).</summary>
    internal sealed class PartParityDustRaycastSummary
    {
        internal string Surface = PartParityFxRules.DustSurfaceNone;
        internal int Hits;
        internal int Misses;
        internal double DistanceM = double.NaN;
        internal double ScaledFx = double.NaN;
        internal double[] PointM;
        internal double[] Normal;
        internal readonly List<PartParityDustRaycastSample> Samples = new List<PartParityDustRaycastSample>();
    }

    /// <summary>
    /// The pure decisions of the live burst and dust driver (design-part-ghost-parity.md
    /// Appendix G, piece X4, <see cref="PartParityBurstDriver"/>): the wave kinds and their
    /// admission, the trial and dust slots, the tagging of an effect to its source, the
    /// control variations, the setup statuses, the stock raycast summary, the control evidence
    /// flags and the dust option clones. No Unity call: every rule here is unit-tested.
    /// </summary>
    internal static class PartParityBurstPlan
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // ----- wave kinds and admission -----

        /// <summary>The wave kind of a burst case: one case per wave, its four trials side by side.</summary>
        internal const string KindBurst = "p9:burst";

        /// <summary>The wave kind of a dust case: up to <see cref="MaxDustWaveCases"/> per wave.</summary>
        internal const string KindDust = "p9:dust";

        internal const int MaxDustWaveCases = 4;

        /// <summary>The time box's worst case of one wave (spawn, settle, window, write), seconds.</summary>
        internal const double BurstWaveWorstSeconds = 60.0;
        internal const double DustWaveWorstSeconds = 150.0;

        /// <summary>The wave kind of a P9 family; null for any other family.</summary>
        internal static string WaveKind(string family)
        {
            if (PartParityFxRules.IsBurstFamily(family)) return KindBurst;
            if (PartParityFxRules.IsDustFamily(family)) return KindDust;
            return null;
        }

        internal static bool IsP9Kind(string kind) => kind == KindBurst || kind == KindDust;

        /// <summary>A burst or dust control's wave kind, also when it found no subject (its
        /// case then carries no option); null for any other control.</summary>
        internal static string ControlWaveKind(string controlId)
        {
            if (Array.IndexOf(TestCommandPartParitySweep.BurstControlIds, controlId) >= 0) return KindBurst;
            if (Array.IndexOf(TestCommandPartParitySweep.DustControlIds, controlId) >= 0) return KindDust;
            return null;
        }

        /// <summary>The load and unpack range a dust wave widens its vessels to, metres: the
        /// ocean slot can sit kilometres east of the pad, beyond stock's 2.5 km unpack range.</summary>
        internal const double DustLoadRangeM = 15000.0;

        /// <summary>How many cases one wave of the kind takes.</summary>
        internal static int MaxWaveCases(string kind) => kind == KindDust ? MaxDustWaveCases : 1;

        /// <summary>The time box (design 5.2): the first wave of a step always runs, a later one
        /// only when its worst case still fits.</summary>
        internal static bool AdmitWave(string kind, double elapsedSeconds, int maxSeconds, bool firstWave)
        {
            if (firstWave) return true;
            double worst = kind == KindDust ? DustWaveWorstSeconds : BurstWaveWorstSeconds;
            return PartParitySweepPlan.ShouldStartStateWave(elapsedSeconds, maxSeconds, worst);
        }

        /// <summary>Whether an enumeration exclusion belongs to a run: a P9 family the run's
        /// words select, in the run's context.</summary>
        internal static bool ExclusionApplies(PartParityFxExclusion e, IList<string> fxWords, string runContext)
        {
            if (e == null || fxWords == null || !PartParityFxRules.IsBuiltFamily(e.Family)) return false;
            if (!string.Equals(e.Context, runContext, StringComparison.Ordinal)) return false;
            foreach (string w in fxWords)
                if (string.Equals(PartParityFxRules.FamilyForArg(w), e.Family, StringComparison.Ordinal)) return true;
            return false;
        }

        // ----- the burst slots (G.2.1) -----

        /// <summary>Settle time of the held trial vessels before the trigger.</summary>
        internal const double BurstSettleSeconds = 1.5;

        /// <summary>The ortho half-height of the near views and of the far view, metres; the
        /// camera stands this far from its source.</summary>
        internal const double BurstNearOrthoM = 15.0;
        internal const double BurstFarOrthoM = 45.0;
        internal const double BurstCameraStandM = 100.0;

        /// <summary>The largest burst capture tile (the 384 accumulators of a case at 128 px are
        /// about 44 MB; the atlas 6144 x 512).</summary>
        internal const int BurstMaxTilePx = 128;

        /// <summary>A trial's real slot, metres north of the band centre: four slots
        /// <see cref="PartParityFxRules.BurstTrialSpacingM"/> apart, centred.</summary>
        internal static double TrialNorthOffsetM(int trial)
            => (trial - (PartParityFxRules.BurstTrials - 1) / 2.0) * PartParityFxRules.BurstTrialSpacingM;

        /// <summary>The burst tile: the run's tile, at most <see cref="BurstMaxTilePx"/>.</summary>
        internal static int BurstTilePx(int runTile) => Math.Max(16, Math.Min(runTile, BurstMaxTilePx));

        /// <summary>The ortho half-height of a burst view.</summary>
        internal static double BurstOrthoM(string view)
            => view == PartParityFxRules.ViewBurstFar ? BurstFarOrthoM : BurstNearOrthoM;

        /// <summary>A length in metres in a view's pixels; null for a senseless input.</summary>
        internal static double? MetresToPx(double metres, double orthoM, int tile)
            => double.IsNaN(metres) || metres <= 0.0 || orthoM <= 0.0 || tile <= 0 ? (double?)null : metres * tile / (2.0 * orthoM);

        /// <summary>The trial a capture frame samples (one trial per frame, round robin).</summary>
        internal static int TrialOfFrame(int frame) => ((frame % PartParityFxRules.BurstTrials) + PartParityFxRules.BurstTrials) % PartParityFxRules.BurstTrials;

        /// <summary>A new effect's tag radius: <see cref="PartParityFxRules.BurstTagRadiusM"/> plus
        /// half the source's extent (a stack's blasts merge anywhere along it).</summary>
        internal static double TagRadiusM(double sourceExtentM)
            => PartParityFxRules.BurstTagRadiusM + (double.IsNaN(sourceExtentM) || sourceExtentM < 0.0 ? 0.0 : 0.5 * sourceExtentM);

        /// <summary>The index of the source nearest <paramref name="p"/> within
        /// <paramref name="radiusM"/>, or -1 (a stray effect).</summary>
        internal static int NearestSource(IList<double[]> sources, double[] p, double radiusM)
        {
            if (sources == null || p == null || p.Length < 3) return -1;
            int best = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < sources.Count; i++)
            {
                double[] s = sources[i];
                if (s == null || s.Length < 3) continue;
                double dx = s[0] - p[0], dy = s[1] - p[1], dz = s[2] - p[2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d <= radiusM && d < bestD) { best = i; bestD = d; }
            }
            return best;
        }

        /// <summary>A point relative to a source in the slot frame: (east, up, north) metres,
        /// the order <c>offsetM</c> is written in.</summary>
        internal static double[] SlotFrameOffset(double[] east, double[] up, double[] north, double[] source, double[] point)
        {
            double dx = point[0] - source[0], dy = point[1] - source[1], dz = point[2] - source[2];
            return new[] { Dot(east, dx, dy, dz), Dot(up, dx, dy, dz), Dot(north, dx, dy, dz) };
        }

        private static double Dot(double[] a, double x, double y, double z) => a[0] * x + a[1] * y + a[2] * z;

        /// <summary>An effect object's structure key: its name with Unity's <c>(Clone)</c> suffixes cut.</summary>
        internal static string StripClone(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string n = name;
            while (n.EndsWith("(Clone)", StringComparison.Ordinal)) n = n.Substring(0, n.Length - "(Clone)".Length).TrimEnd();
            return n;
        }

        /// <summary>The burst structure diff: names the real side drew that the ghost did not
        /// (missing) and the reverse (extra), ordinal order, every entry rendered.</summary>
        internal static void DiffStructure(ICollection<string> real, ICollection<string> ghost,
            List<PartParityFxStructureEntry> missing, List<PartParityFxStructureEntry> extra)
        {
            var r = new SortedSet<string>(real ?? new string[0], StringComparer.Ordinal);
            var g = new SortedSet<string>(ghost ?? new string[0], StringComparer.Ordinal);
            foreach (string n in r) if (!g.Contains(n)) missing.Add(new PartParityFxStructureEntry { Key = n, Rendered = true });
            foreach (string n in g) if (!r.Contains(n)) extra.Add(new PartParityFxStructureEntry { Key = n, Rendered = true });
        }

        // ----- the controls' variations (G.4) -----

        internal static bool IsRealVsReal(string control)
            => control == TestCommandPartParitySweep.ControlBurstRealVsReal || control == TestCommandPartParitySweep.ControlDustRealVsReal;

        /// <summary><c>ctl-burst-real-vs-real</c>: trials 1 and 3 (0, 2) are the real side, 2 and 4
        /// the ghost side; each renumbered within its side.</summary>
        internal static void RealVsRealTrial(int trial, out bool realSide, out int number)
        {
            realSide = trial % 2 == 0;
            number = trial / 2;
        }

        internal static bool TriggersNothing(string control) => control == TestCommandPartParitySweep.ControlBurstEmpty;

        internal static bool DropsGhostEvent(string control)
            => control == TestCommandPartParitySweep.ControlBurstOff || IsRealVsReal(control) || TriggersNothing(control);

        internal static double GhostTriggerDelaySeconds(string control)
            => control == TestCommandPartParitySweep.ControlBurstLate ? PartParityFxRules.BurstLateControlSeconds : 0.0;

        internal static double GhostTriggerShiftM(string control)
            => control == TestCommandPartParitySweep.ControlBurstShift ? PartParityFxRules.BurstShiftControlM : 0.0;

        internal static bool RotatesGhostHue(string control) => control == TestCommandPartParitySweep.ControlBurstHue;

        internal static bool LowersGhostPower(string control) => control == TestCommandPartParitySweep.ControlBurstPower;

        /// <summary><c>ctl-burst-power</c>: the power of the prefab one index below the one
        /// <paramref name="power"/> draws (the middle of that index's band; index 0 stays 0).</summary>
        internal static double LowerIndexPower(double power, int prefabCount)
        {
            int i = PartParityFxRules.FxMongerPrefabIndex(power, prefabCount);
            if (i < 0 || prefabCount <= 1) return double.IsNaN(power) ? 0.0 : power;
            int j = Math.Max(0, i - PartParityFxRules.BurstPowerControlIndexStep);
            double p = (j + 0.5) / (prefabCount - 1);
            return p > 1.0 ? 1.0 : p;
        }

        /// <summary><c>ctl-dust-off</c>: the ghost's dust is hidden.</summary>
        internal static bool HidesGhostDust(string control) => control == TestCommandPartParitySweep.ControlDustOff;

        // ----- setup statuses (G.2) -----

        /// <summary>The families whose real side is an FXMonger blast (a real schedule is owed).</summary>
        internal static bool IsFxMongerFamily(string family)
            => family == PartParityFxRules.FamilyExplode || family == PartParityFxRules.FamilyVesselExplode
               || family == PartParityFxRules.FamilyBurstCustom;

        /// <summary>
        /// A captured burst case's status: <c>ok</c>, or <c>burst-not-triggered</c> when its stock
        /// side was meant to draw (every state but <c>joint-break</c>, every control but
        /// <c>ctl-burst-empty</c>) and drew nothing: no trigger, no real envelope, nothing
        /// tagged, or, for an FXMonger family, no real blast in the schedule.
        /// </summary>
        internal static string BurstSetupStatus(string control, string state, string family, bool triggered,
            bool realEnvelope, int realTagged, int realBlasts)
        {
            if (TriggersNothing(control)) return TestCommandPartParitySweep.SetupOk;
            if (state == PartParityFxRules.StateJointBreak)
                return triggered ? TestCommandPartParitySweep.SetupOk : TestCommandPartParitySweep.SetupBurstNotTriggered;
            bool drew = triggered && realEnvelope && realTagged > 0 && (!IsFxMongerFamily(family) || realBlasts > 0);
            return drew ? TestCommandPartParitySweep.SetupOk : TestCommandPartParitySweep.SetupBurstNotTriggered;
        }

        /// <summary>A captured dust case's status: <c>dust-no-stock-hit</c> when stock's raycast
        /// never hit (above-reach's pass condition; its judge accepts it), else <c>ok</c>.</summary>
        internal static string DustSetupStatus(int hits)
            => hits <= 0 ? TestCommandPartParitySweep.SetupDustNoStockHit : TestCommandPartParitySweep.SetupOk;

        // ----- the ground context (G.2.2) -----

        /// <summary>Dust slots of one wave, metres apart along the band (a dust cloud spreads up
        /// to the reach; 150 m keeps a neighbour's cloud out of the frame).</summary>
        internal const double DustSlotSpacingM = 150.0;

        /// <summary>The dust slot <paramref name="index"/> of <paramref name="count"/>, metres north of the band centre.</summary>
        internal static double DustSlotNorthOffsetM(int index, int count)
        {
            int n = Math.Max(1, count);
            return (index - (n - 1) / 2.0) * DustSlotSpacingM;
        }

        /// <summary>The root altitude that puts the thrust transform <paramref name="heightM"/>
        /// above the ground: the nozzle stands <paramref name="nozzleUpM"/> above the root.</summary>
        internal static double DustRootAltitudeM(double groundAltitudeM, double heightM, double nozzleUpM)
            => groundAltitudeM + heightM - nozzleUpM;

        internal const double WaterSearchStartM = 300.0;
        internal const double WaterSearchEndM = 12000.0;
        internal const double WaterSearchStepM = 100.0;

        /// <summary>The terrain under a water slot must read below this (metres): the sea floor, not a beach.</summary>
        internal const double WaterDepthBelowM = -10.0;

        /// <summary>A water slot sits this far past the first deep point, off the shore.</summary>
        internal const double WaterInsetM = 300.0;

        /// <summary>The water slot east of the pad: the first point along the pad's latitude,
        /// <see cref="WaterSearchStartM"/> to <see cref="WaterSearchEndM"/> in
        /// <see cref="WaterSearchStepM"/> steps, whose terrain altitude reads below
        /// <see cref="WaterDepthBelowM"/>, plus <see cref="WaterInsetM"/>; NaN when none.</summary>
        internal static double FindWaterEastM(Func<double, double> terrainAltitudeAtEastM)
        {
            if (terrainAltitudeAtEastM == null) return double.NaN;
            for (double d = WaterSearchStartM; d <= WaterSearchEndM; d += WaterSearchStepM)
            {
                double alt = terrainAltitudeAtEastM(d);
                if (!double.IsNaN(alt) && alt < WaterDepthBelowM) return d + WaterInsetM;
            }
            return double.NaN;
        }

        /// <summary>Stock's <c>SurfaceType</c> (decompiled: None 0, Terrain 1, Water 2, Launchpad 3) as a dust surface.</summary>
        internal static string SurfaceName(int surfaceType)
        {
            switch (surfaceType)
            {
                case 1: return PartParityFxRules.DustSurfaceTerrain;
                case 2: return PartParityFxRules.DustSurfaceWater;
                case 3: return PartParityFxRules.DustSurfaceLaunchpad;
                default: return PartParityFxRules.DustSurfaceNone;
            }
        }

        /// <summary>
        /// Stock's raycast over the samples: hits and misses, the most frequent hit surface (ties
        /// in <see cref="PartParityFxRules.DustSurfaces"/> order; <c>none</c> with no hit), and the
        /// mean distance, scale, point and normal over the hits.
        /// </summary>
        internal static PartParityDustRaycastSummary SummarizeRaycast(IList<PartParityDustRaycastSample> samples)
        {
            var s = new PartParityDustRaycastSummary();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            double dist = 0.0, scale = 0.0;
            int distN = 0, scaleN = 0, pointN = 0, normalN = 0;
            var point = new double[3];
            var normal = new double[3];
            if (samples != null)
                foreach (PartParityDustRaycastSample x in samples)
                {
                    if (x == null) continue;
                    s.Samples.Add(x);
                    if (!x.Hit) { s.Misses++; continue; }
                    s.Hits++;
                    counts.TryGetValue(x.Surface ?? PartParityFxRules.DustSurfaceNone, out int c);
                    counts[x.Surface ?? PartParityFxRules.DustSurfaceNone] = c + 1;
                    if (!double.IsNaN(x.DistanceM)) { dist += x.DistanceM; distN++; }
                    if (!double.IsNaN(x.ScaledFx)) { scale += x.ScaledFx; scaleN++; }
                    if (x.PointM != null && x.PointM.Length >= 3) { for (int i = 0; i < 3; i++) point[i] += x.PointM[i]; pointN++; }
                    if (x.Normal != null && x.Normal.Length >= 3) { for (int i = 0; i < 3; i++) normal[i] += x.Normal[i]; normalN++; }
                }
            if (s.Hits == 0) return s;
            int best = -1;
            foreach (string surface in PartParityFxRules.DustSurfaces)
                if (surface != PartParityFxRules.DustSurfaceNone && counts.TryGetValue(surface, out int n) && n > best)
                {
                    best = n;
                    s.Surface = surface;
                }
            if (best < 0) s.Surface = PartParityFxRules.DustSurfaceTerrain;
            if (distN > 0) s.DistanceM = dist / distN;
            if (scaleN > 0) s.ScaledFx = scale / scaleN;
            if (pointN > 0) s.PointM = new[] { point[0] / pointN, point[1] / pointN, point[2] / pointN };
            if (normalN > 0) s.Normal = new[] { normal[0] / normalN, normal[1] / normalN, normal[2] / normalN };
            return s;
        }

        /// <summary>The dust case's capture rounds: 16, or 32 for real-vs-real (its two sides take
        /// alternate rounds of one real part).</summary>
        internal static int DustSampleRounds(string control) => IsRealVsReal(control) ? 32 : 16;

        // ----- the dust options -----

        /// <summary>The P8 plume option a dust case presses with (the engine behind its
        /// <c>ModuleSurfaceFX</c> at full throttle): the dust option's clone with the plume
        /// family, the engine as its module and the full-burn state.</summary>
        internal static PartParityFxOption PlumeOptionForDust(PartParityFxOption dust)
        {
            if (dust == null) return null;
            PartParityFxOption o = dust.InContext(dust.Context);
            o.Family = PartParityFxRules.FamilyPlume;
            o.State = PartParityFxRules.StateBurnFull;
            o.Module = dust.EngineModule;
            o.ModuleIndex = dust.ThrustProviderModuleIndex;
            return o;
        }

        /// <summary><c>ctl-dust-above-reach</c>'s option: the subject's terrain slot at
        /// <see cref="PartParityFxRules.DustAboveReachFraction"/> of its reach.</summary>
        internal static PartParityFxOption AboveReachOption(PartParityFxOption dustLow)
        {
            if (dustLow == null) return null;
            PartParityFxOption o = dustLow.InContext(dustLow.Context);
            o.SlotFraction = PartParityFxRules.DustAboveReachFraction;
            o.SlotHeightM = PartParityFxRules.DustSlotHeightM(o.SlotFraction, o.SurfaceFxMaxDistanceM);
            return o;
        }

        // ----- part facts (G.2) -----

        /// <summary>Reads a part config's <c>explosionPotential</c> (stock's default when absent or
        /// unreadable) and the FX group of each <c>fx_*</c> key, in config order.</summary>
        internal static void ReadPartConfig(IEnumerable<KeyValuePair<string, string>> values, PartParityBurstPart into)
        {
            into.ExplosionPotential = PartParityFxRules.DefaultExplosionPotential;
            if (values == null) return;
            foreach (KeyValuePair<string, string> kv in values)
            {
                if (kv.Key == null) continue;
                if (kv.Key == PartParityFxRules.KeyExplosionPotential)
                {
                    if (double.TryParse(kv.Value, NumberStyles.Float, Ic, out double p)) into.ExplosionPotential = p;
                }
                else if (kv.Key.StartsWith(PartParityFxRules.FxKeyPrefix, StringComparison.Ordinal))
                {
                    string g = PartParityFxRules.FxGroupOfValue(kv.Value);
                    if (g != null && !into.FxGroups.Contains(g)) into.FxGroups.Add(g);
                }
            }
        }

        // ----- control evidence (G.4), written as one flag -----

        internal const string BurstEvidencePrefix = "burst-evidence:";
        internal const string DustEvidencePrefix = "dust-evidence:";

        internal static string FormatBurstEvidence(PartParityBurstControlEvidence e)
        {
            if (e == null) return null;
            var sb = new StringBuilder(BurstEvidencePrefix);
            sb.Append("allZero=").Append(Bit(e.AllCoverageZero))
                .Append(";real=").Append(e.RealBlasts.ToString(Ic))
                .Append(";ghost=").Append(e.GhostBlasts.ToString(Ic))
                .Append(";indexDiffers=").Append(Bit(e.ScheduleIndexDiffers))
                .Append(";failing=").Append(Ordered(PartParityFxRules.BurstMeasures, e.FailingMeasures));
            return sb.ToString();
        }

        internal static PartParityBurstControlEvidence ParseBurstEvidence(IList<string> flags, string setupStatus)
        {
            Dictionary<string, string> kv = FindFlag(flags, BurstEvidencePrefix);
            if (kv == null) return null;
            var e = new PartParityBurstControlEvidence { SetupStatus = setupStatus ?? TestCommandPartParitySweep.SetupOk };
            e.AllCoverageZero = Get(kv, "allZero") == "1";
            e.RealBlasts = Int(Get(kv, "real"));
            e.GhostBlasts = Int(Get(kv, "ghost"));
            e.ScheduleIndexDiffers = Get(kv, "indexDiffers") == "1";
            AddAll(e.FailingMeasures, Get(kv, "failing"));
            return e;
        }

        internal static string FormatDustEvidence(PartParityDustControlEvidence e)
        {
            if (e == null) return null;
            var sb = new StringBuilder(DustEvidencePrefix);
            sb.Append("hit=").Append(Bit(e.StockRaycastHit))
                .Append(";realDust=").Append(Bit(e.RealDustCovered))
                .Append(";ghostDust=").Append(Bit(e.GhostDustCovered))
                .Append(";dustFailing=").Append(Ordered(PartParityFxRules.DustMeasures, e.FailingDustMeasures))
                .Append(";flameFailing=").Append(Ordered(PartParityFxRules.Measures, e.FailingFlameMeasures));
            return sb.ToString();
        }

        internal static PartParityDustControlEvidence ParseDustEvidence(IList<string> flags, string setupStatus)
        {
            Dictionary<string, string> kv = FindFlag(flags, DustEvidencePrefix);
            if (kv == null) return null;
            var e = new PartParityDustControlEvidence { SetupStatus = setupStatus ?? TestCommandPartParitySweep.SetupOk };
            e.StockRaycastHit = Get(kv, "hit") == "1";
            e.RealDustCovered = Get(kv, "realDust") == "1";
            e.GhostDustCovered = Get(kv, "ghostDust") == "1";
            AddAll(e.FailingDustMeasures, Get(kv, "dustFailing"));
            AddAll(e.FailingFlameMeasures, Get(kv, "flameFailing"));
            return e;
        }

        /// <summary>Whether a control is a burst or dust control.</summary>
        internal static bool IsBuiltControl(string id)
            => Array.IndexOf(TestCommandPartParitySweep.BurstControlIds, id) >= 0
               || Array.IndexOf(TestCommandPartParitySweep.DustControlIds, id) >= 0;

        /// <summary>A burst or dust control's outcome from its sidecar's flags and setup status
        /// (the evidence flag, else the setup fault alone), through X1's judges.</summary>
        internal static string JudgeControl(string id, IList<string> flags, string setupStatus, out string detail)
        {
            if (Array.IndexOf(TestCommandPartParitySweep.BurstControlIds, id) >= 0)
            {
                PartParityBurstControlEvidence e = ParseBurstEvidence(flags, setupStatus);
                if (e == null && TestCommandPartParitySweep.IsSetupFault(setupStatus))
                    e = new PartParityBurstControlEvidence { SetupStatus = setupStatus };
                return PartParityFxRules.JudgeBurstControl(id, e, out detail);
            }
            PartParityDustControlEvidence d = ParseDustEvidence(flags, setupStatus);
            if (d == null && TestCommandPartParitySweep.IsSetupFault(setupStatus))
                d = new PartParityDustControlEvidence { SetupStatus = setupStatus };
            return PartParityFxRules.JudgeDustControl(id, d, out detail);
        }

        private static Dictionary<string, string> FindFlag(IList<string> flags, string prefix)
        {
            if (flags == null) return null;
            foreach (string f in flags)
            {
                if (f == null || !f.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var kv = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string pair in f.Substring(prefix.Length).Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) kv[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                }
                return kv;
            }
            return null;
        }

        private static string Get(Dictionary<string, string> kv, string key) => kv.TryGetValue(key, out string v) ? v : null;

        private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, Ic, out int v) ? v : 0;

        private static void AddAll(HashSet<string> into, string csv)
        {
            if (string.IsNullOrEmpty(csv)) return;
            foreach (string m in csv.Split(',')) if (m.Length > 0) into.Add(m);
        }

        private static string Ordered(string[] order, HashSet<string> set)
        {
            var list = new List<string>();
            foreach (string m in order) if (set.Contains(m)) list.Add(m);
            foreach (string m in new SortedSet<string>(set, StringComparer.Ordinal)) if (Array.IndexOf(order, m) < 0) list.Add(m);
            return string.Join(",", list.ToArray());
        }

        private static string Bit(bool b) => b ? "1" : "0";
    }
}
