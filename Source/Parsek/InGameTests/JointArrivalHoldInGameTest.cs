using System.Collections.Generic;
using System.Globalization;
using Parsek.Reaim;

namespace Parsek.InGameTests
{
    /// <summary>
    /// (M-MIS-4 joint-arrival gate, PR #1255) The automated merge gate for the
    /// SolveArrivalWindow wiring: a destination carrying BOTH a landing (body-rotation)
    /// constraint AND a station rendezvous constraint must resolve the JOINT arrival hold
    /// through the REAL production pipeline - replacing the "fly a landing+station save and
    /// grep for the ARRIVAL HOLD kind=joint line" manual playtest (the
    /// RouteInterBodyBuilderShapeInGameTest / PR #1238 precedent).
    ///
    /// <para><b>What it drives.</b> A synthetic committed-shaped tree (one transfer member
    /// whose OrbitSegments carry the Kerbin->Sun->Duna SOI chain, whose TrackSections carry
    /// the Kerbin launch surface leg, the Duna landing surface leg, and a Relative
    /// rendezvous section anchored to a Duna station pid) is run through the REAL
    /// <c>MissionLoopUnitBuilder.TryBuildLoopUnitForSelection</c> - the same entry the live
    /// scenes and RealSaveMissionFinder use - so the whole chain executes for real:
    /// <c>MissionPeriodicity.ExtractConstraints</c> (Rotation(Duna) via the surface+orbit
    /// hand-off, VesselOrbital via the rendezvous classifier),
    /// <c>DestinationConstraintExtractor</c> (IsJointLandingStation),
    /// <c>ArrivalHoldPlanner.ComputeJointArrivalHold</c> (the hold-aware
    /// <c>DestinationArrivalSolver.SolveArrivalWindow</c> + <c>PlanJointHoldLattice</c>
    /// gates), and the joint fields on the built <c>LoopUnit</c>. No test-side re-derivation
    /// of the solver anywhere.
    ///
    /// <para><b>The one synthetic seam (documented, not hidden).</b> The station's live
    /// orbit resolves through <c>IBodyInfo.TryGetVesselOrbit</c>, which in production reads
    /// a REAL vessel in the save - impossible to conjure at the Space Center without
    /// mutating the save. The test wraps the live <c>FlightGlobalsBodyInfo.Instance</c> in a
    /// delegating <c>IBodyInfo</c> that answers ONLY the synthetic station pid with a
    /// Duna-centered closed orbit and forwards every other query (rotation periods, orbit
    /// periods, parents, mu, SOI, radius, every other vessel pid) to the live instance, so
    /// all celestial geometry the joint solve consumes is the real planet pack's. The
    /// station period is <c>T_rot(Duna)/16</c> (an exact power-of-two division, so the
    /// station/rotation lattice is exactly commensurate: worst miss-run 15, comfortably
    /// inside the 64-whole-period budget on any planet pack) - the numeric edge cases
    /// (tolerance-edge declines, incommensurate lattices, budget clamps) are pinned by the
    /// headless <c>ArrivalHoldPlannerTests</c>/<c>DestinationArrivalSolverTests</c>; this
    /// gate proves the WIRING end to end. The REAL-save counterpart (no synthetic seam at
    /// all) is <c>RealSaveMissionInGameTests.RealSave_JointArrivalMission_AlignsStationAndRotation</c>.
    ///
    /// <para><b>Isolation.</b> Fully store-free: the builder entry is pure over explicit
    /// mission/tree/committed lists, so NOTHING is committed to RecordingStore /
    /// MissionStore / RouteStore. The only seams touched are the log observer
    /// (<c>ParsekLog.TestObserverForTesting</c>, to capture the ARRIVAL HOLD line) and the
    /// two SuppressLogging flags forced OFF for the build; all are snapshotted and restored
    /// in <c>finally</c>. Batch-safe (all-tests-auto policy).
    /// </summary>
    public sealed class JointArrivalHoldInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string Tag = "TestRunner";

        // The synthetic recorded-mission time base (mirrors the proven M5 gate constants:
        // well clear of live UT=0 cold loads; the geometry is a stand-in the builder
        // re-solves against live ephemerides).
        private const double ParkStartUT = 5_000_000.0;
        private const double DepartureUT = 5_000_600.0;   // launch-body SOI exit -> heliocentric leg
        private const double ArrivalUT = 9_000_000.0;     // target SOI entry (= RecordedArrivalUT)
        private const double ArrivalEndUT = 9_000_600.0;  // end of the recorded Duna arrival leg
        private const double LoopEnabledUT = 9_100_000.0; // loop enabled after the mission finished

        // The station period divisor: T_station = T_rot(Duna) / 16. A power-of-two division
        // is EXACT in doubles, so 16 * T_station == T_rot bit-for-bit and the joint lattice
        // (i * T_station mod T_rot) is exactly 16-periodic: the worst out-of-tolerance run is
        // 15 whole periods, always within the 64 budget regardless of the planet pack's
        // actual rotation period. Deterministic engage by construction.
        private const double StationPeriodDivisor = 16.0;

        // Per-loop indices the dual-satisfaction sweep checks. The large index proves the
        // per-loop re-solve does not drift (the fixed-cadence-vs-two-periods core claim).
        private static readonly long[] CheckedLoopIndices = { 0L, 1L, 2L, 3L, 5L, 1000L };

        [InGameTest(Category = "Missions", Scene = GameScenes.SPACECENTER,
            Description = "M-MIS-4 joint-arrival gate (PR #1255): a synthetic landing+station destination resolves through the REAL MissionLoopUnitBuilder (live ephemerides) into a JOINT arrival hold - kind=joint ARRIVAL HOLD line fires, the unit carries the joint fields, and the per-loop hold satisfies BOTH the station lattice (exact) and the rotation tolerance for every checked loop index")]
        public void JointArrivalHold_LandingPlusStation_RealBuilder_EngagesAndAlignsBoth()
        {
            SkipUnlessStockGeometry(out CelestialBody kerbin, out CelestialBody duna);

            double tRot = FlightGlobalsBodyInfo.Instance.RotationPeriod(duna.bodyName);
            if (double.IsNaN(tRot) || double.IsInfinity(tRot) || tRot <= 0.0)
                InGameAssert.Skip($"live {duna.bodyName} rotation period is degenerate ({tRot.ToString("R", IC)}) - no rotation constraint to jointly align");
            double tSta = tRot / StationPeriodDivisor;

            string idPrefix = "mmis4-joint-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            uint stationPid = 987_654_321u; // resolved ONLY by the wrapper, never a live lookup

            // Snapshot every seam we touch; restored in finally.
            var priorObserver = ParsekLog.TestObserverForTesting;
            bool priorBuilderSuppress = MissionLoopUnitBuilder.SuppressLogging;
            bool priorPeriodicitySuppress = MissionPeriodicity.SuppressLogging;
            var captured = new List<string>();
            try
            {
                ParsekLog.TestObserverForTesting = line => captured.Add(line);
                MissionLoopUnitBuilder.SuppressLogging = false;
                MissionPeriodicity.SuppressLogging = false;

                var bodyInfo = new StationInjectingBodyInfo(
                    FlightGlobalsBodyInfo.Instance, stationPid, tSta, duna.bodyName);

                GhostPlaybackLogic.LoopUnit unit = BuildUnitThroughRealBuilder(
                    idPrefix, kerbin, duna, includeStationRendezvous: true, stationPid, bodyInfo,
                    out Mission mission);

                // (1) Re-aim engaged for real (the joint hold only exists on the re-aim path).
                InGameAssert.IsTrue(unit.IsReaim,
                    "the real builder must engage re-aim on the synthetic Kerbin->Duna landing+station mission");
                InGameAssert.AreEqual(duna.bodyName, unit.ReaimPlan.Value.TargetBody,
                    "the re-aim plan's target must be the destination body");

                // (2) The resolved hold is the JOINT kind: applied, station-exact align period,
                //     joint fields carrying the live rotation period + mode tolerance + budget,
                //     no amber (engage, not fail-closed).
                InGameAssert.IsTrue(unit.ArrivalHoldSeconds > 0.0 && !double.IsInfinity(unit.ArrivalHoldSeconds),
                    $"the joint arrival hold must be applied (got {unit.ArrivalHoldSeconds.ToString("R", IC)}s)");
                InGameAssert.ApproxEqual(ArrivalUT, unit.ArrivalHoldAtUT, 1e-6,
                    "the hold must sit at the recorded SOI-entry boundary (RecordedArrivalUT)");
                InGameAssert.ApproxEqual(tSta, unit.ArrivalAlignPeriodSeconds, 1e-9,
                    "the align period must be the STATION period exactly (station-lattice-exact hold)");
                InGameAssert.ApproxEqual(tRot, unit.ArrivalJointSecondaryPeriodSeconds, 1e-9,
                    $"the joint secondary period must be the LIVE {duna.bodyName} rotation period (validated against the live body graph)");
                // The tolerance must be the production mode band for this constraint - computed
                // via the SAME production definition (ScheduleToleranceSecondsFor), not a
                // re-derived magic number.
                double expectedTol = MissionPeriodicity.ScheduleToleranceSecondsFor(
                    new PhaseConstraint
                    {
                        Kind = ConstraintKind.Rotation,
                        BodyName = duna.bodyName,
                        PeriodSeconds = tRot,
                        PhaseOffsetSeconds = 0.0,
                        RelativeToParent = false,
                    },
                    bodyInfo, kerbin.bodyName, TransitedBodyRotationMode.Loose);
                InGameAssert.ApproxEqual(expectedTol, unit.ArrivalJointSecondaryToleranceSeconds, 1e-9,
                    "the joint secondary tolerance must be the production Loose mode band (ScheduleToleranceSecondsFor)");
                InGameAssert.IsTrue(
                    unit.ArrivalJointMaxWholeHoldPeriods > 0
                    && unit.ArrivalJointMaxWholeHoldPeriods <= DestinationArrivalSolver.MaxJointHoldWholePeriods,
                    $"the whole-period budget must be positive and capped at MaxJointHoldWholePeriods (got {unit.ArrivalJointMaxWholeHoldPeriods.ToString(IC)})");
                InGameAssert.IsNull(unit.ArrivalAmberReason,
                    "an engaged joint hold must carry NO arrival amber (amber = fail-closed): "
                    + (unit.ArrivalAmberReason ?? ""));

                // (3) The ARRIVAL HOLD kind=joint log line fired (the exact line the manual
                //     playtest gate greps for).
                InGameAssert.IsTrue(
                    captured.Exists(l => l.Contains("[Reaim]") && l.Contains("ARRIVAL HOLD")
                        && l.Contains("kind=joint") && l.Contains(mission.Name)),
                    "the builder must log the ARRIVAL HOLD kind=joint line for the landing+station mission "
                    + "(the manual playtest gate's grep target)");

                // (4) The per-loop hold satisfies BOTH constraints, validated with independent
                //     modular arithmetic (never by re-calling SolveArrivalWindow): for each
                //     checked loop N, the production per-loop dispatch's hold makes the total
                //     shift from the recorded arrival (a) an exact whole number of STATION
                //     periods and (b) within the rotation tolerance of a whole number of
                //     ROTATION periods.
                double entryOffset0 = unit.PhaseAnchorUT
                    + (GhostPlaybackLogic.CompressSpanUT(unit.ArrivalHoldAtUT, unit.LoiterCuts) - unit.SpanStartUT)
                    - unit.ArrivalHoldAtUT;
                double maxHold = (unit.ArrivalJointMaxWholeHoldPeriods + 1) * tSta;
                foreach (long n in CheckedLoopIndices)
                {
                    double holdN = GhostPlaybackLogic.ComputePerLoopJointArrivalHoldSeconds(
                        unit.ArrivalHoldSeconds, n, unit.CadenceSeconds, unit.ArrivalAlignPeriodSeconds,
                        unit.ArrivalJointSecondaryPeriodSeconds, unit.ArrivalJointSecondaryToleranceSeconds,
                        entryOffset0, unit.ArrivalJointMaxWholeHoldPeriods);
                    InGameAssert.IsTrue(holdN >= 0.0 && holdN <= maxHold + 1e-6,
                        $"loop N={n.ToString(IC)}: hold {holdN.ToString("F3", IC)}s must respect the (budget+1)*T_station bound {maxHold.ToString("F3", IC)}s");

                    double delta = entryOffset0 + n * unit.CadenceSeconds + holdN;
                    double stationErr = IndependentCircularError(delta, tSta);
                    double rotationErr = IndependentCircularError(delta, tRot);
                    InGameAssert.IsTrue(stationErr <= 1e-3,
                        $"loop N={n.ToString(IC)}: the held arrival must land EXACTLY on the station lattice "
                        + $"(station phase error {stationErr.ToString("R", IC)}s, T_station={tSta.ToString("R", IC)}s)");
                    InGameAssert.IsTrue(rotationErr <= unit.ArrivalJointSecondaryToleranceSeconds + 1e-3,
                        $"loop N={n.ToString(IC)}: the held arrival must land within the rotation tolerance "
                        + $"(rotation phase error {rotationErr.ToString("F3", IC)}s vs tolerance "
                        + $"{unit.ArrivalJointSecondaryToleranceSeconds.ToString("F3", IC)}s, T_rot={tRot.ToString("R", IC)}s)");
                }

                ParsekLog.Info(Tag,
                    $"JointArrivalHold: PASS joint hold engaged through the REAL builder - "
                    + $"Tsta={tSta.ToString("R", IC)}s Trot={tRot.ToString("R", IC)}s "
                    + $"tol={unit.ArrivalJointSecondaryToleranceSeconds.ToString("F1", IC)}s "
                    + $"budget={unit.ArrivalJointMaxWholeHoldPeriods.ToString(IC)} "
                    + $"w0={unit.ArrivalHoldSeconds.ToString("R", IC)}s "
                    + $"cadence={unit.CadenceSeconds.ToString("R", IC)}s "
                    + $"loopsChecked={CheckedLoopIndices.Length.ToString(IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                MissionLoopUnitBuilder.SuppressLogging = priorBuilderSuppress;
                MissionPeriodicity.SuppressLogging = priorPeriodicitySuppress;
            }
        }

        [InGameTest(Category = "Missions", Scene = GameScenes.SPACECENTER,
            Description = "M-MIS-4 joint-arrival gate (PR #1255), byte-identical-off control: the SAME synthetic destination WITHOUT the station rendezvous resolves the pre-existing single-period ROTATION hold - joint fields stay at their NaN/0 sentinels and no kind=joint line fires")]
        public void JointArrivalHold_LandingOnly_RealBuilder_KeepsSingleRotationHold()
        {
            SkipUnlessStockGeometry(out CelestialBody kerbin, out CelestialBody duna);

            double tRot = FlightGlobalsBodyInfo.Instance.RotationPeriod(duna.bodyName);
            if (double.IsNaN(tRot) || double.IsInfinity(tRot) || tRot <= 0.0)
                InGameAssert.Skip($"live {duna.bodyName} rotation period is degenerate ({tRot.ToString("R", IC)}) - no rotation hold to control against");

            string idPrefix = "mmis4-joint-ctl-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

            var priorObserver = ParsekLog.TestObserverForTesting;
            bool priorBuilderSuppress = MissionLoopUnitBuilder.SuppressLogging;
            bool priorPeriodicitySuppress = MissionPeriodicity.SuppressLogging;
            var captured = new List<string>();
            try
            {
                ParsekLog.TestObserverForTesting = line => captured.Add(line);
                MissionLoopUnitBuilder.SuppressLogging = false;
                MissionPeriodicity.SuppressLogging = false;

                // No wrapper: the plain live body graph (no station to resolve).
                GhostPlaybackLogic.LoopUnit unit = BuildUnitThroughRealBuilder(
                    idPrefix, kerbin, duna, includeStationRendezvous: false, stationPid: 0u,
                    FlightGlobalsBodyInfo.Instance, out Mission mission);

                InGameAssert.IsTrue(unit.IsReaim,
                    "the real builder must engage re-aim on the landing-only control mission");

                // The joint fields must stay at their documented "not joint" sentinels - the
                // byte-identical-off guarantee for every single-constraint shape.
                InGameAssert.IsTrue(double.IsNaN(unit.ArrivalJointSecondaryPeriodSeconds),
                    $"a landing-only unit must carry the NaN joint-secondary-period sentinel (got {unit.ArrivalJointSecondaryPeriodSeconds.ToString("R", IC)})");
                InGameAssert.IsTrue(double.IsNaN(unit.ArrivalJointSecondaryToleranceSeconds),
                    $"a landing-only unit must carry the NaN joint-tolerance sentinel (got {unit.ArrivalJointSecondaryToleranceSeconds.ToString("R", IC)})");
                InGameAssert.AreEqual(0, unit.ArrivalJointMaxWholeHoldPeriods,
                    "a landing-only unit must carry the 0 joint-budget sentinel");
                InGameAssert.IsFalse(
                    captured.Exists(l => l.Contains("ARRIVAL HOLD") && l.Contains("kind=joint")),
                    "the landing-only control must never log a kind=joint ARRIVAL HOLD line");

                // When the single-period hold applies (the generic case - a zero hold means the
                // live entry happened to be exactly aligned, which the planner maps to None),
                // it must be the pre-existing ROTATION hold: align period = the live rotation
                // period, and the log line reads kind=rotation.
                bool holdApplied = unit.ArrivalHoldSeconds > 0.0 && !double.IsInfinity(unit.ArrivalHoldSeconds);
                if (holdApplied)
                {
                    InGameAssert.ApproxEqual(tRot, unit.ArrivalAlignPeriodSeconds, 1e-9,
                        $"the landing-only hold must align the LIVE {duna.bodyName} rotation period (the pre-existing single-period contract)");
                    InGameAssert.IsTrue(
                        captured.Exists(l => l.Contains("[Reaim]") && l.Contains("ARRIVAL HOLD")
                            && l.Contains("kind=rotation") && l.Contains(mission.Name)),
                        "the landing-only mission must log the pre-existing ARRIVAL HOLD kind=rotation line");
                }

                ParsekLog.Info(Tag,
                    $"JointArrivalHold control: PASS landing-only stays single-period - "
                    + $"holdApplied={holdApplied} hold={unit.ArrivalHoldSeconds.ToString("R", IC)}s "
                    + $"Talign={unit.ArrivalAlignPeriodSeconds.ToString("R", IC)}s "
                    + $"jointSentinels=NaN/NaN/0 amber={(unit.ArrivalAmberReason ?? "none")}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                MissionLoopUnitBuilder.SuppressLogging = priorBuilderSuppress;
                MissionPeriodicity.SuppressLogging = priorPeriodicitySuppress;
            }
        }

        // -----------------------------------------------------------------
        // Fixture + pipeline
        // -----------------------------------------------------------------

        // Live-body preconditions (mirrors the M5 gate): Kerbin/Duna/Sun present, shared
        // parent, distinct orbital periods. A non-stock pack skips cleanly, naming why.
        private static void SkipUnlessStockGeometry(out CelestialBody kerbin, out CelestialBody duna)
        {
            kerbin = FindBody("Kerbin");
            duna = FindBody("Duna");
            if (kerbin == null || duna == null)
                InGameAssert.Skip("Kerbin/Duna not in FlightGlobals.Bodies (non-stock pack) - cannot drive the live re-aim geometry");
            if (kerbin.referenceBody == null || duna.referenceBody == null
                || kerbin.referenceBody != duna.referenceBody)
                InGameAssert.Skip("Kerbin and Duna do not share a parent in this pack - not a cross-parent single-hop transfer");
            if (kerbin.orbit == null || duna.orbit == null
                || System.Math.Abs(kerbin.orbit.period - duna.orbit.period) < 1.0)
                InGameAssert.Skip("Kerbin/Duna orbital periods are degenerate/equal - no synodic period, re-aim cannot schedule");
        }

        /// <summary>
        /// Builds the synthetic mission tree via the SHARED pure fixture
        /// (<see cref="BuildJointFixture"/>, also driven headlessly by
        /// <c>RealSaveMissionFinderTests</c> so the shape cannot drift between the two gates)
        /// and drives the REAL <see cref="MissionLoopUnitBuilder.TryBuildLoopUnitForSelection"/>
        /// over explicit lists (store-free). Skips when the live classifier declines the
        /// synthetic chain (a stock rebalance moved a parent) or the builder yields no unit.
        /// </summary>
        private static GhostPlaybackLogic.LoopUnit BuildUnitThroughRealBuilder(
            string idPrefix, CelestialBody kerbin, CelestialBody duna,
            bool includeStationRendezvous, uint stationPid, IBodyInfo bodyInfo,
            out Mission mission)
        {
            BuildJointFixture(
                idPrefix, kerbin.bodyName, kerbin.referenceBody.bodyName, duna.bodyName,
                includeStationRendezvous, stationPid,
                out mission, out RecordingTree tree, out Recording rec);

            // Sanity-gate the chain against the LIVE classifier (the M5 pattern): a declined
            // fixture skips rather than silently proving nothing.
            ReaimMissionPlan classified = ReaimClassifier.Classify(rec.OrbitSegments, bodyInfo);
            if (!classified.Supported)
                InGameAssert.Skip($"live classifier declined the synthetic Kerbin->Duna chain (reason='{classified.Reason}') - fixture needs re-pinning for this body graph");

            // The REAL production entry, over explicit lists (the same TryBuildMissionUnit
            // pipeline Build and RouteOrchestrator.ResolveLoopUnit run) - no store mutation.
            bool built = MissionLoopUnitBuilder.TryBuildLoopUnitForSelection(
                mission,
                new List<RecordingTree> { tree },
                new List<Recording> { rec },
                LoopTiming.DefaultLoopIntervalSeconds,
                bodyInfo,
                TransitedBodyRotationMode.Loose,
                out GhostPlaybackLogic.LoopUnit unit);
            InGameAssert.IsTrue(built,
                "the REAL builder must resolve a loop unit for the synthetic mission (no unit => the shape never reaches the arrival-hold path)");
            return unit;
        }

        /// <summary>
        /// The PURE joint-gate fixture: one transfer member whose OrbitSegments carry the
        /// launch-parking -&gt; common-ancestor -&gt; target SOI chain (the M5 gate's proven
        /// classifier shape), whose TrackSections carry the launch surface leg (earliest
        /// surface =&gt; launch body + the surface/orbit hand-off emitting Rotation(launch)),
        /// the target landing surface leg (emitting Rotation(target), the landing
        /// constraint), and - for the joint shape - a Relative rendezvous section anchored to
        /// <paramref name="stationPid"/> (emitting the VesselOrbital station constraint
        /// through the real classifier). Sections carry no frames; the extractor resolves
        /// their body from the temporally overlapping OrbitSegment. No Unity, no stores, no
        /// asserts - shared verbatim by the in-game gate and the headless
        /// <c>RealSaveMissionFinderTests</c> mirror so the two gates always drive the SAME
        /// shape.
        /// </summary>
        internal static void BuildJointFixture(
            string idPrefix, string launchBody, string commonAncestor, string targetBody,
            bool includeStationRendezvous, uint stationPid,
            out Mission mission, out RecordingTree tree, out Recording rec)
        {
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    bodyName = launchBody, startUT = ParkStartUT, endUT = DepartureUT,
                    semiMajorAxis = 700000.0, eccentricity = 0.0, epoch = ParkStartUT,
                    isPredicted = false,
                },
                new OrbitSegment
                {
                    bodyName = commonAncestor, startUT = DepartureUT, endUT = ArrivalUT,
                    semiMajorAxis = 1.5e10, eccentricity = 0.2, epoch = DepartureUT,
                    isPredicted = false,
                },
                new OrbitSegment
                {
                    bodyName = targetBody, startUT = ArrivalUT, endUT = ArrivalEndUT,
                    semiMajorAxis = 500000.0, eccentricity = 0.1, epoch = ArrivalUT,
                    isPredicted = false,
                },
            };

            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.SurfaceStationary,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = ParkStartUT, endUT = ParkStartUT + 60.0,
                    frames = new List<TrajectoryPoint>(),
                },
                new TrackSection
                {
                    environment = SegmentEnvironment.SurfaceStationary,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = ArrivalUT + 300.0, endUT = ArrivalEndUT,
                    frames = new List<TrajectoryPoint>(),
                },
            };
            if (includeStationRendezvous)
            {
                sections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = ArrivalUT + 50.0, endUT = ArrivalUT + 150.0,
                    anchorVesselId = stationPid,
                    frames = new List<TrajectoryPoint>(),
                });
            }

            string treeId = idPrefix + "-tree";
            string recId = idPrefix + "-transfer";
            rec = new Recording
            {
                RecordingId = recId,
                VesselName = "MMIS4 Joint Gate Transport",
                ChainId = "C0",
                ChainIndex = 0,
                ChainBranch = 0,
                IsDebris = false,
                VesselPersistentId = 424_242u,
                ExplicitStartUT = ParkStartUT,
                ExplicitEndUT = ArrivalEndUT,
                OrbitSegments = segments,
                TrackSections = sections,
            };
            tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = recId,
            };
            tree.Recordings[rec.RecordingId] = rec;

            mission = new Mission
            {
                Id = idPrefix + "-mission",
                TreeId = treeId,
                Name = idPrefix,
                LoopPlayback = true,
                LoopTimeUnit = LoopTimeUnit.Auto,
                LoopAnchorUT = LoopEnabledUT,
            };
        }

        // Independent modular arithmetic (deliberately NOT MissionPeriodicity.CircularPhaseError,
        // so the dual-satisfaction assertion does not share code with the implementation it
        // checks): distance of x from the nearest whole multiple of period, in [0, period/2].
        private static double IndependentCircularError(double x, double period)
        {
            double m = x % period;
            if (m < 0.0)
                m += period;
            return System.Math.Min(m, period - m);
        }

        private static CelestialBody FindBody(string name)
        {
            if (FlightGlobals.Bodies == null)
                return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody b = FlightGlobals.Bodies[i];
                if (b != null && b.bodyName == name)
                    return b;
            }
            return null;
        }

        /// <summary>
        /// Delegating <see cref="IBodyInfo"/> over the live <see cref="FlightGlobalsBodyInfo"/>:
        /// answers <see cref="TryGetVesselOrbit"/> for ONE synthetic station pid with a
        /// closed orbit around the destination and forwards EVERYTHING else (all celestial
        /// data, every other vessel pid) to the live instance. The narrowest possible seam:
        /// a live station vessel cannot exist in an arbitrary save, and spawning one would
        /// mutate the save the batch runs in.
        /// </summary>
        private sealed class StationInjectingBodyInfo : IBodyInfo
        {
            private readonly IBodyInfo live;
            private readonly uint stationPid;
            private readonly double stationPeriodSeconds;
            private readonly string stationBodyName;

            public StationInjectingBodyInfo(
                IBodyInfo live, uint stationPid, double stationPeriodSeconds, string stationBodyName)
            {
                this.live = live;
                this.stationPid = stationPid;
                this.stationPeriodSeconds = stationPeriodSeconds;
                this.stationBodyName = stationBodyName;
            }

            public double RotationPeriod(string bodyName) => live.RotationPeriod(bodyName);
            public double OrbitPeriod(string bodyName) => live.OrbitPeriod(bodyName);
            public string ReferenceBodyName(string bodyName) => live.ReferenceBodyName(bodyName);
            public double SoiRadius(string bodyName) => live.SoiRadius(bodyName);
            public double OrbitalVelocity(string bodyName) => live.OrbitalVelocity(bodyName);
            public double GravParameter(string bodyName) => live.GravParameter(bodyName);
            public double Radius(string bodyName) => live.Radius(bodyName);

            public bool TryGetVesselOrbit(
                uint vesselPid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            {
                if (vesselPid == stationPid)
                {
                    periodSeconds = stationPeriodSeconds;
                    orbitBodyName = stationBodyName;
                    return true;
                }
                return live.TryGetVesselOrbit(vesselPid, recordedVesselGuid, out periodSeconds, out orbitBodyName);
            }
        }
    }
}
