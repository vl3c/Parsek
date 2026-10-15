using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// EVA-CONSTRUCTION-ON-FOREIGN-VESSEL-UNCLAIMED, the rewind half: a foreign vessel an EVA
    /// kerbal changed joins his tree as an InventoryTransfer member, and the in-flight commit
    /// adoption-stamps that member with the vessel's own pid. A Rewind-to-Launch of the tree
    /// must leave the vessel in the rewind save (it existed before the tree; the chain walker
    /// claims and ghosts it) instead of stripping it as a vessel the tree will re-spawn.
    /// Evidence shape: EVA-11 reading run 2026-10-15_2302 (rover 'rover fuel 0' pid
    /// 1871615455, `Rewind strip scope: strip spawned pid=1871615455 ... via holder-replays`,
    /// then `Stripping orphaned spawned vessel 'rover fuel 0'` and no `Vessel ghosted`).
    /// </summary>
    [Collection("Sequential")]
    public class ForeignAdoptionRewindStripTests : IDisposable
    {
        private const uint RoverPid = 1871615455u;
        private const string RoverGuid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        private const uint BillPid = 2200000001u;
        private const string BillGuid = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
        private const uint PadPid = 2200000002u;
        private const double RewindTargetUT = 40.0;
        private const double AdjustedUT = RewindTargetUT - RecordingStore.RewindToLaunchLeadTimeSeconds;

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public ForeignAdoptionRewindStripTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek_fars_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
        /// Bill's tree: his EVA recording and the rover member under a branch point of
        /// <paramref name="memberBranchType"/>, both adoption-stamped by the in-flight commit.
        /// </summary>
        private (RecordingTree tree, Recording bill, Recording rover) BuildEva11Shape(
            BranchPointType memberBranchType = BranchPointType.InventoryTransfer)
        {
            var bill = Rec("b111", "Bill Kerman", "treeE", RewindTargetUT, 60.0, BillPid, BillGuid);
            bill.SpawnedVesselPersistentId = BillPid;
            bill.VesselSpawned = true;
            var rover = Rec("r222", "rover fuel 0", "treeE", 42.0, 59.9, RoverPid, RoverGuid);
            rover.ParentBranchPointId = "bp1";
            rover.SpawnedVesselPersistentId = RoverPid;
            rover.VesselSpawned = true;

            var tree = new RecordingTree { Id = "treeE", TreeName = "Bill Kerman", RootRecordingId = bill.RecordingId };
            tree.AddOrReplaceRecording(bill);
            tree.AddOrReplaceRecording(rover);
            var bp = new BranchPoint { Id = "bp1", UT = 42.0, Type = memberBranchType };
            bp.ParentRecordingIds.Add(bill.RecordingId);
            bp.ChildRecordingIds.Add(rover.RecordingId);
            tree.BranchPoints.Add(bp);
            Commit(tree);
            return (tree, bill, rover);
        }

        private static void Commit(RecordingTree tree)
        {
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (var r in tree.Recordings.Values)
                RecordingStore.AddRecordingWithTreeForTesting(r);
        }

        private string WriteRewindQuicksave()
        {
            // The rewind save captured at Bill's recording start: Bill on EVA, the pad Kerbal X,
            // the 16-part rover before any construction.
            string content =
                "FLIGHTSTATE\n{\n  UT = " + RewindTargetUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                "  VESSEL\n  {\n    name = Bill Kerman\n    persistentId = " + BillPid + "\n  }\n" +
                "  VESSEL\n  {\n    name = Kerbal X\n    persistentId = " + PadPid + "\n  }\n" +
                "  VESSEL\n  {\n    name = rover fuel 0\n    persistentId = " + RoverPid + "\n  }\n}\n";
            string path = Path.Combine(tempDir, "parsek_rw_eva11.sfs");
            File.WriteAllText(path, content);
            return path;
        }

        private static HashSet<string> MemberIds()
        {
            return ForeignInventoryTransfer.CollectTransferMemberRecordingIds(RecordingStore.CommittedTrees);
        }

        // ---- The repro: the whole plain-rewind strip chain over the EVA-11 shape ----

        [Fact]
        public void Eva11Shape_RewindToLaunch_KeepsAdoptedRoverAndResetsItsSpawn()
        {
            var shape = BuildEva11Shape();
            string sfs = WriteRewindQuicksave();

            RecordingStore.PreProcessRewindSave(
                sfs, new HashSet<string> { "Bill Kerman" },
                RecordingStore.ResolveRewindStripSpawnedPids,
                RecordingStore.RewindToLaunchLeadTimeSeconds);

            var pids = new List<string>();
            foreach (var v in ConfigNode.Load(sfs).GetNode("FLIGHTSTATE").GetNodes("VESSEL"))
                pids.Add(v.GetValue("persistentId"));
            Assert.Contains(RoverPid.ToString(), pids);
            Assert.Contains(PadPid.ToString(), pids);
            Assert.DoesNotContain(BillPid.ToString(), pids);
            Assert.Contains(RoverPid, RewindContext.RewindQuicksaveVesselPids);

            // OnLoad: neither the whitelist nor the launch-identity match strips the rover.
            var candidates = ForeignInventoryTransfer.ExcludeForeignAdoptions(
                RecordingStore.CollectAllCommittedRecordings(), MemberIds(), out int excluded);
            Assert.True(excluded >= 1);
            Assert.DoesNotContain(shape.rover, candidates);
            Assert.Contains(shape.bill, candidates);
            Assert.False(ParsekScenario.ShouldStripVesselForRecordings(
                RoverPid, RoverGuid, Vessel.Situations.LANDED, candidates,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: RewindContext.RewindQuicksaveVesselPids,
                out _, out _));

            // The member's spawn state resets, so the chain tip spawns again at its end.
            RecordingStore.ResetAllPlaybackState(RewindContext.RewindHistoricalSpawnKeepRecordingIds);
            Assert.Equal(0u, shape.rover.SpawnedVesselPersistentId);
            Assert.False(shape.rover.VesselSpawned);
            Assert.Equal(0u, shape.bill.SpawnedVesselPersistentId);

            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("leave spawned pid=" + RoverPid) && l.Contains("member=r222"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("Rewind strip scope: adjustedUT=")
                && l.Contains("foreignAdoptedPids=1") && l.Contains("keptHistoryPids=0"));
            Assert.DoesNotContain(logLines, l => l.Contains("strip spawned pid=" + RoverPid));
        }

        [Fact]
        public void Eva11Shape_ScopeLeavesRoverAndStillStripsBill()
        {
            var shape = BuildEva11Shape();
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), AdjustedUT, MemberIds());

            Assert.DoesNotContain(RoverPid, scope.StripPids);
            Assert.DoesNotContain(RoverPid, scope.KeptPids);
            Assert.Contains(RoverPid, scope.ForeignAdoptedPids);
            Assert.DoesNotContain(shape.rover.RecordingId, scope.KeptHolderRecordingIds);
            Assert.Contains(BillPid, scope.StripPids);
        }

        // ---- Controls: the same shape without the exemption strips the rover ----

        [Fact]
        public void Control_NoMemberIds_StripsRoverAsBefore()
        {
            BuildEva11Shape();
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), AdjustedUT);

            Assert.Contains(RoverPid, scope.StripPids);
            Assert.Empty(scope.ForeignAdoptedPids);
        }

        [Fact]
        public void Control_TreeBornMember_StillStripped()
        {
            // A member born in the tree (an undock) did not exist before it: stripped.
            BuildEva11Shape(BranchPointType.Undock);
            Assert.Empty(MemberIds());
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), AdjustedUT, MemberIds());

            Assert.Contains(RoverPid, scope.StripPids);
            Assert.Empty(scope.ForeignAdoptedPids);
        }

        [Fact]
        public void OnLoadStrip_WithoutWhitelist_FullListStripsRoverFilteredListKeepsIt()
        {
            // The identity-only fallback (no rewind quicksave pids): the member's own source
            // matches the rover, so only the filtered candidate set keeps it.
            BuildEva11Shape();
            var all = RecordingStore.CollectAllCommittedRecordings();

            Assert.True(ParsekScenario.ShouldStripVesselForRecordings(
                RoverPid, RoverGuid, Vessel.Situations.LANDED, all,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: null,
                out _, out _));

            var candidates = ForeignInventoryTransfer.ExcludeForeignAdoptions(all, MemberIds(), out _);
            Assert.False(ParsekScenario.ShouldStripVesselForRecordings(
                RoverPid, RoverGuid, Vessel.Situations.LANDED, candidates,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: null,
                out _, out string reason));
            Assert.StartsWith("no same-launch", reason);

            // Bill is still a same-launch match.
            Assert.True(ParsekScenario.ShouldStripVesselForRecordings(
                BillPid, BillGuid, Vessel.Situations.LANDED, candidates,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: null,
                out _, out _));
        }

        // ---- Mirror direction: an earlier tree that SPAWNED the vessel ----

        private Recording CommitEarlierSpawnerTree(double startUT, double endUT)
        {
            // An earlier tree whose terminal spawn produced the rover (a KSP-unique spawn pid
            // that differs from its recorded vessel pid), later adopted by Bill's tree.
            var spawner = Rec("s333", "rover fuel 0", "treeA", startUT, endUT, 3300000003u, "c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0");
            spawner.SpawnedVesselPersistentId = RoverPid;
            spawner.VesselSpawned = true;
            var tree = new RecordingTree { Id = "treeA", TreeName = "Rover", RootRecordingId = spawner.RecordingId };
            tree.AddOrReplaceRecording(spawner);
            Commit(tree);
            return spawner;
        }

        [Fact]
        public void EarlierSpawnerHistorical_AdoptingMemberReplay_DoesNotStripTheSpawnedVessel()
        {
            // Rewind of Bill's tree: the spawner is history, and the member that flies the same
            // pid ghosts the rover rather than producing it, so the rover stays.
            var spawner = CommitEarlierSpawnerTree(5.0, 20.0);
            BuildEva11Shape();
            var all = RecordingStore.CollectAllCommittedRecordings();

            Assert.False(RecordingStore.IsSpawnedVesselReproducedByRewindReplay(
                spawner, all, AdjustedUT, out _, MemberIds()));
            Assert.True(RecordingStore.IsSpawnedVesselReproducedByRewindReplay(
                spawner, all, AdjustedUT, out string viaUnscoped));
            Assert.StartsWith("same-vessel-recording-replays:r222", viaUnscoped);

            var scope = RecordingStore.ResolveRewindSpawnStripScope(all, AdjustedUT, MemberIds());
            Assert.DoesNotContain(RoverPid, scope.StripPids);
            Assert.Contains(RoverPid, scope.KeptPids);
            Assert.Contains(spawner.RecordingId, scope.KeptHolderRecordingIds);
        }

        [Fact]
        public void EarlierSpawnerReplays_StillStripsTheSpawnedVessel()
        {
            // Rewind to before the spawner's own flight: it replays and re-spawns the rover, so
            // the rover is stripped whatever the later tree adopted (any holder re-produced).
            var spawner = CommitEarlierSpawnerTree(5.0, 20.0);
            BuildEva11Shape();
            var all = RecordingStore.CollectAllCommittedRecordings();

            var scope = RecordingStore.ResolveRewindSpawnStripScope(all, 2.0, MemberIds());
            Assert.Contains(RoverPid, scope.StripPids);
            Assert.Contains(RoverPid, scope.ForeignAdoptedPids);
            Assert.DoesNotContain(spawner.RecordingId, scope.KeptHolderRecordingIds);
        }

        // ---- The pure helpers ----

        [Fact]
        public void FindAdoptedForeignVesselMember_MatchesOnlySameTreeSamePid()
        {
            var shape = BuildEva11Shape();
            var all = RecordingStore.CollectAllCommittedRecordings();
            var ids = MemberIds();

            Assert.Same(shape.rover, ForeignInventoryTransfer.FindAdoptedForeignVesselMember(shape.rover, all, ids));
            Assert.Null(ForeignInventoryTransfer.FindAdoptedForeignVesselMember(shape.bill, all, ids));

            // A chain tip of the member in the same tree, preserve-identity spawned.
            var tip = Rec("t444", "rover fuel 0", "treeE", 59.9, 80.0, RoverPid, RoverGuid);
            tip.SpawnedVesselPersistentId = RoverPid;
            Assert.Same(shape.rover, ForeignInventoryTransfer.FindAdoptedForeignVesselMember(
                tip, new List<Recording>(all) { tip }, ids));

            // Same pid in another tree: not this tree's adoption.
            var other = Rec("o555", "rover fuel 0", "treeZ", 10.0, 20.0, RoverPid, RoverGuid);
            other.SpawnedVesselPersistentId = RoverPid;
            Assert.Null(ForeignInventoryTransfer.FindAdoptedForeignVesselMember(
                other, new List<Recording>(all) { other }, ids));

            Assert.Null(ForeignInventoryTransfer.FindAdoptedForeignVesselMember(shape.rover, all, null));
        }
    }
}
