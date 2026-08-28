using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

using Coverage = Parsek.MapRenderTrace.RenderWindowCoverage;
using Handoff = Parsek.MapRenderTrace.LineHandoffKind;
using Verdict = Parsek.MapRenderTrace.LineToggleVerdict;

namespace Parsek.Tests
{
    /// <summary>
    /// LINE-BLINK-JUMP-STRADDLE-DETECTOR-GAP: the WINDOW-TRANSITION exemption on the gated Tier-C
    /// <c>line-blink</c> anomaly, plus the cells that pin it CANNOT mask a real blink.
    ///
    /// <para>The exemption's claim is one sentence: <b>a toggle pair that leaves the recording's
    /// rendered body-frame window and comes back onto a clock the recording COVERS is two legitimate
    /// transitions, not a flicker.</b> Both halves must be PROVEN - and that is the whole lesson of
    /// this file's history. Judging from ONE half looks sufficient and is not:
    /// <c>parking-conic-loiter-hold</c> holds the line LIT while the clock is outside the window, so a
    /// pair pivoting on it has nothing inside the window and is a real on-screen flash. A
    /// dark-half-only rule swallows it from one edge; a lit-half-only rule swallows it from the
    /// other.</para>
    ///
    /// <para>Fixture UTs / frames / bodies below are the ARCHIVED values from the four measured raises,
    /// not invented ones - see each cell's citation.</para>
    ///
    /// <para>The file also carries the SECOND, disjoint exemption on the same detector -
    /// V15M-LINEBLINK-IS-TRACEDPATH-HANDOFF-CADENCE, the Director's designed StockConic -&gt; TracedPath
    /// descent handoff - because both exemptions widen (or fail to widen) the same gated token and their
    /// source gates constrain each other: the handoff site must NOT become a fifth coverage stamp, and
    /// the coverage sites must NOT become handoff stamps. Its section starts below the archived-raise
    /// replays.</para>
    /// </summary>
    public class LineBlinkWindowExitExemptionTests
    {
        // ---------------------------------------------------------------------------------
        // ClassifyLineToggle - the positive discriminator, one cell per FAIL-CLOSED conjunct
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Classify_DarkAndMeasuredOutside_IsWindowExitOff()
        {
            Assert.Equal(Verdict.WindowExitOff, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: true, lineDefinitivelyLit: false,
                hasFreshIntent: true, intentLineActive: false,
                intentWindowCoverage: Coverage.Outside));
        }

        [Fact]
        public void Classify_LitAndMeasuredInside_IsInsideWindowOn()
        {
            Assert.Equal(Verdict.InsideWindowOn, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: false, lineDefinitivelyLit: true,
                hasFreshIntent: true, intentLineActive: true,
                intentWindowCoverage: Coverage.Inside));
        }

        [Fact]
        public void Classify_DegenerateLineRead_IsOther()
        {
            // "(line-null)" / "(no-renderer)" / "(read-err:...)" land here: neither definitively off
            // nor definitively lit. Not knowing is never evidence of a legitimate transition.
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: false, lineDefinitivelyLit: false,
                hasFreshIntent: true, intentLineActive: false,
                intentWindowCoverage: Coverage.Outside));
        }

        [Fact]
        public void Classify_NoFreshIntent_IsOther()
        {
            // Our Postfix did not run, so nothing measured the clock. BLOCKER-2 route (a): a line lit
            // behind our back after a stamped window-exit OFF must not complete an exemption.
            // Dark half, no decision this frame:
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: true, lineDefinitivelyLit: false,
                hasFreshIntent: false, intentLineActive: false,
                intentWindowCoverage: Coverage.Outside));
            // Lit half, no decision this frame:
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: false, lineDefinitivelyLit: true,
                hasFreshIntent: false, intentLineActive: true,
                intentWindowCoverage: Coverage.Inside));
        }

        [Fact]
        public void Classify_DecisionDisagreesWithTruth_IsOther()
        {
            // That disagreement is the decision-vs-truth anomaly's business and must not be laundered
            // into a line-blink exemption.
            // Truth dark, decision says ON:
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: true, lineDefinitivelyLit: false,
                hasFreshIntent: true, intentLineActive: true,
                intentWindowCoverage: Coverage.Outside));
            // Truth lit, decision says OFF:
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: false, lineDefinitivelyLit: true,
                hasFreshIntent: true, intentLineActive: false,
                intentWindowCoverage: Coverage.Inside));
        }

        [Fact]
        public void Classify_DarkButNotMeasuredOutside_IsOther()
        {
            // THE load-bearing conjunct for the dark half. Every within-window OFF reason
            // (polyline-owns-phase, director-traced-path-suppress, below-atmosphere /
            // terminal-below-atmosphere, stale-segment-awaiting-reseed, post-polyline-release-grace,
            // director-terminal-suppress) leaves coverage Unknown and still raises.
            foreach (Coverage coverage in new[] { Coverage.Unknown, Coverage.Inside })
            {
                Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                    lineDefinitivelyOff: true, lineDefinitivelyLit: false,
                    hasFreshIntent: true, intentLineActive: false,
                    intentWindowCoverage: coverage));
            }
        }

        [Fact]
        public void Classify_LitButNotMeasuredInside_IsOther()
        {
            // THE load-bearing conjunct for the lit half, and BLOCKER-2 route (b). `Inside` is
            // POSITIVE, not "not Outside": `terminal-visible` is LIT past the recorded window and
            // stamps NOTHING (Unknown), so a not-Outside test would read it as covered. `Outside` is
            // `parking-conic-loiter-hold`. Both must be Other.
            foreach (Coverage coverage in new[] { Coverage.Unknown, Coverage.Outside })
            {
                Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                    lineDefinitivelyOff: false, lineDefinitivelyLit: true,
                    hasFreshIntent: true, intentLineActive: true,
                    intentWindowCoverage: coverage));
            }
        }

        // ---------------------------------------------------------------------------------
        // ResolveWindowTransitionExempt - BOTH halves, symmetric across the two edges
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Resolve_LitEdge_InsideOnAfterWindowExitOff_IsExempt()
        {
            // The V10 dres shape: dark(before-body-frame-start) -> lit(director-stockconic-visible).
            Assert.True(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: true, currentToggle: Verdict.InsideWindowOn,
                hasPriorToggle: true, priorToggle: Verdict.WindowExitOff));
        }

        [Fact]
        public void Resolve_DarkEdge_WindowExitOffAfterInsideOn_IsExempt()
        {
            // The V8 eve shape: lit(inside) -> dark(past-body-frame-end).
            Assert.True(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: false, currentToggle: Verdict.WindowExitOff,
                hasPriorToggle: true, priorToggle: Verdict.InsideWindowOn));
        }

        [Fact]
        public void Resolve_LitEdge_LitHalfNotProvenInside_StillRaises()
        {
            // BLOCKER 2 at the predicate: the lit half is `parking-conic-loiter-hold` or
            // `terminal-visible` or an undecided frame. A pair with nothing inside the window is a
            // real on-screen flash.
            Assert.False(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: true, currentToggle: Verdict.Other,
                hasPriorToggle: true, priorToggle: Verdict.WindowExitOff));
        }

        [Fact]
        public void Resolve_DarkEdge_PriorLitHalfNotProvenInside_StillRaises()
        {
            // BLOCKER 1, THE MIRROR, and the cell whose ABSENCE was the gap. Sequence: dark
            // (past-body-frame-end, stamped) -> ... >8 frames ... -> lit (parking-conic-loiter-hold,
            // stamped Other, no raise because it is out of the frame window) -> the hold disarms one
            // frame later -> dark (past-body-frame-end). Caught on the DARK edge with sinceFrames=1.
            // A dark-half-only rule exempted this, so the whole 1-frame lit flash in the dark region
            // produced ZERO raises. The prior toggle was not a proven inside-window ON, so it raises.
            Assert.False(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: false, currentToggle: Verdict.WindowExitOff,
                hasPriorToggle: true, priorToggle: Verdict.Other));
        }

        [Fact]
        public void Resolve_HalvesMustBeOppositeKinds_OtherwiseRaises()
        {
            // A pair is one dark half and one lit half. Two of the same kind is not a transition.
            var cases = new[]
            {
                new object[] { true,  Verdict.InsideWindowOn, Verdict.InsideWindowOn },
                new object[] { true,  Verdict.WindowExitOff,  Verdict.WindowExitOff },
                new object[] { false, Verdict.WindowExitOff,  Verdict.WindowExitOff },
                new object[] { false, Verdict.InsideWindowOn, Verdict.InsideWindowOn },
            };
            foreach (object[] c in cases)
            {
                Assert.False(MapRenderTrace.ResolveWindowTransitionExempt(
                    lineIsLit: (bool)c[0], currentToggle: (Verdict)c[1],
                    hasPriorToggle: true, priorToggle: (Verdict)c[2]));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Resolve_NoPriorToggle_IsNotExempt(bool lineIsLit)
        {
            Assert.False(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: lineIsLit,
                currentToggle: lineIsLit ? Verdict.InsideWindowOn : Verdict.WindowExitOff,
                hasPriorToggle: false,
                priorToggle: lineIsLit ? Verdict.WindowExitOff : Verdict.InsideWindowOn));
        }

        // ---------------------------------------------------------------------------------
        // IsLineBlink - the guard itself, and the CANNOT-MASK pins
        // ---------------------------------------------------------------------------------

        [Fact]
        public void IsLineBlink_DefaultWindowTransitionExempt_PreservesLegacyBehavior()
        {
            // Every pre-existing call site omits the new argument and must be byte-identical.
            Assert.True(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103));
        }

        [Fact]
        public void IsLineBlink_WithinWindow_WindowTransitionExempt_NotBlink()
        {
            Assert.False(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103,
                bodyChanged: false, offWindowCovered: false,
                windowTransitionExempt: true));
        }

        [Fact]
        public void IsLineBlink_WithinWindow_NotAWindowTransition_StillBlink()
        {
            // THE CANNOT-MASK PIN. Identical to the cell above in every parameter but the
            // discriminator.
            Assert.True(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103,
                bodyChanged: false, offWindowCovered: false,
                windowTransitionExempt: false));
        }

        [Fact]
        public void IsLineBlink_NoToggle_WindowTransitionExempt_StillNotBlink()
        {
            Assert.False(MapRenderTrace.IsLineBlink(
                toggled: false, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103,
                bodyChanged: false, offWindowCovered: false,
                windowTransitionExempt: true));
        }

        // ---------------------------------------------------------------------------------
        // The FOUR archived raises, replayed end to end through the whole chain
        // ---------------------------------------------------------------------------------

        /// <summary>Replay one archived raise: classify this frame's half, resolve against the stamped
        /// other half, then ask the detector. Coverage values are the ones the cited decision lines
        /// carry.</summary>
        private static bool RaisesAfterExemption(
            bool lineIsLit,
            Coverage thisFrameCoverage,
            Verdict priorToggle,
            int lastToggleFrame,
            int currentFrame,
            bool bodyChanged = false,
            bool offWindowCovered = false)
        {
            Verdict current = MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: !lineIsLit,
                lineDefinitivelyLit: lineIsLit,
                hasFreshIntent: true,
                intentLineActive: lineIsLit,
                intentWindowCoverage: thisFrameCoverage);
            bool exempt = MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: lineIsLit, currentToggle: current,
                hasPriorToggle: true, priorToggle: priorToggle);
            return MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: lastToggleFrame, currentFrame: currentFrame,
                bodyChanged: bodyChanged, offWindowCovered: offWindowCovered,
                windowTransitionExempt: exempt);
        }

        [Fact]
        public void ArchivedRaise_V8Eve_1111_DarkEdgeStraddle_IsNowExempt()
        {
            // logs/2026-08-11_1111_V8-eve-player-loop/KSP.log:12230
            //   reason=line-blink lineActive=False prevActive=True lastToggleFrame=7839
            //   sinceFrames=4 body=Eve offWindowCovered=False polylinePainted=False
            // Same frame 7843 decision: reason=past-body-frame-end lineActive=False
            //   currentUT=30451100.0 bounds=[30360218.8,30450249.6]  => clock 850.4 s PAST the end.
            // priorToggle IS LOAD-BEARING and is measured, not assumed: frame 7839's decision in the
            //   same log reads reason=director-stockconic-visible lineActive=True
            //   currentUT=30360400.2 bounds=[30360218.8,30450249.6], i.e. a proven InsideWindowOn -
            //   and note the bounds are IDENTICAL to the dark half's.
            Assert.False(RaisesAfterExemption(
                lineIsLit: false, thisFrameCoverage: Coverage.Outside,
                priorToggle: Verdict.InsideWindowOn,
                lastToggleFrame: 7839, currentFrame: 7843));
        }

        [Fact]
        public void ArchivedRaise_V8Eve_1114_DarkEdgeStraddle_IsNowExempt()
        {
            // logs/2026-08-11_1114_V8-eve-player-loop/KSP.log:12099
            //   lineActive=False prevActive=True lastToggleFrame=7611 sinceFrames=7 body=Sun
            // Same frame 7618 decision: reason=past-body-frame-end lineActive=False
            //   currentUT=30360400.0 bounds=[26616878.0,30360218.8]  => 181.2 s PAST the end.
            // priorToggle measured: frame 7611 reads reason=visible-body-frame lineActive=True
            //   bounds=[26616878.0,30360218.8] - a proven InsideWindowOn, and the ONE archived pair
            //   whose two halves are BOTH body-frame decisions (the other five pair an
            //   applied-segment Inside with a body-frame Outside, though all six happen to carry
            //   identical bounds - see LINE-BLINK-EXEMPTION-DOES-NOT-PIN-THE-BOUNDARY).
            Assert.False(RaisesAfterExemption(
                lineIsLit: false, thisFrameCoverage: Coverage.Outside,
                priorToggle: Verdict.InsideWindowOn,
                lastToggleFrame: 7611, currentFrame: 7618));
        }

        [Fact]
        public void ArchivedRaise_V10Dres_0627_LitEdgeRebind_IsNowExempt()
        {
            // logs/2026-08-12_0627_V10-dres-loop-arrival/KSP.log:11582
            //   lineActive=True prevActive=False lastToggleFrame=7218 sinceFrames=1 body=Sun
            // The OFF half is frame 7218: reason=before-body-frame-start lineActive=False
            //   currentUT=31276682.660 bounds=[31276682.7,43162584.5]  => BEFORE the window start.
            // The lit edge's own decision is director-stockconic-visible, i.e. INSIDE.
            Assert.False(RaisesAfterExemption(
                lineIsLit: true, thisFrameCoverage: Coverage.Inside,
                priorToggle: Verdict.WindowExitOff,
                lastToggleFrame: 7218, currentFrame: 7219));
        }

        [Fact]
        public void ArchivedRaise_V10Dres_0632_LitEdgeRebind_IsNowExempt()
        {
            // logs/2026-08-12_0632_V10-dres-loop-arrival/KSP.log:11618
            //   currentUT=31276442.640 lineActive=True prevActive=False lastToggleFrame=7237
            //   sinceFrames=1 body=Sun. Iteration 3's moved escape bracket.
            Assert.False(RaisesAfterExemption(
                lineIsLit: true, thisFrameCoverage: Coverage.Inside,
                priorToggle: Verdict.WindowExitOff,
                lastToggleFrame: 7237, currentFrame: 7238));
        }

        [Fact]
        public void ArchivedRaiseGeometry_ButAHalfIsNotProven_StillRaises()
        {
            // THE NEGATIVE CONTROLS for all four cells above: byte-identical frame geometry, with ONE
            // fact changed each time. All four must still red the lane.

            // (1) LIT edge, V10 _0627 geometry, but the OFF half was decided INSIDE the window
            //     (polyline-owns-phase, a stale-segment reseed lag, ...).
            Assert.True(RaisesAfterExemption(
                lineIsLit: true, thisFrameCoverage: Coverage.Inside,
                priorToggle: Verdict.Other,
                lastToggleFrame: 7218, currentFrame: 7219));

            // (2) LIT edge, same geometry, but the LIT half is itself OUTSIDE the window
            //     (parking-conic-loiter-hold). Nothing in the pair is inside: a real flash.
            Assert.True(RaisesAfterExemption(
                lineIsLit: true, thisFrameCoverage: Coverage.Outside,
                priorToggle: Verdict.WindowExitOff,
                lastToggleFrame: 7218, currentFrame: 7219));

            // (3) DARK edge, V8 _1114 geometry, but the OFF is not a window exit.
            Assert.True(RaisesAfterExemption(
                lineIsLit: false, thisFrameCoverage: Coverage.Unknown,
                priorToggle: Verdict.InsideWindowOn,
                lastToggleFrame: 7611, currentFrame: 7618));

            // (4) DARK edge, same geometry, but the PRIOR lit half was not proven inside - BLOCKER 1's
            //     swallowed sequence end to end.
            Assert.True(RaisesAfterExemption(
                lineIsLit: false, thisFrameCoverage: Coverage.Outside,
                priorToggle: Verdict.Other,
                lastToggleFrame: 7611, currentFrame: 7618));
        }

        // =================================================================================
        // V15M-LINEBLINK-IS-TRACEDPATH-HANDOFF-CADENCE: the TracedPath-handoff exemption
        // =================================================================================
        //
        // A SECOND, DISJOINT exemption on the same detector, for a shape the window-transition rule
        // above structurally cannot reach. The OFF edge here is the Director's DESIGNED StockConic ->
        // TracedPath descent handoff: `IsTracedPathOwnedThisFrame` flips true, the Postfix kills the
        // line with `director-traced-path-suppress`, and the proto NEVER relights - it retires. That is
        // ONE permanent transition, not a flicker out and back.
        //
        // Why none of the three existing exemptions matches (measured, both runs):
        //   bodyChanged            false - Gilly -> Gilly.
        //   windowTransitionExempt false - the suppress site stamps NO RenderWindowCoverage, BY DESIGN:
        //                          it hides the line because the spine handed the leg away, NOT because
        //                          the clock left the rendered window. Making it a fifth coverage stamp
        //                          is the widening this file exists to prevent (and the coverage-count
        //                          gate below would red on it).
        //   offWindowCovered       false - it needs a FINISHED dark window on the re-activation edge,
        //                          and this is caught on the DARK edge; in a map-CLOSED flight lane the
        //                          polyline's ownership/paint publish never runs at all, so no coverage
        //                          bit exists on either edge.
        //
        // Fixture values are the ARCHIVED fingerprint, raised IDENTICALLY nine days and two codebases
        // apart: pid=4257410708 recId=77f724bb currentUT=16656457.000
        // intentReason=director-traced-path-suppress, priorToggleVerdict=InsideWindowOn
        // toggleVerdict=Other, offWindowCovered/polylinePainted/polylineOwns/windowTransitionExempt/
        // bodyChanged all False, sinceFrames 5 (`2026-08-28_1703`) and 8 (`2026-08-19_1810`).

        /// <summary>The V15M raise's frame geometry with only the THREE PRE-EXISTING guards available -
        /// i.e. what the detector did before this exemption. Both runs' sinceFrames raise.</summary>
        private static bool RaisesUnderTheThreePreExistingGuardsOnly(
            int lastToggleFrame, int currentFrame)
        {
            return MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: lastToggleFrame, currentFrame: currentFrame,
                bodyChanged: false, offWindowCovered: false, windowTransitionExempt: false);
        }

        /// <summary>Replay the V15M shape end to end: classify this frame's half (the suppress site
        /// stamps <c>Unknown</c> coverage, so it can only ever be <c>Other</c>), resolve BOTH exemptions,
        /// then ask the detector.</summary>
        private static bool RaisesAfterHandoffExemption(
            Handoff intentHandoff,
            Verdict priorToggle,
            bool publishSurfaceRan,
            bool polylineCovered,
            int lastToggleFrame,
            int currentFrame,
            bool lineIsLit = false,
            bool hasFreshIntent = true,
            bool hasPriorToggle = true)
        {
            Verdict current = MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: !lineIsLit,
                lineDefinitivelyLit: lineIsLit,
                hasFreshIntent: hasFreshIntent,
                intentLineActive: lineIsLit,
                // director-traced-path-suppress is NOT one of the four measuring sites.
                intentWindowCoverage: Coverage.Unknown);
            bool windowTransitionExempt = MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: lineIsLit, currentToggle: current,
                hasPriorToggle: hasPriorToggle, priorToggle: priorToggle);
            bool tracedPathHandoffExempt = MapRenderTrace.ResolveTracedPathHandoffExempt(
                lineDefinitivelyOff: !lineIsLit,
                hasFreshIntent: hasFreshIntent,
                intentLineActive: lineIsLit,
                intentHandoff: intentHandoff,
                hasPriorToggle: hasPriorToggle,
                priorToggle: priorToggle,
                publishSurfaceRan: publishSurfaceRan,
                polylineCovered: polylineCovered);
            return MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: lastToggleFrame, currentFrame: currentFrame,
                bodyChanged: false, offWindowCovered: false,
                windowTransitionExempt: windowTransitionExempt,
                tracedPathHandoffExempt: tracedPathHandoffExempt);
        }

        [Fact]
        public void V15M_GillyHandoff_PreFixInputs_Raised()
        {
            // THE ARTIFACT, as the detector saw it before this exemption existed: sinceFrames 5 and 8,
            // both <= LineBlinkFrameWindow (8), every pre-existing guard false. This cell is what makes
            // the pair of cells below a FIX rather than a fixture.
            Assert.True(RaisesUnderTheThreePreExistingGuardsOnly(
                lastToggleFrame: 100, currentFrame: 105));   // 2026-08-28_1703
            Assert.True(RaisesUnderTheThreePreExistingGuardsOnly(
                lastToggleFrame: 100, currentFrame: 108));   // 2026-08-19_1810
        }

        [Fact]
        public void V15M_GillyHandoff_MapClosedLane_IsNowExempt()
        {
            // The measured lane: V15M is map-CLOSED flight, renderCompose
            // `ownership-publish-surface-never-ran`, so publishSurfaceRan is false and the coverage bits
            // are structurally absent. The selector alone carries the exemption THERE, and only there.
            // Both incarnations' cadences.
            Assert.False(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: false, polylineCovered: false,
                lastToggleFrame: 100, currentFrame: 105));
            Assert.False(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: false, polylineCovered: false,
                lastToggleFrame: 100, currentFrame: 108));
        }

        [Fact]
        public void V15M_FirstIncarnation_TenFrames_NeverRaised_BeforeOrAfter()
        {
            // CADENCE, NOT CODE, DECIDED THE RAISE - and the fix must not depend on that. In BOTH runs
            // the FIRST incarnation crossed the IDENTICAL handoff one loop earlier at sinceFrames=10
            // (frames 6834->6844 in `_1703`, 7108->7118 in `_1810`) and was never flagged, because 10 >
            // LineBlinkFrameWindow. It stays unflagged after the fix, by the frame window rather than by
            // the exemption: the detector's cadence arithmetic is untouched.
            Assert.False(RaisesUnderTheThreePreExistingGuardsOnly(
                lastToggleFrame: 6834, currentFrame: 6844));
            Assert.False(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: false, polylineCovered: false,
                lastToggleFrame: 7108, currentFrame: 7118));
        }

        [Fact]
        public void MapOpen_HandoffThatNeverDraws_StillRaises()
        {
            // THE CANNOT-MASK PIN for this exemption, and the reason the selector alone is not enough.
            // Map OPEN (the ownership/paint publish surface RAN this frame) and TracedPath claims the
            // leg - but nothing was painted and nothing was owned, so the map really did go dark under a
            // claimed handoff. Byte-identical to the exempt cell above in every parameter but the two
            // coverage facts.
            Assert.True(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: true, polylineCovered: false,
                lastToggleFrame: 100, currentFrame: 105));
        }

        [Fact]
        public void MapOpen_HandoffThatActuallyDraws_IsExempt()
        {
            // The map-open counterpart that IS by design: the polyline painted / owned the ghost across
            // the handoff, so the user sees one continuous trajectory and nothing blinked.
            Assert.False(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: true, polylineCovered: true,
                lastToggleFrame: 100, currentFrame: 105));
        }

        [Fact]
        public void HandoffExemption_FailsClosedOnEveryConjunct()
        {
            // One fact changed per case against the exempting baseline (TracedPathOwned / dark edge /
            // fresh agreeing intent / prior InsideWindowOn / surface never ran); each must still raise.

            // (1) NOT a designed handoff: any other OFF reason (polyline-owns-phase, below-atmosphere,
            //     stale-segment-awaiting-reseed, director-terminal-suppress, ...) leaves it None.
            Assert.True(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.None, priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: false, polylineCovered: false,
                lastToggleFrame: 100, currentFrame: 105));

            // (2) The LIT edge. The exemption is dark-edge only: the measured shape never relights, and
            //     an ON arriving out of a handoff has not been measured, so it stays fail-closed.
            Assert.True(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.WindowExitOff,
                publishSurfaceRan: false, polylineCovered: false,
                lastToggleFrame: 100, currentFrame: 105, lineIsLit: true));

            // (3) No fresh intent - our Postfix never decided this frame, so nothing measured anything
            //     and the stamp on file is stale.
            Assert.True(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: false, polylineCovered: false,
                lastToggleFrame: 100, currentFrame: 105, hasFreshIntent: false));

            // (4) The LIT half was not a proven InsideWindowOn - parking-conic-loiter-hold,
            //     terminal-visible, a line lit behind our back. Same both-halves discipline as the
            //     window-transition rule.
            Assert.True(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.Other,
                publishSurfaceRan: false, polylineCovered: false,
                lastToggleFrame: 100, currentFrame: 105));

            // (5) No prior toggle at all.
            Assert.True(RaisesAfterHandoffExemption(
                intentHandoff: Handoff.TracedPathOwned, priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: false, polylineCovered: false,
                lastToggleFrame: 100, currentFrame: 105, hasPriorToggle: false));
        }

        [Fact]
        public void HandoffExemption_DecisionDisagreesWithTruth_NotExempt()
        {
            // A decision of ON against a dark truth is the decision-vs-truth anomaly's business and must
            // not be laundered into an exemption - the same rule ClassifyLineToggle applies. Asserted on
            // the predicate directly, because the disagreement cannot be expressed through the replay
            // helper (which ties both to one lineIsLit).
            Assert.False(MapRenderTrace.ResolveTracedPathHandoffExempt(
                lineDefinitivelyOff: true,
                hasFreshIntent: true,
                intentLineActive: true,
                intentHandoff: Handoff.TracedPathOwned,
                hasPriorToggle: true,
                priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: false,
                polylineCovered: false));
            // And the degenerate read ("(line-null)" / "(no-renderer)" / "(read-err:...)"): neither
            // definitively off nor definitively lit. Not knowing is never evidence.
            Assert.False(MapRenderTrace.ResolveTracedPathHandoffExempt(
                lineDefinitivelyOff: false,
                hasFreshIntent: true,
                intentLineActive: false,
                intentHandoff: Handoff.TracedPathOwned,
                hasPriorToggle: true,
                priorToggle: Verdict.InsideWindowOn,
                publishSurfaceRan: false,
                polylineCovered: false));
        }

        [Fact]
        public void IsLineBlink_DefaultTracedPathHandoffExempt_PreservesLegacyBehavior()
        {
            // Every pre-existing call site omits the new argument and must be byte-identical.
            Assert.True(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103,
                bodyChanged: false, offWindowCovered: false, windowTransitionExempt: false));
        }

        [Fact]
        public void IsLineBlink_NoToggle_TracedPathHandoffExempt_StillNotBlink()
        {
            Assert.False(MapRenderTrace.IsLineBlink(
                toggled: false, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103,
                bodyChanged: false, offWindowCovered: false,
                windowTransitionExempt: false, tracedPathHandoffExempt: true));
        }

        // ---------------------------------------------------------------------------------
        // SOURCE GATE: only the four measuring decisions may stamp coverage
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The strongest anti-over-reach pin, and the one that survives a refactor. Because the stamp
        /// is an ENUM VALUE rather than a bare <c>true</c>, any stamp - named argument, positional
        /// argument, or via a local - must spell <c>RenderWindowCoverage.Inside</c> /
        /// <c>.Outside</c>, so counting those spellings catches every widening the earlier
        /// <c>outsideRenderWindow: true</c> grep would have missed (a trailing positional
        /// <c>..., endUT, true)</c> slipped straight past it).
        ///
        /// <para>EXACTLY four stamp sites, all in <c>GhostOrbitLinePatch.cs</c>: two <c>Outside</c>
        /// (the window-exit OFF, and the parking-conic hold that is LIT out there) and two
        /// <c>Inside</c> (<c>director-stockconic-visible</c>, <c>visible-body-frame</c>). Every other
        /// decision - including <c>terminal-visible</c>, which is lit PAST the recorded window, and
        /// <c>stale-segment-awaiting-reseed</c>, whose "outside bounds" is the applied-segment bounds
        /// lagging INSIDE the window - must leave it <c>Unknown</c>.</para>
        /// </summary>
        [Fact]
        public void CoverageStamps_AreConfinedToTheFourMeasuringDecisions()
        {
            string repoRoot = ResolveRepoRoot();
            string sourceDir = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceDir), "Source/Parsek missing: " + sourceDir);

            var stampRe = new Regex(
                @"RenderWindowCoverage\s*\.\s*(Inside|Outside)", RegexOptions.CultureInvariant);
            int inside = 0, outside = 0;
            foreach (string file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                // MapRenderTrace.cs DECLARES the enum and COMPARES against it inside ClassifyLineToggle.
                // Those are reads, not stamps; the writers are LogOrbitLineDecision's callers. Its sole
                // write path (RecordLineIntent) is pinned to one call site by the sibling cell below.
                if (string.Equals(Path.GetFileName(file), "MapRenderTrace.cs", StringComparison.Ordinal))
                    continue;
                MatchCollection hits = stampRe.Matches(File.ReadAllText(file));
                if (hits.Count == 0)
                    continue;
                Assert.True(
                    string.Equals(Path.GetFileName(file), "GhostOrbitLinePatch.cs", StringComparison.Ordinal),
                    "Only GhostOrbitLinePatch's four measuring decisions may stamp render-window "
                    + "coverage, but RenderWindowCoverage.Inside/.Outside appears in: " + file
                    + ". Widening the stamp widens the line-blink exemption - see "
                    + "MapRenderTrace.ClassifyLineToggle.");
                foreach (Match m in hits)
                {
                    if (m.Groups[1].Value == "Inside") inside++; else outside++;
                }
            }

            Assert.True(outside == 2,
                "Expected EXACTLY 2 RenderWindowCoverage.Outside stamps (past-body-frame-end / "
                + "before-body-frame-start, and the parking-conic loiter hold); found " + outside + ".");
            Assert.True(inside == 2,
                "Expected EXACTLY 2 RenderWindowCoverage.Inside stamps (director-stockconic-visible, "
                + "visible-body-frame); found " + inside + ".");
        }

        /// <summary>
        /// The same anti-over-reach pin for the SECOND exemption's stamp
        /// (V15M-LINEBLINK-IS-TRACEDPATH-HANDOFF-CADENCE). <c>LineHandoffKind</c> is an enum for exactly
        /// this reason: every stamp - named, positional, or via a local - must SPELL
        /// <c>LineHandoffKind.TracedPathOwned</c>, so counting the spellings catches a widening that a
        /// bare <c>true</c> would hide.
        ///
        /// <para>EXACTLY ONE stamp site, in <c>GhostOrbitLinePatch.cs</c>: the
        /// <c>director-traced-path-suppress</c> branch, whose own condition IS
        /// <c>ShadowRenderDriver.IsTracedPathOwnedThisFrame</c>. Every other OFF decision -
        /// polyline-owns-phase, below-atmosphere, stale-segment-awaiting-reseed,
        /// post-polyline-release-grace, director-terminal-suppress - must leave it <c>None</c>: they
        /// hide the line for reasons that are NOT a designed spine handoff, and stamping any of them
        /// would exempt a genuine flicker.</para>
        ///
        /// <para>The COVERAGE cell above is the other half of this pin, from the opposite side: the
        /// suppress site must not ALSO become a fifth <c>RenderWindowCoverage</c> stamp (its 2/2 counts
        /// go to 3 if it does). The two exemptions stay disjoint by construction.</para>
        /// </summary>
        [Fact]
        public void HandoffStamp_IsConfinedToTheSingleTracedPathSuppressSite()
        {
            string repoRoot = ResolveRepoRoot();
            string sourceDir = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceDir), "Source/Parsek missing: " + sourceDir);

            var stampRe = new Regex(
                @"LineHandoffKind\s*\.\s*TracedPathOwned", RegexOptions.CultureInvariant);
            int stamps = 0;
            foreach (string file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                // MapRenderTrace.cs DECLARES the enum and COMPARES against it inside
                // ResolveTracedPathHandoffExempt. Those are reads, not stamps; the writer is
                // LogOrbitLineDecision's caller. Its sole write path (RecordLineIntent) is pinned to one
                // call site - and the intent store to one assignment - by the sibling cell below, which
                // covers the Handoff field for the same reason it covers WindowCoverage: they ride the
                // one channel.
                if (string.Equals(Path.GetFileName(file), "MapRenderTrace.cs", StringComparison.Ordinal))
                    continue;
                MatchCollection hits = stampRe.Matches(File.ReadAllText(file));
                if (hits.Count == 0)
                    continue;
                Assert.True(
                    string.Equals(Path.GetFileName(file), "GhostOrbitLinePatch.cs", StringComparison.Ordinal),
                    "Only GhostOrbitLinePatch's director-traced-path-suppress branch may stamp a line "
                    + "handoff, but LineHandoffKind.TracedPathOwned appears in: " + file
                    + ". Widening the stamp widens the line-blink exemption - see "
                    + "MapRenderTrace.ResolveTracedPathHandoffExempt.");
                stamps += hits.Count;
            }

            Assert.True(stamps == 1,
                "Expected EXACTLY 1 LineHandoffKind.TracedPathOwned stamp (the "
                + "director-traced-path-suppress branch, whose condition IS "
                + "IsTracedPathOwnedThisFrame); found " + stamps + ".");
        }

        /// <summary>
        /// The exemption's OTHER live input, pinned to the EXISTING signal it reuses. "Did the
        /// ownership/paint publish surface run this frame?" is answered by the polyline Driver's
        /// <c>pendingDrawsFrame</c> stamp - written LAST in the decide walk, after every early return,
        /// a few lines below the ownership publish itself - and NOT by a new per-frame flag. Two facts
        /// keep that honest: the accessor reads that field, and the field has exactly one write in the
        /// walk's epilogue (plus the three resets that clear it to -1 before the early returns).
        ///
        /// <para>It matters because the map-CLOSED reading is what lets the selector exempt ALONE. If a
        /// future refactor stamped the frame EARLIER - before the <c>MapView.MapIsEnabled</c> gate - a
        /// map-closed lane would start reporting "the surface ran", the coverage conjunct would be
        /// demanded where no coverage can exist, and the V15M artifact would return. If instead the
        /// accessor were rewired to something that is true when the map is closed, the anti-masking
        /// conjunct would go dead.</para>
        /// </summary>
        [Fact]
        public void PublishSurfaceRanSignal_ReusesTheExistingWalkCompletedStamp()
        {
            string repoRoot = ResolveRepoRoot();
            string renderer = File.ReadAllText(Path.Combine(
                repoRoot, "Source", "Parsek", "Display", "GhostTrajectoryPolylineRenderer.cs"));

            Assert.Contains("internal static bool DidOwnershipPublishRunOnFrame(int frame)", renderer);
            Assert.Contains("d.PendingDrawsFrame == frame", renderer);
            Assert.Contains("internal int PendingDrawsFrame => pendingDrawsFrame;", renderer);

            // ONE write of the walk-completed stamp, in the epilogue: `pendingDrawsFrame = drawFrame;`.
            // The resets to -1 (Clear / OnGameStateLoad / the top-of-LateUpdate clear) are deliberately
            // NOT counted - they are the pre-early-return teardown that makes the stamp mean "ran to
            // completion" in the first place.
            // `(?!=)` keeps the `==` READS out (the onPreCull guard and the prose quoting it); the
            // right-hand side is CAPTURED and filtered in code rather than excluded by a lookahead,
            // because a lookahead after `\s*` backtracks the whitespace away and matches the very
            // resets it was meant to skip.
            var writeRe = new Regex(
                @"pendingDrawsFrame\s*=(?!=)\s*([^;\r\n]*);", RegexOptions.CultureInvariant);
            int writes = 0;
            foreach (Match m in writeRe.Matches(renderer))
                if (m.Groups[1].Value.Trim() != "-1")
                    writes++;
            Assert.True(writes == 1,
                "Expected EXACTLY 1 non-reset write of pendingDrawsFrame (the decide walk's epilogue "
                + "stamp, which is what DidOwnershipPublishRunOnFrame means); found " + writes + ".");

            // And the ownership publish must stay in that same epilogue, so the stamp and the publish
            // remain the same fact.
            Assert.Contains("RenderCompositionRecorder.NoteOwnershipPublish(", renderer);
        }

        /// <summary>The stamp must stay OPT-IN and single-sourced: both seams default to
        /// <c>Unknown</c>, and <c>RecordLineIntent</c> has exactly ONE production call site, so
        /// <c>GhostOrbitLinePatch</c> is provably the only thing that can write coverage at all.</summary>
        [Fact]
        public void CoverageStamp_DefaultsToUnknown_AndHasOneWriter()
        {
            string repoRoot = ResolveRepoRoot();
            string sourceDir = Path.Combine(repoRoot, "Source", "Parsek");

            string patch = File.ReadAllText(
                Path.Combine(sourceDir, "Patches", "GhostOrbitLinePatch.cs"));
            string trace = File.ReadAllText(Path.Combine(sourceDir, "MapRenderTrace.cs"));
            Assert.Contains("RenderWindowCoverage.Unknown", patch);
            Assert.Contains("RenderWindowCoverage windowCoverage = RenderWindowCoverage.Unknown", trace);
            // The handoff stamp rides the SAME channel and must be opt-in the same way: both seams
            // default to None, so every branch that does not spell TracedPathOwned leaves it there.
            Assert.Contains("LineHandoffKind.None", patch);
            Assert.Contains("LineHandoffKind handoff = LineHandoffKind.None", trace);

            var callRe = new Regex(@"RecordLineIntent\s*\(", RegexOptions.CultureInvariant);
            int callSites = 0;
            foreach (string file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), "MapRenderTrace.cs", StringComparison.Ordinal))
                    continue;   // the declaration itself
                callSites += callRe.Matches(File.ReadAllText(file)).Count;
            }
            Assert.True(callSites == 1,
                "RecordLineIntent must have EXACTLY one production call site (GhostOrbitLinePatch's "
                + "LogOrbitLineDecision); found " + callSites + ". A second writer could stamp coverage "
                + "without tripping the enum-spelling gate above.");

            // AND close the hole the two gates above leave BY CONSTRUCTION: the spelling gate skips
            // MapRenderTrace.cs (it legitimately compares against the enum inside ClassifyLineToggle),
            // and the call-site count skips it too (it declares RecordLineIntent). So a second writer
            // added INSIDE the tracer - `lineIntentByPid[key] = new LineRenderIntent { WindowCoverage =
            // RenderWindowCoverage.Inside }` - would trip neither. The intent store is the single
            // channel every stamp must pass through, so pin its assignment sites directly: exactly one,
            // the one inside RecordLineIntent.
            var storeWriteRe = new Regex(
                @"lineIntentByPid\s*\[[^\]]*\]\s*=", RegexOptions.CultureInvariant);
            int storeWrites = storeWriteRe.Matches(trace).Count;
            Assert.True(storeWrites == 1,
                "lineIntentByPid must have EXACTLY one assignment site (inside RecordLineIntent); found "
                + storeWrites + ". A second writer inside MapRenderTrace.cs is invisible to both gates "
                + "above, because each of them deliberately skips that file.");
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
