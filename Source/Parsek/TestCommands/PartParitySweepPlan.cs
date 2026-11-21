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

    /// <summary>One candidate subject of the missing-mesh control, read from its part
    /// prefab: its renderer records (part-local bounds), whether it carries an
    /// <c>Animation</c> component, and its PartModule names.</summary>
    internal sealed class PartParityMissingMeshCandidate
    {
        internal string Subject;
        internal bool HasAnimation;
        internal List<string> ModuleNames = new List<string>();
        internal List<PartParityRendererRecord> Records = new List<PartParityRendererRecord>();
    }

    /// <summary>A still subject in pick order, with its prefab size estimate (the first
    /// renderer whose estimated fractions fall in the window, or none).</summary>
    internal sealed class PartParityMissingMeshSubject
    {
        internal string Subject;
        internal bool EstimateQualifies;
        internal string EstimateRendererPath;
        internal double[] EstimateFractions;

        /// <summary><c>subject=&lt;s&gt;;estimate=&lt;path&gt;;side-a=0.123%;...</c> or
        /// <c>;estimate=none</c> (percent, three decimals, invariant).</summary>
        internal string Describe(IList<string> views)
        {
            var sb = new StringBuilder("subject=").Append(Subject ?? "<none>");
            if (!EstimateQualifies) return sb.Append(";estimate=none").ToString();
            sb.Append(";estimate=").Append(EstimateRendererPath ?? "<none>");
            for (int i = 0; views != null && EstimateFractions != null && i < views.Count && i < EstimateFractions.Length; i++)
                sb.Append(';').Append(views[i]).Append('=')
                    .Append((EstimateFractions[i] * 100.0).ToString("0.000", CultureInfo.InvariantCulture)).Append('%');
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
        /// meshes, in preference order (runtime dot-form names; an absent part is skipped).
        /// Only STILL parts are used (<see cref="IsStillSubject"/>); the prefab estimate orders
        /// them (<see cref="OrderMissingMeshSubjects"/>), and the renderer to hide is MEASURED
        /// on the live ghost (<see cref="ChooseMeasuredRenderer"/>).
        /// </summary>
        internal static readonly string[] MissingMeshCandidates =
        {
            "mk1pod.v2", "Mark1Cockpit", "landerCabinSmall", "probeCoreHex.v2", "probeCoreOcto.v2",
            "RCSBlock.v2", "RCSblock.01.small", "ladder1", "telescopicLadder", "telescopicLadderBay",
            "spotLight1", "spotLight2", "spotLight3", "solarPanels5", "solarPanels4", "solarPanels3",
            "solarPanels2", "solarPanels1", "mk2LanderCabin.v2", "liquidEngine2",
        };

        /// <summary>PartModules that move or reshape a part's meshes (or play an Animation): a
        /// subject carrying one would measure animation, not the hide.</summary>
        internal static readonly string[] AnimatingModuleNames =
        {
            "ModuleAnimateGeneric", "RetractableLadder", "ModuleAnimationGroup", "ModuleAnimateHeat",
            "ModuleAnimateEmissive", "ModuleAnimatedDecoupler", "ModuleGimbal", "ModuleControlSurface",
            "ModuleAeroSurface", "ModuleCargoBay", "ModuleServiceModule", "ModuleProceduralFairing",
            "ModuleJettison", "ModuleParachute", "ModuleLight", "ModuleResourceHarvester",
            "ModuleAsteroidDrill", "ModuleGroundExperiment", "ModuleGroundCommsPart",
        };

        /// <summary>Name prefixes of animating PartModule families (deployables, the
        /// animate-* family, robotics, wheels).</summary>
        internal static readonly string[] AnimatingModulePrefixes =
        {
            "ModuleDeployable", "ModuleAnimate", "FXModuleAnimate", "ModuleRobotic", "ModuleWheel",
        };

        /// <summary>The prefab estimate's window, used ONLY to order subjects: a renderer's
        /// projected bounds as a fraction of the SUM of every rendered renderer's projected
        /// bounds in that view, at least this in one view...</summary>
        internal const double MissingMeshMinViewFraction = 0.001;

        /// <summary>... and at most this in every view.</summary>
        internal const double MissingMeshMaxViewFraction = 0.006;

        /// <summary>The measured pick: the hidden renderer drawn ALONE covers at least this
        /// many pixels in at least one view (over the 64 px shape limit)...</summary>
        internal const int MissingMeshMinSoloPx = 100;

        /// <summary>... and at most this fraction of the FULL ghost coverage in every view (so
        /// every view keeps IoU >= 0.99).</summary>
        internal const double MissingMeshMaxSoloFraction = 0.006;

        /// <summary>The sidecar flag of a missing-mesh control with no still subject or no
        /// measured renderer in the window: the judge reports it <c>bad</c>, detail
        /// <c>no-subject</c>, rather than guessing.</summary>
        internal const string FlagControlNoSubject = "control-no-subject";

        /// <summary>The sidecar flag carrying the missing-mesh pick (the subject, the measured
        /// renderer and its counts); the control's log line repeats it.</summary>
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

        /// <summary>
        /// Whether a prefab can be the missing-mesh subject: no <c>Animation</c> component and
        /// no animating PartModule (<see cref="AnimatingModuleNames"/>, or a name starting with
        /// one of <see cref="AnimatingModulePrefixes"/>). <paramref name="why"/> names the first
        /// reason it cannot.
        /// </summary>
        internal static bool IsStillSubject(bool hasAnimationComponent, IEnumerable<string> moduleNames, out string why)
        {
            why = null;
            if (hasAnimationComponent)
            {
                why = "Animation component";
                return false;
            }
            if (moduleNames == null) return true;
            foreach (string m in moduleNames)
            {
                if (string.IsNullOrEmpty(m)) continue;
                if (Array.IndexOf(AnimatingModuleNames, m) >= 0) { why = m; return false; }
                foreach (string prefix in AnimatingModulePrefixes)
                    if (m.StartsWith(prefix, StringComparison.Ordinal)) { why = m; return false; }
            }
            return true;
        }

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

        /// <summary>
        /// The prefab size estimate of one renderer: its projected bounds as a fraction of the
        /// SUM of every rendered renderer's projected bounds, per view (in
        /// <paramref name="views"/> order). The sum, not the part's box, is the denominator: a
        /// thin part's box overstates how much of the image its meshes cover. NaN for a view
        /// with no direction or an empty sum; null when the renderer has no bounds.
        /// </summary>
        internal static double[] EstimateFractions(PartParityRendererRecord r,
            IList<PartParityRendererRecord> all, IList<string> views)
        {
            if (r == null || views == null) return null;
            PartParityAabb rb = PartParityFramingRule.BoundsOf(r);
            if (rb.IsEmpty) return null;
            var f = new double[views.Count];
            for (int i = 0; i < views.Count; i++)
            {
                if (!TryViewDirection(views[i], out double[] dir)) { f[i] = double.NaN; continue; }
                double sum = 0.0;
                if (all != null)
                    foreach (var o in all)
                    {
                        if (o == null || !o.Rendered) continue;
                        PartParityAabb ob = PartParityFramingRule.BoundsOf(o);
                        if (!ob.IsEmpty) sum += ProjectedBoxArea(ob.Size(), dir);
                    }
                f[i] = sum > 0.0 ? ProjectedBoxArea(rb.Size(), dir) / sum : double.NaN;
            }
            return f;
        }

        /// <summary>The estimate window: at least <see cref="MissingMeshMinViewFraction"/> in
        /// one view, at most <see cref="MissingMeshMaxViewFraction"/> in every view (a NaN view
        /// disqualifies).</summary>
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
        /// The missing-mesh subjects in pick order: ONLY still candidates
        /// (<see cref="IsStillSubject"/>; no animated fallback), those with a rendered renderer
        /// in the estimate window first, then the other still ones, each group in list order.
        /// The runner spawns the first; an empty list means <c>no-subject</c>. The estimate
        /// only orders: the renderer actually hidden is measured on the live ghost.
        /// </summary>
        internal static List<PartParityMissingMeshSubject> OrderMissingMeshSubjects(
            IList<PartParityMissingMeshCandidate> candidates, IList<string> views)
        {
            var qualified = new List<PartParityMissingMeshSubject>();
            var rest = new List<PartParityMissingMeshSubject>();
            if (candidates == null) return qualified;
            foreach (var c in candidates)
            {
                if (c == null || !IsStillSubject(c.HasAnimation, c.ModuleNames, out string _)) continue;
                var subject = new PartParityMissingMeshSubject { Subject = c.Subject };
                if (c.Records != null)
                {
                    foreach (var r in c.Records)
                    {
                        if (r == null || !r.Rendered) continue;
                        double[] f = EstimateFractions(r, c.Records, views);
                        if (!FractionsQualify(f)) continue;
                        subject.EstimateQualifies = true;
                        subject.EstimateRendererPath = r.Path;
                        subject.EstimateFractions = f;
                        break;
                    }
                }
                (subject.EstimateQualifies ? qualified : rest).Add(subject);
            }
            qualified.AddRange(rest);
            return qualified;
        }

        /// <summary>
        /// The measured pick, by SIZE only (never by outcome): <paramref name="soloPx"/>[i][v]
        /// is the coverage of rendered ghost renderer i drawn ALONE in view v (same camera,
        /// box and clears as the capture), <paramref name="fullPx"/>[v] the coverage of the
        /// whole ghost. The first renderer whose solo coverage is at least
        /// <see cref="MissingMeshMinSoloPx"/> in one view and at most
        /// <see cref="MissingMeshMaxSoloFraction"/> of the full coverage in every view wins;
        /// -1 when none does.
        /// </summary>
        internal static int ChooseMeasuredRenderer(IList<int[]> soloPx, int[] fullPx)
        {
            if (soloPx == null || fullPx == null || fullPx.Length == 0) return -1;
            for (int i = 0; i < soloPx.Count; i++)
            {
                int[] solo = soloPx[i];
                if (solo == null || solo.Length != fullPx.Length) continue;
                bool any = false, within = true;
                for (int v = 0; v < solo.Length && within; v++)
                {
                    if (solo[v] > MissingMeshMaxSoloFraction * fullPx[v]) within = false;
                    if (solo[v] >= MissingMeshMinSoloPx) any = true;
                }
                if (within && any) return i;
            }
            return -1;
        }

        /// <summary>The measured counts for the Verbose line: <c>full=side-a:N,...</c> then one
        /// <c>| &lt;label&gt; side-a:N,...</c> entry per candidate renderer (invariant).</summary>
        internal static string FormatMeasuredCounts(IList<string> views, int[] fullPx,
            IList<string> labels, IList<int[]> soloPx)
        {
            var sb = new StringBuilder("full=");
            AppendCounts(sb, views, fullPx);
            for (int i = 0; labels != null && soloPx != null && i < labels.Count && i < soloPx.Count; i++)
            {
                sb.Append(" | ").Append(labels[i] ?? "<null>").Append(' ');
                AppendCounts(sb, views, soloPx[i]);
            }
            return sb.ToString();
        }

        /// <summary>The pick flag / Info value: <c>subject=;renderer=;side-a=N/M;...</c>
        /// (solo px over full px per view), or the reason when none qualified.</summary>
        internal static string FormatMeasuredPick(string subject, string rendererPath, IList<string> views,
            int[] soloPx, int[] fullPx, string none)
        {
            var sb = new StringBuilder("subject=").Append(subject ?? "<none>");
            if (rendererPath == null) return sb.Append(";none=").Append(none ?? "<none>").ToString();
            sb.Append(";renderer=").Append(rendererPath);
            for (int v = 0; views != null && soloPx != null && fullPx != null && v < views.Count && v < soloPx.Length && v < fullPx.Length; v++)
                sb.Append(';').Append(views[v]).Append('=').Append(soloPx[v].ToString(CultureInfo.InvariantCulture))
                    .Append('/').Append(fullPx[v].ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void AppendCounts(StringBuilder sb, IList<string> views, int[] px)
        {
            for (int v = 0; views != null && px != null && v < views.Count && v < px.Length; v++)
            {
                if (v > 0) sb.Append(',');
                sb.Append(views[v]).Append(':').Append(px[v].ToString(CultureInfo.InvariantCulture));
            }
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
