using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The no-op switch / resume check ignores motion the physics makes on its own
    /// (owner ruling 2026-11-01: the player has to actually move the vehicle). EVA-16
    /// flaked because a parked rover's suspension wiggle (a RoboticMotionStarted on
    /// roverWheel1) landed in the commit frame and kept a resume that changed nothing.
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentPassiveSignalTests : IDisposable
    {
        private const uint WheelPid = 4242u;
        private const uint HingePid = 5151u;
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentPassiveSignalTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            // No live KSP: unresolved prefab and body lookups must take the seams.
            SwitchSegmentNoOpClassifier.PrefabModuleNamesForTesting = _ => null;
            SwitchSegmentNoOpClassifier.BodyRadiusForTesting =
                body => body == "Kerbin" ? 600000.0 : double.NaN;
        }

        public void Dispose()
        {
            SwitchSegmentNoOpClassifier.PrefabModuleNamesForTesting = null;
            SwitchSegmentNoOpClassifier.BodyRadiusForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode Part(string name, uint pid, params string[] modules)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", pid.ToString());
            foreach (string m in modules)
            {
                var module = part.AddNode("MODULE");
                module.AddValue("name", m);
            }
            return part;
        }

        /// <summary>A rover: one wheel (motor, steering, suspension - the stock
        /// roverWheel1 robotic order) and one hinge.</summary>
        private static ConfigNode RoverSnapshot()
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddNode(Part("roverWheel1", WheelPid,
                "ModuleWheelBase", "ModuleWheelMotor", "ModuleWheelSteering",
                "ModuleWheelSuspension", "ModuleWheelBrakes", "ModuleWheelDamage"));
            vessel.AddNode(Part("hinge.03", HingePid,
                "ModuleRoboticServoHinge"));
            return vessel;
        }

        private static Recording Rover(double startUT = 0, double endUT = 100)
        {
            var rec = new Recording { RecordingId = "rover", VesselSnapshot = RoverSnapshot() };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
            });
            return rec;
        }

        private static PartEvent Robotic(PartEventType type, uint pid, string part,
            int ordinal, double ut, float value = 0.328f)
        {
            return new PartEvent
            {
                ut = ut, eventType = type, partPersistentId = pid,
                partName = part, moduleIndex = ordinal, value = value
            };
        }

        // ---- module resolution ----------------------------------------------

        [Fact]
        public void RoboticOrdinal_CountsOnlyRoboticModules()
        {
            var names = new List<string> { "ModuleWheelBase", "ModuleWheelMotor",
                "ModuleWheelSteering", "ModuleWheelSuspension", "ModuleWheelBrakes" };
            Assert.Equal("ModuleWheelMotor", SwitchSegmentNoOpClassifier.RoboticModuleNameAtOrdinal(names, 0));
            Assert.Equal("ModuleWheelSuspension", SwitchSegmentNoOpClassifier.RoboticModuleNameAtOrdinal(names, 2));
            Assert.Null(SwitchSegmentNoOpClassifier.RoboticModuleNameAtOrdinal(names, 3));
            Assert.Null(SwitchSegmentNoOpClassifier.RoboticModuleNameAtOrdinal(names, -1));
        }

        [Fact]
        public void SnapshotResolve_ByPid_ThenByName()
        {
            Assert.True(SwitchSegmentNoOpClassifier.TryResolveRoboticModuleNameFromSnapshot(
                RoverSnapshot(), WheelPid, "roverWheel1", 2, out string byPid));
            Assert.Equal("ModuleWheelSuspension", byPid);

            // A re-pidded part (a spawn regenerates part ids) still resolves by name.
            Assert.True(SwitchSegmentNoOpClassifier.TryResolveRoboticModuleNameFromSnapshot(
                RoverSnapshot(), 999u, "roverWheel1", 2, out string byName));
            Assert.Equal("ModuleWheelSuspension", byName);

            Assert.False(SwitchSegmentNoOpClassifier.TryResolveRoboticModuleNameFromSnapshot(
                RoverSnapshot(), 999u, "noSuchPart", 0, out _));
            Assert.False(SwitchSegmentNoOpClassifier.TryResolveRoboticModuleNameFromSnapshot(
                null, WheelPid, "roverWheel1", 0, out _));
        }

        [Fact]
        public void PrefabFallback_ResolvesWhenSnapshotMissing()
        {
            SwitchSegmentNoOpClassifier.PrefabModuleNamesForTesting = part =>
                part == "roverWheel1"
                    ? new List<string> { "ModuleWheelBase", "ModuleWheelSuspension" }
                    : null;
            var evt = Robotic(PartEventType.RoboticMotionStarted, WheelPid, "roverWheel1", 0, 10);

            Assert.True(SwitchSegmentNoOpClassifier.IsWheelRoboticEvent(evt, new Recording()));
            Assert.True(SwitchSegmentNoOpClassifier.IsWheelRoboticEvent(evt, null));
        }

        // ---- wheel jitter (the EVA-16 case) -----------------------------------

        [Theory]
        [InlineData(PartEventType.RoboticMotionStarted)]
        [InlineData(PartEventType.RoboticPositionSample)]
        [InlineData(PartEventType.RoboticMotionStopped)]
        public void WheelSuspensionEvent_InCommitFrame_IsNoOpResumeTail(PartEventType type)
        {
            var rec = Rover();
            // Exactly at the resume anchor: the inclusive >= reaches it.
            rec.PartEvents.Add(Robotic(type, WheelPid, "roverWheel1", 2, 50.0));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 50.0, out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void ManyWheelEvents_AreNoOpSegment_AndLogged()
        {
            var rec = Rover();
            for (int i = 0; i < 25; i++)
            {
                rec.PartEvents.Add(Robotic(
                    i % 2 == 0 ? PartEventType.RoboticMotionStarted : PartEventType.RoboticMotionStopped,
                    WheelPid, "roverWheel1", 2, 1.0 + i));
            }

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out _));
            Assert.Contains(logLines, l => l.Contains("[SwitchSegment]")
                && l.Contains("IsNoOpSegment: no-op despite passive signals rec=rover")
                && l.Contains("wheelRobotic=13")
                && l.Contains("stopTransitions=12"));
        }

        [Fact]
        public void HingeMotion_IsStillKept()
        {
            var rec = Rover();
            rec.PartEvents.Add(Robotic(PartEventType.RoboticMotionStarted, HingePid, "hinge.03", 0, 60));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 50.0, out string reason));
            Assert.Equal("part-event:RoboticMotionStarted", reason);
        }

        [Fact]
        public void UnresolvedRoboticModule_IsKept()
        {
            var rec = new Recording { RecordingId = "nosnap" };
            rec.Points.Add(new TrajectoryPoint { ut = 0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.PartEvents.Add(Robotic(PartEventType.RoboticPositionSample, WheelPid, "roverWheel1", 2, 60));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("part-event:RoboticPositionSample", reason);
        }

        // ---- other physics-made signals ----------------------------------------

        [Theory]
        [InlineData(PartEventType.ConverterDeactivated)]
        [InlineData(PartEventType.EvaRagdollStarted)]
        [InlineData(PartEventType.EvaRagdollEnded)]
        public void PassivePartEvent_IsNotMeaningful(PartEventType type)
        {
            Assert.False(SwitchSegmentNoOpClassifier.IsMeaningfulPartEvent(
                new PartEvent { eventType = type, value = 1f }));
        }

        [Theory]
        [InlineData(PartEventType.ConverterActivated)]
        [InlineData(PartEventType.EvaJetpackThrustStarted)]
        [InlineData(PartEventType.InventoryPartPlaced)]
        [InlineData(PartEventType.DeployableBroken)]
        public void PlayerOrStructuralPartEvent_StaysMeaningful(PartEventType type)
        {
            Assert.True(SwitchSegmentNoOpClassifier.IsMeaningfulPartEvent(
                new PartEvent { eventType = type, value = 1f }));
        }

        // ---- surface settling vs a real drive --------------------------------

        private static TrackSection SurfaceMobile(double startUT, double endUT,
            params (double ut, double lat, double lon)[] frames)
        {
            var sec = new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
            };
            foreach (var f in frames)
                sec.frames.Add(new TrajectoryPoint
                {
                    ut = f.ut, latitude = f.lat, longitude = f.lon, altitude = 70, bodyName = "Kerbin"
                });
            return sec;
        }

        // On Kerbin (r = 600 km) one metre is ~9.55e-5 degrees of latitude.
        private const double DegPerMeter = 180.0 / (Math.PI * 600000.0);

        [Fact]
        public void SettlingRover_SurfaceMobileUnderAMetre_IsNoOp()
        {
            var rec = new Recording { RecordingId = "settle" };
            rec.TrackSections.Add(SurfaceMobile(0, 10,
                (0, 0, 0), (2, 0.2 * DegPerMeter, 0), (4, 0.4 * DegPerMeter, 0), (8, 0.3 * DegPerMeter, 0)));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Null(reason);
            Assert.Contains(logLines, l => l.Contains("settleSections=1") && l.Contains("maxSettleM=0.40"));
        }

        [Fact]
        public void RealDrive_SurfaceMobile_IsKept()
        {
            var rec = new Recording();
            rec.TrackSections.Add(SurfaceMobile(0, 10,
                (0, 0, 0), (5, 3 * DegPerMeter, 0), (10, 25 * DegPerMeter, 0)));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("non-boring-section:SurfaceMobile", reason);
        }

        [Fact]
        public void DriveOutAndBack_IsKept()
        {
            // Net displacement is zero but the rover went 20 m away: max distance counts.
            var rec = new Recording();
            rec.TrackSections.Add(SurfaceMobile(0, 20,
                (0, 0, 0), (10, 20 * DegPerMeter, 0), (20, 0, 0)));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out _));
        }

        [Fact]
        public void ResumeTail_MeasuresFromTheAnchor()
        {
            // The committed drive before the anchor is history; after it the rover only settles.
            var rec = new Recording();
            rec.TrackSections.Add(SurfaceMobile(0, 100,
                (0, 0, 0), (40, 300 * DegPerMeter, 0), (50, 400 * DegPerMeter, 0),
                (60, (400 + 0.3) * DegPerMeter, 0), (70, (400 + 0.2) * DegPerMeter, 0)));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 50.0, out string reason));
            Assert.Null(reason);

            // ...but a move right after the anchor counts.
            rec.TrackSections[0].frames.Add(new TrajectoryPoint
            {
                ut = 80, latitude = 410 * DegPerMeter, altitude = 70, bodyName = "Kerbin"
            });
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 50.0, out reason));
            Assert.Equal("non-boring-section:SurfaceMobile", reason);
        }

        [Fact]
        public void UnmeasurableSurfaceMobile_IsKept()
        {
            // One frame, an unknown body, and a Relative frame: none can be measured.
            var oneFrame = new Recording();
            oneFrame.TrackSections.Add(SurfaceMobile(0, 10, (0, 0, 0)));
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(oneFrame, false, out _));

            var unknownBody = new Recording();
            var sec = SurfaceMobile(0, 10, (0, 0, 0), (5, 0, 0));
            for (int i = 0; i < sec.frames.Count; i++)
            {
                var p = sec.frames[i];
                p.bodyName = "Nowhere";
                sec.frames[i] = p;
            }
            unknownBody.TrackSections.Add(sec);
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(unknownBody, false, out _));

            var relative = new Recording();
            var rel = SurfaceMobile(0, 10, (0, 0, 0), (5, 0, 0));
            rel.referenceFrame = ReferenceFrame.Relative;
            relative.TrackSections.Add(rel);
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(relative, false, out _));
        }
    }
}
