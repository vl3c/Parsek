using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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

    /// <summary>One candidate subject of the missing-mesh control: its rendered renderer
    /// records (part-local bounds) and whether its prefab carries an Animation.</summary>
    internal sealed class PartParityMissingMeshCandidate
    {
        internal string Subject;
        internal bool HasAnimation;
        internal List<PartParityRendererRecord> Records = new List<PartParityRendererRecord>();
    }

    /// <summary>The missing-mesh control's pick: a subject, the renderer to hide
    /// (<c>RendererIndex</c> into the records it was chosen from, -1 when none), and its
    /// projected fractions per view.</summary>
    internal sealed class PartParityMissingMeshPick
    {
        internal string Subject;
        internal int RendererIndex = -1;
        internal string RendererPath;
        internal List<string> Views = new List<string>();
        internal double[] Fractions;
        internal bool Animated;
        internal string Why;

        /// <summary>The flag / log value: <c>subject=&lt;s&gt;;renderer=&lt;path&gt;;side-a=0.123%;...</c>
        /// (percent, three decimals, invariant), or the reason when nothing qualified.</summary>
        internal string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("subject=").Append(Subject ?? "<none>");
            if (RendererIndex < 0)
                return sb.Append(";none=").Append(Why ?? "<none>").ToString();
            sb.Append(";renderer=").Append(RendererPath ?? "<none>");
            sb.Append(";animated=").Append(Animated ? "true" : "false");
            for (int i = 0; i < Views.Count && Fractions != null && i < Fractions.Length; i++)
                sb.Append(';').Append(Views[i]).Append('=')
                    .Append((Fractions[i] * 100.0).ToString("0.000", CultureInfo.InvariantCulture)).Append('%');
            return sb.ToString();
        }
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

        // ----- controls (design 4.5) -----

        /// <summary>The part the four tank controls use.</summary>
        internal const string ControlTankPart = "fuelTankSmallFlat";

        /// <summary>The asymmetric part of the rotation controls (hatch and ladder).</summary>
        internal const string ControlPodPart = "mk1pod.v2";

        /// <summary>
        /// Candidate subjects for <c>ctl-ghost-missing-mesh</c>, stock parts with several
        /// meshes, in preference order (runtime dot-form names; an absent part is skipped). The
        /// subject and the renderer it hides are chosen from data by
        /// <see cref="ChooseMissingMeshSubject"/>, never by list position alone.
        /// </summary>
        internal static readonly string[] MissingMeshCandidates =
        {
            "mk1pod.v2", "Mark1Cockpit", "landerCabinSmall", "probeCoreHex.v2", "probeCoreOcto.v2",
            "RCSBlock.v2", "RCSblock.01.small", "ladder1", "telescopicLadder", "telescopicLadderBay",
            "spotLight1", "spotLight2", "spotLight3", "solarPanels5", "solarPanels4", "solarPanels3",
            "solarPanels2", "solarPanels1", "mk2LanderCabin.v2", "liquidEngine2",
        };

        /// <summary>The hidden renderer must cover at least this fraction of the shared box's
        /// projected area in at least one view (0.1 %: a blob over the 64 px shape limit at
        /// tile 512), so some view fails on a local measure.</summary>
        internal const double MissingMeshMinViewFraction = 0.001;

        /// <summary>The hidden renderer must cover at most this fraction of the shared box's
        /// projected area in every view (0.6 %), so every view keeps IoU >= 0.99.</summary>
        internal const double MissingMeshMaxViewFraction = 0.006;

        /// <summary>The sidecar flag of a missing-mesh control with no qualifying subject: the
        /// judge reports it <c>bad</c> with a <c>no-subject</c> detail rather than guessing.</summary>
        internal const string FlagControlNoSubject = "control-no-subject";

        /// <summary>The sidecar flag carrying the missing-mesh pick (subject, renderer, the
        /// per-view fractions); the control's log line repeats it.</summary>
        internal const string FlagControlPickPrefix = "control-pick:";

        /// <summary>Candidates for <c>ctl-wrong-skin</c>, the first loaded part with a non-base
        /// colour variant wins (<c>FairingGhostVisualMath.ChooseColourVariant</c>).</summary>
        internal static readonly string[] WrongSkinCandidates =
            { "fuelTank", "fuelTankSmallFlat", "fuelTankSmall", "mk1pod.v2", "probeCoreOcto.v2" };

        /// <summary>Starting guesses mirrored from the design's Python verdicts (6), used ONLY to
        /// judge the controls; part verdicts are Python's.</summary>
        internal const int ShapeBlobFailPx = 64;
        internal const double ShapeBlobFailPct = 0.2;
        internal const int ColorBlobFailPx = 64;
        internal const double MissingMeshMinIou = 0.99;
        internal const double WrongSkinMinIou = 0.98;

        /// <summary>The control's subject (part) and its required outcome, in words.</summary>
        internal static string ControlSubject(string controlId)
        {
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlEmpty: return ControlTankPart + ", both sides hidden";
                case TestCommandPartParitySweep.ControlGhostAbsent: return ControlTankPart + ", ghost not built";
                case TestCommandPartParitySweep.ControlRealVsReal: return ControlTankPart + ", real captured as both sides";
                case TestCommandPartParitySweep.ControlGhostScale90: return ControlTankPart + ", ghost scaled 0.9";
                case TestCommandPartParitySweep.ControlGhostRot90: return ControlPodPart + ", ghost rotated 90 deg about +Y";
                case TestCommandPartParitySweep.ControlGhostFlip: return ControlPodPart + ", ghost rotated 180 deg about +X";
                case TestCommandPartParitySweep.ControlGhostMissingMesh: return "multi-renderer part, one small ghost renderer forced off";
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
                case TestCommandPartParitySweep.ControlRealVsReal: return "identical masks and colours";
                case TestCommandPartParitySweep.ControlGhostScale90: return "fails every view on shape";
                case TestCommandPartParitySweep.ControlGhostRot90: return "fails side-a and side-b on shape";
                case TestCommandPartParitySweep.ControlGhostFlip: return "fails every view";
                case TestCommandPartParitySweep.ControlGhostMissingMesh: return "fails a view on local measures, iou >= 0.99 everywhere";
                case TestCommandPartParitySweep.ControlWrongSkin: return "fails a view on colour, iou >= 0.98 everywhere";
                default: return null;
            }
        }

        internal static bool IsControl(string id) => Array.IndexOf(TestCommandPartParitySweep.ControlIds, id) >= 0;

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
        /// As above, reading the sidecar flags too: a control flagged
        /// <see cref="FlagControlNoSubject"/> is <c>bad</c> with a <c>no-subject</c> detail
        /// before anything else. <c>still-animating</c> is NOT a setup fault
        /// (<see cref="TestCommandPartParitySweep.IsSetupFault"/>): it never turns a control
        /// <c>ok</c> or <c>bad</c> by itself; the views decide.
        /// </summary>
        internal static string JudgeControl(
            string controlId, IList<PartParityViewRecord> views, string setupStatus,
            IList<string> flags, out string detail)
        {
            detail = null;
            if (flags != null)
            {
                foreach (string f in flags)
                {
                    if (f == null || !f.StartsWith(FlagControlNoSubject, StringComparison.Ordinal)) continue;
                    detail = "no-subject";
                    return TestCommandPartParitySweep.ControlsBad;
                }
            }
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
                    return All(views, m => m.RealOnlyPx == 0 && m.GhostOnlyPx == 0 && m.ColorOverPx == 0, "difference", out detail)
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

        // ----- the missing-mesh pick (design 4.5) -----

        /// <summary>The camera direction of a view in PART-LOCAL axes (the part looks along
        /// minus this), the same table as <c>PartParityCapture.TryViewAxes</c>.</summary>
        internal static bool TryViewDirection(string view, out double[] dir)
        {
            switch (view)
            {
                case TestCommandPartParitySweep.ViewSideA: dir = new[] { 0.0, 0.0, -1.0 }; return true;
                case TestCommandPartParitySweep.ViewSideB: dir = new[] { 1.0, 0.0, 0.0 }; return true;
                case TestCommandPartParitySweep.ViewTop: dir = new[] { 0.0, 1.0, 0.0 }; return true;
                case TestCommandPartParitySweep.ViewBottom: dir = new[] { 0.0, -1.0, 0.0 }; return true;
                case TestCommandPartParitySweep.ViewSideC: dir = new[] { 0.0, 0.0, 1.0 }; return true;
                case TestCommandPartParitySweep.ViewSideD: dir = new[] { -1.0, 0.0, 0.0 }; return true;
                case TestCommandPartParitySweep.ViewHero:
                {
                    double n = Math.Sqrt(1.0 + 0.36 + 1.0);
                    dir = new[] { 1.0 / n, 0.6 / n, -1.0 / n };
                    return true;
                }
                default:
                    dir = null;
                    return false;
            }
        }

        /// <summary>The area of an axis-aligned box's projection along a unit direction:
        /// |dx| sy sz + |dy| sx sz + |dz| sx sy (each face pair seen at its cosine).</summary>
        internal static double ProjectedBoxArea(double[] size, double[] dir)
        {
            if (size == null || dir == null || size.Length != 3 || dir.Length != 3) return 0.0;
            return Math.Abs(dir[0]) * size[1] * size[2]
                 + Math.Abs(dir[1]) * size[0] * size[2]
                 + Math.Abs(dir[2]) * size[0] * size[1];
        }

        /// <summary>The renderer's projected bounds as a fraction of the shared box's projected
        /// area, per view (in <paramref name="views"/> order); NaN for a view with no
        /// direction or an empty shared projection, null when the renderer has no bounds.</summary>
        internal static double[] ProjectedFractions(PartParityRendererRecord r, PartParityAabb shared, IList<string> views)
        {
            if (r == null || views == null) return null;
            PartParityAabb rb = PartParityFramingRule.BoundsOf(r);
            if (rb.IsEmpty) return null;
            double[] rs = rb.Size();
            double[] ss = shared != null && !shared.IsEmpty ? shared.Size() : null;
            var f = new double[views.Count];
            for (int i = 0; i < views.Count; i++)
            {
                if (ss == null || !TryViewDirection(views[i], out double[] dir)) { f[i] = double.NaN; continue; }
                double denom = ProjectedBoxArea(ss, dir);
                f[i] = denom > 0.0 ? ProjectedBoxArea(rs, dir) / denom : double.NaN;
            }
            return f;
        }

        /// <summary>The window of <see cref="MissingMeshMinViewFraction"/> /
        /// <see cref="MissingMeshMaxViewFraction"/>: at least the floor in one view, at most
        /// the ceiling in every view (a NaN view disqualifies).</summary>
        internal static bool FractionsQualify(double[] fractions)
        {
            if (fractions == null || fractions.Length == 0) return false;
            bool any = false;
            foreach (double f in fractions)
            {
                if (double.IsNaN(f) || f > MissingMeshMaxViewFraction) return false;
                if (f >= MissingMeshMinViewFraction) any = true;
            }
            return any;
        }

        /// <summary>
        /// The first qualifying rendered renderer of one subject, in record order: its bounds
        /// projected in every view of <paramref name="views"/> as a fraction of
        /// <paramref name="shared"/> (<see cref="FractionsQualify"/>). The pick has
        /// <c>RendererIndex = -1</c> and a reason when none qualifies.
        /// </summary>
        internal static PartParityMissingMeshPick ChooseMissingMeshRenderer(
            string subject, IList<PartParityRendererRecord> records, PartParityAabb shared, IList<string> views)
        {
            var pick = new PartParityMissingMeshPick { Subject = subject, Views = views == null ? new List<string>() : new List<string>(views) };
            int rendered = 0;
            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    PartParityRendererRecord r = records[i];
                    if (r == null || !r.Rendered) continue;
                    rendered++;
                    double[] f = ProjectedFractions(r, shared, views);
                    if (!FractionsQualify(f)) continue;
                    pick.RendererIndex = i;
                    pick.RendererPath = r.Path;
                    pick.Fractions = f;
                    return pick;
                }
            }
            pick.Why = "no rendered renderer in the window (" + rendered.ToString(CultureInfo.InvariantCulture) + " rendered)";
            return pick;
        }

        /// <summary>
        /// The missing-mesh control's (subject, renderer): over the candidates in order, the
        /// ones whose prefab plays no Animation first (a moving part would be captured
        /// <c>still-animating</c> and its blob would mix motion into the measure), then the
        /// animated ones; per subject the shared box is the union of its rendered renderers'
        /// bounds and the renderer is <see cref="ChooseMissingMeshRenderer"/>'s. Null when no
        /// candidate qualifies (the control is then <c>bad</c>, <c>no-subject</c>).
        /// </summary>
        internal static PartParityMissingMeshPick ChooseMissingMeshSubject(
            IList<PartParityMissingMeshCandidate> candidates, IList<string> views)
        {
            if (candidates == null) return null;
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var c in candidates)
                {
                    if (c == null || c.HasAnimation != (pass == 1)) continue;
                    var shared = new PartParityAabb();
                    if (c.Records != null)
                        foreach (var r in c.Records)
                            if (r != null && r.Rendered) shared.Encapsulate(PartParityFramingRule.BoundsOf(r));
                    PartParityMissingMeshPick p = ChooseMissingMeshRenderer(c.Subject, c.Records, shared, views);
                    if (p.RendererIndex < 0) continue;
                    p.Animated = c.HasAnimation;
                    return p;
                }
            }
            return null;
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
            if (refs != null) foreach (var r in refs) if (r != null && r.Rendered) n++;
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
