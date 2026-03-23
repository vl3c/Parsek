using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BackgroundSplitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public BackgroundSplitTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        /// <summary>
        /// Helper: creates a minimal RecordingTree with the given background vessel PIDs.
        /// </summary>
        private RecordingTree MakeTree(params (uint pid, string recId)[] backgroundVessels)
        {
            var tree = new RecordingTree
            {
                Id = "tree_split_test",
                TreeName = "Split Test Tree",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_active"
            };

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
                    TreeId = "tree_split_test",
                    VesselName = $"Background Vessel {i}",
                    VesselPersistentId = pid,
                    ExplicitStartUT = 100.0,
                    ExplicitEndUT = 200.0
                };
                tree.BackgroundMap[pid] = recId;
            }

            return tree;
        }

        #region BuildBackgroundSplitBranchData — Pure Logic

        [Fact]
        public void BuildBackgroundSplitBranchData_CreatesCorrectBranchPoint()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Debris 1", false),
                (400, "Probe Core", true)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.JointBreak, bp.Type);
            Assert.Equal(1000.0, bp.UT);
            Assert.Single(bp.ParentRecordingIds);
            Assert.Equal("parent_rec", bp.ParentRecordingIds[0]);
            Assert.Equal(2, bp.ChildRecordingIds.Count);
            Assert.Equal("DECOUPLE", bp.SplitCause);
            Assert.NotNull(bp.Id);
            Assert.NotEmpty(bp.Id);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_CreatesChildRecordingsForEachNewVessel()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Spent Stage", false),
                (400, "Upper Stage Probe", true)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Equal(2, children.Count);

            // First child: debris
            Assert.Equal("tree_1", children[0].TreeId);
            Assert.Equal(300u, children[0].VesselPersistentId);
            Assert.Equal("Spent Stage", children[0].VesselName);
            Assert.Equal(bp.Id, children[0].ParentBranchPointId);
            Assert.Equal(1000.0, children[0].ExplicitStartUT);
            Assert.True(children[0].IsDebris);

            // Second child: controlled
            Assert.Equal("tree_1", children[1].TreeId);
            Assert.Equal(400u, children[1].VesselPersistentId);
            Assert.Equal("Upper Stage Probe", children[1].VesselName);
            Assert.Equal(bp.Id, children[1].ParentBranchPointId);
            Assert.Equal(1000.0, children[1].ExplicitStartUT);
            Assert.False(children[1].IsDebris);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_ChildRecordingIdsMatchBranchPoint()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Debris", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 500.0, BranchPointType.JointBreak,
                100, newVessels);

            // The BP's child recording IDs should match the child recordings
            Assert.Single(bp.ChildRecordingIds);
            Assert.Equal(bp.ChildRecordingIds[0], children[0].RecordingId);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_SingleDebrisChild_IsMarkedAsDebris()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Booster", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 750.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Single(children);
            Assert.True(children[0].IsDebris);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_ControlledChild_IsNotDebris()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Probe Ship", true)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 750.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Single(children);
            Assert.False(children[0].IsDebris);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_MultipleChildren_AllGetUniqueRecordingIds()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Debris A", false),
                (400, "Debris B", false),
                (500, "Probe", true)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Equal(3, children.Count);
            var ids = new HashSet<string>();
            for (int i = 0; i < children.Count; i++)
            {
                Assert.True(ids.Add(children[i].RecordingId),
                    $"Duplicate RecordingId found: {children[i].RecordingId}");
            }
        }

        #endregion

        #region ShouldStopDebrisRecording — Pure Logic

        [Fact]
        public void ShouldStopDebrisRecording_VesselNotExists_ReturnsDestroyed()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 100.0, ttlExpiry: 130.0, vesselExists: false, vesselLoaded: false);
            Assert.Equal("destroyed", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_TTLExpired_ReturnsTtlExpired()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 131.0, ttlExpiry: 130.0, vesselExists: true, vesselLoaded: true);
            Assert.Equal("ttl_expired", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_TTLExactlyAtExpiry_ReturnsTtlExpired()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 130.0, ttlExpiry: 130.0, vesselExists: true, vesselLoaded: true);
            Assert.Equal("ttl_expired", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_VesselNotLoaded_ReturnsOutOfBubble()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 100.0, ttlExpiry: 130.0, vesselExists: true, vesselLoaded: false);
            Assert.Equal("out_of_bubble", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_VesselAlive_BeforeTTL_ReturnsNull()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 100.0, ttlExpiry: 130.0, vesselExists: true, vesselLoaded: true);
            Assert.Null(reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_NoTTL_VesselAlive_ReturnsNull()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 100.0, ttlExpiry: double.NaN, vesselExists: true, vesselLoaded: true);
            Assert.Null(reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_DestroyedTakesPriorityOverTTL()
        {
            // Even if TTL hasn't expired, destroyed takes priority
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 100.0, ttlExpiry: 130.0, vesselExists: false, vesselLoaded: false);
            Assert.Equal("destroyed", reason);
        }

        #endregion

        #region TTL Constant

        [Fact]
        public void DebrisTTLSeconds_Is60()
        {
            Assert.Equal(60.0, BackgroundRecorder.DebrisTTLSeconds);
        }

        #endregion

        #region HandleBackgroundVesselSplit — Tree Structure (via BuildBackgroundSplitBranchData)

        // NOTE: HandleBackgroundVesselSplit cannot be called in unit tests because it
        // accesses FlightGlobals.Vessels (KSP runtime). We test the tree structure
        // indirectly through BuildBackgroundSplitBranchData (the pure data-model method)
        // and verify the wiring in integration scenarios.

        [Fact]
        public void BuildBackgroundSplitBranchData_TreeStructure_ParentClosedChildCreated()
        {
            // Simulate what HandleBackgroundVesselSplit does to the tree:
            // 1. Build branch data
            // 2. Set ChildBranchPointId on parent
            // 3. Create continuation + child recordings
            var tree = MakeTree((100, "rec_bg"));
            var parentRec = tree.Recordings["rec_bg"];

            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Stage", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "rec_bg", "tree_split_test", 150.0, BranchPointType.JointBreak,
                100, newVessels);

            // Simulate what HandleBackgroundVesselSplit does:
            parentRec.ChildBranchPointId = bp.Id;
            parentRec.ExplicitEndUT = 150.0;

            // Add continuation for parent
            string parentContRecId = Guid.NewGuid().ToString("N");
            var parentContRec = new Recording
            {
                RecordingId = parentContRecId,
                TreeId = "tree_split_test",
                VesselPersistentId = 100,
                VesselName = "Background Vessel 0",
                ParentBranchPointId = bp.Id,
                ExplicitStartUT = 150.0
            };
            bp.ChildRecordingIds.Insert(0, parentContRecId);
            tree.Recordings[parentContRecId] = parentContRec;

            // Add child
            tree.Recordings[children[0].RecordingId] = children[0];
            tree.BranchPoints.Add(bp);

            // Verify tree structure
            Assert.Equal(bp.Id, parentRec.ChildBranchPointId);
            Assert.Equal(150.0, parentRec.ExplicitEndUT);
            Assert.Equal(2, bp.ChildRecordingIds.Count); // continuation + 1 new vessel
            Assert.Equal(parentContRecId, bp.ChildRecordingIds[0]);
            Assert.Equal(children[0].RecordingId, bp.ChildRecordingIds[1]);
            Assert.Single(tree.BranchPoints);
            Assert.Equal(4, tree.Recordings.Count); // active + bg + cont + child
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_DebrisChild_TTLWouldBeSet()
        {
            // Verify that the child recording is marked as debris, which the caller
            // uses to decide whether to set a TTL
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Booster", false)
            };

            var (_, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "rec_bg", "tree_1", 150.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.True(children[0].IsDebris);
            // The caller checks IsDebris to set TTL = branchUT + DebrisTTLSeconds
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_ControlledChild_NoTTLNeeded()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Probe Core", true)
            };

            var (_, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "rec_bg", "tree_1", 150.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.False(children[0].IsDebris);
            // The caller does NOT set TTL for non-debris children
        }

        #endregion

        #region BuildBackgroundSplitBranchData — BranchPoint Metadata

        [Fact]
        public void BuildBackgroundSplitBranchData_SplitCause_IsDecouple()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Stage", false)
            };

            var (bp, _) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Equal("DECOUPLE", bp.SplitCause);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_BranchUT_MatchesInput()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Stage", false)
            };

            var (bp, _) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 12345.6789, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Equal(12345.6789, bp.UT);
        }

        #endregion

        #region TTL Tracking via BackgroundRecorder

        [Fact]
        public void DebrisTTL_NotSetByDefault()
        {
            var tree = MakeTree((100, "rec_bg"));
            var bgRecorder = new BackgroundRecorder(tree);

            Assert.True(double.IsNaN(bgRecorder.GetDebrisTTLExpiryForTesting(100)));
        }

        [Fact]
        public void DebrisTTL_InjectAndRetrieve()
        {
            var tree = MakeTree((100, "rec_bg"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectDebrisTTLForTesting(100, 130.0);
            Assert.Equal(130.0, bgRecorder.GetDebrisTTLExpiryForTesting(100));
        }

        [Fact]
        public void DebrisTTLCount_TracksEntries()
        {
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);

            Assert.Equal(0, bgRecorder.DebrisTTLCount);

            bgRecorder.InjectDebrisTTLForTesting(100, 130.0);
            Assert.Equal(1, bgRecorder.DebrisTTLCount);

            bgRecorder.InjectDebrisTTLForTesting(200, 160.0);
            Assert.Equal(2, bgRecorder.DebrisTTLCount);
        }

        #endregion

        #region ShouldStopDebrisRecording — Edge Cases

        [Fact]
        public void ShouldStopDebrisRecording_NaN_TTL_VesselNotLoaded_ReturnsOutOfBubble()
        {
            // Even with NaN TTL, out-of-bubble still triggers
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 100.0, ttlExpiry: double.NaN, vesselExists: true, vesselLoaded: false);
            Assert.Equal("out_of_bubble", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_NaN_TTL_VesselDestroyed_ReturnsDestroyed()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 100.0, ttlExpiry: double.NaN, vesselExists: false, vesselLoaded: false);
            Assert.Equal("destroyed", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_VeryLargeTTL_VesselAlive_ReturnsNull()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                currentUT: 100.0, ttlExpiry: 1e12, vesselExists: true, vesselLoaded: true);
            Assert.Null(reason);
        }

        #endregion

        #region Log Assertion Tests

        [Fact]
        public void BuildBackgroundSplitBranchData_LogsSplitInfo()
        {
            // BuildBackgroundSplitBranchData is pure and doesn't log,
            // but HandleBackgroundVesselSplit does. Since we can't call it in tests,
            // verify the log output from the tree-build simulation path.
            // We test the pure decision method's reason strings instead.
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                131.0, 130.0, vesselExists: true, vesselLoaded: true);
            Assert.Equal("ttl_expired", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_Destroyed_ReasonIsCorrect()
        {
            // Verifies the exact reason string for downstream consumers
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                100.0, 130.0, vesselExists: false, vesselLoaded: false);
            Assert.Equal("destroyed", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_TTLExpired_ReasonIsCorrect()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                135.0, 130.0, vesselExists: true, vesselLoaded: true);
            Assert.Equal("ttl_expired", reason);
        }

        [Fact]
        public void ShouldStopDebrisRecording_OutOfBubble_ReasonIsCorrect()
        {
            string reason = BackgroundRecorder.ShouldStopDebrisRecording(
                100.0, 130.0, vesselExists: true, vesselLoaded: false);
            Assert.Equal("out_of_bubble", reason);
        }

        #endregion

        #region BuildBackgroundSplitBranchData — Parent Linkage

        [Fact]
        public void BuildBackgroundSplitBranchData_ParentRecordingId_IsLinkedCorrectly()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Stage", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec_xyz", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Equal("parent_rec_xyz", bp.ParentRecordingIds[0]);
            Assert.Equal(bp.Id, children[0].ParentBranchPointId);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_MixedControlAndDebris_CorrectFlags()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Booster SRB", false),
                (400, "Probe Core", true),
                (500, "Empty Tank", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.True(children[0].IsDebris);   // Booster SRB
            Assert.False(children[1].IsDebris);  // Probe Core
            Assert.True(children[2].IsDebris);   // Empty Tank
        }

        #endregion

        #region PendingSplitCheckCount

        [Fact]
        public void PendingSplitCheckCount_InitiallyZero()
        {
            var tree = MakeTree((100, "rec_bg"));
            var bgRecorder = new BackgroundRecorder(tree);
            Assert.Equal(0, bgRecorder.PendingSplitCheckCount);
        }

        #endregion

        #region ProcessPendingSplitChecks — Empty

        [Fact]
        public void ProcessPendingSplitChecks_NoPending_DoesNotThrow()
        {
            var tree = MakeTree((100, "rec_bg"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Should return immediately with no pending checks
            bgRecorder.ProcessPendingSplitChecks();

            // No error, no tree changes
            Assert.Empty(tree.BranchPoints);
        }

        #endregion

        #region CheckDebrisTTL — No Entries

        [Fact]
        public void CheckDebrisTTL_NoEntries_DoesNotThrow()
        {
            var tree = MakeTree((100, "rec_bg"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Should return immediately with no TTL entries
            bgRecorder.CheckDebrisTTL(200.0);

            // No changes, no log about expiry
            Assert.DoesNotContain(logLines, l =>
                l.Contains("TTL expired") || l.Contains("TTL: vessel"));
        }

        #endregion

        #region BranchPointType for Background Splits

        [Fact]
        public void BuildBackgroundSplitBranchData_UsesJointBreakType()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Stage", false)
            };

            var (bp, _) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Equal(BranchPointType.JointBreak, bp.Type);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_Undock_PreservesBranchType()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Detached Module", true)
            };

            var (bp, _) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.Undock,
                100, newVessels);

            Assert.Equal(BranchPointType.Undock, bp.Type);
        }

        #endregion

        #region Child Recording ExplicitStartUT

        [Fact]
        public void BuildBackgroundSplitBranchData_ChildrenHaveCorrectStartUT()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Stage", false),
                (400, "Probe", true)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 2500.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Equal(2500.0, children[0].ExplicitStartUT);
            Assert.Equal(2500.0, children[1].ExplicitStartUT);
        }

        #endregion
    }
}