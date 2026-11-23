using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What the settle rule decides for one spawned case on one frame (design 4.1).</summary>
    internal enum PartParitySettle
    {
        /// <summary>Keep waiting.</summary>
        Wait,

        /// <summary>No real-part Animation plays and the minimum settle has passed.</summary>
        Settled,

        /// <summary>The settle cap passed with an Animation still playing: capture anyway, tag it.</summary>
        StillAnimating,
    }

    /// <summary>
    /// The pure decisions of the sweep runner (design-part-ghost-parity.md 4.1, 4.5, 5.1):
    /// wave slots, the settle rule, the control cases with their required outcomes and the
    /// judge that reads them, and the manifest's per-case summary.
    /// </summary>
    internal static class PartParitySweepPlan
    {
        // ----- waves (design 4.1) -----

        /// <summary>Slot positions on ONE side of the host, metres along-track, inner first.
        /// Stock orbit vessel ranges unpack at 200 m and pack at 350 m, so every slot sits
        /// inside 180 m.</summary>
        internal static readonly double[] SlotPositionsM = { 60.0, 100.0, 140.0, 180.0 };

        /// <summary>The most cases one wave holds (both sides, one slot each).</summary>
        internal const int MaxWaveCases = 8;

        /// <summary>A part whose largest dimension exceeds this takes two adjacent slots
        /// (and sits at their midpoint), so its neighbours stay clear.</summary>
        internal const double LargePartMeters = 12.0;

        /// <summary>
        /// Along-track offsets for the next cases, in order: slots alternate both signs
        /// (+60, -60, +100, -100, ...), a large part takes two adjacent slots on one side.
        /// The plan stops at the first case that no side has room for, so the returned list
        /// is a prefix of the input (its count is how many cases the wave holds; at least
        /// one when any case is given: a part too large for two slots still flies alone).
        /// </summary>
        internal static List<double> PlanWave(IList<double> partSizesMeters, int maxCases = MaxWaveCases)
        {
            var offsets = new List<double>();
            if (partSizesMeters == null) return offsets;
            int[] used = { 0, 0 }; // slots consumed per side: 0 = plus, 1 = minus
            int side = 0;
            for (int i = 0; i < partSizesMeters.Count && offsets.Count < maxCases; i++)
            {
                int need = partSizesMeters[i] > LargePartMeters ? 2 : 1;
                int chosen = -1;
                for (int s = 0; s < 2 && chosen < 0; s++)
                {
                    int candidate = (side + s) % 2;
                    if (used[candidate] + need <= SlotPositionsM.Length) chosen = candidate;
                }
                if (chosen < 0) break;
                double pos = 0.0;
                for (int k = 0; k < need; k++) pos += SlotPositionsM[used[chosen] + k];
                pos /= need;
                used[chosen] += need;
                offsets.Add(chosen == 0 ? pos : -pos);
                side = 1 - chosen;
            }
            return offsets;
        }

        /// <summary>
        /// How many queued cases, from the cursor, the next wave may hold before slot
        /// planning (design 5.3): a case owed a solo run flies in a wave of its own, so the
        /// next crash names it alone; otherwise the leading run of cases NOT owed one, up to
        /// <paramref name="maxCases"/> (a solo case never joins a normal wave).
        /// <paramref name="soloFromCursor"/> holds one flag per queued case from the cursor on.
        /// </summary>
        internal static int WaveCandidateCount(IList<bool> soloFromCursor, int maxCases = MaxWaveCases)
        {
            if (soloFromCursor == null || soloFromCursor.Count == 0 || maxCases < 1) return 0;
            if (soloFromCursor[0]) return 1;
            int n = 0;
            while (n < soloFromCursor.Count && n < maxCases && !soloFromCursor[n]) n++;
            return n;
        }

        /// <summary>What a step's cleanup released, as <c>key=value</c> tokens (the abandoned
        /// step's Warn line and the normal step's Verbose line carry it).</summary>
        internal static string FormatCleanup(bool observerRestored, int casesDestroyed, bool captureReleased, int faults)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "observerRestored=" + (observerRestored ? "true" : "false")
                + " casesDestroyed=" + casesDestroyed.ToString(ic)
                + " captureReleased=" + (captureReleased ? "true" : "false")
                + " faults=" + faults.ToString(ic);
        }

        // ----- settle (design 4.1) -----

        internal const double SpawnTimeoutSeconds = 20.0;
        internal const double MinSettleSeconds = 0.5;
        internal const double MaxSettleSeconds = 5.0;
        internal const double VesselGoneTimeoutSeconds = 5.0;

        /// <summary>The settle rule for one case, <paramref name="sinceReadySeconds"/> after it
        /// first read <c>loaded &amp;&amp; !packed</c>.</summary>
        internal static PartParitySettle DecideSettle(double sinceReadySeconds, bool anyAnimationPlaying)
        {
            if (sinceReadySeconds < MinSettleSeconds) return PartParitySettle.Wait;
            if (!anyAnimationPlaying) return PartParitySettle.Settled;
            return sinceReadySeconds >= MaxSettleSeconds ? PartParitySettle.StillAnimating : PartParitySettle.Wait;
        }

        /// <summary>Every K waves the runner unloads unused assets and logs memory.</summary>
        internal const int UnloadEveryWaves = 10;

        internal static bool ShouldUnloadAfterWave(int wavesDone)
            => wavesDone > 0 && wavesDone % UnloadEveryWaves == 0;

        // ----- state waves (design D.3) -----

        /// <summary>The most cases an orbit state wave holds: a deployed part reaches past its
        /// stowed bounds, so a state wave uses every other slot.</summary>
        internal const int MaxStateWaveCases = 4;

        /// <summary>The most chutes an air wave holds (one state, 40 m apart along the band, D.4).</summary>
        internal const int MaxAirWaveCases = 5;

        /// <summary>The capture of one state case: four rows x four views x two clears.</summary>
        internal const double StateCaptureSecondsPerCase = 0.3;

        /// <summary>A wave's teardown, debris destroy included.</summary>
        internal const double StateTeardownSeconds = 5.0;

        /// <summary>
        /// The worst-case wall time of one state wave (design D.3), the bound a wave is admitted
        /// against: the spawn cap, the bucket's state wait, the longest settle cap of the wave's
        /// families (<see cref="PartParityStateRules.SettleCapSeconds"/>: a cabin light or an
        /// airbrake or a light 5 s, else 0.5 s), the slowest tracker's cap when the wave holds
        /// a sun tracker (<paramref name="trackerCapSeconds"/>, 0 for none: 25 s for a solar
        /// panel, 62.5 s for a folding radiator,
        /// <see cref="PartParityStateRules.SunTrackerCapSecondsFor"/>), the capture of every case
        /// and the teardown. Long: <c>20 + 20 + 0.5 (+ tracker cap) + 0.3 n + 5</c>; quick:
        /// <c>20 + 5 + settle + 0.3 n + 5</c>;
        /// air: the spawn cap, the 0.5 s settle, semi (10 s) and full (15 s) each followed by the
        /// 4 s canopy window, then the capture and the teardown (60 s for a wave of five).
        /// </summary>
        internal static double WaveWorstCaseSeconds(PartParityStateBucket bucket, int cases, double trackerCapSeconds,
            IEnumerable<string> families = null)
        {
            double capture = StateCaptureSecondsPerCase * Math.Max(0, cases);
            double settle = MinSettleSeconds;
            if (families != null)
                foreach (string f in families) settle = Math.Max(settle, PartParityStateRules.SettleCapSeconds(f));
            switch (bucket)
            {
                case PartParityStateBucket.Quick:
                    return SpawnTimeoutSeconds + PartParityStateRules.QuickStateTimeoutSeconds + settle
                        + capture + StateTeardownSeconds;
                case PartParityStateBucket.Air:
                    return SpawnTimeoutSeconds + MinSettleSeconds
                        + PartParityStateRules.ChuteSemiTimeoutSeconds + PartParityStateRules.CanopyCapSeconds
                        + PartParityStateRules.ChuteFullTimeoutSeconds + PartParityStateRules.CanopyCapSeconds
                        + capture + StateTeardownSeconds;
                default:
                    return SpawnTimeoutSeconds + PartParityStateRules.LongStateTimeoutSeconds + settle
                        + (double.IsNaN(trackerCapSeconds) || trackerCapSeconds < 0.0 ? 0.0 : trackerCapSeconds)
                        + capture + StateTeardownSeconds;
            }
        }

        /// <summary>Whether a step may start a state wave (design D.3): only when the time spent
        /// so far plus the wave's own worst case stays inside the box (never the last wave's
        /// wall). The first wave of a step always starts.</summary>
        internal static bool ShouldStartStateWave(double elapsedSeconds, int maxSeconds, double worstCaseSeconds)
        {
            if (elapsedSeconds <= 0.0) return true;
            double bound = double.IsNaN(worstCaseSeconds) || worstCaseSeconds < 0.0 ? 0.0 : worstCaseSeconds;
            return elapsedSeconds + bound <= maxSeconds;
        }

        // ----- controls (design 4.5) -----

        /// <summary>The part the four tank controls use.</summary>
        internal const string ControlTankPart = "fuelTankSmallFlat";

        /// <summary>The asymmetric part of the rotation controls (hatch and ladder).</summary>
        internal const string ControlPodPart = "mk1pod.v2";

        /// <summary>The orbit state control's subject, the Communotron 16 (design D.6).</summary>
        internal const string ControlStatePart = "longAntenna";

        /// <summary>The air controls' subject (design D.2, D.6).</summary>
        internal const string ControlAirPart = "parachuteSingle";

        /// <summary>Candidates for <c>ctl-wrong-skin</c>, the first loaded part with a non-base
        /// colour variant wins (<c>FairingGhostVisualMath.ChooseColourVariant</c>).</summary>
        internal static readonly string[] WrongSkinCandidates =
            { "fuelTank", "fuelTankSmallFlat", "fuelTankSmall", "mk1pod.v2", "probeCoreOcto.v2" };

        /// <summary>The planted cube of <c>ctl-ghost-missing-mesh</c> (design 4.5): its face
        /// covers about this fraction of the real box's projected area in <c>side-a</c>...</summary>
        internal const double PlantedCubeAreaFraction = 0.003;

        /// <summary>... and is at least this many pixels across at the capture's ortho scale.</summary>
        internal const int PlantedCubeMinPx = 12;

        /// <summary>The planted cube's GameObject name (its path in the structure diff).</summary>
        internal const string PlantedCubeName = "PartParityPlantedCube";

        /// <summary>Starting guesses mirrored from the design's Python verdicts (6), used ONLY to
        /// judge the controls; part verdicts are Python's.</summary>
        internal const int ShapeBlobFailPx = 64;
        internal const double ShapeBlobFailPct = 0.2;
        internal const int ColorBlobFailPx = 256;
        internal const double MissingMeshMinIou = 0.99;
        internal const double WrongSkinMinIou = 0.98;

        /// <summary><c>ctl-real-vs-real</c> renders the real part twice; the masks must match
        /// exactly, but up to this many overlap pixels per view may differ in colour by more than
        /// the colour threshold (render noise: 1 of the 216 archived views read 1 px, todo
        /// PARTPARITY-REAL-VS-REAL-ONE-PIXEL), an eighth of the 64 px colour warn line.</summary>
        internal const int RealVsRealColorNoisePx = 8;

        /// <summary>The control's subject (part) and its required outcome, in words.</summary>
        internal static string ControlSubject(string controlId) => ControlSubject(controlId, false);

        /// <summary>As above for an orbit or an air run: the air run's real-vs-real control
        /// photographs an armed, falling <see cref="ControlAirPart"/>.</summary>
        internal static string ControlSubject(string controlId, bool airRun)
        {
            if (airRun && controlId == TestCommandPartParitySweep.ControlRealVsReal)
                return ControlAirPart + " armed and falling, real captured as both sides";
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlStateEventsDropped: return ControlStatePart + " at deployed, event-route ghost built without the polled events";
                case TestCommandPartParitySweep.ControlStateEventsDroppedAir: return ControlAirPart + " at semi in the air, event-route ghost built without the polled events";
                case TestCommandPartParitySweep.ControlEmpty: return ControlTankPart + ", both sides hidden";
                case TestCommandPartParitySweep.ControlGhostAbsent: return ControlTankPart + ", ghost not built";
                case TestCommandPartParitySweep.ControlRealVsReal: return ControlTankPart + ", real captured as both sides";
                case TestCommandPartParitySweep.ControlGhostScale90: return ControlTankPart + ", ghost scaled 0.9";
                case TestCommandPartParitySweep.ControlGhostRot90: return ControlPodPart + ", ghost rotated 90 deg about +Y";
                case TestCommandPartParitySweep.ControlGhostFlip: return ControlPodPart + ", ghost rotated 180 deg about +X";
                case TestCommandPartParitySweep.ControlGhostMissingMesh: return ControlTankPart + ", a small cube planted on the real part's +X face that the ghost lacks";
                case TestCommandPartParitySweep.ControlWrongSkin: return "colour-variant part, ghost built from a snapshot naming another variant";
                default: return null;
            }
        }

        internal static string ControlRequired(string controlId)
        {
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlEmpty: return "zero coverage in every view";
                case TestCommandPartParitySweep.ControlGhostAbsent: return "ghost side zero coverage, real side covered";
                case TestCommandPartParitySweep.ControlRealVsReal: return "identical masks, colours within " + RealVsRealColorNoisePx.ToString(CultureInfo.InvariantCulture) + " px of noise";
                case TestCommandPartParitySweep.ControlGhostScale90: return "fails every view on shape";
                case TestCommandPartParitySweep.ControlGhostRot90: return "fails side-a and side-b on shape";
                case TestCommandPartParitySweep.ControlGhostFlip: return "fails every view";
                case TestCommandPartParitySweep.ControlGhostMissingMesh: return "fails a view on local measures, iou >= 0.99 everywhere, a rendered mesh missing on the ghost";
                case TestCommandPartParitySweep.ControlWrongSkin: return "fails a view on colour, iou >= 0.98 everywhere";
                case TestCommandPartParitySweep.ControlStateEventsDropped:
                case TestCommandPartParitySweep.ControlStateEventsDroppedAir:
                    return "the press reached its state, at least one event recorded, the event-route views and stowedRef fail on shape";
                default: return null;
            }
        }

        internal static bool IsControl(string id) => Array.IndexOf(TestCommandPartParitySweep.AllControlIds, id) >= 0;

        /// <summary>
        /// Judges a state control (<c>ctl-state-events-dropped</c>, its air twin; design D.6):
        /// the press reached the state, at least one event was recorded, the event-route views
        /// (built WITHOUT those events) fail on shape in at least one view, and the stowed
        /// reference fails on shape too. A setup fault or no views is <c>bad</c>.
        /// </summary>
        internal static string JudgeStateControl(bool reached, int eventsRecorded,
            IList<PartParityViewRecord> views, string setupStatus, out string detail)
        {
            detail = null;
            if (TestCommandPartParitySweep.IsSetupFault(setupStatus))
            {
                detail = "setup " + (setupStatus ?? "<none>");
                return TestCommandPartParitySweep.ControlsBad;
            }
            if (!reached) { detail = "the press never reached the state"; return TestCommandPartParitySweep.ControlsBad; }
            if (eventsRecorded < 1) { detail = "no event recorded"; return TestCommandPartParitySweep.ControlsBad; }
            if (views == null || views.Count == 0) { detail = "no views"; return TestCommandPartParitySweep.ControlsBad; }
            if (!Any(views, ShapeFails, "the event-route views pass on shape", out detail))
                return TestCommandPartParitySweep.ControlsBad;
            bool stowedFails = false;
            foreach (var v in views)
                if (v?.StowedRef != null && ShapeFails(v.StowedRef)) { stowedFails = true; break; }
            if (!stowedFails)
            {
                detail = "stowedRef passes on shape in every view";
                return TestCommandPartParitySweep.ControlsBad;
            }
            return TestCommandPartParitySweep.ControlsOk;
        }

        internal static bool ShapeFails(PartParityViewMeasures m)
        {
            if (m == null) return false;
            int blob = Math.Max(m.RealOnlyBlobPx, m.GhostOnlyBlobPx);
            double pct = Math.Max(m.RealOnlyBlobPct ?? 0.0, m.GhostOnlyBlobPct ?? 0.0);
            return blob > ShapeBlobFailPx || pct > ShapeBlobFailPct;
        }

        internal static bool ColorFails(PartParityViewMeasures m) => m != null && m.ColorBlobPx > ColorBlobFailPx;

        /// <summary>
        /// Judges one control from its sidecar's views and setup status: <c>ok</c> or
        /// <c>bad</c>, with a detail naming the first view that broke the requirement. A
        /// setup fault, or a control with no views, is <c>bad</c>.
        /// </summary>
        internal static string JudgeControl(
            string controlId, IList<PartParityViewRecord> views, string setupStatus, out string detail)
            => JudgeControl(controlId, views, setupStatus, null, out detail);

        /// <summary>
        /// As above, with the case's structural diff: <c>ctl-ghost-missing-mesh</c> also needs
        /// at least one RENDERED real renderer missing on the ghost (the planted cube). A
        /// <c>still-animating</c> case is judged on its views like any other
        /// (<see cref="TestCommandPartParitySweep.IsSetupFault"/> excludes it).
        /// </summary>
        internal static string JudgeControl(
            string controlId, IList<PartParityViewRecord> views, string setupStatus,
            PartParityStructureDiff structure, out string detail)
        {
            detail = null;
            if (TestCommandPartParitySweep.IsSetupFault(setupStatus))
            {
                detail = "setup " + (setupStatus ?? "<none>");
                return TestCommandPartParitySweep.ControlsBad;
            }
            if (views == null || views.Count == 0)
            {
                detail = "no views";
                return TestCommandPartParitySweep.ControlsBad;
            }
            bool ok = JudgeViews(controlId, views, out detail);
            if (ok && controlId == TestCommandPartParitySweep.ControlGhostMissingMesh && MissingRendered(structure) < 1)
            {
                detail = "structure lists no rendered mesh missing on the ghost";
                ok = false;
            }
            return ok ? TestCommandPartParitySweep.ControlsOk : TestCommandPartParitySweep.ControlsBad;
        }

        private static bool JudgeViews(string controlId, IList<PartParityViewRecord> views, out string detail)
        {
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlEmpty:
                    return All(views, m => m.RealPx == 0 && m.GhostPx == 0, "coverage", out detail);
                case TestCommandPartParitySweep.ControlGhostAbsent:
                    return All(views, m => m.GhostPx == 0, "ghost coverage", out detail)
                        && Any(views, m => m.RealPx > 0, "no real coverage in any view", out detail);
                case TestCommandPartParitySweep.ControlRealVsReal:
                    return All(views, m => m.RealOnlyPx == 0 && m.GhostOnlyPx == 0 && m.ColorOverPx <= RealVsRealColorNoisePx, "difference", out detail)
                        && Any(views, m => m.RealPx > 0, "no coverage in any view", out detail);
                case TestCommandPartParitySweep.ControlGhostScale90:
                    return All(views, ShapeFails, "shape passes", out detail);
                case TestCommandPartParitySweep.ControlGhostRot90:
                    return Named(views, TestCommandPartParitySweep.ViewSideA, ShapeFails, out detail)
                        && Named(views, TestCommandPartParitySweep.ViewSideB, ShapeFails, out detail);
                case TestCommandPartParitySweep.ControlGhostFlip:
                    return All(views, m => ShapeFails(m) || ColorFails(m), "view passes", out detail);
                case TestCommandPartParitySweep.ControlGhostMissingMesh:
                    return Any(views, m => ShapeFails(m) || ColorFails(m), "no view fails", out detail)
                        && All(views, m => m.OrPx == 0 || m.Iou >= MissingMeshMinIou, "iou below " + MissingMeshMinIou.ToString("0.00", CultureInfo.InvariantCulture), out detail);
                case TestCommandPartParitySweep.ControlWrongSkin:
                    return Any(views, ColorFails, "no view fails on colour", out detail)
                        && All(views, m => m.OrPx == 0 || m.Iou >= WrongSkinMinIou, "iou below " + WrongSkinMinIou.ToString("0.00", CultureInfo.InvariantCulture), out detail);
                default:
                    detail = "unknown control";
                    return false;
            }
        }

        private static bool All(IList<PartParityViewRecord> views, Func<PartParityViewMeasures, bool> pred, string what, out string detail)
        {
            detail = null;
            foreach (var v in views)
            {
                if (v?.Measures != null && pred(v.Measures)) continue;
                detail = what + " in " + (v?.View ?? "<null>");
                return false;
            }
            return true;
        }

        private static bool Any(IList<PartParityViewRecord> views, Func<PartParityViewMeasures, bool> pred, string what, out string detail)
        {
            detail = null;
            foreach (var v in views)
                if (v?.Measures != null && pred(v.Measures)) return true;
            detail = what;
            return false;
        }

        private static bool Named(IList<PartParityViewRecord> views, string view, Func<PartParityViewMeasures, bool> pred, out string detail)
        {
            detail = null;
            foreach (var v in views)
            {
                if (v == null || v.View != view) continue;
                if (v.Measures != null && pred(v.Measures)) return true;
                detail = "shape passes in " + view;
                return false;
            }
            detail = view + " not captured";
            return false;
        }

        /// <summary>How many renderers the structural diff lists as missing on the ghost while
        /// rendered on the real side; 0 for a null diff.</summary>
        internal static int MissingRendered(PartParityStructureDiff structure)
        {
            int n = 0;
            if (structure?.MissingOnGhost == null) return n;
            foreach (var r in structure.MissingOnGhost)
                if (r != null && r.Rendered) n++;
            return n;
        }

        /// <summary>
        /// The planted cube's edge in metres (design 4.5), from the real box's size (part-local
        /// metres, before the cube) and the capture tile: its <c>side-a</c> face (edge squared;
        /// <c>side-a</c> looks along the part's Z axis) covers
        /// <see cref="PlantedCubeAreaFraction"/> of the box's <c>side-a</c> projection (size x
        /// times size y), and it is at least <see cref="PlantedCubeMinPx"/> pixels across at the
        /// ortho scale of the box GROWN by the cube on +X (the cube sits outside the +X face, so
        /// it widens the box it is framed in). NaN for an empty or non-finite box.
        /// </summary>
        internal static double PlantedCubeEdge(double[] realBoxSize, int tile)
        {
            if (realBoxSize == null || realBoxSize.Length != 3 || tile <= 0) return double.NaN;
            foreach (double d in realBoxSize)
                if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0.0) return double.NaN;
            double edge = Math.Sqrt(PlantedCubeAreaFraction * realBoxSize[0] * realBoxSize[1]);
            // The floor depends on the grown box; three passes settle it (the growth per pass
            // shrinks by the margin's factor).
            for (int pass = 0; pass < 3; pass++)
            {
                double ortho = PartParityCaptureMath.OrthoSize(new[]
                    { (realBoxSize[0] + edge) / 2.0, realBoxSize[1] / 2.0, realBoxSize[2] / 2.0 });
                double floor = PlantedCubeMinPx * 2.0 * ortho / tile;
                if (edge >= floor) break;
                edge = floor;
            }
            return edge;
        }

        // ----- manifest summaries -----

        /// <summary>The manifest's <c>cases[]</c> entry for one sidecar.</summary>
        internal static PartParityCaseSummary Summarize(int index, PartParityCaseSidecar c)
        {
            var s = new PartParityCaseSummary
            {
                Index = index,
                CaseId = c.CaseId,
                Part = c.Part,
                Title = c.Title,
                Source = c.Source,
                Skin = c.Skin?.Requested,
                State = c.State,
                Context = c.Context,
                Sidecar = TestCommandPartParitySweep.SidecarFileName(c.CaseId),
                SetupStatus = c.Setup?.Status,
                Family = c.StateProof?.Family,
                ActionResult = c.StateProof?.ActionResult,
                Reached = c.StateProof?.Reached,
            };
            if (c.Views != null && c.Views.Count > 0)
            {
                int shape = 0, color = 0;
                double iou = 1.0;
                foreach (var v in c.Views)
                {
                    if (v?.Measures == null) continue;
                    shape = Math.Max(shape, Math.Max(v.Measures.RealOnlyBlobPx, v.Measures.GhostOnlyBlobPx));
                    color = Math.Max(color, v.Measures.ColorBlobPx);
                    iou = Math.Min(iou, v.Measures.Iou);
                }
                s.MaxShapeBlobPx = shape;
                s.MaxColorBlobPx = color;
                s.MinIou = iou;
            }
            if (c.Structure != null)
            {
                s.MissingRendered = CountRendered(c.Structure.MissingOnGhost);
                s.ExtraRendered = CountRendered(c.Structure.ExtraOnGhost);
            }
            return s;
        }

        private static int CountRendered(List<PartParityRendererRef> refs)
        {
            int n = 0;
            // A non-visual renderer (depth mask, effect mesh) the ghost leaves out by design is
            // not counted, matching the partParity row's structure rule.
            if (refs != null) foreach (var r in refs) if (r != null && r.Rendered && r.NonVisual == null) n++;
            return n;
        }

        /// <summary>The selection position the next step resumes at: the first planned case
        /// with no complete sidecar, or the count when every one is complete.</summary>
        internal static int NextIncomplete(IList<bool> completeBySelectionPosition)
        {
            if (completeBySelectionPosition == null) return 0;
            for (int i = 0; i < completeBySelectionPosition.Count; i++)
                if (!completeBySelectionPosition[i]) return i;
            return completeBySelectionPosition.Count;
        }
    }
}
