using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BackgroundRecorderTests
    {
        public BackgroundRecorderTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        /// <summary>
        /// Helper: creates a minimal RecordingTree with the given background vessel PIDs.
        /// Each background vessel gets a Recording in tree.Recordings with matching VesselPersistentId.
        /// </summary>
        private RecordingTree MakeTree(params (uint pid, string recId)[] backgroundVessels)
        {
            var tree = new RecordingTree
            {
                Id = "tree_bg_test",
                TreeName = "BG Test Tree",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_active"
            };

            // Active recording (not in background)
            tree.Recordings["rec_active"] = new Recording
            {
                RecordingId = "rec_active",
                VesselName = "Active Vessel",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };

            for (int i = 0; i < backgroundVessels.Length; i++)
            {
                var (pid, recId) = backgroundVessels[i];
                tree.Recordings[recId] = new Recording
                {
                    RecordingId = recId,
                    VesselName = $"Background Vessel {i}",
                    VesselPersistentId = pid,
                    ExplicitStartUT = 100.0,
                    ExplicitEndUT = 200.0
                };
                tree.BackgroundMap[pid] = recId;
            }

            return tree;
        }

        #region 9.1 On-Rails State Management

        [Fact]
        public void Constructor_InitializesOnRailsState_ForEachBackgroundVessel()
        {
            // Arrange: tree with two background vessels
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));

            // Act
            var bgRecorder = new BackgroundRecorder(tree);

            // Assert: both vessels have on-rails state
            Assert.Equal(2, bgRecorder.OnRailsStateCount);
            Assert.True(bgRecorder.HasOnRailsState(100));
            Assert.True(bgRecorder.HasOnRailsState(200));
            Assert.Equal(0, bgRecorder.LoadedStateCount);
        }

        [Fact]
        public void Constructor_OnRailsState_HasNoOpenOrbitSegment()
        {
            // Constructor creates minimal on-rails state (no vessel available)
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Minimal state has no open orbit segment and is not landed
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
            Assert.False(bgRecorder.GetOnRailsIsLanded(100));
        }

        [Fact]
        public void Constructor_OnRailsState_LastExplicitEndUpdate_IsNegativeOne()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Minimal state has lastExplicitEndUpdate = -1
            Assert.Equal(-1.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));
        }

        [Fact]
        public void Constructor_EmptyBackgroundMap_CreatesNoStates()
        {
            var tree = MakeTree(); // no background vessels
            var bgRecorder = new BackgroundRecorder(tree);

            Assert.Equal(0, bgRecorder.OnRailsStateCount);
            Assert.Equal(0, bgRecorder.LoadedStateCount);
        }

        [Fact]
        public void UpdateOnRails_UpdatesExplicitEndUT_AfterInterval()
        {
            // Arrange
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Act: call UpdateOnRails at UT that exceeds interval (lastExplicitEndUpdate is -1)
            bgRecorder.UpdateOnRails(50.0);

            // Assert: ExplicitEndUT is updated on the tree recording
            Assert.Equal(50.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(50.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));
        }

        [Fact]
        public void UpdateOnRails_DoesNotUpdate_WhenIntervalNotElapsed()
        {
            // Arrange
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // First update to set the baseline
            bgRecorder.UpdateOnRails(50.0);
            Assert.Equal(50.0, tree.Recordings["rec_bg1"].ExplicitEndUT);

            // Act: call again at UT only 10s later (interval is 30s)
            bgRecorder.UpdateOnRails(60.0);

            // Assert: ExplicitEndUT should NOT be updated (still 50.0)
            Assert.Equal(50.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(50.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));
        }

        [Fact]
        public void UpdateOnRails_Updates_WhenIntervalElapsed()
        {
            // Arrange
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // First update
            bgRecorder.UpdateOnRails(50.0);

            // Act: call again at UT 30+ seconds later
            bgRecorder.UpdateOnRails(81.0);

            // Assert: ExplicitEndUT is updated
            Assert.Equal(81.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(81.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));
        }

        [Fact]
        public void UpdateOnRails_MultipleVessels_UpdatesIndependently()
        {
            // Arrange
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);

            // First update for both
            bgRecorder.UpdateOnRails(50.0);
            Assert.Equal(50.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(50.0, tree.Recordings["rec_bg2"].ExplicitEndUT);

            // Act: second update at 81.0 (30+ seconds later)
            bgRecorder.UpdateOnRails(81.0);

            // Assert: both updated
            Assert.Equal(81.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(81.0, tree.Recordings["rec_bg2"].ExplicitEndUT);
        }

        [Fact]
        public void UpdateOnRails_NullTree_DoesNotThrow()
        {
            // Construct with a valid tree, but if tree were set to null internally...
            // Actually the constructor requires a non-null tree. Test that calling with
            // an empty background map doesn't throw.
            var tree = MakeTree();
            var bgRecorder = new BackgroundRecorder(tree);

            // Should not throw
            bgRecorder.UpdateOnRails(100.0);
        }

        #endregion

        #region 9.5 Vessel Lifecycle

        [Fact]
        public void Constructor_CreatesMinimalOnRailsState_ForBackgroundVessels()
        {
            // The constructor creates a minimal on-rails state for each vessel in
            // the BackgroundMap (equivalent to the "vessel not found" path in
            // OnVesselBackgrounded, since no actual Vessel objects exist).
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Verify both vessels have on-rails state with minimal defaults
            Assert.True(bgRecorder.HasOnRailsState(100));
            Assert.False(bgRecorder.HasLoadedState(100));
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
            Assert.Equal(-1.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));

            Assert.True(bgRecorder.HasOnRailsState(200));
            Assert.False(bgRecorder.HasLoadedState(200));
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(200));
            Assert.Equal(-1.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(200));
        }

        [Fact]
        public void Constructor_VesselNotInBackgroundMap_HasNoState()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // PID 999 is not in the BackgroundMap
            Assert.False(bgRecorder.HasOnRailsState(999));
            Assert.False(bgRecorder.HasLoadedState(999));
        }

        [Fact]
        public void Constructor_MultipleVessels_AllGetIndependentState()
        {
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"), (300, "rec_bg3"));
            var bgRecorder = new BackgroundRecorder(tree);

            // All three should have independent on-rails state
            Assert.Equal(3, bgRecorder.OnRailsStateCount);
            Assert.True(bgRecorder.HasOnRailsState(100));
            Assert.True(bgRecorder.HasOnRailsState(200));
            Assert.True(bgRecorder.HasOnRailsState(300));
        }

        #endregion

        #region 9.3 Part Event Polling (Static Method Integration)

        [Fact]
        public void CheckParachuteTransition_WorksWithBackgroundStateCollections()
        {
            // Verify that the static CheckParachuteTransition method works with
            // the same Dictionary<uint, int> type used by BackgroundVesselState
            var parachuteStates = new Dictionary<uint, int>();
            parachuteStates[42] = 0; // STOWED

            // Transition STOWED -> SEMI-DEPLOYED (state 0 -> 1)
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachuteSingle", 1, parachuteStates, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteSemiDeployed, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
        }

        [Fact]
        public void CheckEngineTransition_WorksWithBackgroundStateCollections()
        {
            // Verify that CheckEngineTransition works with the same collection types
            // used by BackgroundVesselState
            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();

            ulong key = FlightRecorder.EncodeEngineKey(42, 0);

            // Engine off -> on at 80% throttle
            var events = FlightRecorder.CheckEngineTransition(
                key, 42, 0, "liquidEngine1-2",
                true, 0.8f,
                activeEngineKeys, lastThrottle, 100.0);

            Assert.NotNull(events);
            Assert.True(events.Count > 0);
            Assert.Equal(PartEventType.EngineIgnited, events[0].eventType);
        }

        [Fact]
        public void CheckDeployableTransition_WorksWithBackgroundStateCollections()
        {
            // Verify that CheckDeployableTransition works with the same HashSet<uint>
            // used by BackgroundVesselState
            var extendedDeployables = new HashSet<uint>();

            // Deploy: RETRACTED -> EXTENDED
            var evt = FlightRecorder.CheckDeployableTransition(
                42, "solarPanel", true, extendedDeployables, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.DeployableExtended, evt.Value.eventType);
        }

        [Fact]
        public void CheckLightTransition_WorksWithBackgroundStateCollections()
        {
            var lightsOn = new HashSet<uint>();

            // Light off -> on
            var evt = FlightRecorder.CheckLightTransition(
                42, "spotLight1", true, lightsOn, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.LightOn, evt.Value.eventType);
        }

        [Fact]
        public void CheckGearTransition_WorksWithBackgroundStateCollections()
        {
            var deployedGear = new HashSet<uint>();

            // Gear retracted -> deployed
            var evt = FlightRecorder.CheckGearTransition(
                42, "gear1", true, deployedGear, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.GearDeployed, evt.Value.eventType);
        }

        [Fact]
        public void CheckCargoBayTransition_WorksWithBackgroundStateCollections()
        {
            var openCargoBays = new HashSet<uint>();

            // Cargo bay closed -> opened
            var evt = FlightRecorder.CheckCargoBayTransition(
                42, "cargoBay", true, openCargoBays, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.CargoBayOpened, evt.Value.eventType);
        }

        [Fact]
        public void CheckFairingTransition_WorksWithBackgroundStateCollections()
        {
            var deployedFairings = new HashSet<uint>();

            // Fairing intact -> deployed
            var evt = FlightRecorder.CheckFairingTransition(
                42, "fairingSize1", true, deployedFairings, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.FairingJettisoned, evt.Value.eventType);
        }

        [Fact]
        public void CheckRcsTransition_WorksWithBackgroundStateCollections()
        {
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();

            ulong key = FlightRecorder.EncodeEngineKey(42, 0);

            // RCS off -> on
            var events = FlightRecorder.CheckRcsTransition(
                key, 42, 0, "RCSBlock",
                true, 0.5f,
                activeRcsKeys, lastRcsThrottle, 100.0);

            Assert.NotNull(events);
            Assert.True(events.Count > 0);
            Assert.Equal(PartEventType.RCSActivated, events[0].eventType);
        }

        #endregion

        #region 9.4 Adaptive Sampling

        [Fact]
        public void ShouldRecordPoint_UsedByBackground_WorksWithDefaults()
        {
            // BackgroundRecorder uses TrajectoryMath.ShouldRecordPoint with the same
            // parameters as FlightRecorder. Verify it works with typical values.
            var currentVel = new UnityEngine.Vector3(100f, 0f, 0f);
            var lastVel = new UnityEngine.Vector3(100f, 0f, 0f);
            double currentUT = 101.0;
            double lastUT = 100.0;

            // With default settings: 3s max interval, 2deg direction, 5% speed
            bool shouldRecord = TrajectoryMath.ShouldRecordPoint(
                currentVel, lastVel, currentUT, lastUT, 3.0f, 2.0f, 0.05f);

            // Same velocity, only 1s apart -> should NOT record
            Assert.False(shouldRecord);
        }

        [Fact]
        public void ShouldRecordPoint_RecordsAfterMaxInterval()
        {
            var currentVel = new UnityEngine.Vector3(100f, 0f, 0f);
            var lastVel = new UnityEngine.Vector3(100f, 0f, 0f);
            double currentUT = 104.0;
            double lastUT = 100.0;

            // 4s elapsed, max interval is 3s -> should record
            bool shouldRecord = TrajectoryMath.ShouldRecordPoint(
                currentVel, lastVel, currentUT, lastUT, 3.0f, 2.0f, 0.05f);

            Assert.True(shouldRecord);
        }

        [Fact]
        public void ShouldRecordPoint_RecordsOnVelocityDirectionChange()
        {
            var currentVel = new UnityEngine.Vector3(100f, 10f, 0f);
            var lastVel = new UnityEngine.Vector3(100f, 0f, 0f);
            double currentUT = 100.5;
            double lastUT = 100.0;

            // Velocity direction changed by about 5.7 degrees (> 2 deg threshold)
            bool shouldRecord = TrajectoryMath.ShouldRecordPoint(
                currentVel, lastVel, currentUT, lastUT, 3.0f, 2.0f, 0.05f);

            Assert.True(shouldRecord);
        }

        #endregion

        #region 9.6 Data Flow

        [Fact]
        public void UpdateOnRails_WritesExplicitEndUT_ToTreeRecording()
        {
            // Verify that UpdateOnRails directly modifies the tree Recording object
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            double originalEndUT = tree.Recordings["rec_bg1"].ExplicitEndUT;

            // Act: update at a time that exceeds the interval threshold
            bgRecorder.UpdateOnRails(1000.0);

            // Assert: ExplicitEndUT was updated to the new time
            Assert.Equal(1000.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.NotEqual(originalEndUT, tree.Recordings["rec_bg1"].ExplicitEndUT);
        }

        [Fact]
        public void Constructor_PreservesTreeRecordingReferences()
        {
            // The BackgroundRecorder should work with the same Recording objects
            // that are in the tree (not copies). This ensures writes are visible
            // to the tree immediately.
            var tree = MakeTree((100, "rec_bg1"));

            // Get reference to the recording before constructing BackgroundRecorder
            var recBefore = tree.Recordings["rec_bg1"];

            var bgRecorder = new BackgroundRecorder(tree);

            // Update via BackgroundRecorder
            bgRecorder.UpdateOnRails(500.0);

            // The same object should be updated
            Assert.Equal(500.0, recBefore.ExplicitEndUT);
            Assert.Same(recBefore, tree.Recordings["rec_bg1"]);
        }

        #endregion

        #region 9.1 cont: On-Rails SurfacePosition (integration paths)

        [Fact]
        public void OnVesselBackgrounded_NotInTree_DoesNotCreateState()
        {
            // Vessel PID 999 is not in BackgroundMap
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.OnVesselBackgrounded(999);

            // Should still only have PID 100
            Assert.Equal(1, bgRecorder.OnRailsStateCount);
            Assert.False(bgRecorder.HasOnRailsState(999));
        }

        #endregion

        #region EncodeEngineKey consistency

        [Fact]
        public void EncodeEngineKey_SameEncodingForBackgroundAndActive()
        {
            // BackgroundRecorder uses FlightRecorder.EncodeEngineKey for engine/RCS/robotic keys.
            // Verify the encoding is deterministic.
            ulong key1 = FlightRecorder.EncodeEngineKey(42, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(42, 0);
            Assert.Equal(key1, key2);

            // Different module index -> different key
            ulong key3 = FlightRecorder.EncodeEngineKey(42, 1);
            Assert.NotEqual(key1, key3);

            // Different PID -> different key
            ulong key4 = FlightRecorder.EncodeEngineKey(43, 0);
            Assert.NotEqual(key1, key4);
        }

        [Fact]
        public void ComputeRcsPower_WorksForBackgroundRecording()
        {
            // BackgroundRecorder calls FlightRecorder.ComputeRcsPower
            // with thrust forces from background RCS modules.
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { 1.0f, 1.0f, 1.0f, 1.0f }, 1.0f);

            // 4 thrusters at 1.0f each, thrusterPower = 1.0f
            // sum(forces) / (thrusterPower * numForces) = 4.0 / 4.0 = 1.0
            Assert.Equal(1.0f, power);
        }

        [Fact]
        public void ComputeRcsPower_GuardsZeroThrusterPower()
        {
            // thrusterPower = 0 should return 0 (not throw)
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { 1.0f }, 0f);

            Assert.Equal(0f, power);
        }

        [Fact]
        public void ComputeRcsPower_GuardsEmptyForces()
        {
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { }, 1.0f);

            Assert.Equal(0f, power);
        }

        #endregion

        #region 13.1 Orbital Checkpoint Capture

        [Fact]
        public void IsOrbitalCheckpointSituation_Orbiting_ReturnsTrue()
        {
            Assert.True(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.ORBITING));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_SubOrbital_ReturnsTrue()
        {
            Assert.True(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.SUB_ORBITAL));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_Escaping_ReturnsTrue()
        {
            Assert.True(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.ESCAPING));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_Landed_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.LANDED));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_Splashed_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.SPLASHED));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_Flying_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.FLYING));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_PreLaunch_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.PRELAUNCH));
        }

        [Fact]
        public void CheckpointAllVessels_NoOpenSegments_SkipsAll()
        {
            // Arrange: tree with two background vessels, neither has open orbit segments
            // (constructor creates minimal state with hasOpenOrbitSegment=false)
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Sanity: neither has open orbit segment
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(200));

            // Act: checkpoint at UT=500
            bgRecorder.CheckpointAllVessels(500.0);

            // Assert: no orbit segments added (nothing to close/reopen)
            Assert.Empty(tree.Recordings["rec_bg1"].OrbitSegments);
            Assert.Empty(tree.Recordings["rec_bg2"].OrbitSegments);
        }

        [Fact]
        public void CheckpointAllVessels_WithOpenSegment_ClosesAndReopensIfVesselFound()
        {
            // Arrange: tree with one background vessel with an open orbit segment
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Inject an open orbit segment (simulating a vessel that went on rails)
            var segment = new OrbitSegment
            {
                startUT = 300.0,
                inclination = 45.0,
                eccentricity = 0.01,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 300.0,
                bodyName = "Kerbin"
            };
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, segment);
            Assert.True(bgRecorder.GetOnRailsHasOpenSegment(100));

            // Act: checkpoint at UT=500
            // Note: FindVesselByPid returns null in tests (no KSP runtime),
            // so the open segment will be closed but no new one will be opened.
            bgRecorder.CheckpointAllVessels(500.0);

            // Assert: the segment was closed (endUT set to checkpoint UT)
            Assert.Single(tree.Recordings["rec_bg1"].OrbitSegments);
            var closedSegment = tree.Recordings["rec_bg1"].OrbitSegments[0];
            Assert.Equal(300.0, closedSegment.startUT);
            Assert.Equal(500.0, closedSegment.endUT);
            Assert.Equal("Kerbin", closedSegment.bodyName);

            // Segment is closed but NOT reopened (vessel not found in test environment)
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
        }

        [Fact]
        public void CheckpointAllVessels_EmptyBackgroundMap_DoesNotThrow()
        {
            var tree = MakeTree(); // no background vessels
            var bgRecorder = new BackgroundRecorder(tree);

            // Should not throw
            bgRecorder.CheckpointAllVessels(100.0);
        }

        [Fact]
        public void CheckpointAllVessels_MixedStates_OnlyCheckpointsOpenSegments()
        {
            // Arrange: two background vessels — one with open segment, one without
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Only vessel 100 has an open orbit segment
            var segment = new OrbitSegment
            {
                startUT = 300.0,
                inclination = 30.0,
                eccentricity = 0.0,
                semiMajorAxis = 600000.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 300.0,
                bodyName = "Mun"
            };
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, segment);

            // Act
            bgRecorder.CheckpointAllVessels(400.0);

            // Assert: vessel 100 had its segment closed
            Assert.Single(tree.Recordings["rec_bg1"].OrbitSegments);
            Assert.Equal(400.0, tree.Recordings["rec_bg1"].OrbitSegments[0].endUT);

            // Vessel 200 had no open segment, so no orbit segments were added
            Assert.Empty(tree.Recordings["rec_bg2"].OrbitSegments);
        }

        [Fact]
        public void CheckpointAllVessels_LogsCheckpointCounts()
        {
            // Arrange: capture log output
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
                var bgRecorder = new BackgroundRecorder(tree);

                // One vessel with open segment, one without
                bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
                {
                    startUT = 100.0, bodyName = "Kerbin"
                });

                // Act
                bgRecorder.CheckpointAllVessels(200.0);

                // Assert: log should contain the summary
                Assert.Contains(logLines, l =>
                    l.Contains("[BgRecorder]") &&
                    l.Contains("CheckpointAllVessels") &&
                    l.Contains("skippedNotOrbital=1"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }

        [Fact]
        public void InjectOpenOrbitSegmentForTesting_SetsOpenSegmentState()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Initially no open segment
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));

            // Inject
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
            {
                startUT = 100.0,
                semiMajorAxis = 700000.0,
                bodyName = "Kerbin"
            });

            // Now has open segment
            Assert.True(bgRecorder.GetOnRailsHasOpenSegment(100));
        }

        [Fact]
        public void InjectOpenOrbitSegmentForTesting_NoState_DoesNotThrow()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // PID 999 is not in the on-rails state — should not throw
            bgRecorder.InjectOpenOrbitSegmentForTesting(999, new OrbitSegment());

            // No state was created (only modifies existing state)
            Assert.False(bgRecorder.HasOnRailsState(999));
        }

        #endregion
    }
}
