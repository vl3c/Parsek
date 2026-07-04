using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 10 / B-row9 + B-row20 (cutover regression harness) - WARP-THROUGH-HOLDPHASE / interior-gap.
    //
    // THE HOLDPHASE DECISION (documented here with evidence): the §11.5 "warp through HoldPhase / loiter /
    // descent re-anchor" row is VACUOUS-UNDER-FLAG-ON for the HoldPhase PRODUCER in v1, because the factory
    // NEVER constructs a live HoldPhase. The only HoldPhase construction in the whole render pipeline is
    // MovingTargetStationApproach.cs (the moving-target station case), which is FAIL-CLOSED / define-only in
    // v1 (FailClosedClassifier routes it to FaithfulFallback - it never reaches a live spine-driven chain).
    // PhaseFactory / ChainAssembler produce ZERO HoldPhases; interior chain gaps are COVERAGE-CLASSIFIED
    // (PhaseChain.ClassifyCoverage -> Coverage.InInteriorGap), NOT modelled as a HoldPhase. Building a new
    // HoldPhase producer to exercise the row would be NEW GEOMETRY (out of this test-only, additive scope).
    // So per the task's allowed disposition, this test does NOT fabricate a HoldPhase; it asserts the
    // OBSERVABLE EQUIVALENT the spine actually uses in v1 - the coverage-state interior-gap HOLD - and
    // documents the HoldPhase vacuity with the factory evidence above.
    //
    // THE COVERAGE-STATE WARP-STEP ASSERTION (the live, valuable test): a single high-warp frame can advance
    // liveUT across an entire interior gap in one step. The spine's contract (GhostRenderDirector.Decide:
    // Coverage.InInteriorGap -> HOLD the prior intent) must mean a warp step that lands IN the gap keeps the
    // prior (visible) intent - no blink, no retire, no icon disappearance - and a warp step that lands PAST
    // the gap in the next phase resumes a visible intent. Critically, across the whole warp sequence the
    // ghost is NEVER Hidden once it has become visible (the no-blink invariant). This drives the SAME pair
    // ShadowRenderDriver.RunFrame inlines for the spine path - ChainSampler.Sample(PhaseChain, liveUT, units)
    // then GhostRenderDirector.Decide(sample, prior, label) - at three live UTs (in phase 1 / IN the gap /
    // past the gap in phase 2) with units=Empty so liveUT maps through unchanged and the gap is exercised
    // deterministically.
    //
    // ARCHITECTURAL TRUTH respected: this asserts the spine's coverage DECISION + the director's gap-hold
    // (the decision-source contract). It introduces no producer geometry and asserts no 5b-only pixel change.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics). The three-frame sampler+director
    // sequence is PURE (hand-built PhaseChain, ChainSampler, director - no KSP reads; review N16 removed
    // the stale KSP-coupling claim and the unused Kerbin lookup), and a HEADLESS TWIN pins it in
    // GhostRenderDirectorTests.WarpStep_AcrossInteriorGap_HoldsPriorIntent_NoBlink_Headless. This copy
    // stays as the in-KSP-runtime confirmation alongside the other spine in-game tests. FLIGHT only;
    // career-independent.
    public class WarpThroughInteriorGapSpineInGameTest
    {
        private const string KerbinBodyName = "Kerbin";

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 10 B-row9/20 warp-through interior-gap (spine): a single high-warp step that "
                + "lands IN an interior gap HOLDS the prior visible intent (no blink/retire) and a step past "
                + "the gap resumes visible - the ghost is never Hidden once visible. Documents HoldPhase as "
                + "vacuous-under-flag-ON in v1 (the factory constructs none).")]
        public void WarpStep_AcrossInteriorGap_HoldsPriorIntent_NoBlink()
        {
            // --- THE HOLDPHASE VACUITY EVIDENCE (documented, asserted) ---
            // A HoldPhase covers its WHOLE span (CoversUt the full [StartUt, EndUt)) BY DESIGN so a warp step
            // never resolves to "no phase" mid-hold - that is the warp-safety contract IF a HoldPhase existed.
            // We assert that warp-safety property directly on a constructed HoldPhase to lock the contract,
            // while documenting that the v1 FACTORY constructs none (so the live row is the interior-gap hold
            // below, not a HoldPhase). This proves we understand the contract without shipping a producer.
            var holdId = new PhaseId("hold-doc", 0, 0);
            var hold = new HoldPhase(holdId, new AnchorFrame.BodyAnchor(KerbinBodyName),
                startUt: 1000.0, endUt: 2000.0);
            // A warp step landing anywhere inside the hold span still resolves to the hold (never a gap),
            // and the hold draws nothing of its own (Treatment.None -> the prior intent is held).
            InGameAssert.IsTrue(hold.CoversUt(1500.0),
                "a HoldPhase must cover its WHOLE span so a high-warp step landing mid-hold never resolves to "
                + "no-phase (the warp-safety contract); documents the HoldPhase behavior even though the v1 "
                + "factory constructs none");
            InGameAssert.AreEqual(Treatment.None, hold.ResolveTreatment(),
                "a HoldPhase draws nothing of its own (the prior intent is held)");

            // --- THE LIVE COVERAGE-STATE WARP-STEP ASSERTION (the interior-gap hold the spine actually uses) ---
            // Build a per-member PhaseChain with TWO StockConic phases separated by an interior GAP inside the
            // window: [winStart .. p1End] phase 1, (p1End .. p2Start) GAP, [p2Start .. winEnd] phase 2. With
            // units=Empty the sampler maps liveUT through unchanged, so we place liveUT in phase 1, then jump
            // (one warp step) INTO the gap, then jump past it into phase 2 - and assert the gap holds.
            double winStart = 100.0;
            double p1End = 400.0;     // phase 1 ends here
            double p2Start = 700.0;   // phase 2 starts here -> (400, 700) is the interior gap
            double winEnd = 1000.0;

            PhaseChain chain = BuildTwoPhaseChainWithGap(winStart, p1End, p2Start, winEnd);
            // Sanity: the chain must actually classify a gap between the two phases (else the test is vacuous).
            Coverage gapProbe = chain.ClassifyCoverage(550.0, out _, out _);
            InGameAssert.AreEqual(Coverage.InInteriorGap, gapProbe,
                "the fixture chain must classify a real interior gap between its two phases (else the warp-step "
                + "hold assertion would be vacuous)");

            GhostPlaybackLogic.LoopUnitSet units = GhostPlaybackLogic.LoopUnitSet.Empty;
            const string label = "warp-gap-ghost";

            // Frame A: liveUT inside phase 1 -> visible StockConic (the prior intent becomes visible).
            GhostSample sampleA = ChainSampler.Sample(chain, 250.0, units);
            GhostRenderIntent intentA = GhostRenderDirector.Decide(sampleA, GhostRenderIntent.Hidden(), label);
            InGameAssert.AreEqual(Coverage.InSegment, sampleA.Coverage,
                "frame A (in phase 1) must be InSegment");
            InGameAssert.IsTrue(intentA.Visible, "frame A must be VISIBLE (phase 1 drawn)");

            // Frame B: ONE high-warp step lands liveUT IN the gap -> the director must HOLD intent A (still
            // visible, same treatment/body), NOT blink to Hidden / retire. This is the warp-step gap-hold.
            GhostSample sampleB = ChainSampler.Sample(chain, 550.0, units);
            GhostRenderIntent intentB = GhostRenderDirector.Decide(sampleB, intentA, label);
            InGameAssert.AreEqual(Coverage.InInteriorGap, sampleB.Coverage,
                "frame B (the warp step landing in the gap) must classify InInteriorGap");
            InGameAssert.IsTrue(intentB.Visible,
                "frame B must HOLD the prior visible intent across the gap (no blink/retire) - the warp-step "
                + "gap-hold contract; a regression that dropped to Hidden in the gap would fail here");
            InGameAssert.AreEqual(intentA.Treatment, intentB.Treatment,
                "the held intent in the gap must keep the prior frame's treatment (no surface flip)");
            InGameAssert.AreEqual(intentA.FrameBodyName, intentB.FrameBodyName,
                "the held intent in the gap must keep the prior frame's body (no re-anchor blink)");

            // Frame C: the next warp step lands liveUT PAST the gap in phase 2 -> visible again. Across A->B->C
            // the ghost was NEVER Hidden once visible (the no-blink invariant a high-warp pass must preserve).
            GhostSample sampleC = ChainSampler.Sample(chain, 850.0, units);
            GhostRenderIntent intentC = GhostRenderDirector.Decide(sampleC, intentB, label);
            InGameAssert.AreEqual(Coverage.InSegment, sampleC.Coverage,
                "frame C (warp step past the gap into phase 2) must be InSegment again");
            InGameAssert.IsTrue(intentC.Visible, "frame C must be VISIBLE (phase 2 drawn)");

            // The no-blink invariant across the whole warp sequence: every frame after first-visible is visible.
            bool everHiddenAfterVisible = !intentB.Visible || !intentC.Visible;
            InGameAssert.IsFalse(everHiddenAfterVisible,
                "across the high-warp A->gap->C sequence the ghost must NEVER blink Hidden once it has become "
                + "visible - the warp-through-interior-gap no-blink invariant");

            ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                "WarpStep_AcrossInteriorGap: holdCoversSpan={0} | A cov={1} vis={2} treat={3} | B(gap) cov={4} "
                + "vis={5} treat={6} | C cov={7} vis={8} | HoldPhase-producer=VACUOUS-under-flag-ON (factory "
                + "constructs none; only MovingTargetStationApproach, fail-closed)",
                hold.CoversUt(1500.0), sampleA.Coverage, intentA.Visible, intentA.Treatment,
                sampleB.Coverage, intentB.Visible, intentB.Treatment, sampleC.Coverage, intentC.Visible));
        }

        // A per-member PhaseChain with two StockConic phases and an interior gap between them. Both phases
        // carry a simple Kerbin conic so the projected sample is a visible StockConic. The gap (p1End,p2Start)
        // is inside [winStart, winEnd], so ClassifyCoverage returns InInteriorGap there.
        private static PhaseChain BuildTwoPhaseChainWithGap(
            double winStart, double p1End, double p2Start, double winEnd)
        {
            OrbitSegment conic1 = BuildConic(winStart, p1End, lan: 0.0);
            OrbitSegment conic2 = BuildConic(p2Start, winEnd, lan: 0.0);
            var phases = new List<TrajectoryPhase>
            {
                new DepartureLoiterPhase(
                    new PhaseId("warp-gap", 0, 0), SegmentProvenance.Recorded,
                    new AnchorFrame.BodyAnchor(KerbinBodyName), winStart, p1End, conic1),
                new ArrivalLoiterPhase(
                    new PhaseId("warp-gap", 0, 1), SegmentProvenance.Recorded,
                    new AnchorFrame.BodyAnchor(KerbinBodyName), p2Start, winEnd, conic2),
            };
            return new PhaseChain(
                "warp-gap-rec", committedIndex: 0, instanceKey: 0, phases, winStart, winEnd);
        }

        private static OrbitSegment BuildConic(double startUT, double endUT, double lan)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 0.0,
                eccentricity = 0.0,
                semiMajorAxis = 850000.0,
                longitudeOfAscendingNode = lan,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                bodyName = KerbinBodyName,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            };
        }
    }
}
