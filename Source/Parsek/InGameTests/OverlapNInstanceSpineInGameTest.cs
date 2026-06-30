using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 11 / N1 (test-automation coverage follow-up) - the OVERLAP N-INSTANCE spine test. It closes the
    // overlap matrix gap at the DECISION layer with the proven P2-pure pattern (a synthetic PhaseChain +
    // LoopUnitSet, sampled via ChainSampler.Sample + GhostRenderDirector.Decide - no ghost / scene), copying
    // the OverlapUnit fixture verbatim from Source/Parsek.Tests/MapRender/ChainSamplerTests.cs:72.
    //
    // THE CONTRACT (the live, valuable assertions): a self-overlap member (OverlapCadenceSeconds < span, so
    // UnitMemberOverlaps is true) replays ONE span instance per overlap cycle. The map shows ONE ghost on the
    // span-clock head-UT (the selected cycle), never enumerating N instances. Sampling across a span of liveUTs
    // that crosses >= 2 overlap cycles, the spine must:
    //   - resolve a DISTINCT selected-cycle head-UT for each cycle (the head advances as the cycle advances -
    //     a regression that froze the head on one cycle, or enumerated all N at once, would fail here);
    //   - stay InSegment (visible) at every liveUT across the span head (the one-segment chain covers the whole
    //     member window, so any in-window selected head lands InSegment); and
    //   - NEVER spuriously Hidden mid-cycle (the no-blink invariant for the looped overlap ghost).
    //
    // DRIVE-UT ANCHOR (weak by construction - the real value of this test is the distinct-heads + no-spurious-
    // hide assertions below): each sampled DriveUT equals GhostPlaybackLogic.ResolveTrackingStationSampleUT for
    // the same member at the same liveUT. ChainSampler.Sample calls that same span clock internally, so this is
    // largely the same computation twice - it confirms the sampler did NOT take the renderHidden / stitcher
    // branch and returned the span-clock head, but it is NOT an independent legacy-vs-spine parity oracle.
    //
    // ARCHITECTURAL TRUTH respected + honest caveat: this asserts the spine's overlap DECISION / parity (which
    // selected-cycle head the sampler resolves, and that it stays visible across cycles), NOT the live
    // ProtoVessel lifecycle or any 5b pixel. The pure span-clock math is locked headlessly in ChainSamplerTests;
    // this drives the SAME ChainSampler.Sample entry the production spine inlines over a real overlap unit.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics) so it runs alongside the other spine in-game
    // tests on a cold launch pad. It builds a PhaseChain + LoopUnitSet and samples them; fast (void, no ghost /
    // scene / save). FLIGHT only; career-independent; self-contained (no live mission / active-vessel data).
    public class OverlapNInstanceSpineInGameTest
    {
        private const string KerbinBodyName = "Kerbin";

        // Fixture mirrors ChainSamplerTests.cs:72 (OverlapUnit) - a single-member SELF-OVERLAP unit: span
        // [100,200], CadenceSeconds == span (one span instance) but OverlapCadenceSeconds < span (so it IS
        // classified overlap). The member window is the full span. This is exactly what a Kerbin launch-to-orbit
        // mission looped shorter than its length produces on the map.
        private const int MemberIdx = 0;
        private const double SpanStart = 100.0;
        private const double SpanEnd = 200.0;
        private const double Cadence = 100.0;        // == span -> one span instance
        private const double OverlapCadence = 25.0;  // < span -> overlap classified
        private const double Anchor = 100.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 11 N1 overlap N-instance (spine): a self-overlap member sampled across >=2 "
                + "overlap cycles resolves a DISTINCT selected-cycle head-UT per cycle, stays InSegment across "
                + "the span head, and is never spuriously Hidden mid-cycle (ONE ghost on the span head, never N "
                + "instances). Asserts the spine DECISION + legacy-head parity, not the ProtoVessel lifecycle.")]
        public void OverlapMember_AcrossCycles_DistinctHeads_AlwaysVisible_NeverBlink()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            GhostPlaybackLogic.LoopUnitSet units = BuildOverlapUnit();
            PhaseChain chain = BuildOverlapMemberChain();

            // liveUTs spanning >= 2 overlap cycles (overlap cadence 25, so cycles tick every 25s). Across
            // elapsed 10..280 the unit cycle advances 0 -> 2 on the span-raised CadenceSeconds (100), with
            // phaseInCycle landing the selected-cycle head at distinct loopUTs in [100,200]. These mirror the
            // ChainSamplerTests cross-cycle cases (110/225/380) plus extra in-cycle samples to prove no blink.
            double[] liveUTs = { 110.0, 150.0, 190.0, 225.0, 260.0, 290.0, 380.0, 420.0 };

            var headsByCycleSlot = new HashSet<long>();   // distinct selected-cycle head buckets observed
            var distinctHeads = new HashSet<double>();
            GhostRenderIntent prior = GhostRenderIntent.Hidden();
            bool everHiddenAfterVisible = false;
            bool everVisible = false;
            int sampleCount = 0;
            int distinctCycleCount = 0;

            for (int k = 0; k < liveUTs.Length; k++)
            {
                double liveUT = liveUTs[k];

                // PARITY ANCHOR: the legacy single-head UT the sampler must glue to.
                double expectedHead = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                    MemberIdx, SpanStart, SpanEnd, liveUT, units, out bool legacyHidden);

                GhostSample sample = ChainSampler.Sample(chain, liveUT, units);
                GhostRenderIntent intent = GhostRenderDirector.Decide(sample, prior, "overlap-ghost");

                // The selected cycle index on the span-raised cadence (NOT the overlap cadence): identifies
                // which span instance the head rides. Distinct cycle slots prove the head advances.
                long cycleSlot = (long)System.Math.Floor((liveUT - Anchor) / Cadence);
                if (headsByCycleSlot.Add(cycleSlot))
                    distinctCycleCount++;

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "OverlapNInstance: liveUT={0:F1} cycleSlot={1} cov={2} vis={3} driveUT={4:F3} "
                    + "expectedHead={5:F3} legacyHidden={6}",
                    liveUT, cycleSlot, sample.Coverage, intent.Visible, sample.DriveUT, expectedHead,
                    legacyHidden));

                // The selected cycle is always inside the member window in this fixture -> never legacy-hidden.
                InGameAssert.IsFalse(legacyHidden,
                    string.Format(CultureInfo.InvariantCulture,
                        "the overlap member's selected cycle must be inside its window at liveUT={0:F1} (else "
                        + "the fixture is mis-built and the visibility assertions are vacuous)", liveUT));

                // Spine-vs-legacy parity: the sampler lands on the SAME selected-cycle head the legacy single
                // head uses (one ghost on the map, not N enumerated instances).
                InGameAssert.AreEqual(Coverage.InSegment, sample.Coverage,
                    string.Format(CultureInfo.InvariantCulture,
                        "the overlap member must classify InSegment at liveUT={0:F1} (the one-segment chain "
                        + "covers the whole member window, so any in-window selected head is InSegment)", liveUT));
                InGameAssert.ApproxEqual(expectedHead, sample.DriveUT, 1e-3,
                    string.Format(CultureInfo.InvariantCulture,
                        "the spine's DriveUT must equal the legacy single-head UT at liveUT={0:F1} (ResolveTrackin"
                        + "gStationSampleUT) - the map ghost rides ONE span instance, never enumerating N", liveUT));

                // Visibility / no-blink: once visible, never spuriously Hidden mid-cycle.
                InGameAssert.IsTrue(intent.Visible,
                    string.Format(CultureInfo.InvariantCulture,
                        "the overlap ghost must be VISIBLE at every in-window liveUT={0:F1} (never blink Hidden "
                        + "mid-cycle - the looped overlap no-blink invariant)", liveUT));
                if (everVisible && !intent.Visible)
                    everHiddenAfterVisible = true;
                if (intent.Visible)
                    everVisible = true;

                distinctHeads.Add(System.Math.Round(sample.DriveUT, 3));
                prior = intent;
                sampleCount++;
            }

            // The selected-cycle head must be DISTINCT across cycles: the fixture spans >= 2 span-cadence
            // cycles, and the head advances as the cycle advances (a frozen head, or N-at-once enumeration,
            // would collapse these).
            InGameAssert.IsTrue(distinctCycleCount >= 2,
                string.Format(CultureInfo.InvariantCulture,
                    "the sampled span must cross >= 2 overlap cycles (saw {0}) so the distinct-head assertion is "
                    + "non-vacuous", distinctCycleCount));
            InGameAssert.IsTrue(distinctHeads.Count >= 2,
                string.Format(CultureInfo.InvariantCulture,
                    "the spine must resolve >= 2 DISTINCT selected-cycle head-UTs across the span (saw {0}); a "
                    + "regression that froze the head on one cycle would collapse these to 1", distinctHeads.Count));
            InGameAssert.IsFalse(everHiddenAfterVisible,
                "across the overlap span the ghost must NEVER blink Hidden once it has become visible - the "
                + "looped overlap no-blink invariant");

            ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                "OverlapNInstance SUMMARY: samples={0} distinctCycles={1} distinctHeads={2} everHiddenAfterVis={3}",
                sampleCount, distinctCycleCount, distinctHeads.Count, everHiddenAfterVisible));
        }

        // Copied verbatim from ChainSamplerTests.cs:72 (OverlapUnit) minus the xUnit Assert (replaced by an
        // in-game guard): a single-member SELF-OVERLAP unit. The two-arg overlap LoopUnit ctor takes the
        // overlapCadenceSeconds positionally; UnitMemberOverlaps must be true (else the test is moot).
        private static GhostPlaybackLogic.LoopUnitSet BuildOverlapUnit()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                MemberIdx, new[] { MemberIdx }, SpanStart, SpanEnd, cadenceSeconds: Cadence,
                phaseAnchorUT: Anchor, overlapCadenceSeconds: OverlapCadence);
            InGameAssert.IsTrue(GhostPlaybackLogic.UnitMemberOverlaps(unit),
                "the N1 fixture unit MUST be an overlap unit (OverlapCadenceSeconds < span) or the overlap "
                + "matrix it covers is not exercised");
            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { MemberIdx, unit } };
            var ownerByIndex = new Dictionary<int, int> { { MemberIdx, MemberIdx } };
            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        // One StockConic phase spanning the whole member window so any in-window selected-cycle head-UT lands
        // InSegment and its DriveUT is observable (mirrors ChainSamplerTests.OverlapMemberChain).
        private static PhaseChain BuildOverlapMemberChain()
        {
            var anchor = new AnchorFrame.BodyAnchor(KerbinBodyName);
            var phase = new DepartureLoiterPhase(
                new PhaseId("rec-overlap", 0, 0), SegmentProvenance.Recorded, anchor, SpanStart, SpanEnd,
                new OrbitSegment
                {
                    startUT = SpanStart, endUT = SpanEnd, bodyName = KerbinBodyName,
                    semiMajorAxis = 850000.0, eccentricity = 0.0, epoch = SpanStart,
                });
            return new PhaseChain(
                "rec-overlap", committedIndex: MemberIdx, instanceKey: 0,
                phases: new List<TrajectoryPhase> { phase }, windowStartUt: SpanStart, windowEndUt: SpanEnd);
        }
    }
}
