using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The predicted-continuation-tail render contract outside the polyline builder
    /// (see <c>docs/dev/design-map-ts-render-architecture.md</c>, "Predicted continuation
    /// tails on the map"). Four families, each closing a measured gap from the
    /// 2026-09-08 re-fly forensics:
    /// <list type="bullet">
    /// <item>the pure conic-radius / span-above-surface math the ownership split rests on;</item>
    /// <item>defect (B): map presence resolving its SEGMENT LOOKUP through the chain's
    /// effective tip, both as the pure decision and end to end over a real two-member
    /// chain in <see cref="RecordingStore"/>;</item>
    /// <item><see cref="TrajectoryMath.TryGetOrbitWindowForMapDisplay"/>, which had ZERO
    /// xUnit coverage and whose merge behaviour across the recorded/predicted boundary
    /// is a deliberate asymmetry worth pinning;</item>
    /// <item>the flight-map marker's RELATIVE-frame dispatch.</item>
    /// </list>
    /// Fixture elements are the measured ones from the session's TIP sidecar (Kerbin,
    /// radius 600000, GM 3.5316e12, sma 865777.71214532177, ecc 0.31755526014312058).
    /// </summary>
    [Collection("Sequential")]
    public class PredictedTailMapPresenceTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinGravParameter = 3.5316000e12;
        private const double TailSma = 865777.71214532177;
        private const double TailEcc = 0.31755526014312058;
        private const double CoastStartUT = 1078.0528338768356;
        private const double AtmosphereEntryUT = 2186.5751775686945;
        private const double ImpactUT = 2348.5488254909719;
        private const double MeanAnomalyAtImpact = 6.1056670826845894;

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public PredictedTailMapPresenceTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // --- helpers ---------------------------------------------------------

        private static OrbitSegment TailSegment(double startUT, double endUT, bool predicted)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = "Kerbin",
                semiMajorAxis = TailSma,
                eccentricity = TailEcc,
                inclination = 0.5473,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 84.509,
                meanAnomalyAtEpoch = MeanAnomalyAtImpact,
                epoch = ImpactUT,
                isPredicted = predicted
            };
        }

        private static TrajectoryPoint Point(
            double ut, double alt, double speed = 0.0, string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = -74.5,
                altitude = alt,
                bodyName = body,
                velocity = new UnityEngine.Vector3((float)speed, 0f, 0f)
            };
        }

        // =====================================================================
        // 1. The pure conic math the ownership split rests on
        // =====================================================================

        [Fact]
        public void ConicRadius_ReproducesTheMeasuredTailGeometry()
        {
            var coast = TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true);

            Assert.True(TrajectoryMath.TryGetConicRadiusAtUT(
                coast, KerbinGravParameter, AtmosphereEntryUT, out double rEntry));
            Assert.True(TrajectoryMath.TryGetConicRadiusAtUT(
                coast, KerbinGravParameter, ImpactUT, out double rImpact));
            Assert.True(TrajectoryMath.TryGetConicRadiusAtUT(
                coast, KerbinGravParameter, CoastStartUT, out double rCoastStart));

            // The finalizer clipped the coast at atmosphere entry (70 km) and the extrapolator
            // terminated at alt 0.0; the elements must reproduce both to the metre, or the
            // ownership split below is measuring a fixture rather than the shipped data.
            Assert.Equal(KerbinRadius + 70000.0, rEntry, 0);
            Assert.Equal(KerbinRadius, rImpact, 0);
            // The tail starts within a few metres of apoapsis, at ~540.7 km altitude.
            Assert.Equal(TailSma * (1.0 + TailEcc), rCoastStart, 0);
        }

        [Fact]
        public void ConicSpanAboveSurface_TrueForTheCoast_FalseForTheDescent()
        {
            var coast = TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true);
            var descent = TailSegment(AtmosphereEntryUT, ImpactUT, predicted: true);

            Assert.True(TrajectoryMath.IsConicSpanAboveSurface(
                coast, KerbinGravParameter, KerbinRadius));
            Assert.False(TrajectoryMath.IsConicSpanAboveSurface(
                descent, KerbinGravParameter, KerbinRadius));
        }

        [Fact]
        public void ConicSpanAboveSurface_FalseWhenTheSpanSwallowsPeriapsis()
        {
            // The endpoint test alone is not sufficient in general: a span that CONTAINS the
            // periapsis passage can start and end above the surface while diving underground in
            // between. Take the coast and extend it a full period past impact so it wraps.
            var wrapped = TailSegment(CoastStartUT, CoastStartUT + 2700.0, predicted: true);
            Assert.False(TrajectoryMath.IsConicSpanAboveSurface(
                wrapped, KerbinGravParameter, KerbinRadius));
        }

        [Fact]
        public void ConicSpanAboveSurface_FalseWithoutAGravitationalParameter()
        {
            var coast = TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true);
            Assert.False(TrajectoryMath.IsConicSpanAboveSurface(coast, 0.0, KerbinRadius));
            Assert.False(TrajectoryMath.IsConicSpanAboveSurface(coast, double.NaN, KerbinRadius));
        }

        [Fact]
        public void ConicSpanContainsPeriapsis_ClosedAndHyperbolic()
        {
            double twoPi = 2.0 * Math.PI;
            Assert.False(TrajectoryMath.ConicSpanContainsPeriapsis(0.5, 1.0, 0.3));
            Assert.True(TrajectoryMath.ConicSpanContainsPeriapsis(twoPi - 0.1, twoPi + 0.1, 0.3));
            // A span longer than one revolution always contains one.
            Assert.True(TrajectoryMath.ConicSpanContainsPeriapsis(0.1, 0.1 + twoPi, 0.3));
            // Hyperbolic: periapsis is the single M = 0 crossing, with no wrap.
            Assert.True(TrajectoryMath.ConicSpanContainsPeriapsis(-0.2, 0.2, 1.4));
            Assert.False(TrajectoryMath.ConicSpanContainsPeriapsis(0.2, 3.0, 1.4));
        }

        [Fact]
        public void ConicRadius_HyperbolicBranchIsSaneAtPeriapsis()
        {
            // sma < 0, ecc > 1: r at M = 0 must equal the periapsis radius a*(1-e).
            var hyper = new OrbitSegment
            {
                startUT = 0.0,
                endUT = 100.0,
                bodyName = "Kerbin",
                semiMajorAxis = -900000.0,
                eccentricity = 1.4,
                meanAnomalyAtEpoch = 0.0,
                epoch = 0.0
            };
            Assert.True(TrajectoryMath.TryGetConicRadiusAtUT(
                hyper, KerbinGravParameter, 0.0, out double r));
            Assert.Equal(-900000.0 * (1.0 - 1.4), r, 3);
        }

        // --- the fail-closed Kepler solve -------------------------------------
        //
        // e = 0.9948 is the surface-rotation ellipse of EVERY landed or prelaunch vessel, not an
        // exotic input. Newton seeded at M + e sin M is not globally convergent there: near
        // periapsis 1 - e cos E approaches zero, one step throws the estimate arbitrarily far
        // from the root, and the rest of the iterations wander. Measured over 200000 mean
        // anomalies in |M| < 0.084, 68 ended with residuals up to 1e9 and effectively random
        // radii, which TryGetConicRadiusAtUT reported as SUCCESS.

        private const double ExtremeEcc = 0.9948;

        /// <summary>
        /// Independent reference solve by bisection. <c>E - e sin E</c> is strictly increasing on
        /// <c>[-pi, pi]</c> for <c>e &lt; 1</c>, so bisection cannot diverge - which is exactly
        /// the property the production Newton solve lacks and the reason this cell does not
        /// simply re-run the same algorithm and compare it to itself.
        /// </summary>
        private static double ReferenceEccentricAnomaly(double m, double ecc)
        {
            double lo = -Math.PI;
            double hi = Math.PI;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (mid - ecc * Math.Sin(mid) < m) lo = mid;
                else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// What the solve returned BEFORE the residual gate: the same Newton loop with no
        /// convergence check. Used only to classify a refusal - a refusal whose estimate is
        /// finite is one the old code reported as success with a wrong radius, which is the
        /// regression class the gate closes.
        /// </summary>
        private static double UngatedNewtonEccentricAnomaly(double meanAnomaly, double ecc)
        {
            double m = meanAnomaly % (2.0 * Math.PI);
            if (m > Math.PI) m -= 2.0 * Math.PI;
            if (m < -Math.PI) m += 2.0 * Math.PI;
            double e = m + ecc * Math.Sin(m);
            for (int i = 0; i < 64; i++)
            {
                double f = e - ecc * Math.Sin(e) - m;
                double fp = 1.0 - ecc * Math.Cos(e);
                if (Math.Abs(fp) < 1e-15) break;
                double step = f / fp;
                e -= step;
                if (Math.Abs(step) < 1e-13) break;
            }
            return e;
        }

        private static OrbitSegment EccentricProbe(double sma, double meanAnomaly, double spanUT)
        {
            return new OrbitSegment
            {
                startUT = 0.0,
                endUT = spanUT,
                bodyName = "Kerbin",
                semiMajorAxis = sma,
                eccentricity = ExtremeEcc,
                meanAnomalyAtEpoch = meanAnomaly,
                epoch = 0.0
            };
        }

        [Fact]
        public void ConicRadius_ExtremeEccentricityNearPeriapsis_IsConvergedOrRefused()
        {
            const double sma = 700000.0;
            const int samples = 20000;
            int refused = 0;

            for (int i = 0; i <= samples; i++)
            {
                double m = -0.1 + 0.2 * i / samples;
                OrbitSegment seg = EccentricProbe(sma, m, 1.0);

                if (!TrajectoryMath.TryGetConicRadiusAtUT(
                        seg, KerbinGravParameter, 0.0, out double radius))
                {
                    refused++;
                    continue;
                }

                double expected = sma * (1.0 - ExtremeEcc
                    * Math.Cos(ReferenceEccentricAnomaly(m, ExtremeEcc)));
                Assert.True(
                    Math.Abs(radius - expected) <= 1e-6 * expected,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "TryGetConicRadiusAtUT reported SUCCESS with a non-converged radius at "
                        + "M={0:R}, e={1:R}: got {2:R} m, reference {3:R} m",
                        m, ExtremeEcc, radius, expected));
            }

            Assert.True(refused > 0,
                "the sweep must actually cross the divergent band, or it pins nothing; "
                + "no mean anomaly in [-0.1, 0.1] was refused");
        }

        [Fact]
        public void ConicSpanAboveSurface_NonConvergedSolve_IsNotOrbitOwned()
        {
            // FAIL-CLOSED, the whole point of the residual gate: a span the solver cannot answer
            // for must read "not orbit-owned" so the piece falls to the traced-leg path, which
            // always draws. sma is sized so the conic clears Kerbin at EVERY anomaly (periapsis
            // radius 2.6e6 m against a 6e5 m body), so a false verdict here can only come from
            // the refusal and never from geometry.
            const double sma = 5.0e8;
            Assert.True(sma * (1.0 - ExtremeEcc) > KerbinRadius + 10.0);

            // The mean-anomaly step the span covers. Small enough that both endpoints stay on the
            // same side of periapsis, so ConicSpanContainsPeriapsis cannot be the cause either.
            const double spanMeanAnomaly = 1e-3;
            double meanMotion = Math.Sqrt(KerbinGravParameter / (sma * sma * sma));
            double spanUT = spanMeanAnomaly / meanMotion;

            // The mean anomaly must be refused AND have a FINITE ungated estimate: that is the
            // exact class the gate exists for - before it, the helper reported SUCCESS with a
            // finite wrong radius. A refusal that only comes from an infinite estimate was
            // already caught by the pre-existing finiteness check and would prove nothing.
            double badM = double.NaN;
            for (int i = 0; i <= 20000 && double.IsNaN(badM); i++)
            {
                double m = 0.02 + 0.065 * i / 20000.0;
                if (TrajectoryMath.TryGetConicRadiusAtUT(
                        EccentricProbe(sma, m, spanUT), KerbinGravParameter, 0.0, out _))
                    continue;
                double ungated = UngatedNewtonEccentricAnomaly(m, ExtremeEcc);
                if (!double.IsNaN(ungated) && !double.IsInfinity(ungated)) badM = m;
            }
            Assert.False(double.IsNaN(badM),
                "no refused mean anomaly with a finite ungated estimate found in [0.02, 0.085]; "
                + "the assertions below would prove nothing");

            OrbitSegment diverging = EccentricProbe(sma, badM, spanUT);
            Assert.False(
                TrajectoryMath.IsConicSpanAboveSurface(diverging, KerbinGravParameter, KerbinRadius),
                "a span whose Kepler solve did not converge must read NOT above-surface (leg-owned)");

            // CONTROL, same elements and same span length at a mean anomaly the solver DOES
            // answer: the geometry really is above the surface, so the false above is the gate.
            double goodM = double.NaN;
            for (int i = 0; i <= 20000 && double.IsNaN(goodM); i++)
            {
                double m = 0.02 + 0.065 * i / 20000.0;
                OrbitSegment probe = EccentricProbe(sma, m, spanUT);
                if (TrajectoryMath.TryGetConicRadiusAtUT(probe, KerbinGravParameter, 0.0, out _)
                    && TrajectoryMath.TryGetConicRadiusAtUT(probe, KerbinGravParameter, spanUT, out _))
                    goodM = m;
            }
            Assert.False(double.IsNaN(goodM));
            Assert.True(TrajectoryMath.IsConicSpanAboveSurface(
                EccentricProbe(sma, goodM, spanUT), KerbinGravParameter, KerbinRadius));
        }

        // =====================================================================
        // 2. Defect (B): map presence resolves segments from the chain's tip
        // =====================================================================

        private static Recording ChainMember(string id, string chainId, int chainIndex)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0
            };
        }

        /// <summary>
        /// The measured shape: HEAD [32.94, 191.04] with 272 points and ZERO segments, TIP
        /// [191.04, 1078.45] carrying every conic including the two predicted tail ones.
        /// Registered under one tree so the chain-tip walker's tree scan resolves.
        /// </summary>
        private static void RegisterSplitChain(out Recording head, out Recording tip)
        {
            head = ChainMember("rec_head", "chain_split", 0);
            head.Points.Add(Point(32.94, 96.0));
            // Sized above ShouldCreateStateVectorOrbit's altitude and speed floors so the
            // state-vector branch can actually succeed and the mirror-direction cell below
            // measures the ROUTING rather than an unrelated create threshold.
            head.Points.Add(Point(150.0, 80000.0, 2200.0));
            head.Points.Add(Point(191.04, 90000.0, 2400.0));

            tip = ChainMember("rec_tip", "chain_split", 1);
            tip.Points.Add(Point(191.04, 90000.0, 2400.0));
            tip.Points.Add(Point(1078.45, 540710.0, 900.0));
            tip.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 216.7,
                endUT = 1041.79,
                bodyName = "Kerbin",
                semiMajorAxis = 791370.05283977953,
                eccentricity = 0.44124279173795367,
                epoch = 216.7,
                isPredicted = false
            });
            tip.OrbitSegments.Add(TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true));
            tip.OrbitSegments.Add(TailSegment(AtmosphereEntryUT, ImpactUT, predicted: true));

            var tree = new RecordingTree { Id = "tree_split", TreeName = "tree_split" };
            tree.RootRecordingId = head.RecordingId;
            head.TreeId = "tree_split";
            tip.TreeId = "tree_split";
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedInternal(tip);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        [Fact]
        public void MapPresenceChainSegments_HeadWithNoSegments_ResolvesTheTipsSegments()
        {
            RegisterSplitChain(out Recording head, out Recording tip);

            List<OrbitSegment> resolved = GhostMapPresence.ResolveMapPresenceChainSegments(
                head,
                (IPlaybackTrajectory traj, out string tipId, out List<OrbitSegment> segs) =>
                {
                    tipId = tip.RecordingId;
                    segs = tip.OrbitSegments;
                    return true;
                },
                out string reportedTipId,
                out int reportedCount);

            Assert.Same(tip.OrbitSegments, resolved);
            Assert.Equal("rec_tip", reportedTipId);
            Assert.Equal(3, reportedCount);
        }

        [Fact]
        public void MapPresenceChainSegments_RecordingWithItsOwnSegments_NeverConsultsTheTip()
        {
            // ADDITIVE-ONLY guard: the resolver must be unreachable for the whole existing
            // population, or every non-chain recording's behaviour would be at risk.
            RegisterSplitChain(out _, out Recording tip);
            bool consulted = false;

            List<OrbitSegment> resolved = GhostMapPresence.ResolveMapPresenceChainSegments(
                tip,
                (IPlaybackTrajectory traj, out string tipId, out List<OrbitSegment> segs) =>
                {
                    consulted = true;
                    tipId = null;
                    segs = null;
                    return false;
                },
                out string reportedTipId,
                out int reportedCount);

            Assert.False(consulted);
            Assert.Same(tip.OrbitSegments, resolved);
            Assert.Null(reportedTipId);
            Assert.Equal(0, reportedCount);
        }

        [Fact]
        public void EffectiveTip_OfTheOptimizerSplitChain_IsTheSegmentBearingTip()
        {
            // The walk the LIVE resolver performs, over the real store rather than a stub. This
            // is what turns "hasOrbitSegments=False on the HEAD" into the tip's three conics.
            RegisterSplitChain(out Recording head, out Recording tip);

            string resolvedTip = EffectiveState.EffectiveTipRecordingId(
                head.RecordingId, new List<RecordingSupersedeRelation>());

            Assert.Equal(tip.RecordingId, resolvedTip);
            Assert.Empty(head.OrbitSegments);
            Assert.Equal(3, tip.OrbitSegments.Count);
        }

        [Fact]
        public void MapPresenceGhostSource_ChainHead_SeedsFromThePredictedTailNotAStateVector()
        {
            // THE cell that would have caught the wrong ellipse the player actually saw. At a UT
            // inside the predicted coast the HEAD must resolve a Segment source carrying the
            // PREDICTED tail elements, not fall through to the state-vector fallback.
            RegisterSplitChain(out Recording head, out _);

            int cachedIndex = -1;
            TrackingStationGhostSourceForTest source = Resolve(head, 1500.0, ref cachedIndex,
                out OrbitSegment segment, out string skipReason);

            Assert.Equal(TrackingStationGhostSourceForTest.Segment, source);
            Assert.True(segment.isPredicted,
                "the resolved segment must be the predicted continuation conic, not a recorded one");
            Assert.Equal(TailSma, segment.semiMajorAxis, 3);
            Assert.Null(skipReason);
        }

        [Fact]
        public void MapPresenceGhostSource_ChainHeadInsideItsOwnSpan_KeepsTheStateVectorPath()
        {
            // MIRROR DIRECTION of the fix, and the regression it could most easily have caused:
            // at a UT the HEAD itself recorded, no tip conic covers it, so the resolver must
            // still take the state-vector path and give the HEAD a map ghost.
            RegisterSplitChain(out Recording head, out _);

            int cachedIndex = -1;
            TrackingStationGhostSourceForTest source = Resolve(head, 150.0, ref cachedIndex,
                out _, out _);

            Assert.Equal(TrackingStationGhostSourceForTest.StateVector, source);
        }

        [Fact]
        public void MapPresenceChainTipMemo_ReResolvesOnASupersedeOnlyBump()
        {
            // The live chain-tip walk is memoized, and its value is EffectiveTipRecordingId,
            // which depends on the supersede table as well as the committed list.
            // SupersedeCommit.AppendRelations mutates RecordingSupersedes and bumps ONLY
            // SupersedeStateVersion - RecordingStore.StateVersion does not move - so a
            // StateVersion-only memo key served the pre-supersede tip for the rest of the
            // session. The key is composite, mirroring the ERS / retired-set caches.
            RegisterSplitChain(out Recording head, out Recording tip);

            // The fork the tip will be superseded by. Registered BEFORE the first resolve, so the
            // committed-list bump it causes cannot be what invalidates the memo below.
            var fork = new Recording
            {
                RecordingId = "rec_fork",
                VesselName = "rec_fork",
                MergeState = MergeState.Immutable
            };
            fork.Points.Add(Point(1000.0, 300000.0, 1500.0));
            fork.Points.Add(Point(3000.0, 300000.0, 1500.0));
            fork.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000.0,
                endUT = 3000.0,
                bodyName = "Kerbin",
                semiMajorAxis = 1234567.0,
                eccentricity = 0.1,
                epoch = 1000.0,
                isPredicted = false
            });
            RecordingStore.AddCommittedInternal(fork);

            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);

            int cachedIndex = -1;
            TrackingStationGhostSourceForTest first = Resolve(
                head, 1500.0, ref cachedIndex, out OrbitSegment firstSegment, out _);
            Assert.Equal(TrackingStationGhostSourceForTest.Segment, first);
            Assert.Equal(TailSma, firstSegment.semiMajorAxis, 3);

            // Supersede the tip. This is the AppendRelations shape: one row plus a supersede-only
            // version bump.
            int storeVersionBefore = RecordingStore.StateVersion;
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_test",
                OldRecordingId = tip.RecordingId,
                NewRecordingId = fork.RecordingId,
                UT = 1200.0
            });
            scenario.BumpSupersedeStateVersion();
            Assert.Equal(storeVersionBefore, RecordingStore.StateVersion);

            int cachedIndex2 = -1;
            TrackingStationGhostSourceForTest second = Resolve(
                head, 1500.0, ref cachedIndex2, out OrbitSegment secondSegment, out _);
            Assert.Equal(TrackingStationGhostSourceForTest.Segment, second);
            Assert.Equal(1234567.0, secondSegment.semiMajorAxis, 3);
        }

        // The production enum is internal to GhostMapPresence; mirror only the two values the
        // cells above assert on, so a rename of an unrelated member does not drag these cells.
        private enum TrackingStationGhostSourceForTest
        {
            Other,
            Segment,
            StateVector
        }

        private static TrackingStationGhostSourceForTest Resolve(
            Recording rec, double currentUT, ref int cachedIndex,
            out OrbitSegment segment, out string skipReason)
        {
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    isSuppressed: false,
                    realVesselExists: false,
                    currentUT: currentUT,
                    stateVectorCachedIndex: ref cachedIndex,
                    segment: out segment,
                    stateVectorPoint: out _,
                    skipReason: out skipReason);
            switch (source)
            {
                case GhostMapPresence.TrackingStationGhostSource.Segment:
                    return TrackingStationGhostSourceForTest.Segment;
                case GhostMapPresence.TrackingStationGhostSource.StateVector:
                    return TrackingStationGhostSourceForTest.StateVector;
                default:
                    return TrackingStationGhostSourceForTest.Other;
            }
        }

        [Fact]
        public void EndpointAlignedOrbitSeed_FallsBackToTheChainTipsSegments()
        {
            // The other half of defect (B): the endpoint seed walks the recording's OWN segment
            // list, which on a HEAD is empty, so `endpoint-segment` could never fire and the
            // ghost fell to `state-vector-fallback`.
            RegisterSplitChain(out Recording head, out Recording tip);

            bool seeded = RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                head,
                out _, out double ecc, out double sma,
                out _, out _, out _, out _,
                out string bodyName,
                out RecordingEndpointResolver.EndpointOrbitSeedDiagnostics diagnostics,
                tip.OrbitSegments);

            Assert.True(seeded, "the chain tip's segments must seed the endpoint orbit");
            Assert.Equal("endpoint-segment", diagnostics.Source);
            Assert.Equal("Kerbin", bodyName);
            Assert.Equal(TailSma, sma, 3);
            Assert.Equal(TailEcc, ecc, 9);
        }

        [Fact]
        public void EndpointAlignedOrbitSeed_WithoutChainTipSegments_IsUnchanged()
        {
            RegisterSplitChain(out Recording head, out _);

            bool seeded = RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                head,
                out _, out _, out _, out _, out _, out _, out _, out _,
                out RecordingEndpointResolver.EndpointOrbitSeedDiagnostics diagnostics);

            Assert.False(seeded);
            Assert.NotEqual("endpoint-segment", diagnostics.Source);
        }

        // =====================================================================
        // 3. TryGetOrbitWindowForMapDisplay - first xUnit coverage
        // =====================================================================

        [Fact]
        public void OrbitWindowForMapDisplay_PredictedTail_MergesCoastAndDescent()
        {
            var segments = new List<OrbitSegment>
            {
                TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true),
                TailSegment(AtmosphereEntryUT, ImpactUT, predicted: true)
            };

            Assert.True(TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments, 1500.0,
                out OrbitSegment segment,
                out double visibleStart, out double visibleEnd,
                out int firstIdx, out int lastIdx, out bool carried));

            Assert.True(segment.isPredicted);
            Assert.Equal(CoastStartUT, visibleStart, 3);
            Assert.Equal(ImpactUT, visibleEnd, 3);
            Assert.Equal(0, firstIdx);
            Assert.Equal(1, lastIdx);
            Assert.False(carried);
        }

        [Fact]
        public void OrbitWindowForMapDisplay_MergesAcrossTheRecordedPredictedBoundary()
        {
            // DELIBERATE ASYMMETRY, pinned so nobody "fixes" it into consistency: this expansion
            // runs through ExpandEquivalentOrbitWindow, which does NOT compare isPredicted,
            // because a reseeded tail IS the same conic continuing and one arc is the correct
            // picture. Its two neighbours (CoalesceSameOrbitFragments, which rewrites the
            // recording, and TryExpandStoredSingleSegmentWindow, which must identify one authored
            // fragment) keep the guard for different reasons. See the design doc's
            // "Predicted continuation tails on the map" section.
            var segments = new List<OrbitSegment>
            {
                TailSegment(500.0, CoastStartUT, predicted: false),
                TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true)
            };

            Assert.True(TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments, 600.0,
                out OrbitSegment segment,
                out double visibleStart, out double visibleEnd,
                out _, out _, out _));

            Assert.False(segment.isPredicted);
            Assert.Equal(500.0, visibleStart, 3);
            Assert.Equal(AtmosphereEntryUT, visibleEnd, 3);
        }

        [Fact]
        public void OrbitWindowForMapDisplay_DoesNotMergeAGenuinelyDifferentConic()
        {
            // The recorded checkpoint conic (sma 791370, ecc 0.4412) is NOT element-equivalent to
            // the predicted tail, so the window must stop at the boundary rather than advertising
            // an arc the vessel never flew on those elements.
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 216.7,
                    endUT = 1041.79,
                    bodyName = "Kerbin",
                    semiMajorAxis = 791370.05283977953,
                    eccentricity = 0.44124279173795367,
                    epoch = 216.7,
                    isPredicted = false
                },
                TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true)
            };

            Assert.True(TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments, 500.0,
                out _, out double visibleStart, out double visibleEnd,
                out _, out _, out _));

            Assert.Equal(216.7, visibleStart, 3);
            Assert.Equal(1041.79, visibleEnd, 3);
        }

        [Fact]
        public void OrbitWindowForMapDisplay_ForeignBodyTail_IsNotMerged()
        {
            var mun = TailSegment(AtmosphereEntryUT, ImpactUT, predicted: true);
            mun.bodyName = "Mun";
            var segments = new List<OrbitSegment>
            {
                TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true),
                mun
            };

            Assert.True(TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments, 1500.0,
                out OrbitSegment segment,
                out double visibleStart, out double visibleEnd,
                out _, out _, out _));

            Assert.Equal("Kerbin", segment.bodyName);
            Assert.Equal(CoastStartUT, visibleStart, 3);
            Assert.Equal(AtmosphereEntryUT, visibleEnd, 3);
        }

        [Fact]
        public void OrbitWindowForMapDisplay_UtBeforeEverySegment_ReturnsFalse()
        {
            var segments = new List<OrbitSegment>
            {
                TailSegment(CoastStartUT, AtmosphereEntryUT, predicted: true)
            };
            Assert.False(TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments, 100.0, out _, out _, out _, out _, out _, out _));
        }

        // =====================================================================
        // 4. The flight-map marker's RELATIVE-frame dispatch
        // =====================================================================

        private static TrackSection Section(
            double startUT, double endUT, ReferenceFrame frame,
            List<TrajectoryPoint> bodyFixed = null)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = frame,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = bodyFixed,
                sampleRateHz = 10f
            };
        }

        [Fact]
        public void MapMarkerFrameSource_RelativeWithoutBodyFixed_Refuses()
        {
            // The CLAUDE.md footgun: in a Relative section latitude/longitude/altitude are
            // anchor-local METRE offsets, so reading them geographically puts the marker inside
            // the planet. There is nothing to fall back to, so the read must refuse.
            var sections = new List<TrackSection>
            {
                Section(0.0, 100.0, ReferenceFrame.Absolute),
                Section(100.0, 200.0, ReferenceFrame.Relative)
            };

            var source = ParsekUI.ResolveMapMarkerFrameSource(
                sections, 150.0, out List<TrajectoryPoint> resolved);

            Assert.Equal(ParsekUI.MapMarkerFrameSource.RelativeUnresolved, source);
            Assert.Null(resolved);
        }

        [Fact]
        public void MapMarkerFrameSource_RelativeWithBodyFixedShadow_UsesTheShadow()
        {
            var shadow = new List<TrajectoryPoint> { Point(100.0, 1000.0), Point(200.0, 2000.0) };
            var sections = new List<TrackSection>
            {
                Section(100.0, 200.0, ReferenceFrame.Relative, shadow)
            };

            var source = ParsekUI.ResolveMapMarkerFrameSource(
                sections, 150.0, out List<TrajectoryPoint> resolved);

            Assert.Equal(ParsekUI.MapMarkerFrameSource.SectionBodyFixedFrames, source);
            Assert.Same(shadow, resolved);
        }

        [Fact]
        public void MapMarkerFrameSource_AbsoluteAndOutOfSection_KeepTheFlatPointsPath()
        {
            // The whole existing population: an Absolute recording, and a UT outside every
            // section, must read exactly as before.
            var sections = new List<TrackSection>
            {
                Section(0.0, 100.0, ReferenceFrame.Absolute)
            };

            Assert.Equal(
                ParsekUI.MapMarkerFrameSource.FlatPoints,
                ParsekUI.ResolveMapMarkerFrameSource(sections, 50.0, out List<TrajectoryPoint> a));
            Assert.Null(a);

            Assert.Equal(
                ParsekUI.MapMarkerFrameSource.FlatPoints,
                ParsekUI.ResolveMapMarkerFrameSource(sections, 500.0, out List<TrajectoryPoint> b));
            Assert.Null(b);

            Assert.Equal(
                ParsekUI.MapMarkerFrameSource.FlatPoints,
                ParsekUI.ResolveMapMarkerFrameSource(null, 50.0, out List<TrajectoryPoint> c));
            Assert.Null(c);
        }
    }
}
