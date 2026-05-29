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
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionPeriodicity.SuppressLogging = false;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
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

            public double RotationPeriod(string b) => Rotation.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double OrbitPeriod(string b) => Orbit.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public string ReferenceBodyName(string b) => Parent.TryGetValue(b ?? "", out string v) ? v : null;
            public double SoiRadius(string b) => Soi.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double OrbitalVelocity(string b) => Velocity.TryGetValue(b ?? "", out double v) ? v : double.NaN;

            public readonly Dictionary<string, double> Sma = new Dictionary<string, double>();
            public readonly Dictionary<string, double> Mu = new Dictionary<string, double>();
            public double SemiMajorAxis(string b) => Sma.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double GravParameter(string b) => Mu.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double TrueLongitudeAtUTDegrees(string b, double ut) => double.NaN;
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
            HashSet<string> excluded = null)
        {
            var committed = new List<Recording>(tree.Recordings.Values);
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
            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("PhaseLock APPLIED") &&
                l.Contains("zeroDrift=yes"));
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
            // The skipped Info line fired with the unsupported reason.
            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("PhaseLock SKIPPED") &&
                l.Contains("UnsupportedCrossParent"));
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
