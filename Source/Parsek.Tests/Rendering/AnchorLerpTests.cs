using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 3 tests for the multi-anchor lerp interval (design doc §6.4
    /// Stage 4 / §8 / §11 / §18 Phase 3 / §19.2 Stage 4 logging table /
    /// §26.1 HR-7 / HR-9). The math evaluator is pure so most of these
    /// tests exercise <see cref="AnchorCorrectionInterval.EvaluateAt"/>
    /// directly; the lookup-shape tests use the test-only
    /// <see cref="RenderSessionState.PutAnchorForTesting"/> seam to populate
    /// both start- and end-side anchors (Phase 3 production code only emits
    /// start-side <see cref="AnchorSource.LiveSeparation"/> anchors — Phase 6
    /// adds the end-side anchor types).
    /// <para>
    /// Touches static state (<see cref="RenderSessionState"/> map +
    /// <see cref="ParsekLog.TestSinkForTesting"/>) so runs in the
    /// <c>Sequential</c> collection.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class AnchorLerpTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AnchorLerpTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RenderSessionState.ResetForTesting();
        }

        public void Dispose()
        {
            RenderSessionState.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- math helpers --------------------------------------------------

        private static AnchorCorrection MakeStart(string recId, int sectionIdx, double ut, Vector3d eps)
        {
            return new AnchorCorrection(
                recordingId: recId, sectionIndex: sectionIdx, side: AnchorSide.Start,
                ut: ut, epsilon: eps, source: AnchorSource.LiveSeparation);
        }

        private static AnchorCorrection MakeEnd(string recId, int sectionIdx, double ut, Vector3d eps)
        {
            return new AnchorCorrection(
                recordingId: recId, sectionIndex: sectionIdx, side: AnchorSide.End,
                ut: ut, epsilon: eps, source: AnchorSource.DockOrMerge);
        }

        // ----- 1. EvaluateAt: StartOnly returns constant ε -------------------

        [Fact]
        public void EvaluateAt_StartOnly_ReturnsConstantEpsilon()
        {
            // What makes it fail: the §6.4 "Anchor at start only" row — ε
            // must be constant across the segment when no end-side anchor
            // exists. A bug that interpolated against a default-initialized
            // End side (UT=0, ε=0) would silently scale ε down with t,
            // producing visible drift on every Phase 2-only ghost segment.
            var start = MakeStart("rec-A", 0, ut: 100.0, eps: new Vector3d(5, -3, 2));
            var interval = AnchorCorrectionInterval.StartOnly(start);

            // Compare component-wise — Vector3d's IEquatable uses an
            // epsilon-based KSP convention that doesn't always satisfy
            // xUnit's Assert.Equal contract. (Same pattern as
            // AnchorCorrectionTests.AnchorCorrection_Default_HasZeroEpsilon.)
            AssertVec(new Vector3d(5, -3, 2), interval.EvaluateAt(50.0));
            AssertVec(new Vector3d(5, -3, 2), interval.EvaluateAt(100.0));
            AssertVec(new Vector3d(5, -3, 2), interval.EvaluateAt(150.0));
            AssertVec(new Vector3d(5, -3, 2), interval.EvaluateAt(1e9));
        }

        private static void AssertVec(Vector3d expected, Vector3d actual)
        {
            Assert.Equal(expected.x, actual.x, 9);
            Assert.Equal(expected.y, actual.y, 9);
            Assert.Equal(expected.z, actual.z, 9);
        }

        // ----- 2. EvaluateAt: EndOnly returns constant ε ---------------------

        [Fact]
        public void EvaluateAt_EndOnly_ReturnsConstantEpsilon()
        {
            // What makes it fail: the §6.4 "Anchor at end only" row — ε
            // must be constant across the segment when only the end-side
            // anchor exists. A bug that lerped against a default Start
            // (UT=0, ε=0) would mis-render the entire segment by sliding
            // from zero up to ε_end.
            var end = MakeEnd("rec-B", 1, ut: 200.0, eps: new Vector3d(-1, 4, 6));
            var interval = AnchorCorrectionInterval.EndOnly(end);

            AssertVec(new Vector3d(-1, 4, 6), interval.EvaluateAt(50.0));
            AssertVec(new Vector3d(-1, 4, 6), interval.EvaluateAt(200.0));
            AssertVec(new Vector3d(-1, 4, 6), interval.EvaluateAt(1e9));
        }

        // ----- 3. EvaluateAt: Both lerps linearly ---------------------------

        [Fact]
        public void EvaluateAt_BothEnds_LinearLerp()
        {
            // What makes it fail: the §6.4 "Anchors at both ends" formula
            // ε(t) = ε_s + (ε_e − ε_s) * (t − t_s) / (t_e − t_s).
            // A sign error, a swap of t_s / t_e, or a missing scale by
            // (1.0 / span) would produce visible jumps inside a long burn —
            // exactly the failure mode 3.3 Phase 3 is supposed to fix.
            var start = MakeStart("rec-C", 0, ut: 100.0, eps: new Vector3d(1, 0, 0));
            var end   = MakeEnd  ("rec-C", 0, ut: 200.0, eps: new Vector3d(3, 0, 0));
            var interval = AnchorCorrectionInterval.Both(start, end);

            const double tol = 1e-9;

            Vector3d at100 = interval.EvaluateAt(100.0);
            Assert.Equal(1.0, at100.x, 9);
            Assert.Equal(0.0, at100.y, 9);
            Assert.Equal(0.0, at100.z, 9);

            Vector3d at150 = interval.EvaluateAt(150.0);
            Assert.Equal(2.0, at150.x, 9);
            Assert.Equal(0.0, at150.y, 9);
            Assert.Equal(0.0, at150.z, 9);

            Vector3d at175 = interval.EvaluateAt(175.0);
            Assert.Equal(2.5, at175.x, 9);

            Vector3d at200 = interval.EvaluateAt(200.0);
            Assert.Equal(3.0, at200.x, 9);
            Assert.Equal(0.0, at200.y, 9);
            Assert.Equal(0.0, at200.z, 9);

            // Sanity: tol is referenced so a tightening change would force
            // the assertion form, not silently relax it.
            Assert.True(tol > 0);
        }

        // ----- 4. EvaluateAt: clamp below start returns start ---------------

        [Fact]
        public void EvaluateAt_BothEnds_ClampBelowStart_ReturnsStart()
        {
            // What makes it fail: HR-7 forbids extrapolation past a hard
            // discontinuity. Sections are bounded; a one-frame race could
            // call EvaluateAt with a UT slightly outside the section's
            // range. Without clamping, the lerp would extrapolate ε out
            // beyond ε_start, biasing the position before the segment even
            // begins. Clamping pins ε at the start value.
            var start = MakeStart("rec-D", 0, 100.0, new Vector3d(1, 0, 0));
            var end   = MakeEnd  ("rec-D", 0, 200.0, new Vector3d(3, 0, 0));
            var interval = AnchorCorrectionInterval.Both(start, end);

            Vector3d below = interval.EvaluateAt(50.0);
            Assert.Equal(1.0, below.x, 9);
            Assert.Equal(0.0, below.y, 9);
            Assert.Equal(0.0, below.z, 9);

            Vector3d wayBelow = interval.EvaluateAt(-1e6);
            Assert.Equal(1.0, wayBelow.x, 9);
        }

        // ----- 5. EvaluateAt: clamp above end returns end -------------------

        [Fact]
        public void EvaluateAt_BothEnds_ClampAboveEnd_ReturnsEnd()
        {
            // What makes it fail: same HR-7 reasoning as Test 4, on the
            // upper side. An un-clamped lerp would happily produce ε >> ε_end
            // for UTs past the section, biasing the next section's first
            // frame before the next section's anchor can take over.
            var start = MakeStart("rec-E", 0, 100.0, new Vector3d(1, 0, 0));
            var end   = MakeEnd  ("rec-E", 0, 200.0, new Vector3d(3, 0, 0));
            var interval = AnchorCorrectionInterval.Both(start, end);

            Vector3d above = interval.EvaluateAt(300.0);
            Assert.Equal(3.0, above.x, 9);

            Vector3d wayAbove = interval.EvaluateAt(1e9);
            Assert.Equal(3.0, wayAbove.x, 9);
        }

        // ----- 6. Degenerate span: logs Warn, returns start -----------------

        [Fact]
        public void EvaluateAt_DegenerateSpan_LogsWarn_ReturnsStart()
        {
            // What makes it fail: HR-9 forbids silent fall-through on a
            // visibly broken interval. A both-end interval where the end
            // UT does not strictly exceed the start UT cannot produce a
            // valid lerp. The evaluator must (a) emit a Pipeline-Lerp Warn
            // — once per (recordingId, sectionIndex) per session — and (b)
            // return ε_start so the renderer at least keeps the value the
            // start anchor agreed on. A silent zero return, or a divide-by-
            // zero NaN, would mask the producer's bug.
            var start = MakeStart("rec-degenerate", 7, ut: 100.0, eps: new Vector3d(9, 0, 0));
            var end   = MakeEnd  ("rec-degenerate", 7, ut: 100.0, eps: new Vector3d(50, 0, 0));
            var interval = AnchorCorrectionInterval.Both(start, end);

            Vector3d result = interval.EvaluateAt(150.0);
            Assert.Equal(9.0, result.x, 9);
            Assert.Equal(0.0, result.y, 9);
            Assert.Equal(0.0, result.z, 9);

            Assert.Contains(logLines,
                l => l.Contains("[Pipeline-Lerp]")
                  && l.Contains("WARN")
                  && l.Contains("degenerate-span")
                  && l.Contains("recordingId=rec-degenerate")
                  && l.Contains("sectionIndex=7"));
        }

        // ----- 7. HasSignificantDivergence below threshold ------------------

        [Fact]
        public void HasSignificantDivergence_BelowThreshold_ReturnsFalse()
        {
            // What makes it fail: §8 — small ε differences are normal
            // (smoothing residual). The divergence Warn must not fire below
            // the configured threshold (50 m); a too-eager threshold would
            // spam Warn at every healthy long burn.
            var start = MakeStart("rec-low", 0, 100.0, new Vector3d(1.0, 0, 0));
            var end   = MakeEnd  ("rec-low", 0, 200.0, new Vector3d(1.1, 0, 0));
            var interval = AnchorCorrectionInterval.Both(start, end);

            bool diverges = interval.HasSignificantDivergence(out double mag);
            Assert.False(diverges);
            Assert.True(mag < AnchorCorrectionInterval.DivergenceWarnThresholdM,
                $"expected divergence < threshold; got {mag}");
        }

        // ----- 8. HasSignificantDivergence above threshold ------------------

        [Fact]
        public void HasSignificantDivergence_AboveThreshold_ReturnsTrue()
        {
            // What makes it fail: §8 — large ε differences indicate genuine
            // recording-vs-playback inconsistency (physics-tick mismatch
            // over thousands of seconds). The divergence Warn must surface
            // the magnitude so an investigator can correlate it against
            // the segment length. Returning false (or zero magnitude) would
            // hide the failure entirely.
            var start = MakeStart("rec-high", 0, 100.0, new Vector3d(1, 0, 0));
            var end   = MakeEnd  ("rec-high", 0, 200.0, new Vector3d(100, 0, 0));
            var interval = AnchorCorrectionInterval.Both(start, end);

            bool diverges = interval.HasSignificantDivergence(out double mag);
            Assert.True(diverges);
            Assert.True(mag > AnchorCorrectionInterval.DivergenceWarnThresholdM,
                $"expected divergence > threshold; got {mag}");
            Assert.Equal(99.0, mag, 6);  // sqrt((100-1)^2) = 99
        }

        // ----- 9. LookupForSegmentInterval: StartOnly ------------------------

        [Fact]
        public void LookupForSegmentInterval_StartOnly_ReturnsStartOnly()
        {
            // What makes it fail: the lookup must distinguish "start present,
            // end absent" from "both present" — otherwise the §6.4 case
            // selection would degrade to "always lerp against default end",
            // silently scaling ε down across every Phase 2 segment.
            var start = MakeStart("rec-S0", 2, ut: 100.0, eps: new Vector3d(7, 0, 0));
            RenderSessionState.PutAnchorForTesting(start);

            AnchorCorrectionInterval? maybe =
                RenderSessionState.LookupForSegmentInterval("rec-S0", 2);

            Assert.True(maybe.HasValue, "expected a non-null interval");
            AnchorCorrectionInterval interval = maybe.Value;
            Assert.Equal(AnchorIntervalKind.StartOnly, interval.Kind);
            Assert.Equal(7.0, interval.Start.Epsilon.x, 9);
            Assert.Equal(2, interval.Start.SectionIndex);
        }

        // ----- 10. LookupForSegmentInterval: Both ----------------------------

        [Fact]
        public void LookupForSegmentInterval_BothEnds_ReturnsBoth()
        {
            // What makes it fail: a buggy lookup that returned StartOnly
            // when both anchors are present would lock ε at ε_start and
            // never lerp — exactly the snap-into-alignment artifact
            // Phase 3 was created to fix.
            var start = MakeStart("rec-B0", 0, ut: 100.0, eps: new Vector3d(1, 0, 0));
            var end   = MakeEnd  ("rec-B0", 0, ut: 200.0, eps: new Vector3d(3, 0, 0));
            RenderSessionState.PutAnchorForTesting(start);
            RenderSessionState.PutAnchorForTesting(end);

            AnchorCorrectionInterval? maybe =
                RenderSessionState.LookupForSegmentInterval("rec-B0", 0);

            Assert.True(maybe.HasValue);
            AnchorCorrectionInterval interval = maybe.Value;
            Assert.Equal(AnchorIntervalKind.Both, interval.Kind);
            Assert.Equal(1.0, interval.Start.Epsilon.x, 9);
            Assert.Equal(3.0, interval.End.Epsilon.x, 9);

            // Sanity: evaluating across the span actually lerps, proving
            // the Both branch is wired through to the math.
            Vector3d mid = interval.EvaluateAt(150.0);
            Assert.Equal(2.0, mid.x, 9);
        }

        // ----- 11. LookupForSegmentInterval: neither -------------------------

        [Fact]
        public void LookupForSegmentInterval_NeitherEnd_ReturnsNull()
        {
            // What makes it fail: a default-construct fallback that returned
            // a zero-ε StartOnly would inject a phantom "no-op" correction
            // into every renderer call — masking the "no anchor available"
            // case behind a meaningless ε=0 path. HR-9 says return null,
            // and the consumer hook gates on HasValue.
            AnchorCorrectionInterval? maybe =
                RenderSessionState.LookupForSegmentInterval("rec-empty", 0);
            Assert.False(maybe.HasValue);
        }

        // ----- 11b. EndOnly via PutAnchorForTesting --------------------------

        [Fact]
        public void LookupForSegmentInterval_EndOnly_ReturnsEndOnly()
        {
            // What makes it fail: the lookup must populate the End side of
            // the interval when only the end anchor exists; a bug that
            // returned null (instead of EndOnly) would suppress the §6.4
            // "Anchor at end only" case, leaving the renderer to draw the
            // entire segment with no correction.
            var end = MakeEnd("rec-E0", 4, ut: 200.0, eps: new Vector3d(0, 5, 0));
            RenderSessionState.PutAnchorForTesting(end);

            AnchorCorrectionInterval? maybe =
                RenderSessionState.LookupForSegmentInterval("rec-E0", 4);

            Assert.True(maybe.HasValue);
            AnchorCorrectionInterval interval = maybe.Value;
            Assert.Equal(AnchorIntervalKind.EndOnly, interval.Kind);
            Assert.Equal(5.0, interval.End.Epsilon.y, 9);
            Assert.Equal(AnchorSide.End, interval.End.Side);
            Assert.Equal(4, interval.End.SectionIndex);
        }

        // -------------------------------------------------------------------
        //  Logging dedup tests (§19.2 Stage 4 + §19.4 HR-9 visibility)
        // -------------------------------------------------------------------

        // ----- 12. Degenerate-span Warn covered above by Test 6.

        // ----- 13. Both + divergence emits Warn once per session -------------

        [Fact]
        public void Both_DivergentEpsilon_EmitsWarnOnce()
        {
            // What makes it fail: §19.2 Stage 4 — the divergence Warn must
            // appear AT LEAST ONCE so the failure is visible in KSP.log.
            // It must appear AT MOST ONCE per session per (recordingId,
            // sectionIndex) so a per-frame ghost render does not flood the
            // log. Any breakage of either bound would damage diagnostics.
            var start = MakeStart("rec-div", 0, ut: 100.0, eps: new Vector3d(1, 0, 0));
            var end   = MakeEnd  ("rec-div", 0, ut: 200.0, eps: new Vector3d(100, 0, 0));
            var interval = AnchorCorrectionInterval.Both(start, end);

            // Drive 100 evaluations through the consumer-hook style call,
            // which is what the production renderer does each frame.
            for (int i = 0; i < 100; i++)
                RenderSessionState.NotifyLerpDivergenceCheck(in interval);

            int warnCount = logLines.Count(l =>
                l.Contains("[Pipeline-Lerp]")
                && l.Contains("WARN")
                && l.Contains("epsilon-divergence")
                && l.Contains("recordingId=rec-div")
                && l.Contains("sectionIndex=0"));
            Assert.Equal(1, warnCount);

            // Sanity-check: the Warn body carries the magnitude and segment
            // length so an investigator can correlate.
            string warn = logLines.First(l =>
                l.Contains("[Pipeline-Lerp]") && l.Contains("epsilon-divergence"));
            Assert.Contains("divergenceM=99.0", warn);
            Assert.Contains("segmentLengthS=100.0", warn);
        }

        // ----- 14. Single-anchor case emits Verbose once per session ---------

        [Fact]
        public void Single_AnchorCase_EmitsVerboseOnce()
        {
            // What makes it fail: §19.2 Stage 4 — the single-anchor Verbose
            // line is the diagnostic that proves the segment was rendered
            // with a constant ε rather than a lerp. Per-frame chatter would
            // bury the signal; total absence would erase the §6.4 row's
            // audit trail.
            var start = MakeStart("rec-single", 3, ut: 100.0, eps: new Vector3d(2, 0, 0));
            var interval = AnchorCorrectionInterval.StartOnly(start);

            for (int i = 0; i < 100; i++)
                RenderSessionState.NotifySingleAnchorLerpCase(in interval);

            int verboseCount = logLines.Count(l =>
                l.Contains("[Pipeline-Lerp]")
                && l.Contains("VERBOSE")
                && l.Contains("Single-anchor case")
                && l.Contains("recordingId=rec-single")
                && l.Contains("sectionIndex=3")
                && l.Contains("side=Start"));
            Assert.Equal(1, verboseCount);
        }

        // ----- 15. Degenerate-span Warn dedup'd across many evaluations -----

        [Fact]
        public void EvaluateAt_DegenerateSpan_WarnEmittedOnceAcrossManyCalls()
        {
            // What makes it fail: the degenerate Warn dedup is owned by
            // RenderSessionState and would re-emit per call without it.
            // Test 6 pinned the single-call path; this test pins the many-
            // call path to prove the dedup set is wired through the math
            // evaluator.
            var start = MakeStart("rec-deg-many", 5, ut: 100.0, eps: new Vector3d(1, 0, 0));
            var end   = MakeEnd  ("rec-deg-many", 5, ut: 100.0, eps: new Vector3d(2, 0, 0));
            var interval = AnchorCorrectionInterval.Both(start, end);

            for (int i = 0; i < 50; i++)
                interval.EvaluateAt(100.0 + i);

            int warnCount = logLines.Count(l =>
                l.Contains("[Pipeline-Lerp]")
                && l.Contains("WARN")
                && l.Contains("degenerate-span")
                && l.Contains("recordingId=rec-deg-many")
                && l.Contains("sectionIndex=5"));
            Assert.Equal(1, warnCount);
        }

        // ----- 16. Reset clears dedup so next session re-emits --------------

        [Fact]
        public void ResetForTesting_ClearsLerpDedupSets()
        {
            // What makes it fail: long-lived processes that span multiple
            // re-fly sessions would only ever see the FIRST session's
            // diagnostics if the dedup set persisted across Clear /
            // ResetForTesting. Each new session must start with a fresh
            // emission budget.
            var start = MakeStart("rec-reset", 0, ut: 100.0, eps: new Vector3d(1, 0, 0));
            var interval = AnchorCorrectionInterval.StartOnly(start);

            RenderSessionState.NotifySingleAnchorLerpCase(in interval);
            int firstCount = logLines.Count(l =>
                l.Contains("[Pipeline-Lerp]") && l.Contains("Single-anchor case"));
            Assert.Equal(1, firstCount);

            RenderSessionState.ResetForTesting();
            // RenderSessionState.ResetForTesting clears anchor / dedup
            // state plus SurfaceLookupOverrideForTesting only — it does
            // NOT touch the ParsekLog sink, which the [Collection("Sequential")]
            // fixture owns and tears down in Dispose. So the log sink keeps
            // capturing across the reset boundary, and the second emission
            // below is the proof that the dedup set was actually cleared.

            RenderSessionState.NotifySingleAnchorLerpCase(in interval);
            int secondCount = logLines.Count(l =>
                l.Contains("[Pipeline-Lerp]") && l.Contains("Single-anchor case"));
            Assert.Equal(2, secondCount);  // 1 from before reset, 1 after
        }

        [Fact]
        public void EvaluateAt_OutsideRange_LogsClampOutOnce()
        {
            // What makes it fail: HR-7 implies the consumer never queries
            // outside [Start.UT, End.UT] in production (per-section dispatch).
            // If a boundary bug ever does query out-of-range, the silent
            // clamp must surface a Verbose Pipeline-Lerp line once per
            // session per (recordingId, sectionIndex). Without the
            // notifier the boundary bug masks itself.
            var ac1 = new AnchorCorrection("rec-clamp", 5, AnchorSide.Start, 100.0, Vector3d.zero, AnchorSource.LiveSeparation);
            var ac2 = new AnchorCorrection("rec-clamp", 5, AnchorSide.End,   200.0, new Vector3d(2, 0, 0), AnchorSource.LiveSeparation);
            var both = AnchorCorrectionInterval.Both(ac1, ac2);

            // Multiple out-of-range evals — per-session dedup must collapse
            // them to one Verbose line.
            both.EvaluateAt(50.0);   // below Start.UT — clamps to 0
            both.EvaluateAt(40.0);   // below Start.UT — already deduped
            both.EvaluateAt(250.0);  // above End.UT — same key, dedup holds

            int clampLines = logLines.Count(l =>
                l.Contains("[Pipeline-Lerp]") && l.Contains("EvaluateAt-clamp-out")
                && l.Contains("recordingId=rec-clamp") && l.Contains("sectionIndex=5"));
            Assert.Equal(1, clampLines);
        }

        [Fact]
        public void EvaluateAt_InsideRange_NoClampOutLog()
        {
            // What makes it fail: a misplaced clamp-out emit would fire on
            // every in-range query and spam the log per-frame. The notifier
            // must only fire when tNorm actually clamps.
            var ac1 = new AnchorCorrection("rec-norm", 0, AnchorSide.Start, 100.0, Vector3d.zero, AnchorSource.LiveSeparation);
            var ac2 = new AnchorCorrection("rec-norm", 0, AnchorSide.End,   200.0, new Vector3d(2, 0, 0), AnchorSource.LiveSeparation);
            var both = AnchorCorrectionInterval.Both(ac1, ac2);

            both.EvaluateAt(100.0);  // exactly at Start
            both.EvaluateAt(150.0);  // midpoint
            both.EvaluateAt(200.0);  // exactly at End

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Pipeline-Lerp]") && l.Contains("EvaluateAt-clamp-out"));
        }
    }
}
