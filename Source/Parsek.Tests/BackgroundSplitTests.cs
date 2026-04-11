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

        #region Cascade Depth Cap (#284) — Pure Logic

        [Fact]
        public void Recording_DefaultGeneration_IsZero()
        {
            var rec = new Recording();
            Assert.Equal(0, rec.Generation);
        }

        [Fact]
        public void MaxRecordingGeneration_IsOne()
        {
            // Cap at gen=1 means primary debris (boosters decoupled by the
            // active vessel) is recorded; secondary fragments are not.
            Assert.Equal(1, BackgroundRecorder.MaxRecordingGeneration);
        }

        [Fact]
        public void ShouldSkipForCascadeCap_Gen0_ReturnsFalse()
        {
            // gen 0 = primary recording (active vessel) — splits allowed
            Assert.False(BackgroundRecorder.ShouldSkipForCascadeCap(0));
        }

        [Fact]
        public void ShouldSkipForCascadeCap_Gen1_ReturnsTrue()
        {
            // gen 1 = primary debris (booster) — its splits are skipped
            Assert.True(BackgroundRecorder.ShouldSkipForCascadeCap(1));
        }

        [Fact]
        public void ShouldSkipForCascadeCap_Gen2_ReturnsTrue()
        {
            // gen 2 = fragment-of-fragment — splits are skipped
            Assert.True(BackgroundRecorder.ShouldSkipForCascadeCap(2));
        }

        [Fact]
        public void ShouldSkipForCascadeCap_LargeGen_ReturnsTrue()
        {
            // sanity check: any positive gen >= cap is gated
            Assert.True(BackgroundRecorder.ShouldSkipForCascadeCap(99));
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_PropagatesGeneration_Gen0Parent_ChildrenAreGen1()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Booster", false),
                (400, "Probe", true)
            };

            var (_, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels, parentGeneration: 0);

            Assert.Equal(1, children[0].Generation);
            Assert.Equal(1, children[1].Generation);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_PropagatesGeneration_Gen1Parent_ChildrenAreGen2()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Fragment", false)
            };

            var (_, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels, parentGeneration: 1);

            Assert.Equal(2, children[0].Generation);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_Gen2Parent_ChildrenAreGen3_NotCapped()
        {
            // The pure builder propagates Generation regardless of the cap.
            // The cap lives in HandleBackgroundVesselSplit (the caller), not here.
            // This documents the separation of concerns.
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Fragment", false)
            };

            var (_, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels, parentGeneration: 2);

            Assert.Equal(3, children[0].Generation);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_DefaultParentGeneration_IsZero()
        {
            // Optional parameter default is 0 — preserves existing test behavior
            // for callers that don't care about generation.
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Stage", false)
            };

            var (_, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec", "tree_1", 1000.0, BranchPointType.JointBreak,
                100, newVessels);

            Assert.Equal(1, children[0].Generation);
        }

        [Fact]
        public void BuildSplitBranchData_PropagatesGeneration_BgChildPlusOne_ActiveChildSame()
        {
            // Active path: activeChild continues the same logical vessel (same gen),
            // bgChild is the spinoff (parentGen + 1).
            var (_, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                "parent_rec", "tree_1", 100.0, BranchPointType.JointBreak,
                42, "Active", 99, "Spinoff",
                parentGeneration: 0);

            Assert.Equal(0, activeChild.Generation);
            Assert.Equal(1, bgChild.Generation);
        }

        [Fact]
        public void BuildSplitBranchData_Gen1Parent_ActiveStaysGen1_BgChildBecomesGen2()
        {
            // Player switches focus to a gen-1 booster and decouples something.
            // activeChild (continuing the booster) stays at gen=1; bgChild=2.
            var (_, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                "parent_rec", "tree_1", 100.0, BranchPointType.JointBreak,
                42, "Active Booster", 99, "Fragment",
                parentGeneration: 1);

            Assert.Equal(1, activeChild.Generation);
            Assert.Equal(2, bgChild.Generation);
        }

        [Fact]
        public void Recording_ApplyPersistenceArtifactsFrom_CopiesGeneration()
        {
            var source = new Recording { Generation = 1 };
            var target = new Recording();

            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(1, target.Generation);
        }

        [Fact]
        public void CreateBreakupChildRecording_PropagatesGeneration_Gen0Parent_ChildIsGen1()
        {
            // Active rocket (gen=0) crashes → debris children must be gen=1.
            // Used by ProcessBreakupEvent.
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            var bp = new BranchPoint { Id = "bp1", UT = 100 };

            var rec = ParsekFlight.CreateBreakupChildRecording(
                tree, bp, 42, null, true, "Debris",
                fallbackSnapshot: null, parentGeneration: 0);

            Assert.Equal(1, rec.Generation);
        }

        [Fact]
        public void CreateBreakupChildRecording_PropagatesGeneration_Gen1Parent_ChildIsGen2()
        {
            // Player flying a gen=1 booster crashes it → fragments would be gen=2.
            // The pure builder propagates without enforcing the cap (cap lives in
            // HandleBackgroundVesselSplit, not in the active path).
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            var bp = new BranchPoint { Id = "bp1", UT = 100 };

            var rec = ParsekFlight.CreateBreakupChildRecording(
                tree, bp, 42, null, true, "Debris",
                fallbackSnapshot: null, parentGeneration: 1);

            Assert.Equal(2, rec.Generation);
        }

        [Fact]
        public void CreateBreakupChildRecording_DefaultParentGeneration_ChildIsGen1()
        {
            // Optional parameter default = 0 — preserves existing test behavior
            // for legacy callers that don't care about generation.
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            var bp = new BranchPoint { Id = "bp1", UT = 100 };

            var rec = ParsekFlight.CreateBreakupChildRecording(
                tree, bp, 42, null, true, "Debris");

            Assert.Equal(1, rec.Generation);
        }

        [Fact]
        public void RecordingTree_SaveLoadRoundTrip_PreservesGeneration()
        {
            // Verify cascade depth survives the .sfs persistence path so a
            // gen=1 booster doesn't reload as gen=0 and slip past the cap on
            // a subsequent breakup after F5/F9.
            var rec = new Recording
            {
                RecordingId = "test_round_trip",
                VesselName = "Test Booster",
                VesselPersistentId = 42,
                Generation = 1
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(1, loaded.Generation);
        }

        [Fact]
        public void RecordingTree_SaveRecordingInto_OmitsGenerationWhenZero()
        {
            // Gen-0 recordings stay byte-identical to legacy saves — the
            // "generation" key is only written for non-default values.
            var rec = new Recording
            {
                RecordingId = "test_gen0",
                VesselName = "Test",
                VesselPersistentId = 42,
                Generation = 0
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            Assert.Null(node.GetValue("generation"));
        }

        [Fact]
        public void RecordingTree_LoadRecordingFrom_LegacyNodeWithoutGeneration_DefaultsToZero()
        {
            // Legacy .sfs files have no "generation" key — must load as gen=0.
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy_id");
            node.AddValue("vesselName", "Old Recording");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(0, rec.Generation);
        }

        #endregion

        #region Bug #285 — Parent Dead at Split Time (Simulation Tests)

        // These tests simulate the two paths in HandleBackgroundVesselSplit:
        // (a) parent alive → continuation created, and (b) parent dead → no continuation.
        // HandleBackgroundVesselSplit itself accesses FlightGlobals so cannot be called
        // in unit tests; we simulate the tree mutations it performs.

        [Fact]
        public void Bug285_ParentAlive_ContinuationCreated_TreeHasExtraRecording()
        {
            // Simulate the "parent alive" path (existing behavior)
            var tree = MakeTree((100, "rec_bg"));
            var parentRec = tree.Recordings["rec_bg"];

            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Debris Fragment", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "rec_bg", "tree_split_test", 150.0, BranchPointType.JointBreak,
                100, newVessels);

            // Close parent
            parentRec.ChildBranchPointId = bp.Id;
            parentRec.ExplicitEndUT = 150.0;
            tree.BackgroundMap.Remove(100);
            tree.BranchPoints.Add(bp);

            // Parent alive → create continuation
            string parentContRecId = Guid.NewGuid().ToString("N");
            var parentContRec = new Recording
            {
                RecordingId = parentContRecId,
                TreeId = "tree_split_test",
                VesselPersistentId = 100,
                VesselName = "Background Vessel 0",
                ParentBranchPointId = bp.Id,
                ExplicitStartUT = 150.0,
                IsDebris = parentRec.IsDebris,
                Generation = parentRec.Generation
            };
            bp.ChildRecordingIds.Insert(0, parentContRecId);
            tree.Recordings[parentContRecId] = parentContRec;
            tree.BackgroundMap[100] = parentContRecId;

            // Add child
            tree.Recordings[children[0].RecordingId] = children[0];

            // Verify: continuation + child = 2 children on BP
            Assert.Equal(2, bp.ChildRecordingIds.Count);
            Assert.Equal(parentContRecId, bp.ChildRecordingIds[0]);
            Assert.Equal(children[0].RecordingId, bp.ChildRecordingIds[1]);
            Assert.True(tree.BackgroundMap.ContainsKey(100));
            // active + bg_parent + continuation + child = 4
            Assert.Equal(4, tree.Recordings.Count);
        }

        [Fact]
        public void Bug285_ParentDead_NoContinuation_TreeHasOnlyChildren()
        {
            // Simulate the "parent dead" path (bug #285 fix)
            var tree = MakeTree((100, "rec_bg"));
            var parentRec = tree.Recordings["rec_bg"];

            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Debris Fragment", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "rec_bg", "tree_split_test", 150.0, BranchPointType.JointBreak,
                100, newVessels);

            // Close parent
            parentRec.ChildBranchPointId = bp.Id;
            parentRec.ExplicitEndUT = 150.0;
            tree.BackgroundMap.Remove(100);
            tree.BranchPoints.Add(bp);

            // Parent dead → skip continuation (the fix)
            // Just add child recordings, no continuation

            tree.Recordings[children[0].RecordingId] = children[0];

            // Verify: only child on BP, no continuation
            Assert.Single(bp.ChildRecordingIds);
            Assert.Equal(children[0].RecordingId, bp.ChildRecordingIds[0]);
            // parentPid no longer in BackgroundMap
            Assert.False(tree.BackgroundMap.ContainsKey(100));
            // active + bg_parent + child = 3 (no continuation)
            Assert.Equal(3, tree.Recordings.Count);
            // Parent still has ChildBranchPointId (it's a branch node, not a leaf)
            Assert.Equal(bp.Id, parentRec.ChildBranchPointId);
        }

        [Fact]
        public void Bug285_ParentDead_NoContinuation_NoChildHasParentPid()
        {
            // Verify that none of the BP's children carry the parent's PID
            var tree = MakeTree((100, "rec_bg"));
            var parentRec = tree.Recordings["rec_bg"];

            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300, "Fragment A", false),
                (400, "Fragment B", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "rec_bg", "tree_split_test", 150.0, BranchPointType.JointBreak,
                100, newVessels);

            // Close parent, skip continuation (parent dead path)
            parentRec.ChildBranchPointId = bp.Id;
            parentRec.ExplicitEndUT = 150.0;
            tree.BackgroundMap.Remove(100);
            tree.BranchPoints.Add(bp);

            for (int i = 0; i < children.Count; i++)
                tree.Recordings[children[i].RecordingId] = children[i];

            // BP children = only the new fragments
            Assert.Equal(newVessels.Count, bp.ChildRecordingIds.Count);

            // None of the child recordings have the parent's PID
            for (int i = 0; i < bp.ChildRecordingIds.Count; i++)
            {
                var childRec = tree.Recordings[bp.ChildRecordingIds[i]];
                Assert.NotEqual(100u, childRec.VesselPersistentId);
            }
        }

        #endregion
    }
}