using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 11 / N4 (test-automation coverage follow-up) - the TERMINAL RETIRE decision test. It closes the
    // terminal / crash retire case at the DECISION layer with the proven P2-pure pattern (synthetic PhaseChain,
    // sampled via ChainSampler.Sample + GhostRenderDirector.Decide - no ghost / scene).
    //
    // THE CONTRACT (design §11.1, GhostRenderDirector.cs:43-44): a recording that ends at a TERMINAL UT (a crash
    // / impact / surface end-of-data) produces a phase chain whose phases end at the recorded end-of-data, so
    // the chain window ENDS at the terminal UT. The producer does NOT model post-terminal time as an interior
    // gap; it leaves the post-terminal UT OUTSIDE the window. The spine must therefore:
    //   - sample INSIDE the window (before the terminal end) as InSegment -> Visible; and
    //   - sample PAST the terminal end as Coverage.OutsideWindow -> the Director returns Hidden EVEN WITH a
    //     visible prior intent (the crash retire: the ghost does not linger at the crash site). A regression
    //     that held the prior intent past the terminal end (treating it as an interior gap) would keep it
    //     Visible here and fail.
    //
    // ARCHITECTURAL TRUTH respected + honest caveat: this asserts the spine's terminal RETIRE DECISION
    // (OutsideWindow -> Hidden past the recorded terminal end), NOT the live ProtoVessel lifecycle (no ghost is
    // created/destroyed) nor any 5b pixel. PhaseChain.ClassifyCoverage / GhostRenderDirector.Decide are unit-
    // tested headlessly; this drives the SAME ChainSampler.Sample entry the production spine inlines over a
    // terminal-ended chain, alongside the other spine in-game tests on a cold pad.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics). Fast (void, no ghost / scene / save). FLIGHT
    // only; career-independent; self-contained.
    public class TerminalRetireDecisionInGameTest
    {
        private const string KerbinBodyName = "Kerbin";

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 11 N4 terminal retire (spine): a one-phase chain ending at a terminal (crash) "
                + "UT samples Visible inside the window and RETIRES (Hidden) past the terminal end even with a "
                + "visible prior intent (Coverage.OutsideWindow -> Decide returns Hidden). Asserts the spine "
                + "retire DECISION, not the ProtoVessel lifecycle.")]
        public void TerminalChain_PastEnd_RetiresHidden_EvenWithVisiblePriorIntent()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            // A short ballistic/surface leg ending at a TERMINAL (crash) UT. The chain window ENDS at crashUT
            // (the recorded end-of-data); there is no post-terminal phase / interior gap.
            const double startUT = 1000.0;
            const double crashUT = 1100.0;
            PhaseChain chain = BuildTerminalChain(startUT, crashUT);

            // Sanity: the recording's EndpointPhase models a terminal (surface impact) end so the fixture
            // represents the crash-retire case, not an orbital one that would never retire.
            // (Documented on the recording shape in BuildTerminalChain.)

            GhostPlaybackLogic.LoopUnitSet units = GhostPlaybackLogic.LoopUnitSet.Empty;

            // --- INSIDE the window: Visible. ---
            double insideUT = 0.5 * (startUT + crashUT);
            GhostSample inside = ChainSampler.Sample(chain, insideUT, units);
            GhostRenderIntent insideIntent = GhostRenderDirector.Decide(inside, GhostRenderIntent.Hidden(), "term");

            InGameAssert.AreEqual(Coverage.InSegment, inside.Coverage,
                "inside the terminal window the spine must classify InSegment (the recorded leg before the crash "
                + "is drawn)");
            InGameAssert.IsTrue(insideIntent.Visible,
                "inside the terminal window the spine's intent must be VISIBLE");

            // --- PAST the terminal end: OutsideWindow -> Hidden, EVEN with the visible prior intent. ---
            double pastEndUT = crashUT + 200.0;
            GhostSample pastEnd = ChainSampler.Sample(chain, pastEndUT, units);
            // Feed the VISIBLE prior intent: the director must STILL retire (OutsideWindow -> Hidden), proving it
            // does not hold a prior visible intent past the terminal end (the crash-linger guard).
            GhostRenderIntent pastEndIntent = GhostRenderDirector.Decide(pastEnd, insideIntent, "term");

            ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                "TerminalRetire: start={0:F1} crash={1:F1} | inside UT={2:F1} cov={3} vis={4} | pastEnd UT={5:F1} "
                + "cov={6} vis={7} (priorWasVisible={8})",
                startUT, crashUT, insideUT, inside.Coverage, insideIntent.Visible, pastEndUT, pastEnd.Coverage,
                pastEndIntent.Visible, insideIntent.Visible));

            InGameAssert.AreEqual(Coverage.OutsideWindow, pastEnd.Coverage,
                "past the terminal end the spine must classify OutsideWindow (the chain window ends at the "
                + "recorded terminal UT - the post-terminal UT is NOT an interior gap, design §11.1)");
            InGameAssert.IsFalse(pastEndIntent.Visible,
                "past the terminal end the spine must RETIRE (Hidden) even though the prior intent was VISIBLE - "
                + "a crashed/impacted ghost does not linger at the crash site (GhostRenderDirector.cs:43-44); a "
                + "regression that held the prior intent (treating post-terminal as an interior gap) would keep "
                + "it Visible here");
        }

        // A one-phase chain ending at a terminal (crash) UT. The single traced leg covers [startUT, crashUT] and
        // the chain window ENDS at crashUT - the producer leaves post-terminal time OUTSIDE the window (design
        // §11.1: a terminal end is not modelled as an interior gap). EndpointPhase=SurfacePosition documents the
        // terminal (impact) endpoint the recording would carry.
        private static PhaseChain BuildTerminalChain(double startUT, double crashUT)
        {
            var anchor = new AnchorFrame.BodyAnchor(KerbinBodyName);
            // A traced surface/ballistic leg (TracedPath) ending at the crash; one phase, window ends at crashUT.
            var phase = new DescentPhase(
                new PhaseId("rec-terminal", 0, 0), SegmentProvenance.Recorded, anchor, startUT, crashUT,
                KerbinBodyName);
            return new PhaseChain(
                "rec-terminal", committedIndex: 0, instanceKey: 0,
                phases: new List<TrajectoryPhase> { phase }, windowStartUt: startUT, windowEndUt: crashUT);
        }
    }
}
