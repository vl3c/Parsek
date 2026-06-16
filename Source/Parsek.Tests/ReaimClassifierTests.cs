using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 2b of re-aim: the pure recording -> phase-model classifier. Synthetic OrbitSegment chains
    // + a fake IBodyInfo cover the supported single-hop case and every bail path (same-parent, no
    // heliocentric leg, deep chain, multi-hop, no parking orbit, no arrival). Each test states the
    // regression it guards.
    public class ReaimClassifierTests
    {
        // Minimal IBodyInfo: only ReferenceBodyName matters to the classifier (it reuses AncestorChain
        // / TryFindCommonAncestor). Stock-like Kerbol parents incl. Duna's moon Ike + a Jool moon.
        private sealed class Bodies : IBodyInfo
        {
            public readonly Dictionary<string, string> Parent = new Dictionary<string, string>();
            public readonly Dictionary<string, double> Mu = new Dictionary<string, double>();
            // Orbit period about the reference body, in seconds (for the heliocentric-parking
            // admissibility gate, which derives the launch body's heliocentric sma via Kepler's 3rd law).
            public readonly Dictionary<string, double> Orbit = new Dictionary<string, double>();
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => Orbit.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public string ReferenceBodyName(string b) => Parent.TryGetValue(b ?? "", out var v) ? v : null;
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b) => Mu.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid, out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        private static Bodies StockParents()
        {
            var f = new Bodies();
            f.Parent["Sun"] = null;
            f.Parent["Kerbin"] = "Sun";
            f.Parent["Mun"] = "Kerbin";
            f.Parent["Duna"] = "Sun";
            f.Parent["Ike"] = "Duna";
            f.Parent["Jool"] = "Sun";
            f.Parent["Tylo"] = "Jool";
            // Gravitational parameters (stock), for loiter-period computation in the classifier rework.
            f.Mu["Kerbin"] = 3.5316e12;
            f.Mu["Mun"] = 6.5138398e10;
            f.Mu["Duna"] = 3.0136321e11;
            f.Mu["Sun"] = 1.1723328e18;
            // Stock Kerbin orbit period about the Sun (heliocentric sma 13,599,840,256 m); the
            // admissibility gate reads this to bound a heliocentric park to the launch body's own orbit.
            f.Orbit["Kerbin"] = 9203544.6;
            return f;
        }

        private static OrbitSegment Seg(string body, double start, double end, bool predicted = false)
        {
            return new OrbitSegment { bodyName = body, startUT = start, endUT = end, isPredicted = predicted };
        }

        [Fact]
        public void Classify_KerbinToDuna_Supported_IdentifiesPhases()
        {
            // Guards the core single-hop case: Kerbin parking -> Sun coast -> Duna arrival.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),   // parking
                Seg("Sun", 5000, 1000000),  // heliocentric transfer (S2)
                Seg("Duna", 1000000, 1005000), // arrival (S3)
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());

            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Kerbin", plan.LaunchBody);
            Assert.Equal("Sun", plan.CommonAncestor);
            Assert.Equal("Duna", plan.TargetBody);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3);
            Assert.Equal(1000000.0, plan.RecordedArrivalUT, 3);
            Assert.Equal(995000.0, plan.RecordedTransferTofSeconds, 3);
            Assert.Equal("Kerbin", plan.ParkingOrbit.bodyName);
            Assert.Equal("Sun", plan.HeliocentricLeg.bodyName);
            Assert.Equal("Duna", plan.ArrivalLeg.bodyName);
        }

        [Fact]
        public void Classify_KerbinToDuna_WithMidCourseCorrection_SpansFullTransfer()
        {
            // A mid-course correction burn splits the heliocentric coast into two Sun-bodied segments
            // (coast1 / [burn gap] / coast2). The transfer time must span the FULL departure->arrival
            // window (5000 -> 1000000), not just the pre-correction coast (5000 -> 400000) - otherwise
            // re-aim would synthesize a wrong, too-short transfer. coast2 (between the heliocentric leg
            // and Duna) is correctly skipped by the arrival search, and the multi-hop guard (which only
            // looks AFTER the arrival) does not fire.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),       // parking
                Seg("Sun", 5000, 400000),       // coast 1 (pre-correction)
                Seg("Sun", 400500, 1000000),    // coast 2 (post-correction; 500s burn gap)
                Seg("Duna", 1000000, 1005000),  // arrival
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());

            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Duna", plan.TargetBody);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3);
            Assert.Equal(1000000.0, plan.RecordedArrivalUT, 3);          // Duna SOI entry, not coast1 end
            Assert.Equal(995000.0, plan.RecordedTransferTofSeconds, 3);  // FULL transfer span
        }

        private static OrbitSegment SegA(string body, double start, double end, double a)
        {
            return new OrbitSegment { bodyName = body, startUT = start, endUT = end, semiMajorAxis = a };
        }

        [Fact]
        public void Classify_SlowMidCourseCorrection_Declined()
        {
            // A mid-course correction: the transfer coasts, the engines fire (the optimizer splits at the
            // ExoPropulsive boundary), and the post-burn orbit differs in sma. The pre-burn coast precedes
            // the transfer run with a WIDE gap (a long coast, then a late burn). The re-aim Lambert assumes
            // departure from the launch body, so a partial (post-MCC) transfer must decline REGARDLESS of
            // the gap width (the old gap<=tolerance decline missed a slow MCC).
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),       // launch parking
                SegA("Sun", 5000, 5000000, 1.6e10),      // pre-MCC heliocentric coast (sma 1.6e10)
                SegA("Sun", 5050000, 9000000, 1.8e10),   // post-MCC coast (+12.5% sma, a real burn);
                                                         //   50000 s gap (a slow MCC, >> burn tolerance)
                SegA("Duna", 9000000, 9005000, 500000),  // arrival
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("mid-course correction", plan.Reason);
        }

        [Fact]
        public void Classify_WarpedTransferSplitByCheckpoints_MeasuresFullTof()
        {
            // A long heliocentric transfer warped at max rate: the optimizer splits it into checkpoint
            // coasts with WIDE gaps but the SAME orbit (no burn, identical sma). They must merge into one
            // transfer run so the FULL tof is measured (not just the last checkpoint), since same-orbit
            // sampling gaps are not maneuvers.
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),       // launch parking
                SegA("Sun", 5000, 3000000, 1.7e10),      // transfer checkpoint 1
                SegA("Sun", 3050000, 6000000, 1.7e10),   // checkpoint 2 (50000 s warp gap, SAME orbit)
                SegA("Sun", 6050000, 9000000, 1.7e10),   // checkpoint 3 (50000 s warp gap, SAME orbit)
                SegA("Duna", 9000000, 9005000, 500000),  // arrival
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3);           // the FULL transfer start (cp 1)
            Assert.Equal(8995000.0, plan.RecordedTransferTofSeconds, 3); // 9000000 - 5000
        }

        [Fact]
        public void Classify_InterloperParkedInLaunchOrbitDuringTransfer_FlattenedBreaks_PerMemberCorrect()
        {
            // Regression (playtest 2026-05-30, save s15 'Duna One'): a SECOND member parked in a
            // launch-body orbit DURING the transfer (a station / a jettisoned stage left in LKO)
            // interleaves its segments with the transfer's heliocentric coasts in the flattened
            // multi-member gather. The classifier's backward walk from the target SOI coast then hits an
            // interleaved launch-body segment and STOPS, collapsing the transfer to the last coast alone
            // (a too-short tof -> a bogus re-aimed geometry; in game tof fell from ~79d to ~35d and the
            // ghost aimed where Duna was not). The builder now classifies PER-MEMBER (each member's own
            // segments), so the parked station never pollutes the transfer member. This pins both halves.
            var transferOnly = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 1000, 700000),  // launch parking
                SegA("Sun", 1000, 2000, 1.7e10),    // transfer coast 1
                SegA("Sun", 2000, 3000, 1.7e10),    // transfer coast 2 (mid-course)
                SegA("Sun", 3000, 4000, 1.7e10),    // transfer coast 3 (ends at the Duna SOI entry)
                SegA("Duna", 4000, 5000, 500000),   // arrival
            };
            var soloPlan = ReaimClassifier.Classify(transferOnly, StockParents());
            Assert.True(soloPlan.Supported, soloPlan.Reason);
            Assert.Equal(1000.0, soloPlan.RecordedDepartureUT, 3);          // full transfer run start
            Assert.Equal(3000.0, soloPlan.RecordedTransferTofSeconds, 3);   // the true ~3000s transfer

            // The same transfer with a parked launch-body-orbit member flattened in, overlapping the
            // transfer UT (the station). Its Kerbin segments interleave between the Sun coasts.
            var flattened = new List<OrbitSegment>(transferOnly)
            {
                SegA("Kerbin", 1500, 2500, 2360000),
                SegA("Kerbin", 2500, 3500, 2360000),
                SegA("Kerbin", 3500, 3999, 2360000),
            };
            var brokenPlan = ReaimClassifier.Classify(flattened, StockParents());
            // The interloper Kerbin segment immediately before the last Sun coast breaks the backward
            // walk: the transfer collapses to the last coast alone (tof 1000), NOT the true 3000. This is
            // precisely why the builder must classify per-member, not on the flattened gather.
            Assert.Equal(3000.0, brokenPlan.RecordedDepartureUT, 3);        // only the last coast
            Assert.Equal(1000.0, brokenPlan.RecordedTransferTofSeconds, 3); // the broken fragment
        }

        [Fact]
        public void Classify_KerbinLoiterBeforeDirectTransfer_DepartsAtTransferStart()
        {
            // A long Kerbin LKO loiter (Kerbin-bodied) then a DIRECT transfer (Sun) to Duna. The Kerbin
            // loiter must NOT affect transfer detection: RecordedDepartureUT = the Sun transfer start
            // (the launch-body SOI exit), not the loiter start.
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 1000000, 700000),    // long LKO loiter (Kerbin-bodied)
                SegA("Sun", 1000000, 2000000, 1.7e10),   // transfer
                SegA("Duna", 2000000, 2005000, 500000),  // arrival
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal(1000000.0, plan.RecordedDepartureUT, 3); // transfer start, not the loiter start
            Assert.Equal(2000000.0, plan.RecordedArrivalUT, 3);
        }

        [Fact]
        public void Classify_RealSmaSingleTransfer_DepartsAtTransferStart()
        {
            // Real sma on every segment (not the sma=0 the older tests use, which zeroes the a-step). A
            // plain Kerbin->Duna with a Kerbin parking + a single Sun transfer arc must classify with
            // RecordedDepartureUT = the transfer start (byte-identical to the pre-rework result).
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),
                SegA("Sun", 5000, 5000000, 1.7e10),       // transfer (~0.4 rev, < 1 rev)
                SegA("Duna", 5000000, 5005000, 500000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3);
            Assert.Equal(5000000.0, plan.RecordedArrivalUT, 3);
            Assert.Equal(4995000.0, plan.RecordedTransferTofSeconds, 3);
        }

        [Fact]
        public void Classify_HeliocentricDebrisAfterDeparture_DoesNotCorruptTransfer()
        {
            // A jettisoned transfer stage coasts heliocentrically AFTER departure (similar sma, does NOT
            // reach Duna), interleaved in the flattened multi-member list. The SOI-contiguity anchor
            // picks the REAL transfer coast (the one ending at the Duna SOI entry); the debris (higher
            // startUT, not contiguous with the arrival) is not pulled into the transfer run (review C1).
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),
                SegA("Sun", 5000, 5000000, 1.7e10),       // the real transfer (ends at the Duna SOI)
                SegA("Sun", 100000, 4000000, 1.69e10),    // jettisoned stage (similar sma, ends mid-transfer)
                SegA("Duna", 5000000, 5005000, 500000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3); // the transfer start, NOT the debris start
            Assert.Equal(4995000.0, plan.RecordedTransferTofSeconds, 3);
        }

        [Fact]
        public void Classify_ChunkedSameSmaHeliocentricLoiter_Declined()
        {
            // A heliocentric loiter on the SAME sma as the transfer (no a-step to separate them) absorbed
            // into the Sun run, making the run span > 1 revolution -> declined (review M1: the a-step
            // alone would miss a same-sma loiter; the >1-rev duration gate catches it).
            // Sun period at sma 1.7e10 ~ 1.286e7 s; ~1 rev loiter + ~0.5 rev transfer -> ~1.5 rev run.
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),
                SegA("Sun", 5000, 13000000, 1.7e10),        // heliocentric loiter (~1 rev, same sma)
                SegA("Sun", 13000000, 20000000, 1.7e10),    // transfer (same sma, ends at Duna SOI)
                SegA("Duna", 20000000, 20005000, 500000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains(">1 revolution", plan.Reason);
        }

        [Fact]
        public void Classify_HeliocentricLoiterBeforeTransfer_Declined()
        {
            // Kerbin parking -> heliocentric loiter (a closed Sun orbit) -> transfer burn (a-step) ->
            // transfer (Sun, ending at Duna) -> Duna. The transfer departs from a SOLAR parking orbit, so
            // re-aim's Lambert (r1 = launch body) would mis-aim -> declined to faithful. Crucially the
            // (different-SMA) heliocentric loiter does NOT become the transfer departure.
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),
                SegA("Sun", 5000, 500000, 2.5e10),       // heliocentric loiter (closed, larger SMA)
                SegA("Sun", 500000, 1000000, 1.7e10),    // transfer (SMA step at the departure burn)
                SegA("Duna", 1000000, 1005000, 500000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric parking", plan.Reason);
        }

        [Fact]
        public void Classify_SameParentMun_NotSupported_StaysFaithful()
        {
            // Guards: a same-parent target (Mun orbits Kerbin) has no heliocentric leg -> re-aim does
            // NOT engage; the mission stays on the faithful path.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Mun", 5000, 8000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric", plan.Reason);
        }

        [Fact]
        public void Classify_NoHeliocentricLeg_NotSupported()
        {
            // Guards C1/A/m4: a Kerbin-only chain (never warped through a Sun coast) -> no S2 -> bail.
            var segs = new List<OrbitSegment> { Seg("Kerbin", 100, 5000) };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric", plan.Reason);
        }

        [Fact]
        public void Classify_DeepTargetIke_NotSupported_Deferred()
        {
            // Guards: Ike is a moon of Duna (not a direct child of the Sun) -> deep/multi-hop, deferred.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Sun", 5000, 1000000),
                Seg("Duna", 1000000, 1002000),
                Seg("Ike", 1002000, 1003000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            // The first arrival after the Sun coast is Duna (a direct child) -> Duna is the target,
            // and Ike is just a later same-system leg; this is still single-hop to Duna. Supported.
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Duna", plan.TargetBody);
        }

        [Fact]
        public void Classify_MultiHop_TwoHeliocentricLegs_NotSupported()
        {
            // Guards: Kerbin -> Sun -> Duna -> Sun -> Jool (two heliocentric legs) is multi-hop -> bail.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Sun", 5000, 1000000),
                Seg("Duna", 1000000, 1002000),
                Seg("Sun", 1002000, 5000000),
                Seg("Jool", 5000000, 5005000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("multi-hop", plan.Reason);
        }

        [Fact]
        public void Classify_StartsHeliocentric_NotSupported()
        {
            // Guards: a recording that starts already in solar orbit makes the launch body the Sun,
            // which has no ancestor leg -> not a re-aim mission (no launch-body parking orbit to depart
            // from). Bails (stays faithful).
            var segs = new List<OrbitSegment>
            {
                Seg("Sun", 5000, 1000000),
                Seg("Duna", 1000000, 1005000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
        }

        [Fact]
        public void Classify_NoArrivalLeg_NotSupported()
        {
            // Guards: a heliocentric coast that never reaches a target body (e.g. recording ended in
            // solar orbit) -> bail.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Sun", 5000, 1000000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("arrival", plan.Reason);
        }

        [Fact]
        public void Classify_PredictedSegments_Ignored()
        {
            // Guards: a predicted (extrapolated) Sun/Duna tail is NOT a recorded leg -> if the only
            // heliocentric/arrival legs are predicted, re-aim does not engage.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Sun", 5000, 1000000, predicted: true),
                Seg("Duna", 1000000, 1005000, predicted: true),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
        }

        [Fact]
        public void Classify_NullInputs_NotSupported()
        {
            Assert.False(ReaimClassifier.Classify(null, StockParents()).Supported);
            Assert.False(ReaimClassifier.Classify(new List<OrbitSegment>(), StockParents()).Supported);
            Assert.False(ReaimClassifier.Classify(new List<OrbitSegment> { Seg("Kerbin", 0, 1) }, null).Supported);
        }

        // ---- Heliocentric-parking (two-burn) departure: s15 Kerbal X #2 ----
        // The transfer departs from a near-circular SOLAR parking orbit co-orbital with Kerbin (the player
        // escaped into ~Kerbin's own solar orbit, phased there ~1.4 revolutions, then burned for Duna).
        // This used to decline as "transfer departs from a heliocentric parking orbit"; it now engages.

        private static OrbitSegment SegAE(string body, double start, double end, double a, double ecc)
        {
            return new OrbitSegment
            {
                bodyName = body, startUT = start, endUT = end, semiMajorAxis = a, eccentricity = ecc
            };
        }

        // The s15 Kerbal X #2 transfer member, mirrored: Kerbin LKO -> 3 same-orbit solar park sub-coasts
        // (sma 1.4072e10 ~ Kerbin's 1.36e10, ecc 0.0327, ~1.4 rev total) -> trans-Duna burn (sma 1.7909e10,
        // ~0.68 rev) ending at the Duna SOI -> Duna hyperbola. Sun period at the park sma ~9.69e6 s.
        private static List<OrbitSegment> S15ParkingDepartureChain()
        {
            return new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 1.37e6, 0.0),         // LKO parking
                SegAE("Sun", 5000, 9000000, 1.4072e10, 0.0327),  // solar park sub-coast 1
                SegAE("Sun", 9000006, 13000000, 1.4072e10, 0.0327), // sub-coast 2 (6 s warp gap, same orbit)
                SegAE("Sun", 13000036, 13560000, 1.4072e10, 0.0327),// sub-coast 3 (36 s gap, same orbit)
                SegAE("Sun", 13560000, 22944036, 1.7909e10, 0.192), // trans-Duna transfer (ends at Duna SOI)
                SegAE("Duna", 22944036, 22949036, -1.88e5, 3.60),   // arrival hyperbola
            };
        }

        [Fact]
        public void Classify_HeliocentricParkingDeparture_Engaged()
        {
            // s15 Kerbal X #2: a >=1-rev near-circular solar park co-orbital with Kerbin is now re-aimable.
            // RecordedDepartureUT is the trans-Duna BURN (the transfer-coast start), NOT the park start, so
            // the tof excludes the ~1.4-rev park (the park is re-timed by the loiter cut, not Lambert'd).
            var plan = ReaimClassifier.Classify(S15ParkingDepartureChain(), StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Kerbin", plan.LaunchBody);
            Assert.Equal("Sun", plan.CommonAncestor);
            Assert.Equal("Duna", plan.TargetBody);
            Assert.True(plan.DepartedFromHeliocentricPark);
            Assert.Equal(13560000.0, plan.RecordedDepartureUT, 3);          // the trans-Duna burn, not the park
            Assert.Equal(22944036.0, plan.RecordedArrivalUT, 3);
            Assert.Equal(9384036.0, plan.RecordedTransferTofSeconds, 3);    // transfer leg only (no park)
        }

        [Fact]
        public void Classify_MultiRevPark_Declined_DeferredScope()
        {
            // A >1-rev (here ~2.6 rev) solar park would fire the launch-side cutBeforeDeparture composition,
            // unvalidated on a heliocentric park -> declined (empty-cut-only scope, Open Q3 2026-06-15).
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 1.37e6, 0.0),
                SegAE("Sun", 5000, 25000000, 1.4072e10, 0.0327),    // ~2.58 rev solar park (wholeRevs=2)
                SegAE("Sun", 25000000, 34384036, 1.7909e10, 0.192), // transfer
                SegAE("Duna", 34384036, 34389036, -1.88e5, 3.60),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric parking", plan.Reason);
        }

        [Fact]
        public void Classify_EccentricPark_FailsAdmissibility_Declined()
        {
            // A >=1-rev park co-orbital in sma but ECCENTRIC (ecc 0.5 > 0.1): its burn point is far from
            // the launch body, so r1 = launchBody is invalid -> declined (fail-closed, the graft).
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 1.37e6, 0.0),
                SegAE("Sun", 5000, 13560000, 1.4072e10, 0.5),       // 1-rev but eccentric
                SegAE("Sun", 13560000, 22944036, 1.7909e10, 0.192),
                SegAE("Duna", 22944036, 22949036, -1.88e5, 3.60),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric parking", plan.Reason);
        }

        [Fact]
        public void Classify_WideSmaPark_FailsAdmissibility_Declined()
        {
            // A >=1-rev near-circular park NOT co-orbital with the launch body (sma 2.0e10, ~47% off
            // Kerbin's 1.36e10): the burn point is far from the launch body -> declined (fail-closed).
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 1.37e6, 0.0),
                SegAE("Sun", 5000, 25000000, 2.0e10, 0.0327),       // 1-rev but a different solar orbit
                SegAE("Sun", 25000000, 34384036, 1.7909e10, 0.192),
                SegAE("Duna", 34384036, 34389036, -1.88e5, 3.60),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric parking", plan.Reason);
        }

        [Fact]
        public void Classify_TwoSolarParks_EarlierMultiRev_Declined()
        {
            // An admissible ~1-rev co-orbital park (the transfer's immediate predecessor) preceded by a
            // DISTINCT earlier multi-rev solar park on a different orbit. ComputeCuts would excise whole
            // periods from the earlier park (firing the unvalidated heliocentric cutBeforeDeparture), so the
            // empty-cut scope must DECLINE even though the immediate predecessor alone looks admissible.
            // Pins the all-common-ancestor-runs scan (not just the predecessor run).
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 1.37e6, 0.0),
                SegAE("Sun", 5000, 25000000, 1.0e10, 0.03),          // parkA: ~4.3 rev, different solar orbit
                SegAE("Sun", 25000000, 38560000, 1.4072e10, 0.0327), // parkB: ~1.4 rev, co-orbital (admissible alone)
                SegAE("Sun", 38560000, 47944036, 1.7909e10, 0.192),  // transfer
                SegAE("Duna", 47944036, 47949036, -1.88e5, 3.60),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric parking", plan.Reason);
        }

        [Fact]
        public void Classify_ParkEccentricAtBurnPoint_Declined()
        {
            // A same-sma solar park run near-circular at the START but ECCENTRIC at the burn point
            // (DetectRuns merges on sma only, not ecc). The departure orbit r1 approximates is the burn
            // point, so admissibility reads the LAST park segment's ecc, not just the run anchor's.
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 1.37e6, 0.0),
                SegAE("Sun", 5000, 9000000, 1.4072e10, 0.0327),      // circular first sub-coast (anchor)
                SegAE("Sun", 9000006, 13560000, 1.4072e10, 0.6),     // SAME sma, eccentric burn point
                SegAE("Sun", 13560000, 22944036, 1.7909e10, 0.192),  // transfer
                SegAE("Duna", 22944036, 22949036, -1.88e5, 3.60),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric parking", plan.Reason);
        }

        [Fact]
        public void IsHeliocentricParkingDeparture_OneRevCoOrbitalPark_True()
        {
            var segs = new List<OrbitSegment>
            {
                SegAE("Sun", 0, 13560000, 1.4072e10, 0.0327),       // ~1.4-rev co-orbital near-circular park
                SegAE("Sun", 13560000, 22944036, 1.7909e10, 0.192), // transfer
            };
            Assert.True(ReaimClassifier.IsHeliocentricParkingDeparture(segs, 1, "Kerbin", "Sun", StockParents()));
        }

        [Fact]
        public void IsHeliocentricParkingDeparture_SubPeriodPark_False()
        {
            // Park < 1 full solar revolution = a real mid-course-correction arc, not a closed park.
            var segs = new List<OrbitSegment>
            {
                SegAE("Sun", 0, 5000000, 1.4072e10, 0.0327),        // ~0.52 rev (sub-period)
                SegAE("Sun", 5000000, 14384036, 1.7909e10, 0.192),
            };
            Assert.False(ReaimClassifier.IsHeliocentricParkingDeparture(segs, 1, "Kerbin", "Sun", StockParents()));
        }

        [Fact]
        public void IsHeliocentricParkingDeparture_MultiRevPark_False()
        {
            var segs = new List<OrbitSegment>
            {
                SegAE("Sun", 0, 25000000, 1.4072e10, 0.0327),       // ~2.58 rev (cut would be non-empty)
                SegAE("Sun", 25000000, 34384036, 1.7909e10, 0.192),
            };
            Assert.False(ReaimClassifier.IsHeliocentricParkingDeparture(segs, 1, "Kerbin", "Sun", StockParents()));
        }

        [Fact]
        public void IsHeliocentricParkingDeparture_NonSunPredecessor_False()
        {
            // The transfer's predecessor is the launch body (a direct departure), not a solar park.
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 0, 5000, 700000, 0.0),
                SegAE("Sun", 5000, 14384036, 1.7909e10, 0.192),
            };
            Assert.False(ReaimClassifier.IsHeliocentricParkingDeparture(segs, 1, "Kerbin", "Sun", StockParents()));
        }

        [Fact]
        public void IsHeliocentricParkingDeparture_NoLaunchBodyOrbitPeriod_FailsClosed()
        {
            // Without the launch body's orbit period the admissibility gate cannot establish co-orbitality
            // -> fail closed (false), even for an otherwise-admissible 1-rev near-circular park.
            var noOrbit = new Bodies();
            noOrbit.Parent["Sun"] = null;
            noOrbit.Parent["Kerbin"] = "Sun";
            noOrbit.Mu["Sun"] = 1.1723328e18;
            noOrbit.Mu["Kerbin"] = 3.5316e12;            // note: NO Orbit["Kerbin"]
            var segs = new List<OrbitSegment>
            {
                SegAE("Sun", 0, 13560000, 1.4072e10, 0.0327),
                SegAE("Sun", 13560000, 22944036, 1.7909e10, 0.192),
            };
            Assert.False(ReaimClassifier.IsHeliocentricParkingDeparture(segs, 1, "Kerbin", "Sun", noOrbit));
        }

        [Fact]
        public void HeliocentricSemiMajorAxis_Kerbin_RecoversStockSma()
        {
            // Kepler's 3rd law round-trip: stock Kerbin orbit period -> sma ~13.6e9 m.
            double a = ReaimClassifier.HeliocentricSemiMajorAxis("Kerbin", "Sun", StockParents());
            Assert.InRange(a, 1.358e10, 1.362e10);
        }

        // =====================================================================================
        // P2 chain-synthesis index spans + CaptureSynthesizable (reaim-fix-plan.md BLOCKER 1, #3/#4)
        // =====================================================================================

        [Fact]
        public void Classify_SimpleTransfer_IndexSpans_SelectParkingEscapeTransferCapture()
        {
            // The clean 3-leg [Kerbin parking, Sun transfer, Duna capture]: the launch-body predecessor of
            // the heliocentric leg IS the parking orbit (no separate escape hyperbola), so the escape run is
            // that single segment (EscapeRunIsParkingOnly). The transfer run is the single Sun coast; the
            // first capture is the Duna leg.
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 700000, 0.0),       // 0: parking (also the degenerate escape run)
                SegAE("Sun", 5000, 1000000, 1.7e10, 0.2),      // 1: transfer
                SegAE("Duna", 1000000, 1005000, -1.88e5, 3.6), // 2: capture hyperbola
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);

            Assert.Equal(0, plan.ParkingIndex);
            Assert.Equal(0, plan.EscapeRunStartIndex);
            Assert.Equal(0, plan.EscapeRunEndIndex);
            Assert.True(plan.EscapeRunIsParkingOnly,
                "a direct SOI exit (no recorded escape hyperbola) has the parking orbit as the only launch-body leg");

            Assert.Equal(1, plan.TransferRunStartIndex);
            Assert.Equal(1, plan.TransferRunEndIndex);

            Assert.Equal(2, plan.FirstCaptureIndex);
            Assert.Equal(1000000.0, plan.FirstCaptureStartUT, 3);
            Assert.Equal(1005000.0, plan.FirstCaptureEndUT, 3);
        }

        [Fact]
        public void Classify_EscapeHyperbolaBeforeTransfer_EscapeRunIsHyperbola_NotParking()
        {
            // The REAL Duna One shape (minus the Ike thread): a circular parking orbit, then a Kerbin ESCAPE
            // HYPERBOLA (sma<0), then the Sun transfer, then the Duna capture. The escape run must select the
            // HYPERBOLA (index 1), the parking orbit (index 0) is kept separate, and EscapeRunIsParkingOnly
            // is false (there is a real escape leg to synthesize).
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 700000, 0.0),         // 0: circular parking
                SegAE("Kerbin", 5000, 6000, -3.8e6, 1.19),       // 1: escape hyperbola
                SegAE("Sun", 6000, 1000000, 1.76e10, 0.21),      // 2: transfer
                SegAE("Duna", 1000000, 1005000, -5.6e5, 1.05),   // 3: capture hyperbola
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);

            Assert.Equal(0, plan.ParkingIndex);
            Assert.Equal(1, plan.EscapeRunStartIndex);
            Assert.Equal(1, plan.EscapeRunEndIndex);
            Assert.False(plan.EscapeRunIsParkingOnly);

            Assert.Equal(2, plan.TransferRunStartIndex);
            Assert.Equal(2, plan.TransferRunEndIndex);
            Assert.Equal(3, plan.FirstCaptureIndex);
            Assert.True(plan.CaptureSynthesizable, plan.CaptureSynthesizableReason);
        }

        [Fact]
        public void Classify_TransferRunSpansMultipleCoasts_IndexSpanCoversAll()
        {
            // A mid-course correction splits the transfer into two Sun coasts. The transfer run span must
            // cover BOTH (start at the first coast, end at the last that hands off to the capture).
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 700000, 0.0),        // 0: parking
                SegAE("Sun", 5000, 400000, 1.7e10, 0.2),        // 1: coast 1
                SegAE("Sun", 400500, 1000000, 1.7e10, 0.2),     // 2: coast 2 (ends at the Duna SOI)
                SegAE("Duna", 1000000, 1005000, -1.88e5, 3.6),  // 3: capture
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal(1, plan.TransferRunStartIndex);
            Assert.Equal(2, plan.TransferRunEndIndex);
            Assert.Equal(3, plan.FirstCaptureIndex);
        }

        [Fact]
        public void Classify_IkeThreadedArrival_CaptureSynthesizableFalse()
        {
            // The DUNA ONE real topology threads Ike between two Duna capture fragments (seg#12/13 Duna,
            // seg#14/15 Ike, seg#16+ Duna). The capture side MUST fail closed (CaptureSynthesizable=false)
            // so chain synthesis keeps the recorded capture/arrival run verbatim. This is the #3/#4 scope
            // correction's load-bearing detection.
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 700000, 0.0),
                SegAE("Kerbin", 5000, 6000, -3.8e6, 1.19),       // escape
                SegAE("Sun", 6000, 1000000, 1.76e10, 0.21),      // transfer
                SegAE("Duna", 1000000, 1010000, -5.6e5, 1.05),   // capture hyperbola (arrival)
                SegAE("Duna", 1010000, 1020000, -5.6e5, 1.05),   // capture fragment 2
                SegAE("Ike", 1020000, 1021000, -2.9e4, 1.02),    // IKE THREAD
                SegAE("Ike", 1021000, 1022000, -2.9e4, 1.02),
                SegAE("Duna", 1022000, 1023000, -5.3e5, 1.04),   // Duna re-capture
                SegAE("Duna", 1023000, 1024000, 4.9e5, 0.45),    // descent ellipse
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Duna", plan.TargetBody);
            Assert.False(plan.CaptureSynthesizable);
            Assert.Contains("Ike", plan.CaptureSynthesizableReason);
            // The FIRST capture is still the first Duna hyperbola (so the transfer run still ends correctly),
            // even though the capture is NOT synthesized.
            Assert.Equal("Duna", segs[plan.FirstCaptureIndex].bodyName);
            Assert.Equal(1000000.0, plan.FirstCaptureStartUT, 3);
        }

        [Fact]
        public void Classify_DunaOneRealFixture_CaptureSynthesizableFalse_IkeThread()
        {
            // The shared P0 golden-master fixture (the 15-segment Duna One member with the Ike thread).
            // CaptureSynthesizable must be false (Ike thread); the escape run must select the escape
            // hyperbola (index 1, not the index-0 parking); the transfer run spans the three Sun coasts.
            var member = Parsek.Tests.Generators.ReaimChainSynthesisFixtures.BuildDunaOneTransferMember();
            var plan = ReaimClassifier.Classify(member, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Kerbin", plan.LaunchBody);
            Assert.Equal("Duna", plan.TargetBody);

            // index 0 = circular parking, index 1 = escape hyperbola (the run), indices 2..4 = the 3 Sun coasts.
            Assert.Equal(0, plan.ParkingIndex);
            Assert.Equal(1, plan.EscapeRunStartIndex);
            Assert.Equal(1, plan.EscapeRunEndIndex);
            Assert.False(plan.EscapeRunIsParkingOnly);
            Assert.Equal(2, plan.TransferRunStartIndex);
            Assert.Equal(4, plan.TransferRunEndIndex);
            Assert.Equal(5, plan.FirstCaptureIndex); // the first Duna capture hyperbola (seg#12)

            // Recorded-span UT spans (P3 STEP 5: the assembler selects the runs by UT span, not by index).
            // Escape run = seg#8 [63966986, 64044033]; transfer run start = the first Sun coast 64044033
            // (== RecordedDepartureUT); transfer run end = the last Sun coast end 70898646
            // (== RecordedArrivalUT == FirstCaptureStartUT); first capture [70898646, 70912684].
            Assert.Equal(63966986.0, plan.EscapeRunStartUT, 3);
            Assert.Equal(64044033.0, plan.EscapeRunEndUT, 3);
            Assert.Equal(64044033.0, plan.RecordedDepartureUT, 3); // transfer-run start
            Assert.Equal(70898646.0, plan.TransferRunEndUT, 3);
            Assert.Equal(70898646.0, plan.FirstCaptureStartUT, 3);
            Assert.Equal(70912684.0, plan.FirstCaptureEndUT, 3);
            // The escape run END is co-located with the transfer-run START (the SOI-exit handoff), so the
            // assembler's STEP 3 co-location gate accepts the escape side.
            Assert.Equal(plan.EscapeRunEndUT, plan.RecordedDepartureUT, 3);

            Assert.False(plan.CaptureSynthesizable);
            Assert.Contains("Ike", plan.CaptureSynthesizableReason);
        }

        [Fact]
        public void Classify_AtmosphericDirectArrival_CaptureSynthesizableFalse()
        {
            // The first target-bodied leg is an elliptic DESCENDING arc (sma>0, ecc<1), not a capture
            // hyperbola - an atmospheric-direct arrival. No capture hyperbola exists to synthesize, so the
            // capture side fails closed.
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 700000, 0.0),
                SegAE("Sun", 5000, 1000000, 1.7e10, 0.2),
                SegAE("Duna", 1000000, 1005000, 4.9e5, 0.45),   // ELLIPTIC descent, not a hyperbola
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.False(plan.CaptureSynthesizable);
            Assert.Contains("atmospheric-direct", plan.CaptureSynthesizableReason);
        }

        [Fact]
        public void Classify_CleanHyperbolaCapture_CaptureSynthesizableTrue()
        {
            // A clean single-capture arrival (a Duna capture hyperbola, no Ike, no descent ellipse after) ->
            // CaptureSynthesizable=true (the case option 3 fully improves).
            var segs = new List<OrbitSegment>
            {
                SegAE("Kerbin", 100, 5000, 700000, 0.0),
                SegAE("Sun", 5000, 1000000, 1.7e10, 0.2),
                SegAE("Duna", 1000000, 1005000, -1.88e5, 3.6),   // capture hyperbola, nothing after
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.True(plan.CaptureSynthesizable, plan.CaptureSynthesizableReason);
            Assert.Contains("clean", plan.CaptureSynthesizableReason);
        }

        // ---- ClassifyCaptureSynthesizable directly (pure predicate) ----

        [Fact]
        public void ClassifyCaptureSynthesizable_HyperbolaNoThread_True()
        {
            var segs = new List<OrbitSegment>
            {
                SegAE("Duna", 1000000, 1005000, -1.88e5, 3.6),   // 0: capture hyperbola (arrivalIdx)
                SegAE("Duna", 1005000, 1006000, 4.9e5, 0.45),    // 1: same-body descent (not a thread)
            };
            Assert.True(ReaimClassifier.ClassifyCaptureSynthesizable(
                segs, arrivalIdx: 0, "Duna", "Sun", "Kerbin", StockParents(), out string reason), reason);
        }

        [Fact]
        public void ClassifyCaptureSynthesizable_SecondaryBodyThread_False()
        {
            var segs = new List<OrbitSegment>
            {
                SegAE("Duna", 1000000, 1005000, -1.88e5, 3.6),   // 0: capture hyperbola
                SegAE("Ike", 1005000, 1006000, -2.9e4, 1.02),    // 1: Ike thread -> fail closed
            };
            Assert.False(ReaimClassifier.ClassifyCaptureSynthesizable(
                segs, arrivalIdx: 0, "Duna", "Sun", "Kerbin", StockParents(), out string reason));
            Assert.Contains("Ike", reason);
        }

        [Fact]
        public void ClassifyCaptureSynthesizable_EllipticFirstLeg_False_AtmosphericDirect()
        {
            var segs = new List<OrbitSegment>
            {
                SegAE("Duna", 1000000, 1005000, 4.9e5, 0.45),    // 0: elliptic descending arc, no hyperbola
            };
            Assert.False(ReaimClassifier.ClassifyCaptureSynthesizable(
                segs, arrivalIdx: 0, "Duna", "Sun", "Kerbin", StockParents(), out string reason));
            Assert.Contains("atmospheric-direct", reason);
        }

        [Fact]
        public void ClassifyCaptureSynthesizable_LaunchBodyAndCommonAncestorAfterCapture_NotAThread()
        {
            // A launch-body or common-ancestor segment after the capture (e.g. jettisoned debris back to
            // Kerbin / a same-orbit Sun fragment) is NOT a secondary-SOI thread - only a DIFFERENT
            // non-target body is. (Guards against over-eager fail-closed.)
            var segs = new List<OrbitSegment>
            {
                SegAE("Duna", 1000000, 1005000, -1.88e5, 3.6),   // 0: capture hyperbola
                SegAE("Kerbin", 1005000, 1006000, 700000, 0.0),  // 1: launch body (not a thread)
                SegAE("Sun", 1006000, 1007000, 1.7e10, 0.2),     // 2: common ancestor (not a thread)
            };
            Assert.True(ReaimClassifier.ClassifyCaptureSynthesizable(
                segs, arrivalIdx: 0, "Duna", "Sun", "Kerbin", StockParents(), out string reason), reason);
        }

        [Fact]
        public void ClassifyCaptureSynthesizable_DegenerateInputs_False()
        {
            Assert.False(ReaimClassifier.ClassifyCaptureSynthesizable(
                null, 0, "Duna", "Sun", "Kerbin", StockParents(), out _));
            Assert.False(ReaimClassifier.ClassifyCaptureSynthesizable(
                new List<OrbitSegment>(), 0, "Duna", "Sun", "Kerbin", StockParents(), out _));
            Assert.False(ReaimClassifier.ClassifyCaptureSynthesizable(
                new List<OrbitSegment> { SegAE("Duna", 0, 1, -1e5, 3.0) }, -1, "Duna", "Sun", "Kerbin", StockParents(), out _));
        }
    }
}
