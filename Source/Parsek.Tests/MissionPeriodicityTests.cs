using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    // Phase 0 of mission periodicity (docs/dev/design-mission-periodicity.md): the pure
    // constraint extractor. These drive MissionPeriodicity.ExtractConstraints with synthetic
    // recordings (TrackSections + OrbitSegments) and a fake IBodyInfo, covering every case in
    // the design's Test Plan. Each test states the regression it guards.
    [Collection("Sequential")]
    public class MissionPeriodicityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionPeriodicityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            // PhaseLock SKIPPED is now VerboseRateLimited (was Info), so the assertion that
            // it fired needs verbose explicitly enabled.
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStructureBuilder.SuppressLogging = false;
            MissionPeriodicity.SuppressLogging = false;
            MissionLoopUnitBuilder.SuppressLogging = false;
            MissionPeriodicity.ResetDriftAmberLogForTesting();
            MissionLoopUnitBuilder.ResetArrivalAmberLogForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = false;
            MissionPeriodicity.SuppressLogging = false;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        // --- Fake IBodyInfo (synthetic stock-like system) ---

        // Stock-like values (read at "build time" through the seam, never hardcoded in
        // production). Kerbin sidereal day ~21549 s, Mun orbit ~138984 s.
        private const double KerbinRotation = 21549.425;
        private const double KerbinOrbit = 9203545.0;     // around the Sun
        private const double MunOrbit = 138984.38;        // around Kerbin
        private const double MunRotation = 138984.38;     // tidally locked == orbit period
        private const double MinmusOrbit = 1077311.0;     // around Kerbin
        private const double DunaOrbit = 17315400.0;      // around the Sun

        private sealed class FakeBodyInfo : IBodyInfo
        {
            public readonly Dictionary<string, double> Rotation = new Dictionary<string, double>();
            public readonly Dictionary<string, double> Orbit = new Dictionary<string, double>();
            public readonly Dictionary<string, string> Parent = new Dictionary<string, string>();
            public readonly Dictionary<string, double> Soi = new Dictionary<string, double>();
            public readonly Dictionary<string, double> Velocity = new Dictionary<string, double>();
            public readonly Dictionary<string, double> Mu = new Dictionary<string, double>();
            // M4a: configurable live vessel orbits (pid -> period + orbited body); a pid not in the
            // dict does not resolve (vanished / hyperbolic anchor).
            public readonly Dictionary<uint, (double period, string body)> VesselOrbits =
                new Dictionary<uint, (double period, string body)>();

            public double RotationPeriod(string b) => Rotation.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double OrbitPeriod(string b) => Orbit.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public string ReferenceBodyName(string b) => Parent.TryGetValue(b ?? "", out string v) ? v : null;
            public double SoiRadius(string b) => Soi.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double OrbitalVelocity(string b) => Velocity.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double GravParameter(string b) => Mu.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double Radius(string b) => 6.0e5;
            // M4a: optional LIVE launch guid per pid; a recorded guid that conclusively
            // differs makes the pid a DIFFERENT launch of the same craft (craft-baked pid), so
            // it does not resolve. Absent entry = unknown live guid = pid-only fallback.
            public readonly Dictionary<uint, string> VesselGuids = new Dictionary<uint, string>();

            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid, out double periodSeconds, out string orbitBodyName)
            {
                if (VesselOrbits.TryGetValue(pid, out var o)
                    && !(VesselGuids.TryGetValue(pid, out string liveGuid)
                        && VesselLaunchIdentity.GuidsConclusivelyDiffer(recordedVesselGuid, liveGuid)))
                {
                    periodSeconds = o.period;
                    orbitBodyName = o.body;
                    return true;
                }
                periodSeconds = double.NaN;
                orbitBodyName = null;
                return false;
            }
        }

        // A stock-like Sun/Kerbin/Mun/Minmus/Duna fake.
        private static FakeBodyInfo StockFake()
        {
            var f = new FakeBodyInfo();
            // Rotation periods.
            f.Rotation["Kerbin"] = KerbinRotation;
            f.Rotation["Mun"] = MunRotation;
            f.Rotation["Minmus"] = 40400.0;
            f.Rotation["Duna"] = 65517.86;
            f.Rotation["Sun"] = 432000.0;
            // Orbit periods (about the reference body).
            f.Orbit["Kerbin"] = KerbinOrbit;
            f.Orbit["Mun"] = MunOrbit;
            f.Orbit["Minmus"] = MinmusOrbit;
            f.Orbit["Duna"] = DunaOrbit;
            f.Orbit["Sun"] = double.NaN;
            // Parents (reference bodies).
            f.Parent["Kerbin"] = "Sun";
            f.Parent["Mun"] = "Kerbin";
            f.Parent["Minmus"] = "Kerbin";
            f.Parent["Duna"] = "Sun";
            f.Parent["Sun"] = null;
            // SOI radii + velocities (only used by the Phase 2 tolerance; present for completeness).
            f.Soi["Mun"] = 2429559.0;
            f.Soi["Minmus"] = 2247428.0;
            f.Soi["Duna"] = 47921949.0;
            f.Velocity["Mun"] = 543.0;
            f.Velocity["Minmus"] = 274.0;
            f.Velocity["Duna"] = 5670.0;
            return f;
        }

        // --- Synthetic recording builders (direct Recording construction) ---

        private static TrajectoryPoint Pt(double ut, string body)
        {
            return new TrajectoryPoint { ut = ut, bodyName = body, rotation = Quaternion.identity };
        }

        // A controlled (non-debris) member recording with a single surface/atmospheric section.
        private static Recording SurfaceLeg(string id, double start, double end, string body)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "V",
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                StartBodyName = body,
                SegmentBodyName = body
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = start,
                endUT = end,
                frames = new List<TrajectoryPoint> { Pt(start, body), Pt(end, body) },
                checkpoints = new List<OrbitSegment>()
            });
            return rec;
        }

        // A controlled member recording with an inertial orbit section only (ExoBallistic),
        // backed by a flat OrbitSegment on the given body.
        private static Recording OrbitLeg(string id, double start, double end, string body,
            bool predicted = false)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "V",
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                StartBodyName = body,
                SegmentBodyName = body
            };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = start,
                endUT = end,
                bodyName = body,
                semiMajorAxis = 700000,
                isPredicted = predicted
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = start,
                endUT = end,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = start, endUT = end, bodyName = body,
                        semiMajorAxis = 700000, isPredicted = predicted }
                }
            });
            return rec;
        }

        // Adds an orbit segment (and matching checkpoint section) on another body to an existing
        // leg - the SOI handoff. checkpointOnly = true emits ONLY the OrbitalCheckpoint section's
        // checkpoint (no flat OrbitSegment) to model a backgrounded / on-rails SOI change.
        private static Recording WithSoiEntry(Recording rec, double start, double end, string body,
            bool checkpointOnly = false)
        {
            var seg = new OrbitSegment
            {
                startUT = start,
                endUT = end,
                bodyName = body,
                semiMajorAxis = -720000,
                eccentricity = 1.38
            };
            if (!checkpointOnly)
                rec.OrbitSegments.Add(seg);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = start,
                endUT = end,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { seg }
            });
            if (end > rec.ExplicitEndUT)
                rec.ExplicitEndUT = end;
            return rec;
        }

        private static RecordingTree TreeOf(string id, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = id,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null
            };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            return tree;
        }

        // Builds the (view, compRoots) read models for a tree.
        private static (MissionThroughLineView, List<MissionCompositionNode>) Models(RecordingTree tree)
        {
            var structure = MissionStructureBuilder.Build(tree);
            var view = MissionThroughLineBuilder.Build(structure);
            var compRoots = MissionCompositionBuilder.Build(structure);
            return (view, compRoots);
        }

        private static ConstraintExtraction Extract(RecordingTree tree, IBodyInfo bodyInfo,
            HashSet<string> excluded = null, List<Recording> extraCommitted = null)
        {
            var committed = new List<Recording>(tree.Recordings.Values);
            if (extraCommitted != null)
                committed.AddRange(extraCommitted); // e.g. a station's own recording (another tree)
            var (view, compRoots) = Models(tree);
            return MissionPeriodicity.ExtractConstraints(
                view, compRoots, committed, excluded ?? new HashSet<string>(), bodyInfo);
        }

        // ===================== Tests =====================

        [Fact]
        public void Extract_SingleBodyOrbit_NoSurface_EmptyConstraintSet()
        {
            // Guards: a bare inertial orbit (ascent trimmed off) imposes NO phase constraint -
            // we never invent a rotation lock for a single-body orbit. P will collapse to
            // MinCycleDuration (Phase 1).
            var tree = TreeOf("t", OrbitLeg("o", 1000, 2000, "Kerbin"));
            var ex = Extract(tree, StockFake());

            Assert.Empty(ex.Constraints);
            Assert.Equal(Support.Supported, ex.Support);
            Assert.Equal("Kerbin", ex.LaunchBodyName); // launch body = earliest orbit body
            Assert.Equal(1000.0, ex.UT0);
        }

        [Fact]
        public void Extract_LaunchPlusKerbinOrbit_OneRotationConstraint()
        {
            // Guards: a surface/atmospheric segment produces a Rotation constraint; the inertial
            // orbit that follows it adds NOTHING new (rotation inheritance is a property of the
            // included set, already covered by the surface segment).
            var surface = SurfaceLeg("s", 100, 200, "Kerbin");
            var orbit = OrbitLeg("o", 200, 800, "Kerbin");
            // Chain them as a continuous vessel so they form one through-line.
            surface.ChainId = "C"; surface.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", surface, orbit);

            var ex = Extract(tree, StockFake());

            Assert.Single(ex.Constraints);
            Assert.Equal(ConstraintKind.Rotation, ex.Constraints[0].Kind);
            Assert.Equal("Kerbin", ex.Constraints[0].BodyName);
            Assert.Equal(KerbinRotation, ex.Constraints[0].PeriodSeconds);
            Assert.Equal(0.0, ex.Constraints[0].PhaseOffsetSeconds); // surface starts at UT0
            Assert.Equal(Support.Supported, ex.Support);
            Assert.Equal("Kerbin", ex.LaunchBodyName);
        }

        [Fact]
        public void Extract_AtmosphericOnly_NoOrbit_NoRotationConstraint()
        {
            // Guards (Q3 fix): a pure atmospheric/surface arc of B with NO inertial orbit of B to
            // hand off to imposes NO phase constraint - it is recorded surface-relative and renders
            // at its correct ground location at ANY universe time. The rotation constraint requires
            // the surface<->inertial-orbit hand-off, NOT a bare surface segment. Without it the
            // config is unconstrained and Solve collapses P to MinCycleDuration (free loop).
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin"); // atmospheric ascent only, no orbit
            var tree = TreeOf("t", ascent);

            var ex = Extract(tree, StockFake());

            Assert.Empty(ex.Constraints); // no Rotation(Kerbin) - no hand-off
            Assert.Equal(Support.Supported, ex.Support);
            Assert.Equal("Kerbin", ex.LaunchBodyName);

            // End-to-end: an empty constraint set solves to the free-loop MinCycleDuration.
            var sol = MissionPeriodicity.Solve(
                ex.Constraints, ex.Support, ex.UT0, ex.UT0, StockFake());
            Assert.Equal(LoopTiming.MinCycleDuration, sol.P);
            Assert.Equal("unconstrained", sol.Method);
        }

        [Fact]
        public void Extract_LaunchPlusOrbit_StillLocksRotation_HandoffRegressionGuard()
        {
            // Guards (Q3 fix regression): the launch + Kerbin orbit config (an atmospheric ascent
            // that hands off to an inertial Kerbin orbit) STILL yields Rotation(Kerbin) - the
            // hand-off case must keep locking. This is the partner guard to the atmospheric-only
            // test above: surface alone -> no lock; surface + orbit of the same body -> lock.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var orbit = OrbitLeg("o", 1100, 5000, "Kerbin");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", ascent, orbit);

            var ex = Extract(tree, StockFake());

            PhaseConstraint rot = ex.Constraints.Single(c => c.Kind == ConstraintKind.Rotation);
            Assert.Equal("Kerbin", rot.BodyName);
            Assert.Equal(KerbinRotation, rot.PeriodSeconds);
            Assert.Equal(Support.Supported, ex.Support);

            // End-to-end: the single Rotation locks to the rotation period (not the free loop).
            var sol = MissionPeriodicity.Solve(
                ex.Constraints, ex.Support, ex.UT0, ex.UT0, StockFake());
            Assert.Equal(KerbinRotation, sol.P);
            Assert.Equal("single-rotation", sol.Method);
        }

        [Fact]
        public void Extract_MunMission_RotationKerbinPlusOrbitalMunDirectChild()
        {
            // Guards: an SOI entry into a direct-child target (Mun from Kerbin) produces an
            // Orbital constraint with the right offset and same-parent (RelativeToParent==false);
            // the launch ascent still produces Rotation(Kerbin). The flagship two-constraint case.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            // Mun SOI entry at 1600.
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);

            var ex = Extract(tree, StockFake());

            Assert.Equal(2, ex.Constraints.Count);
            // Ordered by recorded UT: Rotation(Kerbin) at offset 0, Orbital(Mun) at offset 600.
            PhaseConstraint rot = ex.Constraints.Single(c => c.Kind == ConstraintKind.Rotation);
            PhaseConstraint orb = ex.Constraints.Single(c => c.Kind == ConstraintKind.Orbital);
            Assert.Equal("Kerbin", rot.BodyName);
            Assert.Equal(KerbinRotation, rot.PeriodSeconds);
            Assert.Equal(0.0, rot.PhaseOffsetSeconds);
            Assert.Equal("Mun", orb.BodyName);
            Assert.Equal(MunOrbit, orb.PeriodSeconds);
            Assert.False(orb.RelativeToParent);                 // Mun is a direct child of Kerbin
            Assert.Equal(600.0, orb.PhaseOffsetSeconds);        // 1600 - 1000
            Assert.Equal(Support.Supported, ex.Support);
            Assert.Equal("Kerbin", ex.LaunchBodyName);
        }

        [Fact]
        public void Extract_CrossParentDuna_UnsupportedCrossParent()
        {
            // Guards: a heliocentric (sibling) target never emits a same-parent orbit.period
            // constraint - it is detected as cross-parent and reported "not yet supported"
            // (Phase 4). The constraint is still recorded (RelativeToParent==true) for diagnostics.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 5000, "Kerbin");
            WithSoiEntry(transfer, 5000, 6000, "Duna"); // Sun-orbiting sibling of Kerbin
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);

            var ex = Extract(tree, StockFake());

            Assert.Equal(Support.UnsupportedCrossParent, ex.Support);
            PhaseConstraint orb = ex.Constraints.Single(c => c.Kind == ConstraintKind.Orbital);
            Assert.Equal("Duna", orb.BodyName);
            Assert.True(orb.RelativeToParent); // cross-parent flagged
            Assert.Contains("Duna", ex.UnsupportedReason);
        }

        [Fact]
        public void Extract_TransientFlyby_OrbitalConstraintPresent()
        {
            // Guards: a NON-capturing flyby (the recording still only reaches the body where it
            // will be) is just as binding as a capture - the SOI-change rule registers it via the
            // bodyName field. The transfer enters Mun's SOI then leaves back to Kerbin.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 1900, "Mun");    // brief Mun pass
            WithSoiEntry(transfer, 1900, 3000, "Kerbin"); // back out to Kerbin (no capture)
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);

            var ex = Extract(tree, StockFake());

            Assert.Contains(ex.Constraints, c => c.Kind == ConstraintKind.Orbital && c.BodyName == "Mun");
            Assert.Equal(Support.Supported, ex.Support); // same-parent Mun pass is solvable
        }

        [Fact]
        public void Extract_BackgroundedCheckpointBridgedSoi_SurfacesBodyChange()
        {
            // Guards: a backgrounded/on-rails SOI change that only appears in the
            // OrbitalCheckpoint section's checkpoints (NOT the flat OrbitSegments cache) still
            // surfaces the Mun bodyName change - otherwise a backgrounded transfer would silently
            // extract zero Orbital(Mun) constraints and mis-schedule (CLAUDE.md / design Edge case).
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun", checkpointOnly: true); // checkpoint only
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);

            // Confirm the flat cache has NO Mun segment (only the checkpoint section does).
            Assert.DoesNotContain(transfer.OrbitSegments, s => s.bodyName == "Mun");

            var ex = Extract(tree, StockFake());

            Assert.Contains(ex.Constraints, c => c.Kind == ConstraintKind.Orbital && c.BodyName == "Mun");
        }

        [Fact]
        public void Extract_PredictedTail_DoesNotEmitConstraint()
        {
            // Guards: a predicted (extrapolated ballistic) tail into Mun is NOT a recorded
            // intercept and must not become an Orbital constraint.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            // Predicted Mun segment.
            var predicted = new OrbitSegment { startUT = 1600, endUT = 2000, bodyName = "Mun",
                semiMajorAxis = -720000, isPredicted = true };
            transfer.OrbitSegments.Add(predicted);
            transfer.ExplicitEndUT = 2000;
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);

            var ex = Extract(tree, StockFake());

            Assert.DoesNotContain(ex.Constraints, c => c.Kind == ConstraintKind.Orbital && c.BodyName == "Mun");
        }

        [Fact]
        public void Extract_AscentTrimmed_DropsRotationConstraint()
        {
            // Guards: trimming the launch interval away removes the inherited rotation constraint
            // (the bare inertial orbit alone is free). A pod "L" records a surface section [100,200]
            // then continues to orbit [200,800]; a probe decouples at 200, creating two composition
            // intervals on L's through-line (launch [100,200] head "L", post-decouple orbit
            // [200,800] head "L/seg1"). Excluding "L" start-trims the pod to the decouple (200), so
            // the surface section falls outside the window and the rotation constraint drops.
            var pod = new Recording
            {
                RecordingId = "L",
                VesselName = "Pod",
                IsDebris = false,
                ExplicitStartUT = 100,
                ExplicitEndUT = 800,
                StartBodyName = "Kerbin",
                SegmentBodyName = "Kerbin"
            };
            // Surface ascent [100,200] then orbit [200,800], both Kerbin.
            pod.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 100,
                endUT = 200,
                frames = new List<TrajectoryPoint> { Pt(100, "Kerbin"), Pt(200, "Kerbin") },
                checkpoints = new List<OrbitSegment>()
            });
            pod.OrbitSegments.Add(new OrbitSegment { startUT = 200, endUT = 800, bodyName = "Kerbin",
                semiMajorAxis = 700000 });
            pod.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = 200,
                endUT = 800,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { new OrbitSegment { startUT = 200, endUT = 800,
                    bodyName = "Kerbin", semiMajorAxis = 700000 } }
            });
            // A probe decouples at 200 (a structural peel) -> the post-decouple survivor interval.
            var probe = new Recording
            {
                RecordingId = "p",
                VesselName = "Probe",
                IsDebris = false,
                ExplicitStartUT = 200,
                ExplicitEndUT = 260,
                StartBodyName = "Kerbin",
                ParentAnchorRecordingId = "L"
            };
            var tree = new RecordingTree { Id = "t", RootRecordingId = "L" };
            tree.Recordings["L"] = pod;
            tree.Recordings["p"] = probe;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dp",
                Type = BranchPointType.JointBreak,
                UT = 200,
                SplitCause = "DECOUPLE",
                ParentRecordingIds = new List<string> { "L" },
                ChildRecordingIds = new List<string> { "p" }
            });

            // Sanity: with no exclusions the launch rotation constraint IS present.
            var full = Extract(tree, StockFake());
            Assert.Contains(full.Constraints, c => c.Kind == ConstraintKind.Rotation && c.BodyName == "Kerbin");

            // Exclude the launch interval ("L") + the peeled probe ("p"): start-trim the pod to 200,
            // dropping the surface ascent. The bare orbit is free -> no rotation constraint.
            var ex = Extract(tree, StockFake(), new HashSet<string> { "L", "p" });
            Assert.DoesNotContain(ex.Constraints, c => c.Kind == ConstraintKind.Rotation);
            Assert.Equal(200.0, ex.UT0); // start-trimmed to the decouple
        }

        [Fact]
        public void Extract_NoMembers_EmptyAndNaNUt0()
        {
            // Guards: an empty included set (no committed members) returns empty with NaN UT0
            // and Supported - it does not throw or invent a body.
            var tree = TreeOf("t", OrbitLeg("o", 100, 200, "Kerbin"));
            var committed = new List<Recording>(); // nothing committed
            var (view, compRoots) = Models(tree);

            var ex = MissionPeriodicity.ExtractConstraints(
                view, compRoots, committed, new HashSet<string>(), StockFake());

            Assert.Empty(ex.Constraints);
            Assert.True(double.IsNaN(ex.UT0));
            Assert.Equal(Support.Supported, ex.Support);
        }

        [Fact]
        public void Extract_ZeroRotationPeriod_NoRotationConstraint()
        {
            // Guards: a body with rotationPeriod == 0 (or NaN) produces no rotation constraint
            // (no divide-by-zero downstream). Includes the surface<->inertial-orbit hand-off so the
            // zero-rotation-period guard is the reason the constraint is dropped, not the missing
            // hand-off.
            var fake = StockFake();
            fake.Rotation["Kerbin"] = 0.0;
            var surface = SurfaceLeg("s", 100, 200, "Kerbin");
            var orbit = OrbitLeg("o", 200, 800, "Kerbin");
            surface.ChainId = "C"; surface.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", surface, orbit);

            var ex = Extract(tree, fake);

            Assert.DoesNotContain(ex.Constraints, c => c.Kind == ConstraintKind.Rotation);
        }

        [Fact]
        public void Extract_RetrogradeRotation_UsesMagnitude()
        {
            // Guards: a retrograde (negative) rotation period is read by magnitude, not skipped.
            // Needs the surface<->inertial-orbit hand-off (surface + orbit of the same body) for the
            // Rotation(B) constraint to be emitted at all (a bare surface arc imposes no constraint).
            var fake = StockFake();
            fake.Rotation["Kerbin"] = -KerbinRotation;
            var surface = SurfaceLeg("s", 100, 200, "Kerbin");
            var orbit = OrbitLeg("o", 200, 800, "Kerbin");
            surface.ChainId = "C"; surface.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", surface, orbit);

            var ex = Extract(tree, fake);

            PhaseConstraint rot = ex.Constraints.Single(c => c.Kind == ConstraintKind.Rotation);
            Assert.Equal(KerbinRotation, rot.PeriodSeconds); // magnitude
        }

        [Fact]
        public void Extract_MunSurfaceAndIntercept_TidallyLockedSharePeriod()
        {
            // Guards: a Mun-surface segment's rotation constraint and a Mun intercept's orbital
            // constraint carry the SAME period (Mun is tidally locked, rotationPeriod==orbit.period)
            // - the joint-resonance solver collapses these for free (Phase 1 tidal-lock case).
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            var munLanding = SurfaceLeg("m", 2000, 2500, "Mun");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            munLanding.ChainId = "C"; munLanding.ChainIndex = 2;
            var tree = TreeOf("t", ascent, transfer, munLanding);

            var ex = Extract(tree, StockFake());

            PhaseConstraint munRot = ex.Constraints.Single(
                c => c.Kind == ConstraintKind.Rotation && c.BodyName == "Mun");
            PhaseConstraint munOrb = ex.Constraints.Single(
                c => c.Kind == ConstraintKind.Orbital && c.BodyName == "Mun");
            Assert.Equal(munRot.PeriodSeconds, munOrb.PeriodSeconds); // both == MunOrbit
            Assert.Equal(MunOrbit, munRot.PeriodSeconds);
        }

        [Fact]
        public void Extract_RendezvousRelativeSection_UnsupportedRendezvous()
        {
            // Guards: a controlled member with a Relative TrackSection anchored to another vessel
            // (a rendezvous/dock) is detected and reported unsupported - the body-only solver
            // cannot model alignment to a vessel.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            rendezvous.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Relative,
                source = TrackSectionSource.Active,
                startUT = 1200,
                endUT = 1500,
                anchorVesselId = 4242, // anchored to a live vessel
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            });
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);

            var ex = Extract(tree, StockFake());

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("rendezvous", ex.UnsupportedReason);
        }

        [Fact]
        public void Extract_LogsVerboseSummary()
        {
            // Guards: the per-extraction Verbose summary (the Diagnostic Logging contract) is
            // emitted with the mission tag, support flag, and constraint list.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);

            Extract(tree, StockFake());

            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("ExtractConstraints") &&
                l.Contains("support=Supported") && l.Contains("Orbital(Mun)"));
        }

        [Fact]
        public void Extract_MinmusAlsoDirectChild_Supported()
        {
            // Guards: Minmus (also a direct child of Kerbin) is same-parent and solvable, distinct
            // from Mun, with its own orbit period.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 2000, "Kerbin");
            WithSoiEntry(transfer, 2000, 2500, "Minmus");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);

            var ex = Extract(tree, StockFake());

            PhaseConstraint orb = ex.Constraints.Single(c => c.Kind == ConstraintKind.Orbital);
            Assert.Equal("Minmus", orb.BodyName);
            Assert.Equal(MinmusOrbit, orb.PeriodSeconds);
            Assert.False(orb.RelativeToParent);
            Assert.Equal(Support.Supported, ex.Support);
        }

        // ===================== M4a: VesselOrbital (station rendezvous) =====================
        // Tier 1 of docs/dev/design-mission-phasing-alignment.md: the extraction flip from
        // blanket UnsupportedRendezvous to a VesselOrbital constraint for the supported shape
        // (exactly ONE same-parent closed-orbit vessel anchor), the fail-closed rejects, the D3
        // drift amber, the tolerance, and the solver integration.

        private const double StationPeriod = 1800.0;       // a ~100 km LKO-ish station orbit
        private const uint StationPid = 4242;
        private const double KerbinMu = 3.5316e12;

        // Adds a vessel-anchored Relative section (a rendezvous/dock leg) to a member recording.
        private static Recording WithRendezvous(Recording rec, double start, double end,
            uint anchorPid, string anchorRecId = null)
        {
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Relative,
                source = TrackSectionSource.Active,
                startUT = start,
                endUT = end,
                anchorVesselId = anchorPid,
                anchorRecordingId = anchorRecId,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            });
            return rec;
        }

        // The canonical LKO-resupply shape: Kerbin ascent + Kerbin orbit with a rendezvous
        // Relative section [1200,1500] anchored to the station pid.
        private static RecordingTree ResupplyTree(uint anchorPid = StationPid,
            string anchorRecId = null)
        {
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1200, 1500, anchorPid, anchorRecId);
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            return TreeOf("t", ascent, rendezvous);
        }

        // A StockFake whose live save contains the station: pid -> (period, orbited body).
        private static FakeBodyInfo StationFake(
            double period = StationPeriod, string body = "Kerbin")
        {
            var f = StockFake();
            f.VesselOrbits[StationPid] = (period, body);
            f.Mu["Kerbin"] = KerbinMu;
            return f;
        }

        // Semi-major axis whose elliptical period around mu is t (inverse of OrbitalPeriod).
        private static double SmaForPeriod(double t, double mu)
            => Math.Pow(mu * Math.Pow(t / (2.0 * Math.PI), 2.0), 1.0 / 3.0);

        // A committed (non-member) station recording with one non-predicted OrbitSegment.
        private static Recording StationRecording(string id, double start, double end,
            string body, double sma)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Station",
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                StartBodyName = body,
                SegmentBodyName = body
            };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = start,
                endUT = end,
                bodyName = body,
                semiMajorAxis = sma
            });
            return rec;
        }

        [Fact]
        public void Extract_SingleSameParentAnchor_EmitsVesselOrbital()
        {
            // Test 1: the supported shape (exactly one same-parent closed-orbit anchor) emits a
            // VesselOrbital constraint - live period, offset = first rendezvous UT - UT0, anchor
            // pid - and Support stays Supported (no more blanket UnsupportedRendezvous).
            var ex = Extract(ResupplyTree(), StationFake());

            Assert.Equal(Support.Supported, ex.Support);
            Assert.Null(ex.UnsupportedReason);
            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal("Kerbin", vo.BodyName);                 // the ORBITED body
            Assert.Equal(StationPeriod, vo.PeriodSeconds);       // the LIVE period
            Assert.Equal(StationPid, vo.AnchorVesselPid);
            Assert.Equal(200.0, vo.PhaseOffsetSeconds);          // 1200 - 1000
            Assert.False(vo.RelativeToParent);
            Assert.Null(ex.DriftAmberReason);                    // no anchor recording -> no compare
            // The pad rotation is still extracted alongside (the LKO-resupply two-constraint shape).
            Assert.Contains(ex.Constraints,
                c => c.Kind == ConstraintKind.Rotation && c.BodyName == "Kerbin");
        }

        [Fact]
        public void Extract_MultipleSectionsSamePid_CollapseToEarliestUT()
        {
            // Test 2: several Relative sections to the SAME pid (approach, dock, undock, redock)
            // collapse to ONE constraint at the EARLIEST overlap UT (timeline rigidity aligns the
            // later same-vessel events automatically, design note 5.2).
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1400, 1500, StationPid);  // the LATER redock first in list
            WithRendezvous(rendezvous, 1200, 1300, StationPid);
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);

            var ex = Extract(tree, StationFake());

            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal(200.0, vo.PhaseOffsetSeconds); // earliest overlap (1200), not the redock
        }

        [Fact]
        public void Extract_TwoDistinctPids_UnsupportedRendezvous()
        {
            // Test 3: a multi-rendezvous tour (two DISTINCT vessel anchors in one window) stays
            // fail-closed - one constraint cannot phase two independent stations.
            var fake = StationFake();
            fake.VesselOrbits[5555] = (2400.0, "Kerbin"); // second station, also resolvable
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1200, 1300, StationPid);
            WithRendezvous(rendezvous, 1400, 1500, 5555);
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);

            var ex = Extract(tree, fake);

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("multi-rendezvous", ex.UnsupportedReason);
            Assert.DoesNotContain(ex.Constraints, c => c.Kind == ConstraintKind.VesselOrbital);
        }

        // A committed station recording (its own flight, typically another tree) the
        // anchor-recording-only sections resolve through.
        private static Recording StationRecording(string recId, uint pid, string guid = null)
            => new Recording { RecordingId = recId, VesselPersistentId = pid, RecordedVesselGuid = guid };

        [Fact]
        public void Extract_AnchorRecordingOnlySection_ResolvesThroughCommittedRecording()
        {
            // Test 4 (contract OVERTURNED by the 2026-06-11 playtest): the recorder deliberately
            // ZEROES anchorVesselId whenever it stamps anchorRecordingId (FlightRecorder
            // serialization checkpoints), so anchor-recording-only is the NORMAL recorded shape
            // for a station rendezvous. The pid resolves through the committed anchor recording
            // (recorded pid + launch guid), then the live orbit as usual.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1200, 1500, 0, "station-rec");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);
            var fake = StationFake();
            fake.VesselGuids[StationPid] = "11111111-1111-1111-1111-111111111111";
            var station = StationRecording("station-rec", StationPid,
                "11111111-1111-1111-1111-111111111111");

            var ex = Extract(tree, fake, extraCommitted: new List<Recording> { station });

            Assert.Equal(Support.Supported, ex.Support);
            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal(StationPid, vo.AnchorVesselPid);
            Assert.Equal(200.0, vo.PhaseOffsetSeconds);
        }

        [Fact]
        public void Extract_AnchorRecordingNotCommitted_UnsupportedRendezvous()
        {
            // An anchor-recording-only section whose recording id resolves to NOTHING in the
            // committed set cannot identify the live anchor - fail closed.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1200, 1500, 0, "station-rec");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);

            var ex = Extract(tree, StationFake());

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("not in the committed set", ex.UnsupportedReason);
        }

        [Fact]
        public void Extract_AnchorRecordingIsDifferentLaunch_UnsupportedRendezvous()
        {
            // The craft-baked-pid trap: the live vessel carrying the recorded pid is a DIFFERENT
            // launch of the same craft (launch guids conclusively differ) - it must not read as
            // the recorded station.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1200, 1500, 0, "station-rec");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);
            var fake = StationFake();
            fake.VesselGuids[StationPid] = "22222222-2222-2222-2222-222222222222";
            var station = StationRecording("station-rec", StationPid,
                "11111111-1111-1111-1111-111111111111");

            var ex = Extract(tree, fake, extraCommitted: new List<Recording> { station });

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("different launch", ex.UnsupportedReason);
        }

        [Fact]
        public void Extract_MixedPidAndRecordingSections_SameAnchor_OneConstraintAtEarliest()
        {
            // One pid-stamped section and one anchor-recording-only section to the SAME station
            // merge into a single constraint at the EARLIEST rendezvous (timeline rigidity,
            // design note 5.2).
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1200, 1250, 0, "station-rec"); // earliest, recId shape
            WithRendezvous(rendezvous, 1300, 1400, StationPid);       // later, pid shape
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);
            var station = StationRecording("station-rec", StationPid);

            var ex = Extract(tree, StationFake(), extraCommitted: new List<Recording> { station });

            Assert.Equal(Support.Supported, ex.Support);
            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal(200.0, vo.PhaseOffsetSeconds); // 1200 - ut0(1000)
        }

        [Fact]
        public void Extract_MutualDockAnchoring_ReattributesToPartner_OneConstraintAtFirstRendezvous()
        {
            // The 2026-06-11 retest topology (Depot resupply): the dock merge pulls the PARTNER's
            // segments into the tree, and anchoring is MUTUAL - the partner's sections anchor the
            // mission's own craft (BG recording relative to the active vessel), the craft's
            // post-undock section anchors the partner. The classifier partitions against the SELF
            // launch line: the partner-to-self section REATTRIBUTES to its owner (the partner),
            // the self-to-partner section is a direct target, and both merge to ONE constraint at
            // the FIRST rendezvous UT (the partner-side 1200, not the later direct 1400).
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            ascent.VesselPersistentId = 111;
            ascent.RecordedVesselGuid = "aaaaaaaa-1111-1111-1111-111111111111";
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            rendezvous.VesselPersistentId = 111;
            rendezvous.RecordedVesselGuid = "aaaaaaaa-1111-1111-1111-111111111111";
            var partner = OrbitLeg("partner-rec", 1150, 1600, "Kerbin");
            partner.VesselPersistentId = 222;
            partner.RecordedVesselGuid = "bbbbbbbb-2222-2222-2222-222222222222";
            WithRendezvous(partner, 1200, 1250, 111);            // partner -> self (mutual shape)
            WithRendezvous(rendezvous, 1400, 1450, 0, "partner-rec"); // self -> partner (direct)
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous, partner);
            var fake = StationFake();
            fake.VesselOrbits.Remove(StationPid);
            fake.VesselOrbits[222] = (1958.0, "Kerbin");

            var ex = Extract(tree, fake);

            Assert.Equal(Support.Supported, ex.Support);
            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal(222u, vo.AnchorVesselPid);
            Assert.Equal(200.0, vo.PhaseOffsetSeconds); // first rendezvous = the partner-side 1200
        }

        [Fact]
        public void Extract_AllSelfAnchoring_NoForeignAnchor_NoConstraintNoReject()
        {
            // A mission whose only vessel-anchored sections are intra-self pairs (its own
            // continuation segment anchoring its own launch line) has NO foreign rendezvous
            // target: no VesselOrbital constraint, and crucially NOT an UnsupportedRendezvous
            // reject - the mission keeps its normal body-rule Support.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            ascent.VesselPersistentId = 111;
            ascent.RecordedVesselGuid = "aaaaaaaa-1111-1111-1111-111111111111";
            var coast = OrbitLeg("o", 1100, 1600, "Kerbin");
            coast.VesselPersistentId = 111;
            coast.RecordedVesselGuid = "aaaaaaaa-1111-1111-1111-111111111111";
            WithRendezvous(coast, 1200, 1300, 111); // anchors its own launch line
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            coast.ChainId = "C"; coast.ChainIndex = 1;
            var tree = TreeOf("t", ascent, coast);

            var ex = Extract(tree, StockFake());

            Assert.Equal(Support.Supported, ex.Support);
            Assert.DoesNotContain(ex.Constraints, c => c.Kind == ConstraintKind.VesselOrbital);
        }

        [Fact]
        public void Extract_DriftAmber_BackfilledRecordingCoveringEarliestUT_StillCompares()
        {
            // Scoped-review finding pin: the EARLIEST rendezvous entry is pid-shape (no
            // recording id) and a LATER rec-shape entry backfills the drift-comparison
            // recording. The backfill is safe by construction: merged entries are guid-gated to
            // the SAME vessel, and the comparison only runs when the backfilled recording has a
            // segment COVERING the earliest UT - here it does (900-2000), so the drifted period
            // still ambers while the constraint keeps the earliest entry's UT.
            double recordedPeriod = StationPeriod * 1.056; // ~5.6% drift
            var station = StationRecording(
                "station-rec", 900, 2000, "Kerbin", SmaForPeriod(recordedPeriod, KerbinMu));
            station.VesselPersistentId = StationPid;
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1200, 1250, StationPid);        // earliest: pid-shape
            WithRendezvous(rendezvous, 1400, 1450, 0, "station-rec");  // later: rec-shape
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);

            var ex = Extract(tree, StationFake(), extraCommitted: new List<Recording> { station });

            Assert.Equal(Support.Supported, ex.Support);
            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal(200.0, vo.PhaseOffsetSeconds);   // the earliest entry wins the UT
            Assert.NotNull(ex.DriftAmberReason);          // the later entry's recording compares
            Assert.Contains("drifted", ex.DriftAmberReason);
        }

        [Fact]
        public void Extract_SamePidDifferentLaunches_UnsupportedRendezvous()
        {
            // Two anchor recordings share the craft-baked pid but are conclusively different
            // launches: two distinct physical anchors - multi-rendezvous, fail closed.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var rendezvous = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithRendezvous(rendezvous, 1200, 1250, 0, "station-a");
            WithRendezvous(rendezvous, 1300, 1400, 0, "station-b");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            rendezvous.ChainId = "C"; rendezvous.ChainIndex = 1;
            var tree = TreeOf("t", ascent, rendezvous);
            var stations = new List<Recording>
            {
                StationRecording("station-a", StationPid, "11111111-1111-1111-1111-111111111111"),
                StationRecording("station-b", StationPid, "22222222-2222-2222-2222-222222222222"),
            };

            var ex = Extract(tree, StationFake(), extraCommitted: stations);

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("multi-rendezvous", ex.UnsupportedReason);
        }

        [Fact]
        public void Extract_AnchorNotResolvable_UnsupportedRendezvous()
        {
            // Test 5: a vanished (recovered/deorbited) or hyperbolic anchor does not resolve a
            // live closed orbit - fail closed (design D1: never derive a period from recorded
            // data while the live anchor is gone).
            var fake = StockFake(); // no VesselOrbits entry: TryGetVesselOrbit false
            var ex = Extract(ResupplyTree(), fake);

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("not in save / no closed orbit", ex.UnsupportedReason);
        }

        [Fact]
        public void Extract_NonTransitedStation_UnsupportedRendezvous()
        {
            // Test 6 (M4c revision of the M4a cross-parent reject): a station orbiting a body
            // the mission's included window never TRANSITS (here the Mun - the resupply tree has
            // no Mun orbit segments) has no alignment basis - still fail closed. The M4c-lifted
            // shape (station around a TRANSITED body) is covered by the M4c tests below.
            var ex = Extract(ResupplyTree(), StationFake(body: "Mun"));

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("emitted no Orbital constraint", ex.UnsupportedReason);
        }

        [Fact]
        public void Extract_NullLaunchBody_VesselAnchor_UnsupportedRendezvous()
        {
            // Test 6b: an orbit-only / degenerate config with NO resolvable launch body but a
            // vessel anchor never emits a constraint (the same-parent check is meaningless), even
            // when the anchor itself resolves - rule 0 fires before resolution.
            var rec = new Recording
            {
                RecordingId = "r",
                VesselName = "V",
                IsDebris = false,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 1600,
                StartBodyName = "Kerbin",
                SegmentBodyName = "Kerbin"
            };
            WithRendezvous(rec, 1200, 1500, StationPid); // ONLY a Relative section: no launch body
            var tree = TreeOf("t", rec);

            var ex = Extract(tree, StationFake());

            Assert.Null(ex.LaunchBodyName);
            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("no launch body", ex.UnsupportedReason);
            Assert.DoesNotContain(ex.Constraints, c => c.Kind == ConstraintKind.VesselOrbital);
        }

        [Fact]
        public void Extract_ParentAnchoredRecording_RelativeSectionNotARendezvous()
        {
            // Test 7 (existing rule preserved): a parent-anchored (debris / decoupled-child)
            // recording's Relative sections anchor to its OWN parent - never a rendezvous, so the
            // mission stays Supported with no VesselOrbital even when the pid would resolve.
            var pod = SurfaceLeg("L", 100, 200, "Kerbin");
            pod.OrbitSegments.Add(new OrbitSegment { startUT = 200, endUT = 800,
                bodyName = "Kerbin", semiMajorAxis = 700000 });
            pod.ExplicitEndUT = 800; // continues past the decouple (as Extract_AscentTrimmed)
            var probe = new Recording
            {
                RecordingId = "p",
                VesselName = "Probe",
                IsDebris = false,
                ExplicitStartUT = 200,
                ExplicitEndUT = 260,
                StartBodyName = "Kerbin",
                ParentAnchorRecordingId = "L"
            };
            WithRendezvous(probe, 210, 250, StationPid); // parent-anchored: must not collect
            var tree = new RecordingTree { Id = "t", RootRecordingId = "L" };
            tree.Recordings["L"] = pod;
            tree.Recordings["p"] = probe;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dp",
                Type = BranchPointType.JointBreak,
                UT = 200,
                SplitCause = "DECOUPLE",
                ParentRecordingIds = new List<string> { "L" },
                ChildRecordingIds = new List<string> { "p" }
            });

            var ex = Extract(tree, StationFake());

            Assert.Equal(Support.Supported, ex.Support);
            Assert.DoesNotContain(ex.Constraints, c => c.Kind == ConstraintKind.VesselOrbital);
        }

        [Fact]
        public void Tolerance_VesselOrbital_ThreeDegreesOfPeriod_NotTheSoiFormula()
        {
            // Test 8: the VesselOrbital tolerance is period * 3deg/360 (design note 5.3; widened
            // from 1 degree on 2026-06-11 playtest evidence - see StationPhaseToleranceDegrees).
            // It must NOT fall into the Orbital SoiRadius/OrbitalVelocity formula - here the
            // constraint's BodyName is Mun, whose StockFake SOI tolerance would be ~4474 s, wildly
            // wrong for a 1800 s station orbit.
            var c = new PhaseConstraint
            {
                Kind = ConstraintKind.VesselOrbital,
                BodyName = "Mun",
                PeriodSeconds = StationPeriod,
                PhaseOffsetSeconds = 0.0,
                AnchorVesselPid = StationPid
            };
            double tol = MissionPeriodicity.ToleranceSecondsFor(c, StockFake());
            Assert.Equal(StationPeriod * 3.0 / 360.0, tol, 9); // 15 s, 3 degrees of the station orbit
            // And the schedule tolerance falls through unchanged (the Loose special-case is
            // Rotation-only, so a VesselOrbital is never loosened).
            Assert.Equal(tol, MissionPeriodicity.ScheduleToleranceSecondsFor(
                c, StockFake(), "Kerbin", TransitedBodyRotationMode.Loose), 9);
        }

        [Fact]
        public void Extract_DriftAmber_BeyondTolerance_SetsReason_SupportUnaffected()
        {
            // Test 11a (D3): the anchor recording's rendezvous-time orbit period differs from the
            // LIVE period by > 2% -> DriftAmberReason set; Support and the emitted (live) period
            // unaffected (display-only, live wins).
            double recordedPeriod = StationPeriod * 1.056; // ~5.6% drift
            var station = StationRecording(
                "station-rec", 900, 2000, "Kerbin", SmaForPeriod(recordedPeriod, KerbinMu));
            var tree = ResupplyTree(anchorRecId: "station-rec");
            var committed = new List<Recording>(tree.Recordings.Values) { station };
            var (view, compRoots) = Models(tree);

            var ex = MissionPeriodicity.ExtractConstraints(
                view, compRoots, committed, new HashSet<string>(), StationFake());

            Assert.Equal(Support.Supported, ex.Support);
            Assert.NotNull(ex.DriftAmberReason);
            Assert.Contains("drifted", ex.DriftAmberReason);
            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal(StationPeriod, vo.PeriodSeconds); // live period emitted, never the recorded
        }

        [Fact]
        public void Extract_DriftAmber_WithinTolerance_NoReason()
        {
            // Test 11b (D3): a recorded period within 2% of live -> no amber.
            double recordedPeriod = StationPeriod * 1.008; // ~0.8% drift, within tolerance
            var station = StationRecording(
                "station-rec", 900, 2000, "Kerbin", SmaForPeriod(recordedPeriod, KerbinMu));
            var tree = ResupplyTree(anchorRecId: "station-rec");
            var committed = new List<Recording>(tree.Recordings.Values) { station };
            var (view, compRoots) = Models(tree);

            var ex = MissionPeriodicity.ExtractConstraints(
                view, compRoots, committed, new HashSet<string>(), StationFake());

            Assert.Equal(Support.Supported, ex.Support);
            Assert.Null(ex.DriftAmberReason);
        }

        [Fact]
        public void Extract_DriftAmber_NoAnchorRecording_NoComparisonNoAmber()
        {
            // Test 11c (D3): no resolvable anchor recording (or no covering segment) -> no
            // comparison, no amber - a live-only (class b) anchor never false-positives.
            var ex = Extract(ResupplyTree(anchorRecId: "no-such-recording"), StationFake());

            Assert.Equal(Support.Supported, ex.Support);
            Assert.Null(ex.DriftAmberReason);
        }

        [Fact]
        public void Extract_DriftAmber_TransitionLoggedOncePerChange()
        {
            // Logging contract (plan 2.6): the drift-amber Info line fires once per TRANSITION
            // (set / clear), keyed per mission tag - repeated extractions with the same state log
            // nothing new.
            double recordedPeriod = StationPeriod * 1.056;
            var station = StationRecording(
                "station-rec", 900, 2000, "Kerbin", SmaForPeriod(recordedPeriod, KerbinMu));
            var tree = ResupplyTree(anchorRecId: "station-rec");
            var committed = new List<Recording>(tree.Recordings.Values) { station };
            var (view, compRoots) = Models(tree);

            var drifted = StationFake();
            MissionPeriodicity.ExtractConstraints(
                view, compRoots, committed, new HashSet<string>(), drifted);
            MissionPeriodicity.ExtractConstraints(
                view, compRoots, committed, new HashSet<string>(), drifted);
            Assert.Equal(1, logLines.Count(l =>
                l.Contains("[INFO]") && l.Contains("[MissionPeriodicity]")
                && l.Contains("Drift amber SET") && l.Contains("tree=t")));

            // The station boosts back to the recorded period -> the amber clears, logged once.
            var aligned = StationFake(period: recordedPeriod);
            MissionPeriodicity.ExtractConstraints(
                view, compRoots, committed, new HashSet<string>(), aligned);
            MissionPeriodicity.ExtractConstraints(
                view, compRoots, committed, new HashSet<string>(), aligned);
            Assert.Equal(1, logLines.Count(l =>
                l.Contains("[INFO]") && l.Contains("Drift amber CLEARED") && l.Contains("tree=t")));
        }

        [Fact]
        public void Solve_LoneVesselOrbital_LocksStationPeriod_SingleVesselOrbitalMethod()
        {
            // Test 12a: a lone VesselOrbital locks P to the station period with its own method
            // label (it must not read "single-rotation" or fall to the free loop).
            var sol = MissionPeriodicity.Solve(
                new[] { new PhaseConstraint
                {
                    Kind = ConstraintKind.VesselOrbital, BodyName = "Kerbin",
                    PeriodSeconds = StationPeriod, PhaseOffsetSeconds = 200.0,
                    AnchorVesselPid = StationPid
                } },
                Support.Supported, 1000.0, 1000.0, StockFake());

            Assert.True(sol.ShouldPhaseLock);
            Assert.Equal(StationPeriod, sol.P);
            Assert.Equal("single-vessel-orbital", sol.Method);
            Assert.True(sol.WithinTolerance);
        }

        [Fact]
        public void Solve_PadPlusStation_VesselOrbitalDominatesLikeOrbital()
        {
            // Test 12b: in dominant selection a VesselOrbital ranks WITH Orbital (an
            // intercept-style constraint), so it outranks the pad rotation; the joint best-fit
            // locks a whole multiple of the STATION period.
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Kerbin", KerbinRotation, 0.0),
                new PhaseConstraint
                {
                    Kind = ConstraintKind.VesselOrbital, BodyName = "Kerbin",
                    PeriodSeconds = StationPeriod, PhaseOffsetSeconds = 200.0,
                    AnchorVesselPid = StationPid
                }
            };
            Assert.Equal(1, MissionPeriodicity.SelectDominantConstraintIndex(constraints));

            var sol = MissionPeriodicity.Solve(
                constraints, Support.Supported, 1000.0, 1000.0, StockFake());
            Assert.True(sol.ShouldPhaseLock);
            double ratio = sol.P / StationPeriod;
            Assert.Equal(Math.Round(ratio), ratio, 6); // P = whole multiple of the station period
            Assert.Equal("joint-best-fit", sol.Method); // m=12 best re-aligns the pad (~50 s residual)
        }

        [Fact]
        public void Extract_VesselOrbital_LogsConstraintInSummary()
        {
            // Test 14a: the extraction summary line renders the new kind as
            // VesselOrbital(pid@Body) so the log shows WHICH station the loop phases against.
            Extract(ResupplyTree(), StationFake());

            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("ExtractConstraints") &&
                l.Contains("VesselOrbital(4242@Kerbin)") && l.Contains("support=Supported"));
        }

        [Fact]
        public void Extract_RejectedRendezvous_LogsReason()
        {
            // Test 14b: a reject flows through the summary's why= field with the specific reason
            // (here a station around a never-transited body), so a fail-closed shape is never a
            // silent branch.
            Extract(ResupplyTree(), StationFake(body: "Mun"));

            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("ExtractConstraints") &&
                l.Contains("support=UnsupportedRendezvous") &&
                l.Contains("emitted no Orbital constraint"));
        }

        // ===================== M4c: cross-parent station (Tier 2) =====================
        // docs/dev/plans/mission-station-arrival-hold.md: the rule-3 split (a station around a
        // TRANSITED body emits; emission never touches Support), the destination-station arrival
        // hold, and the D8 dual-constraint amber.

        // The cross-parent resupply shape: Kerbin ascent + Kerbin orbit, then a Duna orbit leg
        // carrying the rendezvous Relative section anchored to the station.
        private static RecordingTree CrossParentResupplyTree(uint anchorPid = StationPid)
        {
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            var arrival = OrbitLeg("a", 1600, 2600, "Duna");
            WithRendezvous(arrival, 1800, 2200, anchorPid);
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            arrival.ChainId = "C"; arrival.ChainIndex = 2;
            return TreeOf("t", ascent, transfer, arrival);
        }

        [Fact]
        public void Extract_CrossParentTransitedStation_EmitsVesselOrbital_SupportUntouched()
        {
            // M4c test 1a: a station orbiting the cross-parent DESTINATION (Duna, which the
            // mission transits - Orbital(Duna) emitted) emits the VesselOrbital constraint, and
            // the emit NEVER touches Support: the mission stays UnsupportedCrossParent (set by
            // the step-4 orbital scan), so the builder's re-aim branch still runs and the
            // arrival hold can consume the constraint. Flipping Support here would phase-lock
            // the mission and silently skip the re-aim transfer (the regression the
            // DestinationConstraintExtractor header warns about).
            var ex = Extract(CrossParentResupplyTree(), StationFake(body: "Duna"));

            Assert.Equal(Support.UnsupportedCrossParent, ex.Support);
            Assert.Contains("synodic", ex.UnsupportedReason); // the cross-parent reason, not a rendezvous reject
            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal("Duna", vo.BodyName);                 // the ORBITED body (the destination)
            Assert.Equal(StationPeriod, vo.PeriodSeconds);     // the LIVE station period
            Assert.Equal(StationPid, vo.AnchorVesselPid);
            Assert.Equal(800.0, vo.PhaseOffsetSeconds);        // 1800 - 1000
        }

        [Fact]
        public void Extract_SameParentTransitedStation_EmitsVesselOrbital_StaysSupported()
        {
            // M4c test 1b (the Mun-depot shape): a station orbiting a TRANSITED same-parent body
            // emits the constraint and Support stays Supported, so the zero-drift schedule + M4b
            // knob align the station with ZERO new scheduling code. The other polarity of the
            // "emit never touches Support" invariant.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            var munLeg = OrbitLeg("m", 1600, 2600, "Mun");
            WithRendezvous(munLeg, 1800, 2200, StationPid);
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            munLeg.ChainId = "C"; munLeg.ChainIndex = 2;
            var tree = TreeOf("t", ascent, transfer, munLeg);

            var ex = Extract(tree, StationFake(body: "Mun"));

            Assert.Equal(Support.Supported, ex.Support);
            Assert.Null(ex.UnsupportedReason);
            PhaseConstraint vo = ex.Constraints.Single(c => c.Kind == ConstraintKind.VesselOrbital);
            Assert.Equal("Mun", vo.BodyName);
            Assert.Equal(StationPid, vo.AnchorVesselPid);
            // The transited-body Orbital constraint coexists (the joint schedule solves both).
            Assert.Contains(ex.Constraints,
                c => c.Kind == ConstraintKind.Orbital && c.BodyName == "Mun");
        }

        [Fact]
        public void Extract_CrossParentStation_VanishedAnchor_Rejected()
        {
            // M4c test 4: the M4a anchor policy applies unchanged to the new shape - a vanished
            // (recovered/deorbited) anchor on a cross-parent destination fails closed to
            // UnsupportedRendezvous (which deliberately overwrites UnsupportedCrossParent for
            // the report; the re-aim transfer still runs off !phaseLocked, just with no station
            // constraint and no hold).
            var ex = Extract(CrossParentResupplyTree(), StockFake()); // no VesselOrbits entry

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("not in save / no closed orbit", ex.UnsupportedReason);
        }

        [Fact]
        public void Build_SameParentDepot_AttachesScheduleNoHold()
        {
            // M4c shape A end-to-end through the live Build if-chain: pad Rotation(Kerbin) +
            // Orbital(Mun) + VesselOrbital(station@Mun) is a drifting multi-constraint config -
            // the zero-drift schedule attaches (the station is just one more constraint), and
            // the arrival hold stays zero (the hold is re-aim-only).
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            WithRendezvous(transfer, 1700, 1900, StationPid);
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 1000.0 + 1.5 * MunOrbit);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StationFake(body: "Mun"));

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.NotNull(unit.RelaunchSchedule);
            Assert.Equal(0.0, unit.ArrivalHoldSeconds);
            Assert.Null(unit.ArrivalAmberReason);
            // The discriminating residual pin (review finding 5): the scheduled launches must
            // honor the STATION tolerance (period * 3deg/360 = 15s for the 1800s fake station).
            // Had the station constraint been silently dropped from the solve, pad+Mun windows
            // would land at an arbitrary station phase and fail this with ~98% probability per
            // launch (the inputs are fixed, so the test is deterministic either way).
            double stationTol = StationPeriod * (3.0 / 360.0);
            double ut0 = 1000.0;
            double launch = unit.RelaunchSchedule.FirstLaunchUT;
            for (int i = 0; i < 3; i++)
            {
                double residual = MissionPeriodicity.CircularPhaseError(launch - ut0, StationPeriod);
                Assert.True(residual <= stationTol + 1e-6,
                    $"launch {i} at {launch} has station residual {residual} > {stationTol}");
                launch = unit.RelaunchSchedule.NextLaunchAfter(launch);
            }
        }

        [Fact]
        public void ScheduleK_TwoShiftableConstraints_JointlySatisfied()
        {
            // M4c (plan test 5, knob half): the M4b shiftable-group scan serves TWO shiftable
            // constraints jointly (the Mun-window Orbital and the Mun-depot VesselOrbital both
            // sit after the phasing loiter, so one rev shift d must satisfy both). Synthetic
            // commensurate geometry: anchor 600, shiftables 3600 + 900, T_park 100 -> the value
            // k*600 + d*100 must be a multiple of 3600 (which covers 900); the first (k, d) by
            // k-then-|d| order is k=5, d=6 (6*5 + 6 = 36 -> 3600).
            bool found = MissionPeriodicity.TryFindNextScheduleK(
                600.0,
                new double[0], new double[0],                  // no unshiftable others
                new[] { 3600.0, 900.0 }, new[] { 50.0, 10.0 }, // Mun-like + station-like, both shiftable
                100.0, -2, 10,                                 // T_park, d bounds
                1, 64,
                out long k, out long d, out double residual, out bool withinTol);

            Assert.True(found);
            Assert.True(withinTol);
            Assert.Equal(5, k);
            Assert.Equal(6, d);
            Assert.True(residual <= 10.0 + 1e-9);
            // Cross-check: the shifted event satisfies BOTH periods.
            double shifted = k * 600.0 + d * 100.0;
            Assert.True(MissionPeriodicity.CircularPhaseError(shifted, 3600.0) <= 50.0);
            Assert.True(MissionPeriodicity.CircularPhaseError(shifted, 900.0) <= 10.0);
        }

        [Fact]
        public void Extract_CrossParentStation_DifferentLaunchGuid_Rejected()
        {
            // M4c (plan test 4, guid variant): the craft-baked-pid trap on the NEW cross-parent
            // shape - the live vessel carrying the recorded pid is a DIFFERENT launch of the
            // same craft, so it must not read as the recorded Duna depot.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            var arrival = OrbitLeg("a", 1600, 2600, "Duna");
            WithRendezvous(arrival, 1800, 2200, 0, "station-rec");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            arrival.ChainId = "C"; arrival.ChainIndex = 2;
            var tree = TreeOf("t", ascent, transfer, arrival);
            var fake = StationFake(body: "Duna");
            fake.VesselGuids[StationPid] = "22222222-2222-2222-2222-222222222222";
            var station = StationRecording("station-rec", StationPid,
                "11111111-1111-1111-1111-111111111111");

            var ex = Extract(tree, fake, extraCommitted: new List<Recording> { station });

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("different launch", ex.UnsupportedReason);
        }

        [Fact]
        public void Extract_CrossParentStation_AnchorRecordingUnresolvable_Rejected()
        {
            // M4c (plan test 4, anchor-recording variant): an anchor-recording-only section on
            // the cross-parent shape whose recording id resolves to nothing committed.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            var arrival = OrbitLeg("a", 1600, 2600, "Duna");
            WithRendezvous(arrival, 1800, 2200, 0, "missing-rec");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            arrival.ChainId = "C"; arrival.ChainIndex = 2;
            var tree = TreeOf("t", ascent, transfer, arrival);

            var ex = Extract(tree, StationFake(body: "Duna"));

            Assert.Equal(Support.UnsupportedRendezvous, ex.Support);
            Assert.Contains("not in the committed set", ex.UnsupportedReason);
        }

        // The full re-aim chain with a destination station: ascent (Kerbin surface) + one
        // journey member whose OrbitSegments classify as Kerbin parking -> Sun coast -> Duna
        // arrival, with the rendezvous Relative section inside the Duna window.
        private static RecordingTree ReaimStationTree(bool withDunaLanding = false)
        {
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var journey = OrbitLeg("o", 1100, 6000, "Kerbin");          // parking (closed, sma 700000)
            WithSoiEntry(journey, 6000, 1000000, "Sun");                 // heliocentric coast
            WithSoiEntry(journey, 1000000, 1005000, "Duna");             // arrival leg
            WithRendezvous(journey, 1001000, 1004000, StationPid);       // dock with the depot
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            journey.ChainId = "C"; journey.ChainIndex = 1;
            if (!withDunaLanding)
                return TreeOf("t", ascent, journey);
            // A Duna landing leg: surface section of Duna -> Rotation(Duna) emitted (the Duna
            // orbit handoff already exists on the journey member) -> the D8 dual shape.
            var landing = SurfaceLeg("d", 1005000, 1005100, "Duna");
            landing.ChainId = "C"; landing.ChainIndex = 2;
            return TreeOf("t", ascent, journey, landing);
        }

        [Fact]
        public void Build_ReaimWithDestinationStation_AppliesStationHold()
        {
            // M4c headline E2E (plan test 14): the integration-only hazard is the
            // ReaimClassifier's plan.TargetBody vs the station constraint's BodyName (from
            // TryGetVesselOrbit) silently mismatching - then HasStation never fires and the hold
            // no-ops with no amber. Drive the live Build if-chain and assert the hold IS the
            // station kind: nonzero hold, align period == T_station (the fake's Duna rotation
            // would give a different period), no amber, kind=station log line.
            var tree = ReaimStationTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 1.2e6);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StationFake(body: "Duna"));

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.True(unit.IsReaim);
            Assert.True(unit.ArrivalHoldSeconds > 0.0,
                $"expected a station hold, got {unit.ArrivalHoldSeconds}");
            Assert.Equal(StationPeriod, unit.ArrivalAlignPeriodSeconds, 9);
            Assert.True(unit.ArrivalHoldSeconds < StationPeriod); // W in [0, T_station)
            Assert.Null(unit.ArrivalAmberReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Reaim]") && l.Contains("ARRIVAL HOLD") &&
                l.Contains("kind=station") && l.Contains("pid=4242") && l.Contains("dest=Duna"));
        }

        [Fact]
        public void Build_ReaimWithStationAndLanding_Loose_EngagesJointHold()
        {
            // Post-M4c SolveArrivalWindow wiring E2E (supersedes the M4c fail-closed pin for D8
            // shape a): under Loose (the shipped default TransitedBodyRotationMode) the
            // landing+station dual now ENGAGES the joint hold - the 1800s station lattice
            // reaches the 65517.86s Duna rotation's 5-degree Loose tolerance within a worst
            // miss-run of 36 whole station periods (inside the 64 budget). The unit carries the
            // station-exact align period + the joint secondary (rotation) fields, no amber, and
            // the ARRIVAL HOLD line logs kind=joint with the consumed window pick.
            var tree = ReaimStationTree(withDunaLanding: true);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 1.2e6);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StationFake(body: "Duna"),
                TransitedBodyRotationMode.Loose);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.True(unit.IsReaim);
            Assert.True(unit.ArrivalHoldSeconds > 0.0,
                $"expected a joint hold, got {unit.ArrivalHoldSeconds}");
            Assert.True(unit.ArrivalHoldSeconds <= StationPeriod + 1e-9); // station-lattice base
            Assert.Equal(StationPeriod, unit.ArrivalAlignPeriodSeconds, 9);
            Assert.Equal(65517.86, unit.ArrivalJointSecondaryPeriodSeconds, 6);
            Assert.Equal(65517.86 * 5.0 / 360.0, unit.ArrivalJointSecondaryToleranceSeconds, 6);
            Assert.True(unit.ArrivalJointMaxWholeHoldPeriods > 0);
            Assert.Null(unit.ArrivalAmberReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Reaim]") && l.Contains("ARRIVAL HOLD") &&
                l.Contains("kind=joint") && l.Contains("dest=Duna") && l.Contains("k="));
            Assert.DoesNotContain(logLines, l => l.Contains("Arrival amber SET"));
        }

        [Fact]
        public void Build_ReaimWithStationAndLanding_Tight_FailsClosedWithAmber()
        {
            // The tolerance-miss polarity of the joint wiring: under Tight (0.25 deg -> 45.5s)
            // the same geometry's lattice needs thousands of whole station periods (past the 64
            // budget), so the joint solve declines - hold zero, the unit carries the amber
            // naming the miss, and the transition logs SET once. Never a silent partial
            // alignment.
            var tree = ReaimStationTree(withDunaLanding: true);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 1.2e6);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StationFake(body: "Duna"),
                TransitedBodyRotationMode.Tight);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.True(unit.IsReaim); // the transfer still re-aims; only the arrival is faithful
            Assert.Equal(0.0, unit.ArrivalHoldSeconds);
            Assert.True(double.IsNaN(unit.ArrivalJointSecondaryPeriodSeconds));
            Assert.NotNull(unit.ArrivalAmberReason);
            Assert.Contains("misses tolerance", unit.ArrivalAmberReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Reaim]") && l.Contains("Arrival amber SET") &&
                l.Contains("tree=t") && l.Contains("misses tolerance"));
        }

        [Fact]
        public void BuildSignature_StationAnchorDigest_TracksLivePeriodAndPresence()
        {
            // M4c plan test 17 (closes the M4a design debt, design doc section 8): the build
            // signature folds in the rendezvous anchor's LIVE orbit identity, so a boosted
            // station (period change past the 1s quantum) or a vanished/recovered one (found
            // flip) rebuilds the cached unit instead of keeping a stale T_station. Lives here
            // (not MissionLoopUnitBuilderTests) for the FakeBodyInfo.VesselOrbits fixture.
            var tree = CrossParentResupplyTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new[] { LoopMissionFor("t", 5000.0) };

            var fakeA = StationFake(body: "Duna");
            string sigA = MissionLoopUnitBuilder.BuildSignature(
                missions, new[] { tree }, committed, 30.0, fakeA);
            string sigA2 = MissionLoopUnitBuilder.BuildSignature(
                missions, new[] { tree }, committed, 30.0, fakeA);
            Assert.Equal(sigA, sigA2); // same inputs -> identical (no churn)

            // Boosted station: period moves past the whole-second quantum -> signature moves.
            var fakeB = StationFake(period: StationPeriod + 5.0, body: "Duna");
            string sigB = MissionLoopUnitBuilder.BuildSignature(
                missions, new[] { tree }, committed, 30.0, fakeB);
            Assert.NotEqual(sigA, sigB);

            // Vanished station (recovered/deorbited): the found flag flips -> signature moves.
            var fakeC = StockFake();
            string sigC = MissionLoopUnitBuilder.BuildSignature(
                missions, new[] { tree }, committed, 30.0, fakeC);
            Assert.NotEqual(sigA, sigC);

            // A NON-looping tree's anchors contribute nothing: the same station change leaves
            // the signature unchanged when the mission is not looping (the digest only runs for
            // looping missions).
            var offMissions = new[] { new Mission("m1", "t", "Off") { LoopPlayback = false } };
            string offA = MissionLoopUnitBuilder.BuildSignature(
                offMissions, new[] { tree }, committed, 30.0, fakeA);
            string offC = MissionLoopUnitBuilder.BuildSignature(
                offMissions, new[] { tree }, committed, 30.0, fakeC);
            Assert.Equal(offA, offC);
        }

        [Fact]
        public void AddAnchorIdentity_OrderIndependentMerge()
        {
            // M4c review finding 1: the station-anchor digest's per-pid guid merge must be
            // order-independent so the signature never flips across save/load enumeration order.
            // Net contract: "ordinal-min of all non-null offers for a pid, else null". The
            // single-anchor digest test above never exercises these branches, so pin them
            // directly (AddAnchorIdentity is internal for this).
            const uint pid = 7u;

            // Non-null beats null regardless of arrival order.
            foreach (var order in new[] { new[] { (string)null, "B" }, new[] { "B", (string)null } })
            {
                var d = new Dictionary<uint, string>();
                foreach (var g in order) MissionLoopUnitBuilder.AddAnchorIdentity(d, pid, g);
                Assert.Equal("B", d[pid]);
            }

            // Ordinal-min wins among differing non-null guids, in every permutation.
            foreach (var order in new[]
            {
                new[] { "A", "B", "C" }, new[] { "C", "B", "A" },
                new[] { "B", "A", "C" }, new[] { "C", "A", "B" },
            })
            {
                var d = new Dictionary<uint, string>();
                foreach (var g in order) MissionLoopUnitBuilder.AddAnchorIdentity(d, pid, g);
                Assert.Equal("A", d[pid]);
            }

            // null + null stays null; the null-then-non-null mix lands ordinal-min too.
            var dn = new Dictionary<uint, string>();
            MissionLoopUnitBuilder.AddAnchorIdentity(dn, pid, null);
            MissionLoopUnitBuilder.AddAnchorIdentity(dn, pid, null);
            Assert.Null(dn[pid]);
            var dm = new Dictionary<uint, string>();
            foreach (var g in new[] { (string)null, "B", (string)null, "A" })
                MissionLoopUnitBuilder.AddAnchorIdentity(dm, pid, g);
            Assert.Equal("A", dm[pid]);
        }

        [Fact]
        public void BuildSignature_StationAnchorDigest_StableAcrossAnchorOrder()
        {
            // M4c review finding 1 (end-to-end): the SAME station referenced once pid-only
            // (guid-less section) and once recording-resolved (guid-bearing) must yield an
            // identical signature regardless of which member the recordings dictionary
            // enumerates first - the order-independence merge applied through the real digest.
            const uint depotPid = 9090u;
            string depotGuid = "33333333-3333-3333-3333-333333333333";
            var fake = StockFake();
            fake.VesselOrbits[depotPid] = (1800.0, "Kerbin");
            fake.Mu["Kerbin"] = KerbinMu;
            fake.VesselGuids[depotPid] = depotGuid;

            // The depot's own committed recording (supplies the guid for the recording-resolved
            // section), and two craft members: one anchors the depot by pid, one by recording id.
            var depotRec = StationRecording("depot-rec", depotPid, depotGuid);

            RecordingTree BuildTree(bool pidMemberFirst)
            {
                var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
                var byPid = OrbitLeg("p", 1100, 1600, "Kerbin");
                WithRendezvous(byPid, 1200, 1300, depotPid);            // guid-less direct pid
                var byRec = OrbitLeg("r", 1600, 2100, "Kerbin");
                WithRendezvous(byRec, 1700, 1800, 0, "depot-rec");      // recording-resolved guid
                ascent.ChainId = "C"; ascent.ChainIndex = 0;
                byPid.ChainId = "C"; byPid.ChainIndex = 1;
                byRec.ChainId = "C"; byRec.ChainIndex = 2;
                return pidMemberFirst
                    ? TreeOf("t", ascent, byPid, byRec)
                    : TreeOf("t", ascent, byRec, byPid);
            }

            var missions = new[] { LoopMissionFor("t", 5000.0) };
            string sigPidFirst = MissionLoopUnitBuilder.BuildSignature(
                missions, new[] { BuildTree(true) }, new List<Recording> { depotRec }, 30.0, fake);
            string sigRecFirst = MissionLoopUnitBuilder.BuildSignature(
                missions, new[] { BuildTree(false) }, new List<Recording> { depotRec }, 30.0, fake);

            Assert.Equal(sigPidFirst, sigRecFirst);
        }

        // ===================== Phase 1: Solve (Tier-1 phase-lock) =====================

        private static PhaseConstraint Rotation(string body, double period, double offset)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = offset, RelativeToParent = false };

        private static PhaseConstraint Orbital(string body, double period, double offset,
            bool crossParent = false)
            => new PhaseConstraint { Kind = ConstraintKind.Orbital, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = offset, RelativeToParent = crossParent };

        [Fact]
        public void Solve_ZeroConstraints_UsesMinCycleDuration()
        {
            // Guards: an unconstrained config = free loop at MinCycleDuration; it still phase-locks
            // (cycle 0 lands at UT0) but P is the minimum cycle.
            var sol = MissionPeriodicity.Solve(
                new List<PhaseConstraint>(), Support.Supported, 1000.0, 1000.0, StockFake());

            Assert.Equal(LoopTiming.MinCycleDuration, sol.P);
            Assert.True(sol.ShouldPhaseLock);
            Assert.Equal("unconstrained", sol.Method);
            Assert.Equal(0.0, sol.ResidualSeconds);
            Assert.True(sol.WithinTolerance);
        }

        [Fact]
        public void Solve_SingleRotation_PIsRotationPeriod_ResidualZero()
        {
            // Guards: one Rotation constraint -> P = rotation period; nothing dropped -> residual 0,
            // within tolerance.
            var sol = MissionPeriodicity.Solve(
                new[] { Rotation("Kerbin", KerbinRotation, 0.0) }, Support.Supported,
                1000.0, 1000.0, StockFake());

            Assert.Equal(KerbinRotation, sol.P);
            Assert.Equal("single-rotation", sol.Method);
            Assert.Equal(0.0, sol.ResidualSeconds);
            Assert.True(sol.WithinTolerance);
            Assert.True(sol.ShouldPhaseLock);
        }

        [Fact]
        public void Solve_SingleDirectChildOrbital_PIsOrbitPeriod()
        {
            // Guards: one direct-child Orbital -> P = C.orbit.period.
            var sol = MissionPeriodicity.Solve(
                new[] { Orbital("Mun", MunOrbit, 600.0) }, Support.Supported,
                1000.0, 1000.0, StockFake());

            Assert.Equal(MunOrbit, sol.P);
            Assert.Equal("single-orbital", sol.Method);
            Assert.Equal(0.0, sol.ResidualSeconds);
            Assert.True(sol.WithinTolerance);
        }

        [Fact]
        public void Solve_MunCase_JointBestFitLocksMunAndMinimizesRotationDrift()
        {
            // Guards the flagship Mun case (Rotation(Kerbin) + Orbital(Mun), incommensurate periods)
            // under the Phase-2 JOINT best-fit: P is a whole multiple of the Mun period (the intercept
            // stays locked at every relaunch), and the multiple is chosen to MINIMIZE the Kerbin-
            // rotation drift over one cadence step. For these stock-like periods the best multiple
            // within the search bound is m=9 (~14.5 days): the launch-pad residual drops from
            // ~9688s/161deg at m=1 to ~993s/16deg. Still amber (a fixed cadence can't hit the
            // quarter-degree rotation tolerance; the zero-drift follow-up closes that).
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Kerbin", KerbinRotation, 0.0),
                Orbital("Mun", MunOrbit, 600.0)
            };
            var sol = MissionPeriodicity.Solve(
                constraints, Support.Supported, 1000.0, 1000.0, StockFake());

            Assert.True(sol.ShouldPhaseLock);
            Assert.Equal("joint-best-fit", sol.Method);
            // P is the joint window = 9 * Mun period (the intercept is locked at every relaunch).
            Assert.Equal(9 * MunOrbit, sol.P, 1);
            // The residual is the per-cycle Kerbin-rotation drift at m=9 (~993 s), strictly better
            // than the m=1 wholesale drop (the circular phase error of one Mun period vs the rotation).
            double m1Residual = MissionPeriodicity.CircularPhaseError(MunOrbit, KerbinRotation);
            Assert.True(sol.ResidualSeconds > 0.0);
            Assert.True(sol.ResidualSeconds < m1Residual);
            Assert.Equal(992.77, sol.ResidualSeconds, 1);
            Assert.False(sol.WithinTolerance); // ~993 s >> the quarter-degree rotation tolerance
        }

        [Fact]
        public void FindBestJointMultiple_ExactResonance_PicksSmallestResonantMultiple()
        {
            // Guards the joint search core: dominant period 100, one dropped period 30. m*100 mod 30
            // is 0 at m=3 (300 = 10*30), so the smallest exactly-resonant multiple is 3 (residual 0).
            // Larger resonant multiples (6, 9, ...) must NOT win the tie - the shortest period wins.
            var constraints = new List<PhaseConstraint>
            {
                Orbital("X", 100.0, 0.0),   // dominant (longer period)
                Rotation("Y", 30.0, 0.0)    // dropped
            };
            int m = MissionPeriodicity.FindBestJointMultiple(
                constraints, dominantIdx: 0, dominantPeriod: 100.0, out double residual);
            Assert.Equal(3, m);
            Assert.Equal(0.0, residual, 6);
        }

        [Fact]
        public void FindBestJointMultiple_NoBetterMultiple_StaysAtOne()
        {
            // Guards: when m=1 already gives the smallest dropped phase error within the bound, the
            // search keeps m=1 (no needless period lengthening). Dominant 1000 just over dropped 999:
            // m*1000 mod 999 == m (for m <= 16), so the error grows monotonically (m=1 -> 1, m=2 ->
            // 2, ...), and m=1 wins.
            var constraints = new List<PhaseConstraint>
            {
                Orbital("X", 1000.0, 0.0),
                Rotation("Y", 999.0, 0.0)
            };
            int m = MissionPeriodicity.FindBestJointMultiple(
                constraints, dominantIdx: 0, dominantPeriod: 1000.0, out double residual);
            Assert.Equal(1, m);
            Assert.Equal(1.0, residual, 6); // circular phase error of 1000 vs 999
        }

        [Fact]
        public void AllDroppedWithinTolerance_JudgesEachConstraintAgainstItsOwnTolerance()
        {
            // Guards the 3+-dropped fix: a SHORT-period dropped constraint whose phase error exceeds
            // ITS OWN (tiny) tolerance must fail the check, even though that error is within the
            // LONGEST-period constraint's larger tolerance (a max-residual vs max-tolerance check
            // would wrongly read OK). Rotation tolerance = period * 0.25/360. At step = 30 s:
            //   B(3600): tol ~2.5 s, err 30 s -> OUT of its own tolerance.
            //   C(100000): tol ~69.4 s, err 30 s -> within its own tolerance.
            var constraints = new List<PhaseConstraint>
            {
                Rotation("A", 10000.0, 0.0), // dominant (idx 0) - skipped
                Rotation("B", 3600.0, 0.0),
                Rotation("C", 100000.0, 0.0)
            };
            Assert.False(MissionPeriodicity.AllDroppedWithinTolerance(
                constraints, dominantIdx: 0, step: 30.0, bodyInfo: StockFake()));

            // A step where BOTH dropped constraints are within their own tolerance -> true. step = 1 s
            // is < both tolerances (~2.5 s and ~69.4 s).
            Assert.True(MissionPeriodicity.AllDroppedWithinTolerance(
                constraints, dominantIdx: 0, step: 1.0, bodyInfo: StockFake()));
        }

        [Fact]
        public void JointStepResidual_WorstDroppedError_OverOneCadenceStep()
        {
            // Guards: the per-step residual is the MAX circular phase error across all dropped
            // constraints (not the dominant) at step = multiple * dominantPeriod.
            var constraints = new List<PhaseConstraint>
            {
                Orbital("D", 100.0, 0.0),   // dominant (idx 0) - excluded
                Rotation("A", 30.0, 0.0),   // 200 mod 30 = 20 -> circ 10
                Rotation("B", 70.0, 0.0)    // 200 mod 70 = 60 -> circ 10
            };
            // step = 2*100 = 200. A: circ(200,30)=10; B: circ(200,70)=10. Worst = 10.
            double r = MissionPeriodicity.JointStepResidual(
                constraints, dominantIdx: 0, dominantPeriod: 100.0, multiple: 2);
            Assert.Equal(10.0, r, 6);
        }

        [Fact]
        public void Solve_TidallyLockedMun_RotationAndOrbitalCollapse()
        {
            // Guards: a Mun-surface Rotation + a Mun Orbital share the SAME period (tidal lock), so
            // the residual collapses to 0 and WithinTolerance is true (no double-counting).
            var constraints = new List<PhaseConstraint>
            {
                Orbital("Mun", MunOrbit, 600.0),
                Rotation("Mun", MunRotation, 1000.0) // same period as the orbit
            };
            var sol = MissionPeriodicity.Solve(
                constraints, Support.Supported, 1000.0, 1000.0 + 3 * MunOrbit, StockFake());

            Assert.Equal(MunOrbit, sol.P);
            Assert.Equal(0.0, sol.ResidualSeconds);   // same period -> collapses
            Assert.True(sol.WithinTolerance);
            Assert.Equal("tidal-collapse", sol.Method);
        }

        [Fact]
        public void Solve_NextWindow_FutureWindowK()
        {
            // Guards: NextWindowUT is the smallest UT0 + k*P >= now. With now well past UT0, k>0.
            double ut0 = 1000.0;
            double p = KerbinRotation;
            double now = ut0 + 3.5 * p; // between k=3 and k=4
            var sol = MissionPeriodicity.Solve(
                new[] { Rotation("Kerbin", p, 0.0) }, Support.Supported, ut0, now, StockFake());

            // Smallest k with ut0 + k*p >= now is k=4.
            Assert.Equal(ut0 + 4 * p, sol.NextWindowUT, 3);
            Assert.True(sol.NextWindowUT >= now);
        }

        [Fact]
        public void Solve_FutureDatedUT0_NegativeK_ResolvesToFirstWindowAtOrAfterNow()
        {
            // Guards: a future-dated recording (UT0 > now, e.g. after a career rewind) -> the
            // smallest UT0 + k*P >= now uses a NEGATIVE k, landing on the first window at or after now.
            double ut0 = 100000.0;
            double p = KerbinRotation;
            double now = 1000.0; // far before UT0
            var sol = MissionPeriodicity.Solve(
                new[] { Rotation("Kerbin", p, 0.0) }, Support.Supported, ut0, now, StockFake());

            Assert.True(sol.NextWindowUT >= now);
            Assert.True(sol.NextWindowUT < now + p); // first window at/after now, not skipping one
            // It must be of the form ut0 + k*p.
            double k = (sol.NextWindowUT - ut0) / p;
            Assert.Equal(Math.Round(k), k, 3); // integer k
        }

        [Fact]
        public void Solve_NowEqualsUT0_WindowIsUT0()
        {
            // Guards: when now == UT0 the next window IS UT0 (k=0), not the next cycle.
            double ut0 = 5000.0;
            var sol = MissionPeriodicity.Solve(
                new[] { Rotation("Kerbin", KerbinRotation, 0.0) }, Support.Supported,
                ut0, ut0, StockFake());

            Assert.Equal(ut0, sol.NextWindowUT, 3);
        }

        [Fact]
        public void Solve_ZeroRotationPeriodConstraint_NeverReachesSolve_ButGuardedAnyway()
        {
            // Guards: a degenerate dominant period (P <= 0) falls back to the free loop rather than
            // dividing by zero / refusing. (Extraction already drops zero-period constraints, so
            // this guards the solver's own defense.)
            var sol = MissionPeriodicity.Solve(
                new[] { Rotation("Kerbin", 0.0, 0.0) }, Support.Supported, 1000.0, 1000.0, StockFake());

            Assert.Equal(LoopTiming.MinCycleDuration, sol.P);
            Assert.True(sol.ShouldPhaseLock);
            Assert.False(double.IsNaN(sol.NextWindowUT));
        }

        [Fact]
        public void Solve_Unsupported_ReturnsNoLockSentinel()
        {
            // Guards: an unsupported config (cross-parent / rendezvous / multi-constraint-pre-P2)
            // returns the no-lock sentinel - the caller keeps today's behavior, never mis-scheduling.
            var crossParent = MissionPeriodicity.Solve(
                new[] { Orbital("Duna", DunaOrbit, 4000.0, crossParent: true) },
                Support.UnsupportedCrossParent, 1000.0, 1000.0, StockFake());
            Assert.False(crossParent.ShouldPhaseLock);
            Assert.True(double.IsNaN(crossParent.P));
            Assert.True(double.IsNaN(crossParent.NextWindowUT));
            Assert.Equal(Support.UnsupportedCrossParent, crossParent.Support);

            var rendezvous = MissionPeriodicity.Solve(
                new[] { Rotation("Kerbin", KerbinRotation, 0.0) },
                Support.UnsupportedRendezvous, 1000.0, 1000.0, StockFake());
            Assert.False(rendezvous.ShouldPhaseLock);
        }

        [Fact]
        public void Solve_OverConstrainedTwoRotations_BestEffortLockNonZeroResidual_NeverThrows()
        {
            // Guards: two Rotation(Kerbin) at incompatible offsets (the over-constrained Mun
            // landing-and-return case, modeled as two distinct same-body rotation constraints with
            // different periods to force a residual) still locks the dominant one and reports a
            // non-zero residual without throwing. (The accurate joint best-fit is Phase 2.)
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Kerbin", KerbinRotation, 0.0),
                Rotation("Minmus", 40400.0, 5000.0) // different period -> incommensurate
            };
            var sol = MissionPeriodicity.Solve(
                constraints, Support.Supported, 1000.0, 1000.0 + 2.3 * 40400.0, StockFake());

            Assert.True(sol.ShouldPhaseLock);
            // Both are Rotation constraints, so the longer period dominates: 40400 (Minmus) >
            // KerbinRotation (21549). Phase-2 joint best-fit locks a whole MULTIPLE of 40400 (so the
            // dominant stays locked) chosen to minimize the Kerbin-rotation drift; the residual is
            // non-zero but strictly better than the m=1 wholesale drop, and it never throws.
            double ratio = sol.P / 40400.0;
            Assert.Equal(Math.Round(ratio), ratio, 3); // P is a whole multiple of the dominant period
            Assert.True(ratio >= 1.0);
            Assert.True(sol.ResidualSeconds > 0.0); // Kerbin rotation residual is non-zero
            double m1Residual = MissionPeriodicity.CircularPhaseError(40400.0, KerbinRotation);
            Assert.True(sol.ResidualSeconds <= m1Residual); // best-fit never worse than m=1
        }

        [Fact]
        public void Solve_ToleranceBoundary_FlipsWithinTolerance()
        {
            // Guards: the green/amber readout threshold. A dropped rotation constraint's residual
            // just within vs just outside the rotation tolerance flips WithinTolerance. The rotation
            // tolerance is RotationToleranceFraction (0.25/360) of the period. We construct a
            // dominant orbital with period chosen so the residual lands either side of the boundary.
            // Use a Rotation drop: lock an Orbital with P, drop a Rotation. Choose periods so that
            // k*P mod rotPeriod is a controlled small value.
            double rotPeriod = 36000.0;
            double tol = rotPeriod * (0.25 / 360.0); // 25 s
            // Dominant orbital period = rotPeriod + delta, so after one cycle k=1, residual = delta.
            // Within: delta < tol; outside: delta > tol.
            double deltaWithin = tol * 0.5;
            double deltaOutside = tol * 2.0;

            var within = MissionPeriodicity.Solve(
                new List<PhaseConstraint>
                {
                    Orbital("Mun", rotPeriod + deltaWithin, 0.0),
                    Rotation("Kerbin", rotPeriod, 0.0)
                },
                Support.Supported, 1000.0, 1000.0 + (rotPeriod + deltaWithin), StockFake());
            Assert.True(within.ResidualSeconds <= tol);
            Assert.True(within.WithinTolerance);

            var outside = MissionPeriodicity.Solve(
                new List<PhaseConstraint>
                {
                    Orbital("Mun", rotPeriod + deltaOutside, 0.0),
                    Rotation("Kerbin", rotPeriod, 0.0)
                },
                Support.Supported, 1000.0, 1000.0 + (rotPeriod + deltaOutside), StockFake());
            Assert.True(outside.ResidualSeconds > tol);
            Assert.False(outside.WithinTolerance);
        }

        [Fact]
        public void Solve_LogsVerboseSummary()
        {
            // Guards: the per-solve Verbose summary (Diagnostic Logging contract) with P, the next
            // window, residual, within-tolerance, and method.
            MissionPeriodicity.Solve(
                new[] { Orbital("Mun", MunOrbit, 600.0) }, Support.Supported, 1000.0, 1000.0, StockFake());

            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("Solve:") &&
                l.Contains("method=single-orbital") && l.Contains("nextWindow="));
        }

        [Fact]
        public void Solve_OverConstrainedLogsWarn()
        {
            // Guards: the over-constrained Warn (residual exceeds tolerance) so a config that cannot
            // loop accurately is never a silent branch.
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Kerbin", KerbinRotation, 0.0),
                Orbital("Mun", MunOrbit, 600.0)
            };
            MissionPeriodicity.Solve(
                constraints, Support.Supported, 1000.0, 1000.0 + MunOrbit + 5000.0, StockFake());

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[MissionPeriodicity]") &&
                l.Contains("exceeds tolerance"));
        }

        // ===================== Quantize helper =====================

        [Fact]
        public void Quantize_RaisesToMultipleOfP_AtOrAboveCadence()
        {
            // Guards: the cadence is quantized UP to the nearest multiple of P (never below the
            // existing floor). cadence 50, P 30 -> 60 (2*P).
            Assert.Equal(60.0, MissionPeriodicity_QuantizePublic(50.0, 30.0), 6);
            // cadence exactly a multiple stays put.
            Assert.Equal(90.0, MissionPeriodicity_QuantizePublic(90.0, 30.0), 6);
            // cadence below P -> P.
            Assert.Equal(30.0, MissionPeriodicity_QuantizePublic(10.0, 30.0), 6);
            // degenerate P leaves cadence unchanged.
            Assert.Equal(50.0, MissionPeriodicity_QuantizePublic(50.0, 0.0), 6);
        }

        // Thin wrapper so the quantize test reads through the builder's internal helper.
        private static double MissionPeriodicity_QuantizePublic(double cadence, double p)
            => MissionLoopUnitBuilder.QuantizeCadenceToMultipleOfP(cadence, p);

        // ===================== Phase 1: builder wiring (gated) =====================

        private static Mission LoopMissionFor(string treeId, double anchorUT,
            LoopTimeUnit unit = LoopTimeUnit.Auto, double interval = 0.0)
        {
            return new Mission("m1", treeId, "Loopy")
            {
                LoopPlayback = true,
                LoopTimeUnit = unit,
                LoopIntervalSeconds = interval,
                LoopAnchorUT = anchorUT
            };
        }

        [Fact]
        public void Build_SupportedSingleBody_SnapsPhaseAnchorToNextWindow()
        {
            // Guards: a supported single-body config (a Kerbin-rotation constraint) SNAPS
            // phaseAnchorUT to the next faithful launch (UT0 + k*rotationPeriod >= LoopAnchorUT),
            // instead of leaving it at the raw LoopAnchorUT. The lock is applied.
            var surface = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var orbit = OrbitLeg("o", 1100, 5000, "Kerbin");
            surface.ChainId = "C"; surface.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", surface, orbit);
            var committed = new List<Recording>(tree.Recordings.Values);

            // Loop enabled at UT0 + 2.5 rotation periods -> next window is UT0 + 3*period.
            double ut0 = 1000.0;
            double anchorEnable = ut0 + 2.5 * KerbinRotation;
            var mission = LoopMissionFor("t", anchorEnable);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            double expectedWindow = ut0 + 3 * KerbinRotation;
            Assert.Equal(expectedWindow, unit.PhaseAnchorUT, 3); // snapped, NOT anchorEnable
            Assert.NotEqual(anchorEnable, unit.PhaseAnchorUT);
            // The applied-phase-lock Info line fired.
            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("PhaseLock APPLIED") &&
                l.Contains("method=single-rotation"));
        }

        [Fact]
        public void Build_SupportedSameParentMun_AttachesZeroDriftSchedule()
        {
            // Guards: a supported same-parent Mun config (Rotation(Kerbin) + Orbital(Mun), two
            // incommensurate constraints) is a DRIFTING config, so the builder attaches the zero-drift
            // per-window schedule. The schedule anchors on the TIGHTEST constraint - the launch-pad
            // ROTATION (a fraction of a degree), NOT the Mun (a generous SOI tolerance) - so the launch
            // lands PIXEL-PERFECT over the pad (UT0 + k*KerbinRotation) with the Mun within its SOI
            // tolerance, and faithful windows recur every few days instead of the ~3.5 years the
            // wrong "lock the Mun exactly" choice produced. The unit is non-overlapping (the INVARIANT),
            // and the log says zeroDrift=yes.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);
            var committed = new List<Recording>(tree.Recordings.Values);

            double ut0 = 1000.0;
            double anchorEnable = ut0 + 1.5 * MunOrbit;
            var mission = LoopMissionFor("t", anchorEnable);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.NotNull(unit.RelaunchSchedule);                       // zero-drift schedule attached
            // INVARIANT: a scheduled unit is non-overlapping (the overlap engine path never sees it).
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(unit));
            Assert.Equal(unit.RelaunchSchedule.FirstLaunchUT, unit.PhaseAnchorUT, 3);
            Assert.True(unit.PhaseAnchorUT >= anchorEnable);             // first-play / enable floor honored
            // The PAD (Kerbin rotation, the tightest constraint) is locked EXACTLY: the launch is
            // UT0 + k*KerbinRotation for integer k >= 1, so the pad residual is ZERO.
            double k = (unit.PhaseAnchorUT - ut0) / KerbinRotation;
            Assert.Equal(Math.Round(k), k, 3);
            Assert.True(k >= 1.0);
            double padResidual = MissionPeriodicity.CircularPhaseError(unit.PhaseAnchorUT - ut0, KerbinRotation);
            Assert.True(padResidual < 1e-3, $"pad residual {padResidual} should be ~0 (pad locked exactly)");
            // The Mun is WITHIN its SOI tolerance (SoiRadius/OrbitalVelocity), so the transfer reaches it.
            double munTol = 2429559.0 / 543.0; // StockFake Mun SoiRadius / OrbitalVelocity
            double munResidual = MissionPeriodicity.CircularPhaseError(unit.PhaseAnchorUT - ut0, MunOrbit);
            Assert.True(munResidual <= munTol,
                $"Mun residual {munResidual} should be within SOI tolerance {munTol}");
            // The window is FREQUENT (found within a few dozen pad rotations of the enable time), not
            // the ~3.5 years the old exact-Mun lock produced.
            Assert.True(unit.PhaseAnchorUT - anchorEnable < 100.0 * KerbinRotation,
                "the first faithful window should be reached within ~100 pad rotations (days), not years");
            // The scheduled line surfaces the schedule's OWN aggregate tolerance (not the fixed-cadence
            // fallback), so a reader sees the windows actually flown. Assert the exact CONTRAST the fix
            // exists to surface for this drifting same-parent Mun config: the fixed-cadence fallback is
            // amber (fixedCadenceWithinTol=no - the dropped pad-rotation cannot hold at a whole Mun
            // multiple), while the zero-drift schedule flies faithful (scheduleWithinTol=yes - the pad
            // is locked exactly and the Mun sits within its SOI tolerance).
            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("PhaseLock APPLIED") &&
                l.Contains("zeroDrift=yes") && l.Contains("scheduleWithinTol=yes") &&
                l.Contains("scheduleWorstResidual=") && l.Contains("fixedCadenceWithinTol=no"));
        }

        [Fact]
        public void Build_SingleConstraint_NoZeroDriftSchedule_KeepsFixedCadence()
        {
            // Guards: a single-constraint (Kerbin-rotation only) config does NOT drift, so it gets the
            // FIXED cadence (no schedule). RelaunchSchedule is null and the log says zeroDrift=no.
            var surface = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var orbit = OrbitLeg("o", 1100, 5000, "Kerbin");
            surface.ChainId = "C"; surface.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", surface, orbit);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 1000.0 + 2.5 * KerbinRotation);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Null(unit.RelaunchSchedule);
            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("PhaseLock APPLIED") &&
                l.Contains("zeroDrift=no"));
        }

        [Fact]
        public void Build_DriftingLongSpan_ThrottleFlooredAtSpan_NonOverlappingByConstruction()
        {
            // Guards the non-overlap INVARIANT under the span-floored throttle (review S1): a drifting
            // Mun config whose mission SPAN is very long would, without the floor, produce faithful
            // windows closer together than the span (overlap). The builder floors the throttle
            // (minSpacing) at the span, so consecutive launches are >= span apart and the unit is
            // non-overlapping BY CONSTRUCTION (not by an after-the-fact min-interval probe). Here the
            // transfer member spans 2000 Mun periods; the schedule is still ATTACHED (not rejected),
            // throttled to >= span, and UnitMemberOverlaps is false.
            double ut0 = 1000.0;
            double hugeEnd = 1100.0 + 2000.0 * MunOrbit;
            double span = hugeEnd - ut0;
            var ascent = SurfaceLeg("s", ut0, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, hugeEnd, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", hugeEnd + MunOrbit);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.NotNull(unit.RelaunchSchedule);                       // attached, throttled to >= span
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(unit));   // non-overlapping by construction
            Assert.True(unit.RelaunchSchedule.MinIntervalSeconds >= span);
            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("PhaseLock APPLIED") &&
                l.Contains("zeroDrift=yes"));
        }

        [Fact]
        public void Build_Scheduled_HasNoLoiterCutsOrArrivalHold()
        {
            // INV-3 (4.3): the schedule branch of the span clock (TryComputeSpanLoopUT) returns early
            // BEFORE the loiterCut / arrival-hold remap. That early return is correct ONLY because a
            // scheduled unit is mutually exclusive with cuts/hold BY CONSTRUCTION: the schedule attaches
            // exclusively in the same-parent phase-locked block, and the re-aim (cross-parent) block -
            // the only producer of LoiterCuts / ArrivalHoldSeconds - sets RelaunchSchedule=null. This
            // test drives the live Build if-chain (NOT the private TryBuildMissionUnit) with a same-parent
            // drifting Mun config and asserts the mutual-exclusion delta directly on the LoopUnit: a unit
            // with a non-null schedule carries NO loiter cuts and a zero arrival hold, so the span clock's
            // bypass can never silently drop a cut/hold. The first three assertions overlap the existing
            // Build_SupportedSameParentMun.../Build_DriftingLongSpan... tests; the LoiterCuts/ArrivalHold
            // pair is the new INV-3 delta.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);
            var committed = new List<Recording>(tree.Recordings.Values);

            double ut0 = 1000.0;
            double spanEnd = 2000.0;            // last member end = the first play's span end
            double span = spanEnd - ut0;
            double anchorEnable = ut0 + 1.5 * MunOrbit;
            var mission = LoopMissionFor("t", anchorEnable);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            // Schedule attached, anchored on its own first launch, throttled to >= the span (overlap
            // invariant). These overlap the existing same-parent / long-span tests.
            Assert.NotNull(unit.RelaunchSchedule);
            Assert.Equal(unit.RelaunchSchedule.FirstLaunchUT, unit.PhaseAnchorUT, 3);
            Assert.True(unit.RelaunchSchedule.MinIntervalSeconds >= span);
            // The INV-3 mutual-exclusion delta: a scheduled unit NEVER carries a loiter cut or an arrival
            // hold (those are re-aim-only, and re-aim nulls the schedule). The span clock's early return
            // before the cut/hold remap is therefore safe.
            Assert.Null(unit.LoiterCuts);
            Assert.Equal(0.0, unit.ArrivalHoldSeconds);
        }

        [Theory]
        // configKind drives which same-parent (or unsupported) tree is built; each must attach NO schedule.
        [InlineData("single-kerbin")]   // single Rotation(Kerbin) constraint - no drift -> fixed cadence
        [InlineData("tidal-mun")]       // a Mun surface<->orbit handoff (tidally-locked body) - no drift
        [InlineData("cross-parent")]    // UnsupportedCrossParent (Kerbin -> Duna) - never phase-locks
        public void Build_NonDriftingOrUnsupportedConfigs_AttachNoSchedule(string configKind)
        {
            // Matrix (complements Build_SingleConstraint_NoZeroDriftSchedule... at :1145 and
            // Build_UnsupportedCrossParent... at :1248, driven through the live Build if-chain): a
            // single-constraint config, a tidally-locked-body handoff, and an UnsupportedCrossParent
            // config all leave RelaunchSchedule NULL (only a same-parent DRIFTING multi-constraint config
            // attaches the zero-drift schedule). Asserting the matrix here pins that the schedule-attach
            // gate is selective, not "attach for anything supported".
            RecordingTree tree;
            switch (configKind)
            {
                case "single-kerbin":
                {
                    // Kerbin surface ascent + Kerbin orbit handoff = ONE Rotation(Kerbin) constraint.
                    var surface = SurfaceLeg("s", 1000, 1100, "Kerbin");
                    var orbit = OrbitLeg("o", 1100, 5000, "Kerbin");
                    surface.ChainId = "C"; surface.ChainIndex = 0;
                    orbit.ChainId = "C"; orbit.ChainIndex = 1;
                    tree = TreeOf("t", surface, orbit);
                    break;
                }
                case "tidal-mun":
                {
                    // A Mun-only surface<->orbit handoff: the only constraint is Rotation(Mun) (the body is
                    // tidally locked, rotationPeriod == orbit period), so there is nothing incommensurate to
                    // drift against -> no schedule. No Kerbin pad rotation in the included set.
                    var munSurface = SurfaceLeg("s", 1000, 1100, "Mun");
                    var munOrbit = OrbitLeg("o", 1100, 5000, "Mun");
                    munSurface.ChainId = "C"; munSurface.ChainIndex = 0;
                    munOrbit.ChainId = "C"; munOrbit.ChainIndex = 1;
                    tree = TreeOf("t", munSurface, munOrbit);
                    break;
                }
                case "cross-parent":
                {
                    // Kerbin ascent + a heliocentric Duna SOI entry = UnsupportedCrossParent.
                    var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
                    var transfer = OrbitLeg("o", 1100, 5000, "Kerbin");
                    WithSoiEntry(transfer, 5000, 6000, "Duna");
                    ascent.ChainId = "C"; ascent.ChainIndex = 0;
                    transfer.ChainId = "C"; transfer.ChainIndex = 1;
                    tree = TreeOf("t", ascent, transfer);
                    break;
                }
                default:
                    throw new ArgumentException("unknown configKind " + configKind);
            }

            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 123456.0);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Null(unit.RelaunchSchedule);
            // No schedule => the cut/hold remap is also absent (re-aim-only), so the mutual-exclusion
            // delta holds vacuously here too.
            Assert.Null(unit.LoiterCuts);
            Assert.Equal(0.0, unit.ArrivalHoldSeconds);
        }

        [Fact]
        public void Schedule_SaveLoadRoundTrip_RederivesIdentical()
        {
            // INV-4 (4.5): the MissionRelaunchSchedule is NEVER serialized; it is rebuilt on every load
            // from the persisted Mission.LoopAnchorUT plus the live body geometry (folded into
            // MissionLoopUnitBuilder.BuildSignature). So a save/reload must re-derive a BYTE-IDENTICAL
            // launch sequence, or the engine would relaunch at different UTs after a reload than before.
            //
            // We model the round-trip with the actually-serialized inputs: Mission.LoopAnchorUT survives
            // OnSave/OnLoad as a scalar, and the body geometry is re-read each Build from the live
            // IBodyInfo seam (the same value StockFake() returns on every call). We do NOT drive
            // ParsekScenario.OnSave/OnLoad in xUnit (Planetarium / GameEvents are unguarded there); the
            // source-gate / pure equivalent is: persist LoopAnchorUT through a ConfigNode scalar, rebuild
            // a SECOND Mission from the parsed value, Build both with a freshly-constructed StockFake()
            // body digest, and assert the resolved launch sequence over k=0..K is identical.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);
            var committed = new List<Recording>(tree.Recordings.Values);

            double anchorEnable = 1000.0 + 1.5 * MunOrbit;

            // --- PRE-SAVE: build the schedule from the in-memory mission. ---
            var missionBefore = LoopMissionFor("t", anchorEnable);
            var setBefore = MissionLoopUnitBuilder.Build(
                new[] { missionBefore }, new[] { tree }, committed, 30.0, StockFake());
            Assert.True(setBefore.TryGetUnitForMember(0, out var unitBefore));
            Assert.NotNull(unitBefore.RelaunchSchedule);

            // --- SAVE: the only persisted schedule INPUT is the LoopAnchorUT scalar (round-tripped via a
            // ConfigNode value using the project's InvariantCulture "R" contract). ---
            var node = new ConfigNode("MISSION");
            node.AddValue("LoopAnchorUT",
                missionBefore.LoopAnchorUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            // --- LOAD: parse the scalar back and reconstruct a fresh Mission. The body geometry is NOT
            // persisted; it is re-read from a freshly-constructed StockFake() (the live-seam equivalent). ---
            double parsedAnchorUT = double.Parse(
                node.GetValue("LoopAnchorUT"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(missionBefore.LoopAnchorUT, parsedAnchorUT, 6); // the scalar survives the trip
            var missionAfter = LoopMissionFor("t", parsedAnchorUT);
            var setAfter = MissionLoopUnitBuilder.Build(
                new[] { missionAfter }, new[] { tree }, committed, 30.0, StockFake());
            Assert.True(setAfter.TryGetUnitForMember(0, out var unitAfter));
            Assert.NotNull(unitAfter.RelaunchSchedule);

            // --- ASSERT: the re-derived schedule resolves an IDENTICAL launch sequence. ---
            var a = unitBefore.RelaunchSchedule;
            var b = unitAfter.RelaunchSchedule;
            Assert.Equal(a.FirstLaunchUT, b.FirstLaunchUT, 6);
            Assert.Equal(unitBefore.PhaseAnchorUT, unitAfter.PhaseAnchorUT, 6);
            Assert.Equal(a.MinIntervalSeconds, b.MinIntervalSeconds, 6);

            // Resolve over k=0..K through the assembled schedule object: every launch UT + cycle index
            // matches pre/post. Sweep UTs that step across the first ~K launches (one anchor period per
            // step easily clears each non-uniform relaunch gap), plus a pre-first-launch parked probe and
            // a far-future warp probe.
            const int K = 64;
            double firstLaunch = a.FirstLaunchUT;
            var probes = new List<double> { firstLaunch - MunOrbit }; // parked before L_0
            for (int k = 0; k <= K; k++)
                probes.Add(firstLaunch + k * MunOrbit);
            probes.Add(firstLaunch + 5e8); // far-future warp

            foreach (double probe in probes)
            {
                bool ra = a.TryResolveActiveLaunch(probe, out double la, out long ca);
                bool rb = b.TryResolveActiveLaunch(probe, out double lb, out long cb);
                Assert.Equal(ra, rb);
                if (ra)
                {
                    Assert.Equal(la, lb, 6);
                    Assert.Equal(ca, cb);
                }
                Assert.Equal(a.NextLaunchAfter(probe), b.NextLaunchAfter(probe), 6);
            }
        }

        [Fact]
        public void Build_LoopAnchorBeforeFirstPlay_ClampsAnchorToAfterTheFirstPlay()
        {
            // Guards: a looped mission must never relaunch before its first real play completes - the
            // original recording, [spanStart, spanEnd], which spawns a real vessel at spanEnd. A
            // LoopAnchorUT set BEFORE the recording finished (e.g. a future-dated recording after a
            // career rewind, or a NaN anchor) must be clamped so the phase anchor lands at/after
            // spanEndUT, since the span clock plays nothing before the phase anchor.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 1600, "Kerbin");
            WithSoiEntry(transfer, 1600, 2000, "Mun");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);
            var committed = new List<Recording>(tree.Recordings.Values);
            const double spanEnd = 2000.0; // last member end (the first play's end)
            const double ut0 = 1000.0;     // recorded launch (= spanStart)

            // Anchor BEFORE the recorded LAUNCH (a future-dated recording, e.g. after a career
            // reset): the loop was enabled at UT 500 but the mission is not flown until UT 1000.
            // WITHOUT the clamp the locked path would snap to the window at UT0=1000 (k=0, < spanEnd
            // = a relaunch during/before the first play) and the unlocked path to 500 (the raw
            // anchor); the floor must push both to >= spanEnd.
            var mission = LoopMissionFor("t", anchorUT: 500.0);

            // Phase-locked path: the zero-drift schedule's first launch stays a faithful window
            // (UT0 + k*KerbinRotation, the pad locked exactly) AND is clamped >= spanEnd. Without the
            // floor the first window could fall at/before spanEnd (a relaunch during the first play);
            // the floor pushes the schedule's first launch past it.
            var locked = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());
            Assert.True(locked.TryGetUnitForMember(0, out var lockedUnit));
            Assert.NotNull(lockedUnit.RelaunchSchedule);
            Assert.True(lockedUnit.PhaseAnchorUT >= spanEnd,
                $"locked anchor {lockedUnit.PhaseAnchorUT} must be >= first-play end {spanEnd}");
            double k = (lockedUnit.PhaseAnchorUT - ut0) / KerbinRotation; // pad (Kerbin rot) locked exactly
            Assert.Equal(Math.Round(k), k, 3); // still a faithful UT0 + k*KerbinRotation window
            Assert.True(k >= 1.0, "the clamp pushed past the k=0 (UT0) window that precedes spanEnd");

            // No-body-info (no phase-lock) path: clamps the raw anchor straight to spanEnd.
            var today = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, null);
            Assert.True(today.TryGetUnitForMember(0, out var todayUnit));
            Assert.Equal(spanEnd, todayUnit.PhaseAnchorUT, 3);
        }

        [Fact]
        public void Build_UnsupportedCrossParent_LeavesAnchorAtRawLoopAnchorUT_NoRegression()
        {
            // Guards: an unsupported (cross-parent Duna) config does NOT phase-lock - the anchor
            // stays at the raw LoopAnchorUT and the cadence is the today's-behavior value, so
            // nothing regresses for cases we don't yet handle.
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 5000, "Kerbin");
            WithSoiEntry(transfer, 5000, 6000, "Duna");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);
            var committed = new List<Recording>(tree.Recordings.Values);

            double anchorEnable = 123456.0;
            var mission = LoopMissionFor("t", anchorEnable);

            // With body-info (phase-lock wired) but unsupported config.
            var lockedSet = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());
            Assert.True(lockedSet.TryGetUnitForMember(0, out var lockedUnit));

            // Without body-info (today's behavior).
            var todaySet = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0, null);
            Assert.True(todaySet.TryGetUnitForMember(0, out var todayUnit));

            // The unsupported config is byte-identical to today's behavior: same anchor + cadences.
            Assert.Equal(anchorEnable, lockedUnit.PhaseAnchorUT);
            Assert.Equal(todayUnit.PhaseAnchorUT, lockedUnit.PhaseAnchorUT);
            Assert.Equal(todayUnit.CadenceSeconds, lockedUnit.CadenceSeconds);
            Assert.Equal(todayUnit.OverlapCadenceSeconds, lockedUnit.OverlapCadenceSeconds);
            // The skipped verdict line fired with the unsupported reason (now
            // VerboseRateLimited, demoted from Info so it never spams with verbose off).
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") &&
                l.Contains("[MissionPeriodicity]") && l.Contains("PhaseLock SKIPPED") &&
                l.Contains("UnsupportedCrossParent"));
            // Regression guard for the route log flood: the verdict must NEVER be Info
            // (Info bypasses the verbose-off setting and spammed ~4.2k lines per playtest).
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[INFO]") && l.Contains("PhaseLock SKIPPED"));
        }

        // Mirrors the contract RouteOrchestrator.ResolveLoopUnit relies on: with the three
        // builder SuppressLogging flags set, a Build emits NONE of the pure-derivation
        // diagnostic lines (BuildMissionStructure / ExtractConstraints / Solve / ReaimDiag /
        // MissionLoopUnit / PhaseLock) - yet still builds the unit. This is what stops the
        // per-tick / per-frame route resolve from flooding the log under time warp.
        [Fact]
        public void Build_AllSuppressFlags_EmitsNoPipelineDiagnostics()
        {
            var ascent = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var transfer = OrbitLeg("o", 1100, 5000, "Kerbin");
            WithSoiEntry(transfer, 5000, 6000, "Duna");
            ascent.ChainId = "C"; ascent.ChainIndex = 0;
            transfer.ChainId = "C"; transfer.ChainIndex = 1;
            var tree = TreeOf("t", ascent, transfer);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 123456.0);

            MissionStructureBuilder.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = true;
            GhostPlaybackLogic.LoopUnitSet set;
            try
            {
                set = MissionLoopUnitBuilder.Build(
                    new[] { mission }, new[] { tree }, committed, 30.0, StockFake());
            }
            finally
            {
                MissionStructureBuilder.SuppressLogging = false;
                MissionPeriodicity.SuppressLogging = false;
                MissionLoopUnitBuilder.SuppressLogging = false;
            }

            // Suppression is logging-only: the unit is still built.
            Assert.True(set.TryGetUnitForMember(0, out _));
            // No pipeline diagnostics leaked.
            Assert.DoesNotContain(logLines, l => l.Contains("BuildMissionStructure:"));
            Assert.DoesNotContain(logLines, l => l.Contains("[MissionPeriodicity]"));
            Assert.DoesNotContain(logLines, l => l.Contains("MissionLoopUnit:"));
            Assert.DoesNotContain(logLines, l => l.Contains("[ReaimDiag]"));
        }

        [Fact]
        public void Build_NoBodyInfo_ByteIdenticalToTodaysBehavior()
        {
            // Guards: with no IBodyInfo (the default overload, used by every existing caller/test),
            // the builder NEVER phase-locks - the result is byte-identical to the merged #958 loop
            // behavior (anchor = LoopAnchorUT, raw cadences). This is the strict-superset guarantee.
            var surface = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var orbit = OrbitLeg("o", 1100, 5000, "Kerbin");
            surface.ChainId = "C"; surface.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", surface, orbit);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 8888.0);

            var set = MissionLoopUnitBuilder.Build(
                new[] { mission }, new[] { tree }, committed, 30.0); // no bodyInfo overload

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(8888.0, unit.PhaseAnchorUT); // raw LoopAnchorUT, no snap
        }

        [Fact]
        public void BuildSignature_BodyGeometryChange_Rebuilds()
        {
            // Guards: a body-geometry change (the live rotation/orbit period of a transited body)
            // moves the signature when bodyInfo is supplied, so the cached unit re-derives P.
            var surface = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var orbit = OrbitLeg("o", 1100, 5000, "Kerbin");
            surface.ChainId = "C"; surface.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", surface, orbit);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 8888.0);

            var fakeA = StockFake();
            var fakeB = StockFake();
            fakeB.Rotation["Kerbin"] = KerbinRotation + 100.0; // a planet-pack-like change

            string sigA = MissionLoopUnitBuilder.BuildSignature(
                new[] { mission }, new[] { tree }, committed, 30.0, fakeA);
            string sigB = MissionLoopUnitBuilder.BuildSignature(
                new[] { mission }, new[] { tree }, committed, 30.0, fakeB);

            Assert.NotEqual(sigA, sigB);
        }

        [Fact]
        public void BuildSignature_NoBodyInfo_ByteIdenticalToOldSignature()
        {
            // Guards: without bodyInfo the signature is byte-identical to the pre-periodicity
            // signature (no transited-body digest appended), so nothing about the cache changes for
            // the non-phase-lock path.
            var surface = SurfaceLeg("s", 1000, 1100, "Kerbin");
            var orbit = OrbitLeg("o", 1100, 5000, "Kerbin");
            surface.ChainId = "C"; surface.ChainIndex = 0;
            orbit.ChainId = "C"; orbit.ChainIndex = 1;
            var tree = TreeOf("t", surface, orbit);
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMissionFor("t", 8888.0);

            string noBody = MissionLoopUnitBuilder.BuildSignature(
                new[] { mission }, new[] { tree }, committed, 30.0);
            string nullBody = MissionLoopUnitBuilder.BuildSignature(
                new[] { mission }, new[] { tree }, committed, 30.0, null);

            Assert.Equal(noBody, nullBody);
            // And the body-info variant DIFFERS (it appends the digest).
            string withBody = MissionLoopUnitBuilder.BuildSignature(
                new[] { mission }, new[] { tree }, committed, 30.0, StockFake());
            Assert.NotEqual(noBody, withBody);
        }

        // ===== Body-hierarchy walk (salvaged from PR #968; re-aim segment classification) =====

        [Theory]
        [InlineData("Sun", "Sun")]
        [InlineData("Kerbin", "Kerbin,Sun")]
        [InlineData("Mun", "Mun,Kerbin,Sun")]
        [InlineData("Minmus", "Minmus,Kerbin,Sun")]
        [InlineData("Duna", "Duna,Sun")]
        public void AncestorChain_StockBodies_ChildToRootInclusive(string body, string expectedCsv)
        {
            var chain = MissionPeriodicity.AncestorChain(body, StockFake());
            Assert.Equal(expectedCsv, string.Join(",", chain));
        }

        [Fact]
        public void AncestorChain_NullOrUnknown_HandledCleanly()
        {
            Assert.Empty(MissionPeriodicity.AncestorChain(null, StockFake()));
            Assert.Empty(MissionPeriodicity.AncestorChain("", StockFake()));
            // Unknown body: ReferenceBodyName returns null, so the chain is just the body itself.
            Assert.Equal(new[] { "Nope" }, MissionPeriodicity.AncestorChain("Nope", StockFake()));
        }

        [Fact]
        public void AncestorChain_CyclicGraph_TerminatesNoHang()
        {
            var f = new FakeBodyInfo();
            f.Parent["A"] = "B";
            f.Parent["B"] = "A"; // cycle
            Assert.Equal(new[] { "A", "B" }, MissionPeriodicity.AncestorChain("A", f));
        }

        [Theory]
        // launch, target, expectedAncestor, launchToAnc(csv), targetToAnc(csv)
        [InlineData("Kerbin", "Mun", "Kerbin", "", "Mun")]
        [InlineData("Kerbin", "Minmus", "Kerbin", "", "Minmus")]
        [InlineData("Kerbin", "Duna", "Sun", "Kerbin", "Duna")]
        public void TryFindCommonAncestor_StockPairs(
            string launch, string target, string expAnc, string expLaunchCsv, string expTargetCsv)
        {
            bool ok = MissionPeriodicity.TryFindCommonAncestor(
                launch, target, StockFake(), out string anc, out var l2a, out var t2a);
            Assert.True(ok);
            Assert.Equal(expAnc, anc);
            Assert.Equal(expLaunchCsv, string.Join(",", l2a));
            Assert.Equal(expTargetCsv, string.Join(",", t2a));
        }

        [Fact]
        public void TryFindCommonAncestor_SameBody_AncestorIsItselfBothChainsEmpty()
        {
            bool ok = MissionPeriodicity.TryFindCommonAncestor(
                "Kerbin", "Kerbin", StockFake(), out string anc, out var l2a, out var t2a);
            Assert.True(ok);
            Assert.Equal("Kerbin", anc);
            Assert.Empty(l2a);
            Assert.Empty(t2a);
        }

        [Fact]
        public void TryFindCommonAncestor_SameParentVsCrossParent_DistinguishesReaimMode()
        {
            // The re-aim mode switch: launchToAnc EMPTY == same-parent (faithful replay);
            // launchToAnc NON-EMPTY == cross-parent (re-aim).
            MissionPeriodicity.TryFindCommonAncestor("Kerbin", "Mun", StockFake(), out _, out var munLaunch, out _);
            Assert.Empty(munLaunch);                 // Mun is same-parent -> faithful
            MissionPeriodicity.TryFindCommonAncestor("Kerbin", "Duna", StockFake(), out _, out var dunaLaunch, out _);
            Assert.NotEmpty(dunaLaunch);             // Duna is cross-parent -> re-aim
        }

        [Fact]
        public void TryFindCommonAncestor_Disconnected_ReturnsFalse()
        {
            var f = new FakeBodyInfo();
            f.Parent["Kerbin"] = "Sun"; f.Parent["Sun"] = null;
            f.Parent["X"] = "OtherStar"; f.Parent["OtherStar"] = null;
            bool ok = MissionPeriodicity.TryFindCommonAncestor(
                "Kerbin", "X", f, out string anc, out var l2a, out var t2a);
            Assert.False(ok);
            Assert.Null(anc);
            Assert.Empty(l2a);
            Assert.Empty(t2a);
        }
    }
}
