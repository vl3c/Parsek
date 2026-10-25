using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FOREIGN-BOARD-TARGET-STRIPPED-AT-REWIND: a recorded kerbal boards (or takes the seat of)
    /// a vessel from outside his tree, and the in-flight commit adoption-stamps the merged child
    /// with that vessel's pid. A Rewind-to-Launch must leave the vessel in the rewind save for
    /// the chain walker's hold instead of stripping it as a vessel the tree's replay produces.
    /// Evidence: EVA-14 `2026-10-25_0131` (`Rewind strip scope: strip spawned pid=3620499050 of
    /// 'Kerbal X' ... via holder-replays`, `Captured 0 rewind-save copy(ies)`, `held=0`).
    /// </summary>
    [Collection("Sequential")]
    public class ForeignMergeAdoptionRewindStripTests : IDisposable
    {
        private const uint KerbalXPid = 3620499050u;
        private const string KerbalXGuid = "cb9f5ae95a7b4ec29f88bc81d2a32600";
        private const uint BillPid = 2200000001u;
        private const string BillGuid = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
        private const double RewindTargetUT = 27.1;
        private const double BoardUT = 45.0;
        private const double AdjustedUT = RewindTargetUT - RecordingStore.RewindToLaunchLeadTimeSeconds;

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public ForeignMergeAdoptionRewindStripTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ClaimedVesselHoldGhost.ResetRewindSaveVesselsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek_fmars_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ClaimedVesselHoldGhost.ResetRewindSaveVesselsForTesting();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static Recording Rec(
            string id, string name, string treeId, double startUT, double endUT,
            uint vesselPid, string guid)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                TreeId = treeId,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselPersistentId = vesselPid,
                RecordedVesselGuid = guid,
            };
        }

        /// <summary>
        /// EVA-14's shape: Bill's EVA root ends Boarded into Kerbal X, a vessel outside his tree;
        /// the board child flies Kerbal X and carries the commit's adoption stamp.
        /// </summary>
        private (RecordingTree tree, Recording bill, Recording child, BranchPoint bp) BuildBoardShape(
            BranchPointType mergeType = BranchPointType.Board, uint targetPid = KerbalXPid,
            uint childPid = KerbalXPid, bool commit = true)
        {
            var bill = Rec("b111", "Bill Kerman", "treeB", RewindTargetUT, BoardUT, BillPid, BillGuid);
            var child = Rec("k222", "Kerbal X", "treeB", BoardUT, 75.0, childPid, KerbalXGuid);
            child.ParentBranchPointId = "bp1";
            child.SpawnedVesselPersistentId = childPid;
            child.VesselSpawned = true;

            var tree = new RecordingTree { Id = "treeB", TreeName = "Bill Kerman", RootRecordingId = bill.RecordingId };
            tree.AddOrReplaceRecording(bill);
            tree.AddOrReplaceRecording(child);
            var bp = new BranchPoint
            {
                Id = "bp1", UT = BoardUT, Type = mergeType,
                TargetVesselPersistentId = targetPid, TargetVesselGuid = KerbalXGuid,
            };
            bp.ParentRecordingIds.Add(bill.RecordingId);
            bp.ChildRecordingIds.Add(child.RecordingId);
            tree.BranchPoints.Add(bp);
            if (commit)
            {
                RecordingStore.AddCommittedTreeForTesting(tree);
                foreach (var r in tree.Recordings.Values)
                    RecordingStore.AddRecordingWithTreeForTesting(r);
            }
            return (tree, bill, child, bp);
        }

        private static HashSet<string> MemberIds()
        {
            return ForeignInventoryTransfer.CollectForeignAdoptedVesselMemberRecordingIds(
                RecordingStore.CommittedTrees);
        }

        private string WriteRewindQuicksave()
        {
            string content =
                "FLIGHTSTATE\n{\n  UT = " + RewindTargetUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                "  VESSEL\n  {\n    name = Bill Kerman\n    persistentId = " + BillPid + "\n  }\n" +
                "  VESSEL\n  {\n    name = Kerbal X\n    persistentId = " + KerbalXPid + "\n  }\n}\n";
            string path = Path.Combine(tempDir, "parsek_rw_eva14.sfs");
            File.WriteAllText(path, content);
            return path;
        }

        // ---- The predicate ----

        [Fact]
        public void ForeignBoardChild_IsAnAdoptionMember()
        {
            var shape = BuildBoardShape();
            Assert.True(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.child));
            Assert.False(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.bill));
            Assert.Contains("k222", MemberIds());
            Assert.DoesNotContain("b111", MemberIds());
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Foreign adoption members: transferMembers=0 foreignMergeChildren=1"));
        }

        [Fact]
        public void ForeignSeatCouple_DockChildFlyingThePartner_IsAnAdoptionMember()
        {
            // SEAT-3: a seat couple is a Dock merge the rover survives.
            var shape = BuildBoardShape(BranchPointType.Dock);
            Assert.True(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.child));
        }

        [Fact]
        public void UnstampedMerge_IsNotAnAdoption()
        {
            var shape = BuildBoardShape(targetPid: 0);
            Assert.False(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.child));
        }

        [Fact]
        public void MergeChildFlyingItsOwnVessel_IsNotAnAdoption()
        {
            // The tree's vessel absorbed the foreign partner: the child flies the tree's own
            // vessel, which its replay does produce.
            var shape = BuildBoardShape(BranchPointType.Dock, targetPid: KerbalXPid, childPid: BillPid);
            Assert.False(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.child));
        }

        [Fact]
        public void SameTreeTwoParentMerge_IsNotAnAdoption()
        {
            // EVA-3's shape: the boarded vessel is a background parent of the same tree.
            var shape = BuildBoardShape(commit: false);
            var kxBg = Rec("x333", "Kerbal X", "treeB", RewindTargetUT, BoardUT, KerbalXPid, KerbalXGuid);
            shape.tree.AddOrReplaceRecording(kxBg);
            shape.bp.ParentRecordingIds.Add(kxBg.RecordingId);
            Assert.False(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.child));
        }

        [Fact]
        public void PartnerFlownEarlierInTheTree_IsNotAnAdoption()
        {
            var shape = BuildBoardShape(commit: false);
            var earlier = Rec("x444", "Kerbal X", "treeB", RewindTargetUT, 30.0, KerbalXPid, KerbalXGuid);
            shape.tree.AddOrReplaceRecording(earlier);
            Assert.False(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.child));
        }

        [Fact]
        public void ChildsOwnChainSegment_DoesNotDisqualifyIt()
        {
            var shape = BuildBoardShape(commit: false);
            var segment = Rec("k555", "Kerbal X", "treeB", 60.0, 75.0, KerbalXPid, KerbalXGuid);
            shape.tree.AddOrReplaceRecording(segment);
            Assert.True(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.child));
        }

        [Fact]
        public void UndockChild_IsNotAnAdoption()
        {
            var shape = BuildBoardShape(BranchPointType.Undock);
            Assert.False(ForeignInventoryTransfer.IsForeignMergeAdoptionChild(shape.tree, shape.child));
        }

        // ---- The strip scope ----

        [Fact]
        public void Scope_LeavesTheBoardedVesselForTheHold()
        {
            BuildBoardShape();
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), AdjustedUT, MemberIds());

            Assert.DoesNotContain(KerbalXPid, scope.StripPids);
            Assert.DoesNotContain(KerbalXPid, scope.KeptPids);
            Assert.Contains(KerbalXPid, scope.ForeignAdoptedPids);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("leave spawned pid=" + KerbalXPid) && l.Contains("member=k222"));
            Assert.DoesNotContain(logLines, l => l.Contains("strip spawned pid=" + KerbalXPid));
        }

        [Fact]
        public void Control_TransferMembersOnly_StripsTheBoardedVessel()
        {
            // The pre-fix member set: the board child replays, so its adopted pid is stripped.
            BuildBoardShape();
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), AdjustedUT,
                ForeignInventoryTransfer.CollectTransferMemberRecordingIds(RecordingStore.CommittedTrees));
            Assert.Contains(KerbalXPid, scope.StripPids);
            Assert.Contains(logLines, l => l.Contains("strip spawned pid=" + KerbalXPid) && l.Contains("via holder-replays"));
        }

        [Fact]
        public void RewindToLaunch_KeepsTheBoardedVesselInTheSaveAndTakesItsHoldCopy()
        {
            var shape = BuildBoardShape();
            string sfs = WriteRewindQuicksave();

            RecordingStore.PreProcessRewindSave(
                sfs, RecordingStore.RewindOwnerStrip.FromNames(new HashSet<string> { "Bill Kerman" }),
                RecordingStore.ResolveRewindStripSpawnedPids,
                RecordingStore.RewindToLaunchLeadTimeSeconds,
                ut => new HashSet<uint> { KerbalXPid });

            var pids = new List<string>();
            foreach (var v in ConfigNode.Load(sfs).GetNode("FLIGHTSTATE").GetNodes("VESSEL"))
                pids.Add(v.GetValue("persistentId"));
            Assert.Contains(KerbalXPid.ToString(), pids);
            Assert.DoesNotContain(BillPid.ToString(), pids);
            Assert.Contains(KerbalXPid, RewindContext.RewindQuicksaveVesselPids);
            Assert.NotNull(ClaimedVesselHoldGhost.TryGetRewindSaveVessel(KerbalXPid, null));
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Captured 1 rewind-save copy(ies) of 1 vessel(s) a committed future claims"));

            // OnLoad: the launch-identity match leaves the boarded vessel to the hold.
            var candidates = ForeignInventoryTransfer.ExcludeForeignAdoptions(
                RecordingStore.CollectAllCommittedRecordings(), MemberIds(), out int excluded);
            Assert.True(excluded >= 1);
            Assert.DoesNotContain(shape.child, candidates);
            Assert.False(ParsekScenario.ShouldStripVesselForRecordings(
                KerbalXPid, KerbalXGuid, Vessel.Situations.PRELAUNCH, candidates,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: RewindContext.RewindQuicksaveVesselPids,
                out _, out _));

            // The child's adoption stamp resets, so the chain tip resolves again at its end.
            RecordingStore.ResetAllPlaybackState(RewindContext.RewindHistoricalSpawnKeepRecordingIds);
            Assert.Equal(0u, shape.child.SpawnedVesselPersistentId);
            Assert.False(shape.child.VesselSpawned);
        }
    }
}
