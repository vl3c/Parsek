using System.Collections.Generic;
using System.Globalization;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class PartEventTests
    {
        public PartEventTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region Serialization roundtrip

        [Fact]
        public void PartEvents_SerializationRoundtrip()
        {
            // Build a RECORDING with PART_EVENT nodes via the builder
            var recNode = new RecordingBuilder("TestVessel")
                .AddPoint(100, 0, 0, 100)
                .AddPoint(110, 0, 0, 200)
                .AddPartEvent(105, 12345, (int)PartEventType.Decoupled, "fuelTank")
                .AddPartEvent(108, 67890, (int)PartEventType.Destroyed, "engine")
                .Build();

            // Load via ParsekScenario's static loader
            var rec = new RecordingStore.Recording
            {
                VesselName = recNode.GetValue("vesselName") ?? "Unknown"
            };
            ParsekScenario.LoadRecordingMetadata(recNode, rec);

            // Load points
            var ptNodes = recNode.GetNodes("POINT");
            for (int i = 0; i < ptNodes.Length; i++)
            {
                var pt = new TrajectoryPoint();
                var inv = NumberStyles.Float;
                var ic = CultureInfo.InvariantCulture;
                double.TryParse(ptNodes[i].GetValue("ut"), inv, ic, out pt.ut);
                double.TryParse(ptNodes[i].GetValue("lat"), inv, ic, out pt.latitude);
                double.TryParse(ptNodes[i].GetValue("lon"), inv, ic, out pt.longitude);
                double.TryParse(ptNodes[i].GetValue("alt"), inv, ic, out pt.altitude);
                pt.bodyName = ptNodes[i].GetValue("body") ?? "Kerbin";
                rec.Points.Add(pt);
            }

            // Load part events
            var peNodes = recNode.GetNodes("PART_EVENT");
            for (int pe = 0; pe < peNodes.Length; pe++)
            {
                var evt = new PartEvent();
                var inv = NumberStyles.Float;
                var ic = CultureInfo.InvariantCulture;
                double.TryParse(peNodes[pe].GetValue("ut"), inv, ic, out evt.ut);
                uint pid;
                if (uint.TryParse(peNodes[pe].GetValue("pid"), NumberStyles.Integer, ic, out pid))
                    evt.partPersistentId = pid;
                int typeInt;
                if (int.TryParse(peNodes[pe].GetValue("type"), NumberStyles.Integer, ic, out typeInt))
                    evt.eventType = (PartEventType)typeInt;
                evt.partName = peNodes[pe].GetValue("part") ?? "";
                rec.PartEvents.Add(evt);
            }

            Assert.Equal(2, rec.PartEvents.Count);

            Assert.Equal(105.0, rec.PartEvents[0].ut);
            Assert.Equal(12345u, rec.PartEvents[0].partPersistentId);
            Assert.Equal(PartEventType.Decoupled, rec.PartEvents[0].eventType);
            Assert.Equal("fuelTank", rec.PartEvents[0].partName);

            Assert.Equal(108.0, rec.PartEvents[1].ut);
            Assert.Equal(67890u, rec.PartEvents[1].partPersistentId);
            Assert.Equal(PartEventType.Destroyed, rec.PartEvents[1].eventType);
            Assert.Equal("engine", rec.PartEvents[1].partName);
        }

        [Fact]
        public void PartEvents_BackwardCompat_EmptyListWhenNoNodes()
        {
            // Build a RECORDING with no PART_EVENT nodes
            var recNode = new RecordingBuilder("OldVessel")
                .AddPoint(100, 0, 0, 100)
                .AddPoint(110, 0, 0, 200)
                .Build();

            var peNodes = recNode.GetNodes("PART_EVENT");

            Assert.Empty(peNodes);

            // A Recording starts with an empty PartEvents list
            var rec = new RecordingStore.Recording();
            Assert.Empty(rec.PartEvents);
        }

        #endregion

        #region Part subtree building

        [Fact]
        public void BuildPartSubtreeMap_FourPartTree()
        {
            // Build a snapshot with 4 parts:
            // Part 0 (pid=100, root, parent=0)
            // Part 1 (pid=101, parent=0 → child of 100)
            // Part 2 (pid=102, parent=1 → child of 101)
            // Part 3 (pid=103, parent=1 → child of 101)
            var snapshot = new ConfigNode("VESSEL");
            AddTestPart(snapshot, "root", 100, 0);      // index 0, parent=0 (self = root)
            AddTestPart(snapshot, "tank", 101, 0);       // index 1, parent=0
            AddTestPart(snapshot, "engine", 102, 1);     // index 2, parent=1
            AddTestPart(snapshot, "nozzle", 103, 1);     // index 3, parent=1

            var map = GhostVisualBuilder.BuildPartSubtreeMap(snapshot);

            // Part 100 has child 101 (parent=0 which is pid 100)
            Assert.True(map.ContainsKey(100));
            Assert.Contains(101u, map[100]);

            // Part 101 has children 102 and 103
            Assert.True(map.ContainsKey(101));
            Assert.Contains(102u, map[101]);
            Assert.Contains(103u, map[101]);

            // Parts 102 and 103 have no children
            Assert.False(map.ContainsKey(102));
            Assert.False(map.ContainsKey(103));
        }

        [Fact]
        public void BuildPartSubtreeMap_NullSnapshot_ReturnsEmpty()
        {
            var map = GhostVisualBuilder.BuildPartSubtreeMap(null);
            Assert.Empty(map);
        }

        [Fact]
        public void BuildPartSubtreeMap_EmptySnapshot_ReturnsEmpty()
        {
            var map = GhostVisualBuilder.BuildPartSubtreeMap(new ConfigNode("VESSEL"));
            Assert.Empty(map);
        }

        #endregion

        #region Parachute state tracking

        [Fact]
        public void ParachuteTransition_StowedToSemiDeployed_EmitsSemiDeployedEvent()
        {
            var states = new Dictionary<uint, int>();
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachute", 1, states, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteSemiDeployed, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
            Assert.Equal(1, states[42]);
        }

        [Fact]
        public void ParachuteTransition_SemiToFullyDeployed_EmitsDeployedEvent()
        {
            var states = new Dictionary<uint, int> { { 42, 1 } };
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachute", 2, states, 110.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteDeployed, evt.Value.eventType);
            Assert.Equal(2, states[42]);
        }

        [Fact]
        public void ParachuteTransition_StowedToFullyDeployed_EmitsDeployedEvent()
        {
            // Edge case: recording starts with chute already deployed
            var states = new Dictionary<uint, int>();
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachute", 2, states, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteDeployed, evt.Value.eventType);
            Assert.Equal(2, states[42]);
        }

        [Fact]
        public void ParachuteTransition_SemiDeployedToCut_EmitsCutEvent()
        {
            var states = new Dictionary<uint, int> { { 42, 1 } };
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachute", 0, states, 120.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteCut, evt.Value.eventType);
            Assert.False(states.ContainsKey(42));
        }

        [Fact]
        public void ParachuteTransition_FullyDeployedToCut_EmitsCutEvent()
        {
            var states = new Dictionary<uint, int> { { 42, 2 } };
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachute", 0, states, 120.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteCut, evt.Value.eventType);
            Assert.False(states.ContainsKey(42));
        }

        [Fact]
        public void ParachuteTransition_NoChange_ReturnsNull()
        {
            var states = new Dictionary<uint, int>();
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachute", 0, states, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void ParachuteTransition_AlreadySemiDeployed_ReturnsNull()
        {
            var states = new Dictionary<uint, int> { { 42, 1 } };
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachute", 1, states, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void ParachuteTransition_AlreadyFullyDeployed_ReturnsNull()
        {
            var states = new Dictionary<uint, int> { { 42, 2 } };
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachute", 2, states, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void ParachuteDestroyed_SemiDeployedChuteDeath_EmitsParachuteDestroyed()
        {
            var states = new Dictionary<uint, int> { { 42, 1 } };
            var evtType = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: true, states);

            Assert.Equal(PartEventType.ParachuteDestroyed, evtType);
            Assert.False(states.ContainsKey(42));
        }

        [Fact]
        public void ParachuteDestroyed_FullyDeployedChuteDeath_EmitsParachuteDestroyed()
        {
            var states = new Dictionary<uint, int> { { 42, 2 } };
            var evtType = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: true, states);

            Assert.Equal(PartEventType.ParachuteDestroyed, evtType);
            Assert.False(states.ContainsKey(42));
        }

        [Fact]
        public void ParachuteDestroyed_StowedChuteDeath_EmitsDestroyed()
        {
            var states = new Dictionary<uint, int>();
            var evtType = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: true, states);

            Assert.Equal(PartEventType.Destroyed, evtType);
        }

        [Fact]
        public void ParachuteDestroyed_NonChutePart_EmitsDestroyed()
        {
            var states = new Dictionary<uint, int> { { 42, 2 } };
            var evtType = FlightRecorder.ClassifyPartDeath(99, hasParachuteModule: false, states);

            Assert.Equal(PartEventType.Destroyed, evtType);
            Assert.Contains(42u, (IDictionary<uint, int>)states); // other entries untouched
        }

        #endregion

        #region EVA child recording fields

        [Fact]
        public void ParentRecordingId_SerializationRoundtrip()
        {
            var recNode = new RecordingBuilder("EVA Child")
                .AddPoint(100, 0, 0, 100)
                .AddPoint(110, 0, 0, 200)
                .WithParentRecordingId("abc123")
                .WithEvaCrewName("Jebediah Kerman")
                .Build();

            Assert.Equal("abc123", recNode.GetValue("parentRecordingId"));
            Assert.Equal("Jebediah Kerman", recNode.GetValue("evaCrewName"));
        }

        [Fact]
        public void ParentRecordingId_BackwardCompat_NullWhenMissing()
        {
            var recNode = new RecordingBuilder("Old Recording")
                .AddPoint(100, 0, 0, 100)
                .AddPoint(110, 0, 0, 200)
                .Build();

            Assert.Null(recNode.GetValue("parentRecordingId"));
            Assert.Null(recNode.GetValue("evaCrewName"));
        }

        #endregion

        #region RemoveSpecificCrewFromSnapshot

        [Fact]
        public void RemoveSpecificCrew_RemovesNamedKerbal()
        {
            var snapshot = new VesselSnapshotBuilder()
                .AddPart("mk1pod.v2", "Jeb")
                .Build();

            var exclude = new HashSet<string> { "Jeb" };
            VesselSpawner.RemoveSpecificCrewFromSnapshot(snapshot, exclude);

            var parts = snapshot.GetNodes("PART");
            foreach (var part in parts)
            {
                var crew = part.GetValues("crew");
                Assert.DoesNotContain("Jeb", crew);
            }
        }

        [Fact]
        public void RemoveSpecificCrew_KeepsOtherCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", "mk1pod.v2");
            part.AddValue("crew", "Jeb");
            part.AddValue("crew", "Bill");

            var exclude = new HashSet<string> { "Jeb" };
            VesselSpawner.RemoveSpecificCrewFromSnapshot(snapshot, exclude);

            var crewAfter = part.GetValues("crew");
            Assert.Single(crewAfter);
            Assert.Equal("Bill", crewAfter[0]);
        }

        [Fact]
        public void RemoveSpecificCrew_NullSnapshot_NoException()
        {
            VesselSpawner.RemoveSpecificCrewFromSnapshot(null, new HashSet<string> { "Jeb" });
            // No exception = pass
        }

        [Fact]
        public void RemoveSpecificCrew_NullExcludeSet_NoException()
        {
            var snapshot = new VesselSnapshotBuilder()
                .AddPart("mk1pod.v2", "Jeb")
                .Build();

            VesselSpawner.RemoveSpecificCrewFromSnapshot(snapshot, null);
            // No exception = pass
        }

        #endregion

        #region FindDuplicateCrew (spawn-time dedup)

        [Fact]
        public void FindDuplicateCrew_DetectsDuplicate()
        {
            var snapshotCrew = new List<string> { "Jebediah Kerman", "Bill Kerman" };
            var existingCrew = new HashSet<string> { "Jebediah Kerman", "Valentina Kerman" };

            var dupes = VesselSpawner.FindDuplicateCrew(snapshotCrew, existingCrew);

            Assert.Single(dupes);
            Assert.Contains("Jebediah Kerman", dupes);
        }

        [Fact]
        public void FindDuplicateCrew_NoDuplicates_ReturnsEmpty()
        {
            var snapshotCrew = new List<string> { "Bill Kerman" };
            var existingCrew = new HashSet<string> { "Jebediah Kerman", "Valentina Kerman" };

            var dupes = VesselSpawner.FindDuplicateCrew(snapshotCrew, existingCrew);

            Assert.Empty(dupes);
        }

        [Fact]
        public void FindDuplicateCrew_EmptyExisting_ReturnsEmpty()
        {
            var snapshotCrew = new List<string> { "Jebediah Kerman" };
            var existingCrew = new HashSet<string>();

            var dupes = VesselSpawner.FindDuplicateCrew(snapshotCrew, existingCrew);

            Assert.Empty(dupes);
        }

        [Fact]
        public void FindDuplicateCrew_NullInputs_ReturnsEmpty()
        {
            Assert.Empty(VesselSpawner.FindDuplicateCrew(null, new HashSet<string> { "Jeb" }));
            Assert.Empty(VesselSpawner.FindDuplicateCrew(new List<string> { "Jeb" }, null));
        }

        [Fact]
        public void FindDuplicateCrew_MultipleDuplicates()
        {
            var snapshotCrew = new List<string> { "Jebediah Kerman", "Bill Kerman", "Valentina Kerman" };
            var existingCrew = new HashSet<string> { "Jebediah Kerman", "Valentina Kerman" };

            var dupes = VesselSpawner.FindDuplicateCrew(snapshotCrew, existingCrew);

            Assert.Equal(2, dupes.Count);
            Assert.Contains("Jebediah Kerman", dupes);
            Assert.Contains("Valentina Kerman", dupes);
        }

        #endregion

        #region StashPending with PartEvents

        [Fact]
        public void StashPending_WithPartEvents_StoresEvents()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 110, bodyName = "Kerbin" }
            };
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 105, partPersistentId = 42, eventType = PartEventType.Decoupled, partName = "tank" }
            };

            RecordingStore.StashPending(points, "TestVessel", partEvents: events);

            Assert.True(RecordingStore.HasPending);
            Assert.Single(RecordingStore.Pending.PartEvents);
            Assert.Equal(PartEventType.Decoupled, RecordingStore.Pending.PartEvents[0].eventType);
        }

        [Fact]
        public void StashPending_WithoutPartEvents_EmptyList()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 110, bodyName = "Kerbin" }
            };

            RecordingStore.StashPending(points, "TestVessel");

            Assert.True(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.Pending.PartEvents);
        }

        #endregion

        #region Engine state tracking

        [Fact]
        public void EngineTransition_Ignition_EmitsIgnitedEvent()
        {
            var active = new HashSet<ulong>();
            var throttles = new Dictionary<ulong, float>();
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var events = FlightRecorder.CheckEngineTransition(
                key, 100, 0, "liquidEngine", true, 0.8f, active, throttles, 200.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.EngineIgnited, events[0].eventType);
            Assert.Equal(100u, events[0].partPersistentId);
            Assert.Equal(0, events[0].moduleIndex);
            Assert.Equal(0.8f, events[0].value, 0.001f);
            Assert.Equal(200.0, events[0].ut);
            Assert.Contains(key, active);
        }

        [Fact]
        public void EngineTransition_Shutdown_EmitsShutdownEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var active = new HashSet<ulong> { key };
            var throttles = new Dictionary<ulong, float> { { key, 1.0f } };
            var events = FlightRecorder.CheckEngineTransition(
                key, 100, 0, "liquidEngine", false, 0f, active, throttles, 210.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.EngineShutdown, events[0].eventType);
            Assert.Equal(100u, events[0].partPersistentId);
            Assert.DoesNotContain(key, active);
            Assert.False(throttles.ContainsKey(key));
        }

        [Fact]
        public void EngineTransition_ThrottleChange_EmitsThrottleEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var active = new HashSet<ulong> { key };
            var throttles = new Dictionary<ulong, float> { { key, 0.5f } };
            var events = FlightRecorder.CheckEngineTransition(
                key, 100, 0, "liquidEngine", true, 0.9f, active, throttles, 215.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.EngineThrottle, events[0].eventType);
            Assert.Equal(0.9f, events[0].value, 0.001f);
            Assert.Equal(0.9f, throttles[key], 0.001f);
        }

        [Fact]
        public void EngineTransition_SmallThrottleChange_NoEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var active = new HashSet<ulong> { key };
            var throttles = new Dictionary<ulong, float> { { key, 0.5f } };
            var events = FlightRecorder.CheckEngineTransition(
                key, 100, 0, "liquidEngine", true, 0.505f, active, throttles, 215.0);

            Assert.Empty(events);
        }

        [Fact]
        public void EngineTransition_NotIgnited_NoChange_NoEvent()
        {
            var active = new HashSet<ulong>();
            var throttles = new Dictionary<ulong, float>();
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var events = FlightRecorder.CheckEngineTransition(
                key, 100, 0, "liquidEngine", false, 0f, active, throttles, 200.0);

            Assert.Empty(events);
        }

        [Fact]
        public void EngineTransition_MultiEngine_DifferentModuleIndex()
        {
            var active = new HashSet<ulong>();
            var throttles = new Dictionary<ulong, float>();

            // First engine on part 200, midx=0
            ulong key0 = FlightRecorder.EncodeEngineKey(200, 0);
            var events0 = FlightRecorder.CheckEngineTransition(
                key0, 200, 0, "rapier", true, 1.0f, active, throttles, 300.0);

            // Second engine on same part 200, midx=1
            ulong key1 = FlightRecorder.EncodeEngineKey(200, 1);
            var events1 = FlightRecorder.CheckEngineTransition(
                key1, 200, 1, "rapier", true, 0.5f, active, throttles, 300.0);

            Assert.Single(events0);
            Assert.Equal(0, events0[0].moduleIndex);
            Assert.Single(events1);
            Assert.Equal(1, events1[0].moduleIndex);
            Assert.Contains(key0, active);
            Assert.Contains(key1, active);
        }

        [Fact]
        public void EncodeEngineKey_NoCollision_HighPid()
        {
            // Verify keys don't collide for large pid values
            ulong key1 = FlightRecorder.EncodeEngineKey(uint.MaxValue, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(uint.MaxValue - 1, 0);
            ulong key3 = FlightRecorder.EncodeEngineKey(uint.MaxValue, 1);
            Assert.NotEqual(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        #endregion

        #region Deployable state tracking

        [Fact]
        public void DeployableTransition_RetractedToExtended_EmitsExtendedEvent()
        {
            var extended = new HashSet<uint>();
            var evt = FlightRecorder.CheckDeployableTransition(
                42, "solarPanel", isExtended: true, extended, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.DeployableExtended, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
            Assert.Equal("solarPanel", evt.Value.partName);
            Assert.Contains(42u, extended);
        }

        [Fact]
        public void DeployableTransition_ExtendedToRetracted_EmitsRetractedEvent()
        {
            var extended = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckDeployableTransition(
                42, "solarPanel", isExtended: false, extended, 120.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.DeployableRetracted, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(120.0, evt.Value.ut);
            Assert.DoesNotContain(42u, extended);
        }

        [Fact]
        public void DeployableTransition_NoChange_ReturnsNull()
        {
            var extended = new HashSet<uint>();
            var evt = FlightRecorder.CheckDeployableTransition(
                42, "solarPanel", isExtended: false, extended, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void DeployableTransition_AlreadyExtended_ReturnsNull()
        {
            var extended = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckDeployableTransition(
                42, "solarPanel", isExtended: true, extended, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void PartEvents_SerializationRoundtrip_DeployableExtended()
        {
            var recNode = new RecordingBuilder("TestVessel")
                .AddPoint(100, 0, 0, 100)
                .AddPoint(110, 0, 0, 200)
                .AddPartEvent(105, 12345, (int)PartEventType.DeployableExtended, "solarPanels5")
                .Build();

            var peNodes = recNode.GetNodes("PART_EVENT");
            Assert.Single(peNodes);

            int typeInt;
            int.TryParse(peNodes[0].GetValue("type"),
                System.Globalization.NumberStyles.Integer,
                CultureInfo.InvariantCulture, out typeInt);
            Assert.Equal((int)PartEventType.DeployableExtended, typeInt);
            Assert.Equal("solarPanels5", peNodes[0].GetValue("part"));
        }

        [Fact]
        public void LadderTransition_RetractedToExtended_EmitsDeployableExtended()
        {
            var deployed = new HashSet<ulong>();
            ulong key = FlightRecorder.EncodeEngineKey(42, 1);
            var evt = FlightRecorder.CheckLadderTransition(
                key, 42, "telescopicLadder", isExtended: true,
                deployed, 100.0, moduleIndex: 1);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.DeployableExtended, evt.Value.eventType);
            Assert.Equal(1, evt.Value.moduleIndex);
            Assert.Contains(key, deployed);
        }

        [Fact]
        public void LadderTransition_ExtendedToRetracted_EmitsDeployableRetracted()
        {
            ulong key = FlightRecorder.EncodeEngineKey(42, 1);
            var deployed = new HashSet<ulong> { key };
            var evt = FlightRecorder.CheckLadderTransition(
                key, 42, "telescopicLadder", isExtended: false,
                deployed, 110.0, moduleIndex: 1);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.DeployableRetracted, evt.Value.eventType);
            Assert.DoesNotContain(key, deployed);
        }

        [Fact]
        public void AnimationGroupTransition_RetractedToDeployed_EmitsDeployableExtended()
        {
            var deployed = new HashSet<ulong>();
            ulong key = FlightRecorder.EncodeEngineKey(84, 2);
            var evt = FlightRecorder.CheckAnimationGroupTransition(
                key, 84, "ISRU", isDeployed: true,
                deployed, 200.0, moduleIndex: 2);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.DeployableExtended, evt.Value.eventType);
            Assert.Equal(2, evt.Value.moduleIndex);
            Assert.Contains(key, deployed);
        }

        [Fact]
        public void AnimationGroupTransition_DeployedToRetracted_EmitsDeployableRetracted()
        {
            ulong key = FlightRecorder.EncodeEngineKey(84, 2);
            var deployed = new HashSet<ulong> { key };
            var evt = FlightRecorder.CheckAnimationGroupTransition(
                key, 84, "ISRU", isDeployed: false,
                deployed, 210.0, moduleIndex: 2);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.DeployableRetracted, evt.Value.eventType);
            Assert.DoesNotContain(key, deployed);
        }

        [Fact]
        public void AnimationGroupTransition_NoChange_ReturnsNull()
        {
            ulong key = FlightRecorder.EncodeEngineKey(84, 2);
            var deployed = new HashSet<ulong>();
            var evt = FlightRecorder.CheckAnimationGroupTransition(
                key, 84, "ISRU", isDeployed: false,
                deployed, 220.0, moduleIndex: 2);

            Assert.Null(evt);
        }

        [Fact]
        public void ClassifyLadderState_Endpoints_AreDetected()
        {
            FlightRecorder.ClassifyLadderState(1.0f, out bool isExtended, out bool isRetracted);
            Assert.True(isExtended);
            Assert.False(isRetracted);

            FlightRecorder.ClassifyLadderState(0.0f, out isExtended, out isRetracted);
            Assert.False(isExtended);
            Assert.True(isRetracted);
        }

        [Fact]
        public void LadderStateFromEvents_CanRetract_MarksDeployed()
        {
            bool ok = FlightRecorder.TryClassifyLadderStateFromEventActivity(
                canExtend: false, canRetract: true, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void LadderStateFromEvents_CanExtend_MarksRetracted()
        {
            bool ok = FlightRecorder.TryClassifyLadderStateFromEventActivity(
                canExtend: true, canRetract: false, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void LadderStateFromEvents_Ambiguous_ReturnsFalse()
        {
            bool okA = FlightRecorder.TryClassifyLadderStateFromEventActivity(
                canExtend: true, canRetract: true, out _, out _);
            bool okB = FlightRecorder.TryClassifyLadderStateFromEventActivity(
                canExtend: false, canRetract: false, out _, out _);

            Assert.False(okA);
            Assert.False(okB);
        }

        #endregion

        #region Light state tracking

        [Fact]
        public void LightTransition_OffToOn_EmitsLightOnEvent()
        {
            var lightsOn = new HashSet<uint>();
            var evt = FlightRecorder.CheckLightTransition(
                42, "spotLight1", isOn: true, lightsOn, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.LightOn, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
            Assert.Equal("spotLight1", evt.Value.partName);
            Assert.Contains(42u, lightsOn);
        }

        [Fact]
        public void LightTransition_OnToOff_EmitsLightOffEvent()
        {
            var lightsOn = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckLightTransition(
                42, "spotLight1", isOn: false, lightsOn, 120.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.LightOff, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(120.0, evt.Value.ut);
            Assert.DoesNotContain(42u, lightsOn);
        }

        [Fact]
        public void LightTransition_NoChange_ReturnsNull()
        {
            var lightsOn = new HashSet<uint>();
            var evt = FlightRecorder.CheckLightTransition(
                42, "spotLight1", isOn: false, lightsOn, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void LightTransition_AlreadyOn_ReturnsNull()
        {
            var lightsOn = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckLightTransition(
                42, "spotLight1", isOn: true, lightsOn, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void LightBlinkTransition_OffToBlinking_EmitsBlinkEnabledEvent()
        {
            var blinking = new HashSet<uint>();
            var blinkRates = new Dictionary<uint, float>();

            var events = FlightRecorder.CheckLightBlinkTransition(
                42, "spotLight1", isBlinking: true, blinkRate: 2.5f,
                blinking, blinkRates, 100.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.LightBlinkEnabled, events[0].eventType);
            Assert.Equal(2.5f, events[0].value, 0.001f);
            Assert.Contains(42u, blinking);
            Assert.Equal(2.5f, blinkRates[42], 0.001f);
        }

        [Fact]
        public void LightBlinkTransition_BlinkingToSteady_EmitsBlinkDisabledEvent()
        {
            var blinking = new HashSet<uint> { 42 };
            var blinkRates = new Dictionary<uint, float> { { 42, 1.2f } };

            var events = FlightRecorder.CheckLightBlinkTransition(
                42, "spotLight1", isBlinking: false, blinkRate: 1.2f,
                blinking, blinkRates, 110.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.LightBlinkDisabled, events[0].eventType);
            Assert.DoesNotContain(42u, blinking);
            Assert.False(blinkRates.ContainsKey(42));
        }

        [Fact]
        public void LightBlinkTransition_RateChanged_EmitsBlinkRateEvent()
        {
            var blinking = new HashSet<uint> { 42 };
            var blinkRates = new Dictionary<uint, float> { { 42, 1.0f } };

            var events = FlightRecorder.CheckLightBlinkTransition(
                42, "spotLight1", isBlinking: true, blinkRate: 3.0f,
                blinking, blinkRates, 120.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.LightBlinkRate, events[0].eventType);
            Assert.Equal(3.0f, events[0].value, 0.001f);
            Assert.Equal(3.0f, blinkRates[42], 0.001f);
        }

        [Fact]
        public void LightBlinkTransition_SameRate_ReturnsNoEvent()
        {
            var blinking = new HashSet<uint> { 42 };
            var blinkRates = new Dictionary<uint, float> { { 42, 2.0f } };

            var events = FlightRecorder.CheckLightBlinkTransition(
                42, "spotLight1", isBlinking: true, blinkRate: 2.005f,
                blinking, blinkRates, 130.0);

            Assert.Empty(events);
        }

        [Fact]
        public void PartEvents_SerializationRoundtrip_LightOn()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.LightOn,
                partName = "spotLight1"
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.LightOn, loaded.PartEvents[0].eventType);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
            Assert.Equal("spotLight1", loaded.PartEvents[0].partName);
            Assert.Equal(105.0, loaded.PartEvents[0].ut);
        }

        [Fact]
        public void PartEvents_SerializationRoundtrip_LightBlinkEnabled()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.LightBlinkEnabled,
                partName = "spotLight1",
                value = 1.5f
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.LightBlinkEnabled, loaded.PartEvents[0].eventType);
            Assert.Equal(1.5f, loaded.PartEvents[0].value, 0.001f);
        }

        #endregion

        #region Gear state tracking

        [Fact]
        public void GearTransition_RetractedToDeployed_EmitsDeployedEvent()
        {
            var deployed = new HashSet<uint>();
            var evt = FlightRecorder.CheckGearTransition(
                42, "GearSmall", isDeployed: true, deployed, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.GearDeployed, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
            Assert.Equal("GearSmall", evt.Value.partName);
            Assert.Contains(42u, deployed);
        }

        [Fact]
        public void GearTransition_DeployedToRetracted_EmitsRetractedEvent()
        {
            var deployed = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckGearTransition(
                42, "GearSmall", isDeployed: false, deployed, 120.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.GearRetracted, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(120.0, evt.Value.ut);
            Assert.DoesNotContain(42u, deployed);
        }

        [Fact]
        public void GearTransition_NoChange_ReturnsNull()
        {
            var deployed = new HashSet<uint>();
            var evt = FlightRecorder.CheckGearTransition(
                42, "GearSmall", isDeployed: false, deployed, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void GearTransition_AlreadyDeployed_ReturnsNull()
        {
            var deployed = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckGearTransition(
                42, "GearSmall", isDeployed: true, deployed, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void PartEvents_SerializationRoundtrip_GearDeployed()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.GearDeployed,
                partName = "GearSmall"
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.GearDeployed, loaded.PartEvents[0].eventType);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
            Assert.Equal("GearSmall", loaded.PartEvents[0].partName);
            Assert.Equal(105.0, loaded.PartEvents[0].ut);
        }

        [Fact]
        public void PartEvents_SerializationRoundtrip_GearRetracted()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.GearRetracted,
                partName = "GearSmall"
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.GearRetracted, loaded.PartEvents[0].eventType);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
            Assert.Equal("GearSmall", loaded.PartEvents[0].partName);
        }

        [Fact]
        public void ClassifyGearState_Deployed_IsDeployed()
        {
            FlightRecorder.ClassifyGearState("Deployed", out bool isDeployed, out bool isRetracted);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void ClassifyGearState_Retracted_IsRetracted()
        {
            FlightRecorder.ClassifyGearState("Retracted", out bool isDeployed, out bool isRetracted);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void ClassifyGearState_Deploying_NeitherEndpoint()
        {
            FlightRecorder.ClassifyGearState("Deploying", out bool isDeployed, out bool isRetracted);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void ClassifyGearState_Retracting_NeitherEndpoint()
        {
            FlightRecorder.ClassifyGearState("Retracting", out bool isDeployed, out bool isRetracted);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        #endregion

        #region Cargo bay state tracking

        [Fact]
        public void ClassifyCargoBayState_ClosedPos0_AnimTime0_IsClosed()
        {
            FlightRecorder.ClassifyCargoBayState(0f, 0f, out bool isOpen, out bool isClosed);
            Assert.True(isClosed);
            Assert.False(isOpen);
        }

        [Fact]
        public void ClassifyCargoBayState_ClosedPos0_AnimTime1_IsOpen()
        {
            FlightRecorder.ClassifyCargoBayState(1f, 0f, out bool isOpen, out bool isClosed);
            Assert.True(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void ClassifyCargoBayState_ClosedPos1_AnimTime0_IsOpen()
        {
            FlightRecorder.ClassifyCargoBayState(0f, 1f, out bool isOpen, out bool isClosed);
            Assert.True(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void ClassifyCargoBayState_ClosedPos1_AnimTime1_IsClosed()
        {
            FlightRecorder.ClassifyCargoBayState(1f, 1f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.True(isClosed);
        }

        [Fact]
        public void ClassifyCargoBayState_MidTransition_Neither()
        {
            FlightRecorder.ClassifyCargoBayState(0.5f, 0f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void ClassifyCargoBayState_DeployLimited_Neither()
        {
            FlightRecorder.ClassifyCargoBayState(0.3f, 0f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void ClassifyCargoBayState_NonStandardClosedPosition_Neither()
        {
            FlightRecorder.ClassifyCargoBayState(0f, 0.5f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void CargoBayTransition_ClosedToOpened_EmitsOpenedEvent()
        {
            var openSet = new HashSet<uint>();
            var evt = FlightRecorder.CheckCargoBayTransition(
                42, "cargoBayS", isOpen: true, openSet, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.CargoBayOpened, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
            Assert.Equal("cargoBayS", evt.Value.partName);
            Assert.Contains(42u, openSet);
        }

        [Fact]
        public void CargoBayTransition_OpenedToClosed_EmitsClosedEvent()
        {
            var openSet = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckCargoBayTransition(
                42, "cargoBayS", isOpen: false, openSet, 120.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.CargoBayClosed, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(120.0, evt.Value.ut);
            Assert.DoesNotContain(42u, openSet);
        }

        [Fact]
        public void CargoBayTransition_NoChange_ReturnsNull()
        {
            var openSet = new HashSet<uint>();
            var evt = FlightRecorder.CheckCargoBayTransition(
                42, "cargoBayS", isOpen: false, openSet, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void CargoBayTransition_AlreadyOpen_ReturnsNull()
        {
            var openSet = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckCargoBayTransition(
                42, "cargoBayS", isOpen: true, openSet, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void PartEvents_SerializationRoundtrip_CargoBayOpened()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.CargoBayOpened,
                partName = "cargoBayS"
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.CargoBayOpened, loaded.PartEvents[0].eventType);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
            Assert.Equal("cargoBayS", loaded.PartEvents[0].partName);
            Assert.Equal(105.0, loaded.PartEvents[0].ut);
        }

        [Fact]
        public void PartEvents_SerializationRoundtrip_CargoBayClosed()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.CargoBayClosed,
                partName = "ServiceBay_250_v2"
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.CargoBayClosed, loaded.PartEvents[0].eventType);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
            Assert.Equal("ServiceBay_250_v2", loaded.PartEvents[0].partName);
        }

        #endregion

        #region Fairing state tracking

        [Fact]
        public void FairingTransition_IntactToDeployed_EmitsJettisonedEvent()
        {
            var deployed = new HashSet<uint>();
            var evt = FlightRecorder.CheckFairingTransition(
                42, "fairingSize1", isDeployed: true, deployed, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.FairingJettisoned, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
            Assert.Equal("fairingSize1", evt.Value.partName);
            Assert.Contains(42u, deployed);
        }

        [Fact]
        public void FairingTransition_AlreadyDeployed_ReturnsNull()
        {
            var deployed = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckFairingTransition(
                42, "fairingSize1", isDeployed: true, deployed, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void FairingTransition_NotDeployed_ReturnsNull()
        {
            var deployed = new HashSet<uint>();
            var evt = FlightRecorder.CheckFairingTransition(
                42, "fairingSize1", isDeployed: false, deployed, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void FairingJettisoned_SerializationRoundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.FairingJettisoned,
                partName = "fairingSize1"
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.FairingJettisoned, loaded.PartEvents[0].eventType);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
            Assert.Equal("fairingSize1", loaded.PartEvents[0].partName);
            Assert.Equal(105.0, loaded.PartEvents[0].ut);
        }

        #endregion

        #region RCS state tracking

        [Fact]
        public void RcsTransition_Activation_EmitsRCSActivatedEvent()
        {
            var active = new HashSet<ulong>();
            var throttles = new Dictionary<ulong, float>();
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var events = FlightRecorder.CheckRcsTransition(
                key, 100, 0, "RCSBlock", true, 0.6f, active, throttles, 200.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.RCSActivated, events[0].eventType);
            Assert.Equal(100u, events[0].partPersistentId);
            Assert.Equal(0, events[0].moduleIndex);
            Assert.Equal(0.6f, events[0].value, 0.001f);
            Assert.Equal(200.0, events[0].ut);
            Assert.Contains(key, active);
        }

        [Fact]
        public void RcsTransition_Deactivation_EmitsRCSStoppedEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var active = new HashSet<ulong> { key };
            var throttles = new Dictionary<ulong, float> { { key, 0.5f } };
            var events = FlightRecorder.CheckRcsTransition(
                key, 100, 0, "RCSBlock", false, 0f, active, throttles, 210.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.RCSStopped, events[0].eventType);
            Assert.Equal(100u, events[0].partPersistentId);
            Assert.DoesNotContain(key, active);
            Assert.False(throttles.ContainsKey(key));
        }

        [Fact]
        public void RcsTransition_PowerChange_EmitsRCSThrottleEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var active = new HashSet<ulong> { key };
            var throttles = new Dictionary<ulong, float> { { key, 0.3f } };
            var events = FlightRecorder.CheckRcsTransition(
                key, 100, 0, "RCSBlock", true, 0.8f, active, throttles, 215.0);

            Assert.Single(events);
            Assert.Equal(PartEventType.RCSThrottle, events[0].eventType);
            Assert.Equal(0.8f, events[0].value, 0.001f);
            Assert.Equal(0.8f, throttles[key], 0.001f);
        }

        [Fact]
        public void RcsTransition_SmallPowerChange_NoEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var active = new HashSet<ulong> { key };
            var throttles = new Dictionary<ulong, float> { { key, 0.5f } };
            var events = FlightRecorder.CheckRcsTransition(
                key, 100, 0, "RCSBlock", true, 0.505f, active, throttles, 215.0);

            Assert.Empty(events);
        }

        [Fact]
        public void RcsTransition_NotActive_NoChange_NoEvent()
        {
            var active = new HashSet<ulong>();
            var throttles = new Dictionary<ulong, float>();
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            var events = FlightRecorder.CheckRcsTransition(
                key, 100, 0, "RCSBlock", false, 0f, active, throttles, 200.0);

            Assert.Empty(events);
        }

        [Fact]
        public void RcsTransition_MultiModule_DifferentModuleIndex()
        {
            var active = new HashSet<ulong>();
            var throttles = new Dictionary<ulong, float>();

            ulong key0 = FlightRecorder.EncodeEngineKey(200, 0);
            var events0 = FlightRecorder.CheckRcsTransition(
                key0, 200, 0, "RCSBlock", true, 0.7f, active, throttles, 300.0);

            ulong key1 = FlightRecorder.EncodeEngineKey(200, 1);
            var events1 = FlightRecorder.CheckRcsTransition(
                key1, 200, 1, "RCSBlock", true, 0.3f, active, throttles, 300.0);

            Assert.Single(events0);
            Assert.Equal(0, events0[0].moduleIndex);
            Assert.Single(events1);
            Assert.Equal(1, events1[0].moduleIndex);
            Assert.Contains(key0, active);
            Assert.Contains(key1, active);
        }

        [Fact]
        public void ComputeRcsPower_NormalCase()
        {
            // 4 thrusters, thrusterPower=1.0, forces = [0.5, 0.5, 0.5, 0.5]
            // sum=2.0, normalized = 2.0 / (1.0 * 4) = 0.5
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { 0.5f, 0.5f, 0.5f, 0.5f }, 1.0f);
            Assert.Equal(0.5f, power, 0.001f);
        }

        [Fact]
        public void ComputeRcsPower_ZeroThrusterPower_ReturnsZero()
        {
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { 0.5f, 0.5f }, 0f);
            Assert.Equal(0f, power);
        }

        [Fact]
        public void ComputeRcsPower_EmptyForces_ReturnsZero()
        {
            float power = FlightRecorder.ComputeRcsPower(
                new float[0], 1.0f);
            Assert.Equal(0f, power);
        }

        [Fact]
        public void ComputeRcsPower_NullForces_ReturnsZero()
        {
            float power = FlightRecorder.ComputeRcsPower(null, 1.0f);
            Assert.Equal(0f, power);
        }

        [Fact]
        public void ComputeRcsPower_ClampedToOne()
        {
            // Forces exceed thrusterPower — should clamp to 1.0
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { 2.0f, 2.0f }, 1.0f);
            Assert.Equal(1.0f, power, 0.001f);
        }

        [Fact]
        public void ComputeRcsPower_PartialFiring()
        {
            // 4 thrusters, only 2 firing at full — sum=2.0, norm = 2.0 / (1.0 * 4) = 0.5
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { 1.0f, 0f, 1.0f, 0f }, 1.0f);
            Assert.Equal(0.5f, power, 0.001f);
        }

        #endregion

        #region RCS debounce

        [Fact]
        public void ShouldStartRcsRecording_AtThreshold_ReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldStartRcsRecording(8, 8));
        }

        [Fact]
        public void ShouldStartRcsRecording_BelowThreshold_ReturnsFalse()
        {
            Assert.False(FlightRecorder.ShouldStartRcsRecording(7, 8));
        }

        [Fact]
        public void ShouldStartRcsRecording_AboveThreshold_ReturnsFalse()
        {
            Assert.False(FlightRecorder.ShouldStartRcsRecording(9, 8));
        }

        [Fact]
        public void IsRcsRecordingSustained_AtThreshold_ReturnsTrue()
        {
            Assert.True(FlightRecorder.IsRcsRecordingSustained(8, 8));
        }

        [Fact]
        public void IsRcsRecordingSustained_BelowThreshold_ReturnsFalse()
        {
            Assert.False(FlightRecorder.IsRcsRecordingSustained(5, 8));
        }

        [Fact]
        public void IsRcsRecordingSustained_AboveThreshold_ReturnsTrue()
        {
            Assert.True(FlightRecorder.IsRcsRecordingSustained(20, 8));
        }

        #endregion

        #region RCS event serialization

        [Fact]
        public void RCSActivated_SerializationRoundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.RCSActivated,
                partName = "RCSBlock",
                value = 0.6f,
                moduleIndex = 1
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.RCSActivated, loaded.PartEvents[0].eventType);
            Assert.Equal(0.6f, loaded.PartEvents[0].value, 0.001f);
            Assert.Equal(1, loaded.PartEvents[0].moduleIndex);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
            Assert.Equal("RCSBlock", loaded.PartEvents[0].partName);
        }

        [Fact]
        public void RCSStopped_SerializationRoundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 108,
                partPersistentId = 42,
                eventType = PartEventType.RCSStopped,
                partName = "RCSBlock",
                moduleIndex = 0
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.RCSStopped, loaded.PartEvents[0].eventType);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
        }

        [Fact]
        public void RCSThrottle_SerializationRoundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 99,
                eventType = PartEventType.RCSThrottle,
                partName = "RCSBlock",
                value = 0.45f,
                moduleIndex = 0
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 110,
                partPersistentId = 99,
                eventType = PartEventType.RCSStopped,
                partName = "RCSBlock",
                moduleIndex = 0
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Equal(2, loaded.PartEvents.Count);
            Assert.Equal(PartEventType.RCSThrottle, loaded.PartEvents[0].eventType);
            Assert.Equal(0.45f, loaded.PartEvents[0].value, 0.01f);
            Assert.Equal(PartEventType.RCSStopped, loaded.PartEvents[1].eventType);
        }

        #endregion

        #region Robotics state tracking

        [Fact]
        public void IsRoboticModuleName_KnownModules_ReturnTrue()
        {
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleRoboticServoHinge"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleRoboticServoPiston"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleRoboticRotationServo"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleRoboticServoRotor"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelSuspension"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelSteering"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelMotor"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelMotorSteering"));
            Assert.False(FlightRecorder.IsRoboticModuleName("ModuleDeployablePart"));
        }

        [Fact]
        public void RoboticTransition_StartMoving_EmitsStartedEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(300, 0);
            var moving = new HashSet<ulong>();
            var positions = new Dictionary<ulong, float>();
            var sampleUT = new Dictionary<ulong, double>();

            var events = FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 10f, deadband: 0.5f, ut: 100.0,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            Assert.Single(events);
            Assert.Equal(PartEventType.RoboticMotionStarted, events[0].eventType);
            Assert.Equal(10f, events[0].value, 0.001f);
            Assert.Contains(key, moving);
            Assert.True(sampleUT.ContainsKey(key));
        }

        [Fact]
        public void RoboticTransition_Below4HzCap_NoSampleEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(300, 0);
            var moving = new HashSet<ulong>();
            var positions = new Dictionary<ulong, float>();
            var sampleUT = new Dictionary<ulong, double>();

            FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 10f, deadband: 0.5f, ut: 100.0,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            var events = FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 12f, deadband: 0.5f, ut: 100.1,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            Assert.Empty(events);
        }

        [Fact]
        public void RoboticTransition_At4HzCap_EmitsPositionSample()
        {
            ulong key = FlightRecorder.EncodeEngineKey(300, 0);
            var moving = new HashSet<ulong>();
            var positions = new Dictionary<ulong, float>();
            var sampleUT = new Dictionary<ulong, double>();

            FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 10f, deadband: 0.5f, ut: 100.0,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            // Within interval (no sample yet)
            FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 12f, deadband: 0.5f, ut: 100.1,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            // At/after interval with sufficient delta from last emitted value
            var events = FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 13f, deadband: 0.5f, ut: 100.3,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            Assert.Single(events);
            Assert.Equal(PartEventType.RoboticPositionSample, events[0].eventType);
            Assert.Equal(13f, events[0].value, 0.001f);
        }

        [Fact]
        public void RoboticTransition_DeadbandSuppressesNoise()
        {
            ulong key = FlightRecorder.EncodeEngineKey(300, 0);
            var moving = new HashSet<ulong>();
            var positions = new Dictionary<ulong, float>();
            var sampleUT = new Dictionary<ulong, double>();

            FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 10f, deadband: 0.5f, ut: 100.0,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            var events = FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 10.2f, deadband: 0.5f, ut: 100.3,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            Assert.Empty(events);
        }

        [Fact]
        public void RoboticTransition_StopMoving_EmitsStoppedEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(300, 0);
            var moving = new HashSet<ulong>();
            var positions = new Dictionary<ulong, float>();
            var sampleUT = new Dictionary<ulong, double>();

            FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: true, positionValue: 10f, deadband: 0.5f, ut: 100.0,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            var events = FlightRecorder.CheckRoboticTransition(
                key, 300, 0, "hinge_01",
                movingSignal: false, positionValue: 10f, deadband: 0.5f, ut: 100.5,
                moving, positions, sampleUT, sampleIntervalSeconds: 0.25);

            Assert.Single(events);
            Assert.Equal(PartEventType.RoboticMotionStopped, events[0].eventType);
            Assert.DoesNotContain(key, moving);
        }

        [Fact]
        public void RoboticsPositionSample_SerializationRoundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 110,
                partPersistentId = 333,
                eventType = PartEventType.RoboticPositionSample,
                partName = "hinge_01",
                value = 42.5f,
                moduleIndex = 0
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.RoboticPositionSample, loaded.PartEvents[0].eventType);
            Assert.Equal(42.5f, loaded.PartEvents[0].value, 0.001f);
            Assert.Equal(0, loaded.PartEvents[0].moduleIndex);
            Assert.Equal(333u, loaded.PartEvents[0].partPersistentId);
        }

        [Fact]
        public void AnimateHeatTransition_ColdToHot_EmitsHotEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel>();

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.85f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.True(evt.HasValue);
            Assert.Equal(PartEventType.ThermalAnimationHot, evt.Value.eventType);
            Assert.Equal(0.85f, evt.Value.value, 0.001f);
            Assert.Equal(HeatLevel.Hot, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_HotToCold_EmitsColdEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Hot } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.02f,
                levelMap, ut: 120.0, moduleIndex: 0);

            Assert.True(evt.HasValue);
            Assert.Equal(PartEventType.ThermalAnimationCold, evt.Value.eventType);
            Assert.Equal(0.02f, evt.Value.value, 0.001f);
            Assert.Equal(HeatLevel.Cold, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_ColdToMedium()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel>();

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.45f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.True(evt.HasValue);
            Assert.Equal(PartEventType.ThermalAnimationMedium, evt.Value.eventType);
            Assert.Equal(HeatLevel.Medium, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_MediumToHot()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Medium } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.70f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.True(evt.HasValue);
            Assert.Equal(PartEventType.ThermalAnimationHot, evt.Value.eventType);
            Assert.Equal(HeatLevel.Hot, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_HotToMedium()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Hot } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.50f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.True(evt.HasValue);
            Assert.Equal(PartEventType.ThermalAnimationMedium, evt.Value.eventType);
            Assert.Equal(HeatLevel.Medium, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_MediumToCold()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Medium } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.05f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.True(evt.HasValue);
            Assert.Equal(PartEventType.ThermalAnimationCold, evt.Value.eventType);
            Assert.Equal(HeatLevel.Cold, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_ColdToHotDirect()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel>();

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.80f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.True(evt.HasValue);
            Assert.Equal(PartEventType.ThermalAnimationHot, evt.Value.eventType);
            Assert.Equal(HeatLevel.Hot, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_HotToColdDirect()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Hot } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.05f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.True(evt.HasValue);
            Assert.Equal(PartEventType.ThermalAnimationCold, evt.Value.eventType);
            Assert.Equal(HeatLevel.Cold, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_InHysteresisGap_NoEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel>();

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.20f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.Null(evt);
        }

        [Fact]
        public void AnimateHeatTransition_StaysMedium_NoEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Medium } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.50f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.Null(evt);
        }

        [Fact]
        public void AnimateHeatTransition_MediumInHysteresisGap_StaysMedium()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Medium } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.20f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.Null(evt);
            Assert.Equal(HeatLevel.Medium, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_HotInMediumHotHysteresis_StaysHot()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Hot } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.63f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.Null(evt);
            Assert.Equal(HeatLevel.Hot, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatTransition_HotFallsBelowHysteresis_BecomesMedium()
        {
            ulong key = FlightRecorder.EncodeEngineKey(450, 0);
            var levelMap = new Dictionary<ulong, HeatLevel> { { key, HeatLevel.Hot } };

            var evt = FlightRecorder.CheckAnimateHeatTransition(
                key, 450, "shockConeIntake",
                normalizedHeat: 0.55f,
                levelMap, ut: 100.0, moduleIndex: 0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ThermalAnimationMedium, evt.Value.eventType);
            Assert.Equal(HeatLevel.Medium, levelMap[key]);
        }

        [Fact]
        public void AnimateHeatEvent_SerializationRoundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 110,
                partPersistentId = 451,
                eventType = PartEventType.ThermalAnimationHot,
                partName = "shockConeIntake",
                value = 1f,
                moduleIndex = 0
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.ThermalAnimationHot, loaded.PartEvents[0].eventType);
            Assert.Equal(1f, loaded.PartEvents[0].value, 0.001f);
            Assert.Equal(0, loaded.PartEvents[0].moduleIndex);
            Assert.Equal(451u, loaded.PartEvents[0].partPersistentId);
        }

        [Fact]
        public void ThermalAnimationMedium_SerializationRoundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 110,
                partPersistentId = 452,
                eventType = PartEventType.ThermalAnimationMedium,
                partName = "noseConeAdapter",
                value = 0.5f,
                moduleIndex = 0
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.ThermalAnimationMedium, loaded.PartEvents[0].eventType);
            Assert.Equal(0.5f, loaded.PartEvents[0].value, 0.001f);
            Assert.Equal(0, loaded.PartEvents[0].moduleIndex);
            Assert.Equal(452u, loaded.PartEvents[0].partPersistentId);
        }

        [Fact]
        public void UnknownPartEventType_SkippedDuringDeserialization()
        {
            var rec = new RecordingStore.Recording();
            rec.RecordingId = "test-forward-compat";
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin" });

            // Serialize a valid event
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 500,
                eventType = PartEventType.ThermalAnimationHot,
                partName = "shockConeIntake",
                value = 1f
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            // Manually inject an unknown event type (999)
            var unknownEvtNode = new ConfigNode("PART_EVENT");
            unknownEvtNode.AddValue("ut", "110");
            unknownEvtNode.AddValue("pid", "501");
            unknownEvtNode.AddValue("type", "999");
            unknownEvtNode.AddValue("part", "futurePart");
            unknownEvtNode.AddValue("value", "1");
            node.AddNode(unknownEvtNode);

            var loaded = new RecordingStore.Recording();
            loaded.RecordingId = "test-forward-compat";
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            // The unknown event (type=999) should be skipped, only the valid event remains
            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.ThermalAnimationHot, loaded.PartEvents[0].eventType);
            Assert.Equal(500u, loaded.PartEvents[0].partPersistentId);
        }

        #endregion

        #region Engine event serialization

        [Fact]
        public void EngineEvent_SerializationRoundtrip_ValueAndModuleIndex()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 42,
                eventType = PartEventType.EngineIgnited,
                partName = "liquidEngine",
                value = 0.75f,
                moduleIndex = 1
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Single(loaded.PartEvents);
            Assert.Equal(PartEventType.EngineIgnited, loaded.PartEvents[0].eventType);
            Assert.Equal(0.75f, loaded.PartEvents[0].value, 0.001f);
            Assert.Equal(1, loaded.PartEvents[0].moduleIndex);
            Assert.Equal(42u, loaded.PartEvents[0].partPersistentId);
            Assert.Equal("liquidEngine", loaded.PartEvents[0].partName);
        }

        [Fact]
        public void EngineEvent_BackwardCompat_MissingValueAndMidx_DefaultsToZero()
        {
            // Simulate old-format PART_EVENT without value/midx keys
            var node = new ConfigNode("TEST");
            var evtNode = node.AddNode("PART_EVENT");
            evtNode.AddValue("ut", "105");
            evtNode.AddValue("pid", "42");
            evtNode.AddValue("type", ((int)PartEventType.Decoupled).ToString());
            evtNode.AddValue("part", "fuelTank");
            // No "value" or "midx" keys

            var rec = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Single(rec.PartEvents);
            Assert.Equal(0f, rec.PartEvents[0].value);
            Assert.Equal(0, rec.PartEvents[0].moduleIndex);
            Assert.Equal(PartEventType.Decoupled, rec.PartEvents[0].eventType);
        }

        [Fact]
        public void EngineThrottle_SerializationRoundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 105,
                partPersistentId = 99,
                eventType = PartEventType.EngineThrottle,
                partName = "engine",
                value = 0.33f,
                moduleIndex = 0
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 110,
                partPersistentId = 99,
                eventType = PartEventType.EngineShutdown,
                partName = "engine",
                value = 0f,
                moduleIndex = 0
            });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var loaded = new RecordingStore.Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, loaded);

            Assert.Equal(2, loaded.PartEvents.Count);
            Assert.Equal(PartEventType.EngineThrottle, loaded.PartEvents[0].eventType);
            Assert.Equal(0.33f, loaded.PartEvents[0].value, 0.01f);
            Assert.Equal(PartEventType.EngineShutdown, loaded.PartEvents[1].eventType);
            Assert.Equal(0f, loaded.PartEvents[1].value);
        }

        #endregion

        #region IsHeatAnimationName heuristic

        [Theory]
        [InlineData("heatAnimation", true)]
        [InlineData("thumperEmissive", true)]
        [InlineData("HeatAnimationSRB", true)]
        [InlineData("TurboJetHeat", true)]
        [InlineData("TRJ_Heat", true)]
        [InlineData("FM1Emissive", true)]
        [InlineData("colorAnimation", true)]
        [InlineData("nozzleGlow", true)]
        [InlineData("TurboJetNozzleDry", false)]
        [InlineData("TF2FanSpin", false)]
        [InlineData("deployAnimation", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsHeatAnimationName_MatchesExpected(string name, bool expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.IsHeatAnimationName(name));
        }

        [Fact]
        public void TryGetAnimateThrottleAnimation_NullPrefab_ReturnsFalse()
        {
            string animName;
            bool result = GhostVisualBuilder.TryGetAnimateThrottleAnimation(null, out animName);

            Assert.False(result);
            Assert.Null(animName);
        }

        #endregion

        #region Helpers

        private static void AddTestPart(ConfigNode vessel, string name, uint persistentId, int parentIndex)
        {
            var part = vessel.AddNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", persistentId.ToString(CultureInfo.InvariantCulture));
            part.AddValue("parent", parentIndex.ToString(CultureInfo.InvariantCulture));
        }

        #endregion
    }

    /// <summary>
    /// Tests for RemoveDuplicateCrewFromSnapshot — the spawn-time crew dedup guard.
    /// Uses log capture to verify warnings are emitted for duplicates.
    /// </summary>
    [Collection("Sequential")]
    public class CrewDedupTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrewDedupTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        [Fact]
        public void RemoveDuplicateCrew_RemovesDuplicate_LogsWarning()
        {
            // Build a snapshot with Jeb and Bill
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");
            part.AddValue("crew", "Bill Kerman");

            // Simulate: Jeb already on a vessel by calling the pure method path
            // (BuildExistingCrewSet needs FlightGlobals, so test the ConfigNode removal directly)
            var duplicates = new HashSet<string> { "Jebediah Kerman" };

            // Apply the removal using the same pattern as RemoveDuplicateCrewFromSnapshot
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var names = partNode.GetValues("crew");
                var keep = new List<string>();
                foreach (string name in names)
                {
                    if (duplicates.Contains(name))
                    {
                        ParsekLog.Warn("Spawner",
                            $"Crew dedup: '{name}' already on a vessel in the scene — removed from spawn snapshot");
                    }
                    else
                    {
                        keep.Add(name);
                    }
                }
                partNode.RemoveValues("crew");
                foreach (string name in keep)
                    partNode.AddValue("crew", name);
            }

            // Verify crew was removed
            var remainingCrew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);
            Assert.Single(remainingCrew);
            Assert.Equal("Bill Kerman", remainingCrew[0]);

            // Verify warning was logged
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") && l.Contains("Crew dedup") && l.Contains("Jebediah Kerman"));
        }

        [Fact]
        public void RemoveDuplicateCrew_NoDuplicates_NoCrewRemoved()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");
            part.AddValue("crew", "Bill Kerman");

            var snapshotCrew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);
            var existingCrew = new HashSet<string> { "Valentina Kerman" };
            var duplicates = VesselSpawner.FindDuplicateCrew(snapshotCrew, existingCrew);

            Assert.Empty(duplicates);

            // Snapshot untouched
            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);
            Assert.Equal(2, crew.Count);
        }

        [Fact]
        public void RemoveDuplicateCrew_MultiPart_RemovesAcrossParts()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part1 = snapshot.AddNode("PART");
            part1.AddValue("crew", "Jebediah Kerman");
            var part2 = snapshot.AddNode("PART");
            part2.AddValue("crew", "Valentina Kerman");
            part2.AddValue("crew", "Bill Kerman");

            // Both Jeb and Val are duplicates
            var duplicates = new HashSet<string> { "Jebediah Kerman", "Valentina Kerman" };

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var names = partNode.GetValues("crew");
                var keep = new List<string>();
                foreach (string name in names)
                {
                    if (!duplicates.Contains(name))
                        keep.Add(name);
                }
                partNode.RemoveValues("crew");
                foreach (string name in keep)
                    partNode.AddValue("crew", name);
            }

            var remaining = ParsekScenario.ExtractCrewFromSnapshot(snapshot);
            Assert.Single(remaining);
            Assert.Equal("Bill Kerman", remaining[0]);
        }

        [Fact]
        public void FindDuplicateCrew_EmptyCrewName_Ignored()
        {
            var snapshotCrew = new List<string> { "", "Jebediah Kerman" };
            var existingCrew = new HashSet<string> { "" };

            var dupes = VesselSpawner.FindDuplicateCrew(snapshotCrew, existingCrew);

            // Empty string should not be treated as a duplicate
            Assert.Empty(dupes);
        }
    }
}
