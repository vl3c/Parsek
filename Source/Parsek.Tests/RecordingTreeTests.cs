using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingTreeTests
    {
        public RecordingTreeTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private static RecordingTree.LegacyResourceResidual ConsumeLegacyResidualOrThrow(RecordingTree tree)
        {
            var residual = tree.ConsumeLegacyResidual();
            Assert.NotNull(residual);
            return residual;
        }

        // --- SurfacePosition round-trip ---

        [Fact]
        public void SurfacePosition_SaveLoad_RoundTrips()
        {
            var pos = new SurfacePosition
            {
                body = "Mun",
                latitude = -0.5,
                longitude = 23.4,
                altitude = 1234.5,
                rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                situation = SurfaceSituation.Landed
            };

            var node = new ConfigNode("TEST");
            SurfacePosition.SaveInto(node, pos);

            var restored = SurfacePosition.LoadFrom(node);

            Assert.Equal("Mun", restored.body);
            Assert.Equal(-0.5, restored.latitude);
            Assert.Equal(23.4, restored.longitude);
            Assert.Equal(1234.5, restored.altitude);
            Assert.Equal(0.1f, restored.rotation.x, 0.00001f);
            Assert.Equal(0.2f, restored.rotation.y, 0.00001f);
            Assert.Equal(0.3f, restored.rotation.z, 0.00001f);
            Assert.Equal(0.9f, restored.rotation.w, 0.00001f);
            Assert.True(restored.HasRecordedRotation);
            Assert.Equal(SurfaceSituation.Landed, restored.situation);
        }

        [Fact]
        public void SurfacePosition_Splashed_PreservesSituation()
        {
            var pos = new SurfacePosition
            {
                body = "Kerbin",
                latitude = 10.0,
                longitude = -50.0,
                altitude = 0.5,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                situation = SurfaceSituation.Splashed
            };

            var node = new ConfigNode("TEST");
            SurfacePosition.SaveInto(node, pos);

            var restored = SurfacePosition.LoadFrom(node);

            Assert.Equal(SurfaceSituation.Splashed, restored.situation);
        }

        [Fact]
        public void SurfacePosition_LoadFromLegacyNodeWithoutRotation_MarksRotationUnrecorded()
        {
            var node = new ConfigNode("TEST");
            node.AddValue("body", "Kerbin");
            node.AddValue("lat", "10");
            node.AddValue("lon", "20");
            node.AddValue("alt", "30");
            node.AddValue("situation", ((int)SurfaceSituation.Landed).ToString(CultureInfo.InvariantCulture));

            var restored = SurfacePosition.LoadFrom(node);

            Assert.False(restored.HasRecordedRotation);
            Assert.Equal(0f, restored.rotation.x);
            Assert.Equal(0f, restored.rotation.y);
            Assert.Equal(0f, restored.rotation.z);
            Assert.Equal(1f, restored.rotation.w);
        }

        // --- BranchPoint serialization ---

        [Fact]
        public void BranchPoint_Undock_SerializesParentsAndChildren()
        {
            var bp = new BranchPoint
            {
                Id = "bp001",
                UT = 17050.5,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_parent" },
                ChildRecordingIds = new List<string> { "rec_child1", "rec_child2" }
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp001", restored.Id);
            Assert.Equal(17050.5, restored.UT);
            Assert.Equal(BranchPointType.Undock, restored.Type);
            Assert.Single(restored.ParentRecordingIds);
            Assert.Equal("rec_parent", restored.ParentRecordingIds[0]);
            Assert.Equal(2, restored.ChildRecordingIds.Count);
            Assert.Equal("rec_child1", restored.ChildRecordingIds[0]);
            Assert.Equal("rec_child2", restored.ChildRecordingIds[1]);
        }

        [Fact]
        public void BranchPoint_Dock_SerializesTwoParentsOneChild()
        {
            var bp = new BranchPoint
            {
                Id = "bp_dock",
                UT = 18000.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { "rec_a", "rec_b" },
                ChildRecordingIds = new List<string> { "rec_merged" }
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp_dock", restored.Id);
            Assert.Equal(BranchPointType.Dock, restored.Type);
            Assert.Equal(2, restored.ParentRecordingIds.Count);
            Assert.Equal("rec_a", restored.ParentRecordingIds[0]);
            Assert.Equal("rec_b", restored.ParentRecordingIds[1]);
            Assert.Single(restored.ChildRecordingIds);
            Assert.Equal("rec_merged", restored.ChildRecordingIds[0]);
        }

        // --- TerminalState enum ---

        [Fact]
        public void TerminalState_AllValues_RoundTripAsInts()
        {
            var ic = CultureInfo.InvariantCulture;
            foreach (TerminalState ts in Enum.GetValues(typeof(TerminalState)))
            {
                string serialized = ((int)ts).ToString(ic);
                int parsed;
                Assert.True(int.TryParse(serialized, NumberStyles.Integer, ic, out parsed));
                Assert.True(Enum.IsDefined(typeof(TerminalState), parsed));
                Assert.Equal(ts, (TerminalState)parsed);
            }

            // Verify we have all 8 values (0-7)
            Assert.Equal(8, Enum.GetValues(typeof(TerminalState)).Length);
        }

        // --- Recording tree with single node ---

        [Fact]
        public void RecordingTree_SingleNode_RoundTrips()
        {
            var tree = new RecordingTree
            {
                Id = "tree001",
                TreeName = "Kerbal X",
                RootRecordingId = "rec001",
                ActiveRecordingId = "rec001"
            };

            var rec = new Recording
            {
                RecordingId = "rec001",
                VesselName = "Kerbal X",
                VesselPersistentId = 12345,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
                TreeId = "tree001"
            };
            tree.Recordings["rec001"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            var restored = RecordingTree.Load(node);

            Assert.Equal("tree001", restored.Id);
            Assert.Equal("Kerbal X", restored.TreeName);
            Assert.Equal("rec001", restored.RootRecordingId);
            Assert.Equal("rec001", restored.ActiveRecordingId);
            Assert.Equal(RecordingTree.CurrentTreeFormatVersion, restored.TreeFormatVersion);
            Assert.Null(restored.ConsumeLegacyResidual());

            Assert.Single(restored.Recordings);
            Assert.True(restored.Recordings.ContainsKey("rec001"));
            var restoredRec = restored.Recordings["rec001"];
            Assert.Equal("rec001", restoredRec.RecordingId);
            Assert.Equal("Kerbal X", restoredRec.VesselName);
            Assert.Equal((uint)12345, restoredRec.VesselPersistentId);
            Assert.Equal(100.0, restoredRec.ExplicitStartUT);
            Assert.Equal(200.0, restoredRec.ExplicitEndUT);
            Assert.Equal("tree001", restoredRec.TreeId);

            Assert.Empty(restored.BranchPoints);
        }

        // --- Recording tree with undock branch ---

        [Fact]
        public void RecordingTree_UndockBranch_RoundTrips()
        {
            var tree = new RecordingTree
            {
                Id = "tree_undock",
                TreeName = "Undock Test",
                RootRecordingId = "R1",
                ActiveRecordingId = "R2"
            };

            var r1 = new Recording
            {
                RecordingId = "R1",
                VesselName = "Parent Vessel",
                TreeId = "tree_undock",
                ChildBranchPointId = "BP1",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            var r2 = new Recording
            {
                RecordingId = "R2",
                VesselName = "Child 1",
                TreeId = "tree_undock",
                ParentBranchPointId = "BP1",
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 300.0
            };
            var r3 = new Recording
            {
                RecordingId = "R3",
                VesselName = "Child 2",
                TreeId = "tree_undock",
                ParentBranchPointId = "BP1",
                VesselPersistentId = 999,
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 300.0
            };

            tree.Recordings["R1"] = r1;
            tree.Recordings["R2"] = r2;
            tree.Recordings["R3"] = r3;

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "BP1",
                UT = 200.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "R1" },
                ChildRecordingIds = new List<string> { "R2", "R3" }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            var restored = RecordingTree.Load(node);

            Assert.Equal(3, restored.Recordings.Count);
            Assert.Equal("BP1", restored.Recordings["R1"].ChildBranchPointId);
            Assert.Equal("BP1", restored.Recordings["R2"].ParentBranchPointId);
            Assert.Equal("BP1", restored.Recordings["R3"].ParentBranchPointId);

            Assert.Single(restored.BranchPoints);
            var bp = restored.BranchPoints[0];
            Assert.Equal("BP1", bp.Id);
            Assert.Equal(200.0, bp.UT);
            Assert.Equal(BranchPointType.Undock, bp.Type);
            Assert.Single(bp.ParentRecordingIds);
            Assert.Equal("R1", bp.ParentRecordingIds[0]);
            Assert.Equal(2, bp.ChildRecordingIds.Count);
            Assert.Contains("R2", bp.ChildRecordingIds);
            Assert.Contains("R3", bp.ChildRecordingIds);
        }

        // --- Recording tree with dock merge ---

        [Fact]
        public void RecordingTree_DockMerge_RoundTrips()
        {
            var tree = new RecordingTree
            {
                Id = "tree_dock",
                TreeName = "Dock Test",
                RootRecordingId = "R1",
                ActiveRecordingId = "R4"
            };

            var r1 = new Recording
            {
                RecordingId = "R1",
                VesselName = "Root",
                TreeId = "tree_dock",
                ChildBranchPointId = "BP1",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            var r2 = new Recording
            {
                RecordingId = "R2",
                VesselName = "Branch A",
                TreeId = "tree_dock",
                ParentBranchPointId = "BP1",
                ChildBranchPointId = "BP2",
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 400.0
            };
            var r3 = new Recording
            {
                RecordingId = "R3",
                VesselName = "Branch B",
                TreeId = "tree_dock",
                ParentBranchPointId = "BP1",
                ChildBranchPointId = "BP2",
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 400.0
            };
            var r4 = new Recording
            {
                RecordingId = "R4",
                VesselName = "Merged",
                TreeId = "tree_dock",
                ParentBranchPointId = "BP2",
                ExplicitStartUT = 400.0,
                ExplicitEndUT = 500.0
            };

            tree.Recordings["R1"] = r1;
            tree.Recordings["R2"] = r2;
            tree.Recordings["R3"] = r3;
            tree.Recordings["R4"] = r4;

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "BP1",
                UT = 200.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "R1" },
                ChildRecordingIds = new List<string> { "R2", "R3" }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "BP2",
                UT = 400.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { "R2", "R3" },
                ChildRecordingIds = new List<string> { "R4" }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            var restored = RecordingTree.Load(node);

            Assert.Equal(4, restored.Recordings.Count);

            // Topology checks
            Assert.Equal("BP1", restored.Recordings["R1"].ChildBranchPointId);
            Assert.Null(restored.Recordings["R1"].ParentBranchPointId);

            Assert.Equal("BP1", restored.Recordings["R2"].ParentBranchPointId);
            Assert.Equal("BP2", restored.Recordings["R2"].ChildBranchPointId);

            Assert.Equal("BP1", restored.Recordings["R3"].ParentBranchPointId);
            Assert.Equal("BP2", restored.Recordings["R3"].ChildBranchPointId);

            Assert.Equal("BP2", restored.Recordings["R4"].ParentBranchPointId);
            Assert.Null(restored.Recordings["R4"].ChildBranchPointId);

            // Branch point checks
            Assert.Equal(2, restored.BranchPoints.Count);

            var bp1 = restored.BranchPoints.Find(b => b.Id == "BP1");
            Assert.NotNull(bp1);
            Assert.Equal(BranchPointType.Undock, bp1.Type);
            Assert.Single(bp1.ParentRecordingIds);
            Assert.Equal("R1", bp1.ParentRecordingIds[0]);
            Assert.Equal(2, bp1.ChildRecordingIds.Count);

            var bp2 = restored.BranchPoints.Find(b => b.Id == "BP2");
            Assert.NotNull(bp2);
            Assert.Equal(BranchPointType.Dock, bp2.Type);
            Assert.Equal(2, bp2.ParentRecordingIds.Count);
            Assert.Single(bp2.ChildRecordingIds);
            Assert.Equal("R4", bp2.ChildRecordingIds[0]);
        }

        // --- Terminal state fields ---

        [Fact]
        public void Recording_OrbitalTerminalState_RoundTrips()
        {
            var tree = new RecordingTree
            {
                Id = "tree_orbit",
                TreeName = "Orbit Test",
                RootRecordingId = "rec_orb"
            };

            var rec = new Recording
            {
                RecordingId = "rec_orb",
                VesselName = "Orbiter",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitInclination = 28.5,
                TerminalOrbitEccentricity = 0.001,
                TerminalOrbitSemiMajorAxis = 700000.0,
                TerminalOrbitLAN = 45.0,
                TerminalOrbitArgumentOfPeriapsis = 90.0,
                TerminalOrbitMeanAnomalyAtEpoch = 1.234,
                TerminalOrbitEpoch = 17000.0,
                TerminalOrbitBody = "Kerbin",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_orb"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            var restored = RecordingTree.Load(node);
            var r = restored.Recordings["rec_orb"];

            Assert.Equal(TerminalState.Orbiting, r.TerminalStateValue);
            Assert.Equal(28.5, r.TerminalOrbitInclination);
            Assert.Equal(0.001, r.TerminalOrbitEccentricity);
            Assert.Equal(700000.0, r.TerminalOrbitSemiMajorAxis);
            Assert.Equal(45.0, r.TerminalOrbitLAN);
            Assert.Equal(90.0, r.TerminalOrbitArgumentOfPeriapsis);
            Assert.Equal(1.234, r.TerminalOrbitMeanAnomalyAtEpoch);
            Assert.Equal(17000.0, r.TerminalOrbitEpoch);
            Assert.Equal("Kerbin", r.TerminalOrbitBody);
        }

        [Fact]
        public void Recording_LandedTerminalState_RoundTrips()
        {
            var tree = new RecordingTree
            {
                Id = "tree_land",
                TreeName = "Land Test",
                RootRecordingId = "rec_land"
            };

            var termPos = new SurfacePosition
            {
                body = "Mun",
                latitude = -12.5,
                longitude = 34.5,
                altitude = 567.8,
                rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                situation = SurfaceSituation.Landed
            };

            var rec = new Recording
            {
                RecordingId = "rec_land",
                VesselName = "Lander",
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = termPos,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_land"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            var restored = RecordingTree.Load(node);
            var r = restored.Recordings["rec_land"];

            Assert.Equal(TerminalState.Landed, r.TerminalStateValue);
            Assert.True(r.TerminalPosition.HasValue);
            Assert.Equal("Mun", r.TerminalPosition.Value.body);
            Assert.Equal(-12.5, r.TerminalPosition.Value.latitude);
            Assert.Equal(34.5, r.TerminalPosition.Value.longitude);
            Assert.Equal(567.8, r.TerminalPosition.Value.altitude);
            Assert.Equal(0.1f, r.TerminalPosition.Value.rotation.x, 0.00001f);
            Assert.Equal(0.2f, r.TerminalPosition.Value.rotation.y, 0.00001f);
            Assert.Equal(0.3f, r.TerminalPosition.Value.rotation.z, 0.00001f);
            Assert.Equal(0.9f, r.TerminalPosition.Value.rotation.w, 0.00001f);
            Assert.Equal(SurfaceSituation.Landed, r.TerminalPosition.Value.situation);
        }

        [Fact]
        public void Recording_DestroyedTerminalState_NoOrbitNoPosition()
        {
            var tree = new RecordingTree
            {
                Id = "tree_dest",
                TreeName = "Destroyed Test",
                RootRecordingId = "rec_dest"
            };

            var rec = new Recording
            {
                RecordingId = "rec_dest",
                VesselName = "Doomed",
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_dest"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            var restored = RecordingTree.Load(node);
            var r = restored.Recordings["rec_dest"];

            Assert.Equal(TerminalState.Destroyed, r.TerminalStateValue);
            Assert.Null(r.TerminalPosition);
            Assert.Null(r.TerminalOrbitBody);
        }

        [Fact]
        public void Recording_NullTerminalState_OmitsKey()
        {
            var tree = new RecordingTree
            {
                Id = "tree_null_ts",
                TreeName = "Null TS Test",
                RootRecordingId = "rec_null"
            };

            var rec = new Recording
            {
                RecordingId = "rec_null",
                VesselName = "Active",
                TerminalStateValue = null,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_null"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            // Verify the key is absent from the RECORDING node
            ConfigNode recNode = node.GetNodes("RECORDING")[0];
            Assert.Null(recNode.GetValue("terminalState"));

            var restored = RecordingTree.Load(node);
            Assert.Null(restored.Recordings["rec_null"].TerminalStateValue);
        }

        // --- Background map rebuild ---

        [Fact]
        public void RecordingTree_RebuildBackgroundMap_CorrectEntries()
        {
            var tree = new RecordingTree
            {
                Id = "tree_bg",
                TreeName = "BG Test",
                RootRecordingId = "R1",
                ActiveRecordingId = "R3"
            };

            // R1: terminated (Docked), should NOT be in background map
            var r1 = new Recording
            {
                RecordingId = "R1",
                VesselPersistentId = 100,
                TerminalStateValue = TerminalState.Docked,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            // R2: still recording (null terminal), NOT active -> should be in map
            var r2 = new Recording
            {
                RecordingId = "R2",
                VesselPersistentId = 200,
                TerminalStateValue = null,
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 300.0
            };
            // R3: still recording (null terminal), IS active -> should NOT be in map
            var r3 = new Recording
            {
                RecordingId = "R3",
                VesselPersistentId = 300,
                TerminalStateValue = null,
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 300.0
            };

            tree.Recordings["R1"] = r1;
            tree.Recordings["R2"] = r2;
            tree.Recordings["R3"] = r3;

            tree.RebuildBackgroundMap();

            Assert.Single(tree.BackgroundMap);
            Assert.True(tree.BackgroundMap.ContainsKey(200));
            Assert.Equal("R2", tree.BackgroundMap[200]);
            Assert.False(tree.BackgroundMap.ContainsKey(100));
            Assert.False(tree.BackgroundMap.ContainsKey(300));
        }

        [Fact]
        public void RecordingTree_RebuildBackgroundMap_ZeroPidExcluded()
        {
            var tree = new RecordingTree
            {
                Id = "tree_zero",
                TreeName = "Zero PID Test",
                RootRecordingId = "R1"
            };

            var r1 = new Recording
            {
                RecordingId = "R1",
                VesselPersistentId = 0,
                TerminalStateValue = null,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["R1"] = r1;

            tree.RebuildBackgroundMap();

            Assert.Empty(tree.BackgroundMap);
        }

        [Fact]
        public void RecordingTree_RebuildBackgroundMap_PopulatesRecordedVesselPids()
        {
            var tree = new RecordingTree
            {
                Id = "tree_recorded",
                TreeName = "Recorded PID Test",
                RootRecordingId = "R1",
                ActiveRecordingId = "R2"
            };

            tree.Recordings["R1"] = new Recording
            {
                RecordingId = "R1",
                VesselPersistentId = 111,
                TerminalStateValue = null,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 150.0
            };
            tree.Recordings["R2"] = new Recording
            {
                RecordingId = "R2",
                VesselPersistentId = 222,
                TerminalStateValue = null,
                ExplicitStartUT = 150.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["R3"] = new Recording
            {
                RecordingId = "R3",
                VesselPersistentId = 111,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 250.0
            };

            tree.RebuildBackgroundMap();

            Assert.Contains((uint)111, tree.RecordedVesselPids);
            Assert.Contains((uint)222, tree.RecordedVesselPids);
            Assert.Equal(2, tree.RecordedVesselPids.Count);
        }

        [Fact]
        public void FindDuplicateBackgroundMapPids_ReturnsEligibleDuplicatesOnly()
        {
            var tree = new RecordingTree
            {
                Id = "tree_dup_bg",
                TreeName = "Duplicate Background PID Test",
                RootRecordingId = "R1",
                ActiveRecordingId = "R4"
            };

            tree.Recordings["R1"] = new Recording
            {
                RecordingId = "R1",
                VesselPersistentId = 500,
                TerminalStateValue = null,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 150.0
            };
            tree.Recordings["R2"] = new Recording
            {
                RecordingId = "R2",
                VesselPersistentId = 500,
                TerminalStateValue = null,
                ExplicitStartUT = 150.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["R3"] = new Recording
            {
                RecordingId = "R3",
                VesselPersistentId = 600,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 250.0
            };
            tree.Recordings["R4"] = new Recording
            {
                RecordingId = "R4",
                VesselPersistentId = 700,
                TerminalStateValue = null,
                ExplicitStartUT = 250.0,
                ExplicitEndUT = 300.0
            };

            var duplicates = tree.FindDuplicateBackgroundMapPids();

            Assert.Single(duplicates);
            Assert.Contains((uint)500, duplicates);
            Assert.DoesNotContain((uint)600, duplicates);
            Assert.DoesNotContain((uint)700, duplicates);
        }

        [Fact]
        public void BackgroundMap_DuplicateEligiblePids_PreservesTreeOrderWinnerAcrossSaveLoad()
        {
            var tree = new RecordingTree
            {
                Id = "tree_overlap_bg",
                TreeName = "Overlap BackgroundMap Test",
                RootRecordingId = "R1",
                ActiveRecordingId = null
            };

            tree.Recordings["R1"] = new Recording
            {
                RecordingId = "R1",
                VesselPersistentId = 900,
                TreeOrder = 10,
                TerminalStateValue = null,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 150.0
            };
            tree.Recordings["R2"] = new Recording
            {
                RecordingId = "R2",
                VesselPersistentId = 900,
                TreeOrder = 20,
                TerminalStateValue = null,
                ExplicitStartUT = 120.0,
                ExplicitEndUT = 180.0
            };
            tree.Recordings["R3"] = new Recording
            {
                RecordingId = "R3",
                VesselPersistentId = 999,
                TreeOrder = 30,
                TerminalStateValue = null,
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 250.0
            };

            tree.RebuildBackgroundMap();
            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            var restored = RecordingTree.Load(node);

            Assert.Contains((uint)900, tree.RecordedVesselPids);
            Assert.Equal("R2", tree.BackgroundMap[900]);
            Assert.Equal("R3", tree.BackgroundMap[999]);
            Assert.Equal("R2", restored.BackgroundMap[900]);
            Assert.Equal("R3", restored.BackgroundMap[999]);
        }

        [Fact]
        public void BackgroundMapEligibility_IgnoresOptimizerSplitIntermediateChainSegments()
        {
            var tree = new RecordingTree
            {
                Id = "tree_chain_bg",
                TreeName = "Chain Segment BackgroundMap Test",
                RootRecordingId = "R1",
                ActiveRecordingId = "R3"
            };

            tree.Recordings["R1"] = new Recording
            {
                RecordingId = "R1",
                VesselPersistentId = 500,
                TerminalStateValue = null,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 150.0,
                ChainId = "chain-1",
                ChainIndex = 0,
                ChainBranch = 0
            };
            tree.Recordings["R2"] = new Recording
            {
                RecordingId = "R2",
                VesselPersistentId = 500,
                TerminalStateValue = null,
                ExplicitStartUT = 150.0,
                ExplicitEndUT = 200.0,
                ChainId = "chain-1",
                ChainIndex = 1,
                ChainBranch = 0
            };
            tree.Recordings["R3"] = new Recording
            {
                RecordingId = "R3",
                VesselPersistentId = 700,
                TerminalStateValue = null,
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 250.0
            };

            Assert.False(tree.IsBackgroundMapEligible(tree.Recordings["R1"]));
            Assert.True(tree.IsBackgroundMapEligible(tree.Recordings["R2"]));

            var duplicates = tree.FindDuplicateBackgroundMapPids();
            tree.RebuildBackgroundMap();

            Assert.Empty(duplicates);
            Assert.Equal("R2", tree.BackgroundMap[500]);
        }

        // --- Resource fields (Phase F: legacy fields are NOT persisted) ---

        [Fact]
        public void RecordingTree_LegacyResourceFields_NotPersistedOnSave()
        {
            var tree = new RecordingTree
            {
                Id = "tree_res",
                TreeName = "Resource Test",
                RootRecordingId = "rec_r"
            };
            tree.SetLegacyResidualForTesting(
                deltaFunds: -5000.123,
                deltaScience: 150.5,
                deltaReputation: -10.25f,
                resourcesApplied: true,
                preTreeFunds: 123456.789,
                preTreeScience: 987.654,
                preTreeReputation: 42.5f);

            var rec = new Recording
            {
                RecordingId = "rec_r",
                VesselName = "Resource Ship",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_r"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            // Save must not have written any of the legacy keys.
            Assert.Null(node.GetValue("preTreeFunds"));
            Assert.Null(node.GetValue("preTreeScience"));
            Assert.Null(node.GetValue("preTreeRep"));
            Assert.Null(node.GetValue("deltaFunds"));
            Assert.Null(node.GetValue("deltaScience"));
            Assert.Null(node.GetValue("deltaRep"));
            Assert.Null(node.GetValue("resourcesApplied"));
            Assert.Equal(
                RecordingTree.CurrentTreeFormatVersion.ToString(CultureInfo.InvariantCulture),
                node.GetValue("treeFormatVersion"));

            var restored = RecordingTree.Load(node);

            Assert.Equal(RecordingTree.CurrentTreeFormatVersion, restored.TreeFormatVersion);
            Assert.Null(restored.ConsumeLegacyResidual());
        }

        // --- SurfacePos (background landed recording) ---

        [Fact]
        public void Recording_SurfacePos_RoundTrips()
        {
            var tree = new RecordingTree
            {
                Id = "tree_sp",
                TreeName = "SurfacePos Test",
                RootRecordingId = "rec_sp"
            };

            var surfPos = new SurfacePosition
            {
                body = "Duna",
                latitude = 5.5,
                longitude = -30.2,
                altitude = 100.0,
                rotation = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f),
                situation = SurfaceSituation.Landed
            };

            var rec = new Recording
            {
                RecordingId = "rec_sp",
                VesselName = "Landed Base",
                SurfacePos = surfPos,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_sp"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            var restored = RecordingTree.Load(node);
            var r = restored.Recordings["rec_sp"];

            Assert.True(r.SurfacePos.HasValue);
            Assert.Equal("Duna", r.SurfacePos.Value.body);
            Assert.Equal(5.5, r.SurfacePos.Value.latitude);
            Assert.Equal(-30.2, r.SurfacePos.Value.longitude);
            Assert.Equal(100.0, r.SurfacePos.Value.altitude);
            Assert.Equal(0.5f, r.SurfacePos.Value.rotation.x, 0.00001f);
            Assert.Equal(0.5f, r.SurfacePos.Value.rotation.y, 0.00001f);
            Assert.Equal(0.5f, r.SurfacePos.Value.rotation.z, 0.00001f);
            Assert.Equal(0.5f, r.SurfacePos.Value.rotation.w, 0.00001f);
            Assert.Equal(SurfaceSituation.Landed, r.SurfacePos.Value.situation);
        }

        [Fact]
        public void Recording_SurfacePos_NullOmitsNode()
        {
            var tree = new RecordingTree
            {
                Id = "tree_sp_null",
                TreeName = "Null SP Test",
                RootRecordingId = "rec_sp_null"
            };

            var rec = new Recording
            {
                RecordingId = "rec_sp_null",
                VesselName = "No Surface",
                SurfacePos = null,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_sp_null"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            ConfigNode recNode = node.GetNodes("RECORDING")[0];
            Assert.Null(recNode.GetNode("SURFACE_POSITION"));

            var restored = RecordingTree.Load(node);
            Assert.Null(restored.Recordings["rec_sp_null"].SurfacePos);
        }

        // --- ExplicitStartUT / ExplicitEndUT ---

        [Fact]
        public void Recording_ExplicitUT_UsedWhenNoPoints()
        {
            var rec = new Recording
            {
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 500.0
            };

            Assert.Equal(100.0, rec.StartUT);
            Assert.Equal(500.0, rec.EndUT);
        }

        [Fact]
        public void Recording_ExplicitUT_ExtendsPointBounds()
        {
            var rec = new Recording
            {
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 500.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 400.0 });

            Assert.Equal(100.0, rec.StartUT);
            Assert.Equal(500.0, rec.EndUT);
        }

        // --- Legacy residual seam ---

        [Fact]
        public void ConsumeLegacyResidual_DefaultsNull()
        {
            var tree = new RecordingTree();
            Assert.Null(tree.ConsumeLegacyResidual());
        }

        [Fact]
        public void TreeFormatVersion_SaveWritesCurrentVersion()
        {
            var tree = new RecordingTree
            {
                Id = "tree_ra",
                TreeName = "RA Test",
                RootRecordingId = "rec_ra"
            };

            var rec = new Recording
            {
                RecordingId = "rec_ra",
                VesselName = "Ship RA",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_ra"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            Assert.Equal(
                RecordingTree.CurrentTreeFormatVersion.ToString(CultureInfo.InvariantCulture),
                node.GetValue("treeFormatVersion"));

            var restored = RecordingTree.Load(node);

            Assert.Equal(RecordingTree.CurrentTreeFormatVersion, restored.TreeFormatVersion);
            Assert.Null(restored.ConsumeLegacyResidual());
        }

        [Fact]
        public void TreeFormatVersion_MissingOnLoad_DefaultsZero()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("id", "tree_delta");
            node.AddValue("treeName", "Delta Test");
            node.AddValue("rootRecordingId", "rec_d");

            var tree = RecordingTree.Load(node);

            Assert.Equal(0, tree.TreeFormatVersion);
            Assert.Null(tree.ConsumeLegacyResidual());
        }

        [Fact]
        public void LegacyResourceFields_LoadHydratesFromPreFSaveFormat()
        {
            // Phase A relies on Load still understanding the legacy field keys so
            // it can hydrate residuals from pre-Phase-F .sfs files. We simulate
            // that here by hand-crafting a ConfigNode with the legacy keys.
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("id", "legacy");
            node.AddValue("treeName", "Legacy");
            node.AddValue("rootRecordingId", "legacy-root");
            node.AddValue("preTreeFunds", "60000");
            node.AddValue("preTreeScience", "200");
            node.AddValue("preTreeRep", "30");
            node.AddValue("deltaFunds", "-1500.5");
            node.AddValue("deltaScience", "12.5");
            node.AddValue("deltaRep", "-2.5");
            node.AddValue("resourcesApplied", "False");

            var tree = RecordingTree.Load(node);
            var residual = ConsumeLegacyResidualOrThrow(tree);

            Assert.Equal(0, tree.TreeFormatVersion);
            Assert.Equal(60000, residual.PreTreeFunds);
            Assert.Equal(200, residual.PreTreeScience);
            Assert.Equal(30f, residual.PreTreeReputation);
            Assert.Equal(-1500.5, residual.DeltaFunds);
            Assert.Equal(12.5, residual.DeltaScience);
            Assert.Equal(-2.5f, residual.DeltaReputation);
            Assert.False(residual.ResourcesApplied);
            Assert.True(residual.ResourcesAppliedFieldPresent);
            Assert.Null(tree.ConsumeLegacyResidual());
        }

        // --- Edge cases ---

        [Fact]
        public void RecordingTree_EmptyTree_SaveLoadDoesNotCrash()
        {
            var tree = new RecordingTree
            {
                Id = "tree_empty",
                TreeName = "Empty",
                RootRecordingId = ""
            };

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            var restored = RecordingTree.Load(node);

            Assert.Equal("tree_empty", restored.Id);
            Assert.Equal("Empty", restored.TreeName);
            Assert.Empty(restored.Recordings);
            Assert.Empty(restored.BranchPoints);
        }

        [Fact]
        public void BranchPoint_EmptyParentChildLists_HandleGracefully()
        {
            var bp = new BranchPoint
            {
                Id = "bp_empty",
                UT = 1000.0,
                Type = BranchPointType.EVA
                // parentRecordingIds and childRecordingIds left as empty lists
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp_empty", restored.Id);
            Assert.Equal(BranchPointType.EVA, restored.Type);
            Assert.NotNull(restored.ParentRecordingIds);
            Assert.Empty(restored.ParentRecordingIds);
            Assert.NotNull(restored.ChildRecordingIds);
            Assert.Empty(restored.ChildRecordingIds);
        }

        [Fact]
        public void RecordingTree_ActiveRecordingIdNull_OmitsKey()
        {
            var tree = new RecordingTree
            {
                Id = "tree_no_active",
                TreeName = "No Active",
                RootRecordingId = "rec_x",
                ActiveRecordingId = null
            };

            var rec = new Recording
            {
                RecordingId = "rec_x",
                VesselName = "Ship",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings["rec_x"] = rec;

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            Assert.Null(node.GetValue("activeRecordingId"));

            var restored = RecordingTree.Load(node);
            Assert.Null(restored.ActiveRecordingId);
        }

        // ============================================================
        // B5: RebuildBackgroundMap — realistic multi-level integration
        // ============================================================

        [Fact]
        public void RebuildBackgroundMap_MultiLevel_AllExcluded_ThenOneIncluded()
        {
            var tree = new RecordingTree
            {
                Id = "tree_ml",
                TreeName = "MultiLevel BG",
                RootRecordingId = "root",
                ActiveRecordingId = "grandchild2"
            };

            // root (pid=1, has child) — excluded: has child branch
            tree.Recordings["root"] = new Recording
            {
                RecordingId = "root",
                VesselPersistentId = 1,
                ChildBranchPointId = "bp1",
                TerminalStateValue = null
            };
            // child1 (pid=2, Destroyed) — excluded: terminated
            tree.Recordings["child1"] = new Recording
            {
                RecordingId = "child1",
                VesselPersistentId = 2,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp1"
            };
            // child2 (pid=3, has child bp2) — excluded: has child branch
            tree.Recordings["child2"] = new Recording
            {
                RecordingId = "child2",
                VesselPersistentId = 3,
                ChildBranchPointId = "bp2",
                TerminalStateValue = null,
                ParentBranchPointId = "bp1"
            };
            // grandchild1 (pid=4, Docked) — excluded: terminated
            tree.Recordings["grandchild1"] = new Recording
            {
                RecordingId = "grandchild1",
                VesselPersistentId = 4,
                TerminalStateValue = TerminalState.Docked,
                ParentBranchPointId = "bp2"
            };
            // grandchild2 (pid=5, no terminal, no child) — excluded: is ActiveRecordingId
            tree.Recordings["grandchild2"] = new Recording
            {
                RecordingId = "grandchild2",
                VesselPersistentId = 5,
                TerminalStateValue = null,
                ParentBranchPointId = "bp2"
            };

            tree.RebuildBackgroundMap();

            // Every recording excluded for a different reason
            Assert.Empty(tree.BackgroundMap);

            // Now change ActiveRecordingId so grandchild2 is no longer excluded
            tree.ActiveRecordingId = "root";
            tree.RebuildBackgroundMap();

            Assert.Single(tree.BackgroundMap);
            Assert.True(tree.BackgroundMap.ContainsKey(5));
            Assert.Equal("grandchild2", tree.BackgroundMap[5]);
        }

        // ============================================================
        // D1: Empty tree — query methods safe
        // ============================================================

        [Fact]
        public void EmptyTree_QueryMethods_Safe()
        {
            var tree = new RecordingTree
            {
                Id = "tree_empty_q",
                TreeName = "Empty Query",
                RootRecordingId = ""
            };

            var spawnable = tree.GetSpawnableLeaves();
            var all = tree.GetAllLeaves();
            tree.RebuildBackgroundMap();

            Assert.NotNull(spawnable);
            Assert.Empty(spawnable);
            Assert.NotNull(all);
            Assert.Empty(all);
            Assert.Empty(tree.BackgroundMap);
        }

        // ============================================================
        // D2: Load with unknown fields — forward compat
        // ============================================================

        [Fact]
        public void Load_UnknownFields_SilentlyIgnored()
        {
            // Build a ConfigNode with standard fields + unknown future fields
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("id", "tree_fwd");
            node.AddValue("treeName", "Forward Compat");
            node.AddValue("rootRecordingId", "rec_fwd");
            node.AddValue("preTreeFunds", "50000");
            node.AddValue("preTreeScience", "100");
            node.AddValue("preTreeRep", "50");
            node.AddValue("deltaFunds", "-2000");
            node.AddValue("deltaScience", "10");
            node.AddValue("deltaRep", "-3");
            node.AddValue("resourcesApplied", "False");

            // Future/unknown fields
            node.AddValue("futureFeatureFlag", "true");
            node.AddValue("newMetric", "42.5");
            node.AddValue("experimentalMode", "quantum");

            // Add a recording
            var recNode = node.AddNode("RECORDING");
            recNode.AddValue("recordingId", "rec_fwd");
            recNode.AddValue("vesselName", "Future Ship");
            recNode.AddValue("vesselPersistentId", "999");
            recNode.AddValue("recordingFormatVersion", "0");
            recNode.AddValue("loopPlayback", "False");
            recNode.AddValue("loopIntervalSeconds", "10");
            recNode.AddValue("lastResIdx", "-1");
            recNode.AddValue("pointCount", "0");

            var tree = RecordingTree.Load(node);
            var residual = ConsumeLegacyResidualOrThrow(tree);

            // Standard fields loaded correctly
            Assert.Equal("tree_fwd", tree.Id);
            Assert.Equal("Forward Compat", tree.TreeName);
            Assert.Equal("rec_fwd", tree.RootRecordingId);
            Assert.Equal(0, tree.TreeFormatVersion);
            Assert.Equal(50000, residual.PreTreeFunds);
            Assert.Equal(100, residual.PreTreeScience);
            Assert.Equal(50f, residual.PreTreeReputation);
            Assert.Equal(-2000, residual.DeltaFunds);
            Assert.Equal(10, residual.DeltaScience);
            Assert.Equal(-3f, residual.DeltaReputation);
            Assert.False(residual.ResourcesApplied);
            Assert.True(residual.ResourcesAppliedFieldPresent);

            // Recording loaded correctly despite unknown fields on parent
            Assert.Single(tree.Recordings);
            Assert.True(tree.Recordings.ContainsKey("rec_fwd"));
            Assert.Equal("Future Ship", tree.Recordings["rec_fwd"].VesselName);
        }

        // ============================================================
        // D5: BackgroundMap after save/load round-trip
        // ============================================================

        [Fact]
        public void BackgroundMap_PopulatedAfterSaveLoadRoundTrip()
        {
            var tree = new RecordingTree
            {
                Id = "tree_bg_rt",
                TreeName = "BG Round-Trip",
                RootRecordingId = "rec_bg",
                ActiveRecordingId = null // no active recording
            };

            // One background-eligible recording: non-active, non-terminated, no child, pid=42
            tree.Recordings["rec_bg"] = new Recording
            {
                RecordingId = "rec_bg",
                VesselPersistentId = 42,
                VesselName = "BG Ship",
                TerminalStateValue = null,
                ChildBranchPointId = null,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };

            // Save
            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            // Load — RebuildBackgroundMap should be called during Load
            var restored = RecordingTree.Load(node);

            Assert.True(restored.BackgroundMap.ContainsKey(42));
            Assert.Equal("rec_bg", restored.BackgroundMap[42]);
        }
    }
}
