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
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = false;
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
            // (no divide-by-zero downstream).
            var fake = StockFake();
            fake.Rotation["Kerbin"] = 0.0;
            var surface = SurfaceLeg("s", 100, 200, "Kerbin");
            var tree = TreeOf("t", surface);

            var ex = Extract(tree, fake);

            Assert.DoesNotContain(ex.Constraints, c => c.Kind == ConstraintKind.Rotation);
        }

        [Fact]
        public void Extract_RetrogradeRotation_UsesMagnitude()
        {
            // Guards: a retrograde (negative) rotation period is read by magnitude, not skipped.
            var fake = StockFake();
            fake.Rotation["Kerbin"] = -KerbinRotation;
            var surface = SurfaceLeg("s", 100, 200, "Kerbin");
            var tree = TreeOf("t", surface);

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
    }
}
