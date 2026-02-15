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

        #region Subtree collection

        [Fact]
        public void SubtreeCollect_DecoupleAtNode101_CollectsAllDescendants()
        {
            // Same tree as above:
            // 100 → 101 → {102, 103}
            var tree = new Dictionary<uint, List<uint>>
            {
                { 100, new List<uint> { 101 } },
                { 101, new List<uint> { 102, 103 } }
            };

            // Collect subtree from 101 (what HidePartSubtree would traverse)
            var collected = CollectSubtree(101, tree);

            Assert.Contains(101u, collected);
            Assert.Contains(102u, collected);
            Assert.Contains(103u, collected);
            Assert.DoesNotContain(100u, collected);
        }

        [Fact]
        public void SubtreeCollect_LeafNode_CollectsOnlyItself()
        {
            var tree = new Dictionary<uint, List<uint>>
            {
                { 100, new List<uint> { 101 } },
                { 101, new List<uint> { 102, 103 } }
            };

            var collected = CollectSubtree(103, tree);

            Assert.Single(collected);
            Assert.Contains(103u, collected);
        }

        #endregion

        #region Parachute state tracking

        [Fact]
        public void ParachuteTransition_StowedToDeployed_EmitsDeployedEvent()
        {
            var deployed = new HashSet<uint>();
            var evt = FlightRecorder.CheckParachuteTransition(
                42, "parachute", isDeployed: true, deployed, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteDeployed, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
            Assert.Contains(42u, deployed);
        }

        [Fact]
        public void ParachuteTransition_DeployedToCut_EmitsCutEvent()
        {
            var deployed = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckParachuteTransition(
                42, "parachute", isDeployed: false, deployed, 120.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteCut, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.DoesNotContain(42u, deployed);
        }

        [Fact]
        public void ParachuteTransition_NoChange_ReturnsNull()
        {
            var deployed = new HashSet<uint>();
            var evt = FlightRecorder.CheckParachuteTransition(
                42, "parachute", isDeployed: false, deployed, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void ParachuteTransition_AlreadyDeployed_ReturnsNull()
        {
            var deployed = new HashSet<uint> { 42 };
            var evt = FlightRecorder.CheckParachuteTransition(
                42, "parachute", isDeployed: true, deployed, 100.0);

            Assert.Null(evt);
        }

        [Fact]
        public void ParachuteDestroyed_DeployedChuteDeath_EmitsParachuteDestroyed()
        {
            var deployed = new HashSet<uint> { 42 };
            var evtType = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: true, deployed);

            Assert.Equal(PartEventType.ParachuteDestroyed, evtType);
            Assert.DoesNotContain(42u, deployed);
        }

        [Fact]
        public void ParachuteDestroyed_StowedChuteDeath_EmitsDestroyed()
        {
            var deployed = new HashSet<uint>();
            var evtType = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: true, deployed);

            Assert.Equal(PartEventType.Destroyed, evtType);
        }

        [Fact]
        public void ParachuteDestroyed_NonChutePart_EmitsDestroyed()
        {
            var deployed = new HashSet<uint> { 42 };
            var evtType = FlightRecorder.ClassifyPartDeath(99, hasParachuteModule: false, deployed);

            Assert.Equal(PartEventType.Destroyed, evtType);
            Assert.Contains(42u, deployed); // other entries untouched
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

        #region Helpers

        private static void AddTestPart(ConfigNode vessel, string name, uint persistentId, int parentIndex)
        {
            var part = vessel.AddNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", persistentId.ToString(CultureInfo.InvariantCulture));
            part.AddValue("parent", parentIndex.ToString(CultureInfo.InvariantCulture));
        }

        private static HashSet<uint> CollectSubtree(uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            var result = new HashSet<uint>();
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                result.Add(pid);
                List<uint> children;
                if (tree.TryGetValue(pid, out children))
                    for (int c = 0; c < children.Count; c++)
                        stack.Push(children[c]);
            }
            return result;
        }

        #endregion
    }
}
