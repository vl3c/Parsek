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

        /// <summary>Candidates for <c>ctl-ghost-missing-mesh</c>, the first loaded part with at
        /// least <see cref="MinMissingMeshRenderers"/> mesh renderers wins.</summary>
        internal static readonly string[] MissingMeshCandidates =
            { "mk1pod.v2", "probeCoreOcto.v2", "liquidEngine2", "Mark1Cockpit", "mk2LanderCabin.v2" };

        internal const int MinMissingMeshRenderers = 3;

        /// <summary>Candidates for <c>ctl-wrong-skin</c>, the first loaded part with a non-base
        /// colour variant wins (<c>FairingGhostVisualMath.ChooseColourVariant</c>).</summary>
        internal static readonly string[] WrongSkinCandidates =
            { "fuelTank", "fuelTankSmallFlat", "fuelTankSmall", "mk1pod.v2", "probeCoreOcto.v2" };

        /// <summary>The missing-mesh control hides the smallest rendered ghost renderer whose
        /// bounds diagonal is at least this fraction of the shared box diagonal (a renderer
        /// much smaller may be sub-pixel or fully inside the part).</summary>
        internal const double MissingMeshMinDiagonalFraction = 0.04;

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

        /// <summary>
        /// The missing-mesh control's target among the ghost's rendered renderers: the
        /// smallest (by bounds diagonal) whose diagonal is at least
        /// <see cref="MissingMeshMinDiagonalFraction"/> of <paramref name="sharedDiagonal"/>;
        /// -1 when none qualifies.
        /// </summary>
        internal static int ChooseMissingMeshTarget(IList<double> renderedDiagonals, double sharedDiagonal)
        {
            int best = -1;
            if (renderedDiagonals == null) return best;
            double floor = MissingMeshMinDiagonalFraction * sharedDiagonal;
            for (int i = 0; i < renderedDiagonals.Count; i++)
            {
                double d = renderedDiagonals[i];
                if (double.IsNaN(d) || d < floor) continue;
                if (best < 0 || d < renderedDiagonals[best]) best = i;
            }
            return best;
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
