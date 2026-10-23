using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A stored part moved between the recorded EVA kerbal and a FOREIGN vessel's inventory
    /// container (docs/parsek-flight-recorder-design.md section 4.12; todo
    /// EVA-INVENTORY-MOVE-TO-FOREIGN-VESSEL-UNCLAIMED). The foreign vessel joins the tree as
    /// a background member whose own recording carries the transfer, so the ghost chain
    /// walker claims it and its own post-transfer recording is the chain tip.
    /// </summary>
    [Collection("Sequential")]
    public class ForeignInventoryTransferTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const uint LanderPid = 50u;
        private const uint KerbalPid = 60u;
        private const uint RoverPid = 900u;
        private const uint ContainerPartPid = 4242u;
        private const string CargoPart = "evaRepairKit";

        public ForeignInventoryTransferTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekInventoryWrites.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekInventoryWrites.ResetForTesting();
        }

        // ---- the gate ----

        private static ForeignInventoryTransferVerdict Evaluate(
            bool hasActiveTree = true,
            bool recorderIsRecording = true,
            bool parsekWriteInProgress = false,
            bool activeVesselIsEva = true,
            uint activeVesselPid = KerbalPid,
            uint activeRecordingVesselPid = KerbalPid,
            uint changedVesselPid = RoverPid,
            bool changedVesselIsGhost = false,
            ForeignInventoryMembership membership = ForeignInventoryMembership.None)
        {
            return ForeignInventoryTransfer.Evaluate(
                hasActiveTree, recorderIsRecording, parsekWriteInProgress, activeVesselIsEva,
                activeVesselPid, activeRecordingVesselPid, changedVesselPid,
                changedVesselIsGhost, membership);
        }

        [Fact]
        public void Evaluate_RecordedKerbalChangesAForeignContainer_Adopts()
        {
            Assert.Equal(ForeignInventoryTransferVerdict.Adopt, Evaluate());
        }

        [Fact]
        public void Evaluate_AnEarlierCargoPartner_AppendsToItsMember()
        {
            Assert.Equal(ForeignInventoryTransferVerdict.AppendToMember,
                Evaluate(membership: ForeignInventoryMembership.TransferMember));
        }

        [Fact]
        public void Evaluate_EachMissingCondition_SkipsWithItsOwnReason()
        {
            Assert.Equal(ForeignInventoryTransferVerdict.NoActiveTree, Evaluate(hasActiveTree: false));
            Assert.Equal(ForeignInventoryTransferVerdict.NotRecording, Evaluate(recorderIsRecording: false));
            Assert.Equal(ForeignInventoryTransferVerdict.ParsekWrite, Evaluate(parsekWriteInProgress: true));
            Assert.Equal(ForeignInventoryTransferVerdict.ActiveVesselNotEva, Evaluate(activeVesselIsEva: false));
            Assert.Equal(ForeignInventoryTransferVerdict.ActiveVesselNotRecorded,
                Evaluate(activeRecordingVesselPid: 77u));
            Assert.Equal(ForeignInventoryTransferVerdict.ActiveVesselNotRecorded,
                Evaluate(activeVesselPid: 0u, activeRecordingVesselPid: 0u));
            Assert.Equal(ForeignInventoryTransferVerdict.NoChangedVessel, Evaluate(changedVesselPid: 0u));
            // The kerbal's own inventory: his own snapshot carries it.
            Assert.Equal(ForeignInventoryTransferVerdict.ChangedVesselIsActive,
                Evaluate(changedVesselPid: KerbalPid));
            Assert.Equal(ForeignInventoryTransferVerdict.ChangedVesselIsGhost,
                Evaluate(changedVesselIsGhost: true));
            // The kerbal's lander (a tree member): its own recording's end snapshot carries it.
            Assert.Equal(ForeignInventoryTransferVerdict.ChangedVesselIsTreeMember,
                Evaluate(membership: ForeignInventoryMembership.OtherMember));
        }

        [Fact]
        public void Evaluate_ARouteWriteNeverRecords_EvenForAnAdoptedMember()
        {
            Assert.Equal(ForeignInventoryTransferVerdict.ParsekWrite,
                Evaluate(parsekWriteInProgress: true,
                    membership: ForeignInventoryMembership.TransferMember));
        }

        [Fact]
        public void ParsekInventoryWrites_NestsAndClearsOnDispose()
        {
            Assert.False(ParsekInventoryWrites.InProgress);
            using (ParsekInventoryWrites.Begin())
            {
                Assert.True(ParsekInventoryWrites.InProgress);
                using (ParsekInventoryWrites.Begin())
                    Assert.True(ParsekInventoryWrites.InProgress);
                Assert.True(ParsekInventoryWrites.InProgress);
            }
            Assert.False(ParsekInventoryWrites.InProgress);
        }

        // ---- the event ----

        [Theory]
        [InlineData(1, 0, SegmentEventType.PartRemoved)]   // took the only one
        [InlineData(3, 2, SegmentEventType.PartRemoved)]   // took one from a stack
        [InlineData(0, 1, SegmentEventType.PartAdded)]     // put one in an empty slot
        [InlineData(2, 3, SegmentEventType.PartAdded)]     // stacked one more
        [InlineData(null, 0, SegmentEventType.PartRemoved)] // first sight, slot now empty
        [InlineData(null, 2, SegmentEventType.PartAdded)]   // first sight, slot occupied
        public void ClassifySlotChange_ComparesQuantities(
            int? before, int after, SegmentEventType expected)
        {
            Assert.Equal(expected, ForeignInventoryTransfer.ClassifySlotChange(before, after));
        }

        [Fact]
        public void TryReadStoredSlot_ReadsTheSavedStockLayout()
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleInventoryPart");
            ConfigNode stored = module.AddNode("STOREDPARTS");
            ConfigNode a = stored.AddNode("STOREDPART");
            a.AddValue("slotIndex", "0");
            a.AddValue("partName", CargoPart);
            a.AddValue("quantity", "3");
            ConfigNode b = stored.AddNode("STOREDPART");
            b.AddValue("slotIndex", "4");
            b.AddValue("partName", "solarPanels5");
            b.AddValue("quantity", "1");

            Assert.True(ForeignInventoryTransfer.TryReadStoredSlot(module, 0, out string n0, out int q0));
            Assert.Equal(CargoPart, n0);
            Assert.Equal(3, q0);
            Assert.True(ForeignInventoryTransfer.TryReadStoredSlot(module, 4, out string n4, out int q4));
            Assert.Equal("solarPanels5", n4);
            Assert.Equal(1, q4);

            // A readable module with nothing in the slot: empty before the change.
            Assert.True(ForeignInventoryTransfer.TryReadStoredSlot(module, 2, out string n2, out int q2));
            Assert.Null(n2);
            Assert.Equal(0, q2);
            Assert.True(ForeignInventoryTransfer.TryReadStoredSlot(
                new ConfigNode("MODULE"), 0, out _, out int qEmpty));
            Assert.Equal(0, qEmpty);

            // No node: unknown.
            Assert.False(ForeignInventoryTransfer.TryReadStoredSlot(null, 0, out _, out _));
        }

        [Fact]
        public void BuildTransferSegmentEvent_IsAGhostingTrigger_WithInvariantDetails()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                SegmentEvent e = ForeignInventoryTransfer.BuildTransferSegmentEvent(
                    1100.25, SegmentEventType.PartRemoved, CargoPart, 3, 0, ContainerPartPid);

                Assert.Equal(1100.25, e.ut);
                Assert.Equal(SegmentEventType.PartRemoved, e.type);
                Assert.Equal("inventory part=evaRepairKit slot=3 qty=0 container=4242", e.details);
                Assert.True(GhostingTriggerClassifier.IsGhostingSegmentEvent(e.type));

                SegmentEvent unnamed = ForeignInventoryTransfer.BuildTransferSegmentEvent(
                    1.0, SegmentEventType.PartAdded, null, 0, 1, ContainerPartPid);
                Assert.Equal("inventory part=unknown slot=0 qty=1 container=4242", unnamed.details);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---- the tree shape ----

        // Lander (root) -> EVA -> [lander continuation, kerbal]; the kerbal is active.
        private static (RecordingTree tree, Recording lander, Recording kerbal) BuildEvaTree()
        {
            var tree = new RecordingTree { Id = "tree-eva", TreeName = "Lander", RootRecordingId = "lander" };
            var lander = new Recording
            {
                RecordingId = "lander", TreeId = tree.Id, VesselPersistentId = LanderPid,
                VesselName = "Lander", ExplicitStartUT = 1000, ExplicitEndUT = 1050,
                ChildBranchPointId = "bp-eva", Points = new List<TrajectoryPoint>()
            };
            var landerCont = new Recording
            {
                RecordingId = "lander-cont", TreeId = tree.Id, VesselPersistentId = LanderPid,
                VesselName = "Lander", ExplicitStartUT = 1050, ExplicitEndUT = 1300,
                ParentBranchPointId = "bp-eva", TerminalStateValue = TerminalState.Landed,
                Points = new List<TrajectoryPoint>()
            };
            var kerbal = new Recording
            {
                RecordingId = "kerbal", TreeId = tree.Id, VesselPersistentId = KerbalPid,
                VesselName = "Jebediah Kerman", EvaCrewName = "Jebediah Kerman",
                ExplicitStartUT = 1050, ExplicitEndUT = 1250, ParentBranchPointId = "bp-eva",
                TerminalStateValue = TerminalState.Landed, Generation = 1,
                VesselSnapshot = new ConfigNode("VESSEL"), Points = new List<TrajectoryPoint>()
            };
            tree.AddOrReplaceRecording(lander);
            tree.AddOrReplaceRecording(landerCont);
            tree.AddOrReplaceRecording(kerbal);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-eva", UT = 1050, Type = BranchPointType.EVA, SplitCause = "EVA",
                ParentRecordingIds = new List<string> { "lander" },
                ChildRecordingIds = new List<string> { "lander-cont", "kerbal" }
            });
            tree.BackgroundMap[LanderPid] = "lander-cont";
            tree.ActiveRecordingId = "kerbal";
            return (tree, lander, kerbal);
        }

        // The kerbal took a part from the foreign rover's container at UT 1100.
        private static (RecordingTree tree, Recording kerbal, Recording member, BranchPoint bp) BuildTransferTree()
        {
            var (tree, _, kerbal) = BuildEvaTree();
            var (bp, member) = ForeignInventoryTransfer.BuildMemberBranchData(
                kerbal.RecordingId, tree.Id, 1100.0, RoverPid, "Rover", kerbal.Generation);
            member.VesselSnapshot = new ConfigNode("VESSEL");
            member.Points = new List<TrajectoryPoint>();
            ForeignInventoryTransfer.AttachMember(tree, kerbal, bp, member,
                ForeignInventoryTransfer.BuildTransferSegmentEvent(
                    1100.0, SegmentEventType.PartRemoved, CargoPart, 0, 0, ContainerPartPid));
            return (tree, kerbal, member, bp);
        }

        [Fact]
        public void BuildMemberBranchData_ParentIsTheKerbal_ChildIsTheForeignVessel()
        {
            var (bp, member) = ForeignInventoryTransfer.BuildMemberBranchData(
                "kerbal", "tree-eva", 1100.5, RoverPid, "Rover", parentGeneration: 2);

            Assert.Equal(BranchPointType.InventoryTransfer, bp.Type);
            Assert.Equal(1100.5, bp.UT);
            Assert.Equal(new[] { "kerbal" }, bp.ParentRecordingIds);
            Assert.Equal(new[] { member.RecordingId }, bp.ChildRecordingIds);
            Assert.Equal(0u, bp.TargetVesselPersistentId);
            Assert.Null(bp.SplitCause);
            Assert.Null(bp.MergeCause);

            Assert.Equal("tree-eva", member.TreeId);
            Assert.Equal(RoverPid, member.VesselPersistentId);
            Assert.Equal("Rover", member.VesselName);
            Assert.Equal(bp.Id, member.ParentBranchPointId);
            Assert.Equal(1100.5, member.ExplicitStartUT);
            Assert.Equal(3, member.Generation);
            Assert.False(member.IsDebris);
            Assert.Null(member.ParentAnchorRecordingId);
            Assert.Null(member.ChildBranchPointId);
            Assert.Null(member.EvaCrewName);
        }

        [Fact]
        public void AttachMember_WiresTheTree_AndLeavesTheKerbalRecordingAlone()
        {
            var (tree, kerbal, member, bp) = BuildTransferTree();

            Assert.Contains(bp, tree.BranchPoints);
            Assert.Same(member, tree.Recordings[member.RecordingId]);
            Assert.Equal(member.RecordingId, tree.BackgroundMap[RoverPid]);
            Assert.Null(kerbal.ChildBranchPointId);
            Assert.Equal(kerbal.RecordingId, tree.ActiveRecordingId);
            Assert.Empty(kerbal.SegmentEvents);

            SegmentEvent e = Assert.Single(member.SegmentEvents);
            Assert.Equal(SegmentEventType.PartRemoved, e.type);
            Assert.Equal(1100.0, e.ut);
            Assert.True(ForeignInventoryTransfer.IsTransferMember(tree, member));
            Assert.False(ForeignInventoryTransfer.IsTransferMember(tree, kerbal));
            Assert.False(ForeignInventoryTransfer.IsTransferMember(null, member));

            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Foreign inventory transfer: vessel joined the tree vesselPid=900 name='Rover' " +
                              "rec=" + member.RecordingId + " parentRec=kerbal bp=" + bp.Id +
                              " ut=1100.00 event=PartRemoved"));
        }

        [Fact]
        public void AppendTransferEvent_KeepsStableUtOrder()
        {
            var (_, _, member, _) = BuildTransferTree();
            ForeignInventoryTransfer.AppendTransferEvent(member,
                ForeignInventoryTransfer.BuildTransferSegmentEvent(
                    1150.0, SegmentEventType.PartAdded, CargoPart, 1, 1, ContainerPartPid));
            ForeignInventoryTransfer.AppendTransferEvent(member,
                ForeignInventoryTransfer.BuildTransferSegmentEvent(
                    1120.0, SegmentEventType.PartAdded, CargoPart, 2, 1, ContainerPartPid));

            Assert.Equal(new[] { 1100.0, 1120.0, 1150.0 }, member.SegmentEvents.Select(e => e.ut).ToArray());
        }

        [Fact]
        public void ResolveMembership_ForeignCargoPartnerOrOtherTreeVessel()
        {
            var (tree, kerbal, member, _) = BuildTransferTree();

            Assert.Equal(ForeignInventoryMembership.TransferMember,
                ForeignInventoryTransfer.ResolveMembership(tree, RoverPid));
            Assert.Equal(ForeignInventoryMembership.OtherMember,
                ForeignInventoryTransfer.ResolveMembership(tree, LanderPid));
            Assert.Equal(ForeignInventoryMembership.OtherMember,
                ForeignInventoryTransfer.ResolveMembership(tree, KerbalPid));
            Assert.Equal(ForeignInventoryMembership.None,
                ForeignInventoryTransfer.ResolveMembership(tree, 12345u));
            Assert.Equal(ForeignInventoryMembership.None,
                ForeignInventoryTransfer.ResolveMembership(null, RoverPid));
        }

        [Fact]
        public void KerbalWhoMovedCargo_StaysTheLeafOfHisOwnFlight()
        {
            // A hand-built InventoryTransfer branch point: the kerbal is its parent while he
            // keeps recording, so the #114 safety net must skip it exactly as it skips a
            // ground-part placement, or the kerbal would never spawn.
            var (tree, _, kerbal) = BuildEvaTree();
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-cargo", UT = 1100, Type = BranchPointType.InventoryTransfer,
                ParentRecordingIds = new List<string> { kerbal.RecordingId },
                ChildRecordingIds = new List<string> { "rover" }
            });

            Assert.False(GhostPlaybackLogic.IsNonLeafInTree(kerbal, tree));
            Assert.True(GhostPlaybackLogic.IsFinalSpawnSegment(kerbal, tree));
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(kerbal, false, tree);
            Assert.True(needsSpawn, reason);
            Assert.True(RecordingTree.IsKerbalSideBranch(BranchPointType.InventoryTransfer));
            Assert.True(RecordingTree.IsKerbalSideBranch(BranchPointType.GroundPartPlaced));
            Assert.False(RecordingTree.IsKerbalSideBranch(BranchPointType.EVA));
            Assert.False(RecordingTree.IsKerbalSideBranch(BranchPointType.Board));
        }

        [Fact]
        public void SwitchSegmentSubtree_IncludesTheKerbalsCargoPartner()
        {
            var (tree, kerbal, member, _) = BuildTransferTree();
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                TreeId = tree.Id,
                ActiveSegmentRecordingId = kerbal.RecordingId
            };

            HashSet<string> ids = RecordingStore.CollectSwitchSegmentSubtreeRecordingIds(tree, session);

            Assert.Contains(kerbal.RecordingId, ids);
            Assert.Contains(member.RecordingId, ids);
        }

        // ---- the ghost chain ----

        [Fact]
        public void ChainWalker_ClaimsTheForeignVessel_AndItsOwnRecordingIsTheTip()
        {
            var (tree, kerbal, member, _) = BuildTransferTree();
            member.ExplicitEndUT = 1300;
            member.TerminalStateValue = TerminalState.Landed;

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900.0, supersedes: null);

            Assert.True(chains.ContainsKey(RoverPid));
            GhostChain chain = chains[RoverPid];
            // The tip supplies the spawn snapshot: the rover's own post-transfer recording,
            // never the kerbal's (that would replace the rover by the kerbal at the tip).
            Assert.Equal(member.RecordingId, chain.TipRecordingId);
            Assert.Equal(tree.Id, chain.TipTreeId);
            Assert.Equal(1300.0, chain.SpawnUT);
            Assert.False(chain.IsTerminated);
            Assert.All(chain.Links, l => Assert.Equal(member.RecordingId, l.recordingId));
            Assert.Equal(1100.0, chain.GhostStartUT);

            // Neither tree vessel is claimed, and the kerbal still spawns as himself.
            Assert.False(chains.ContainsKey(KerbalPid));
            Assert.False(chains.ContainsKey(LanderPid));
            Assert.Null(GhostChainWalker.FindIntermediateLinkChain(chains, kerbal));
            Assert.Null(GhostChainWalker.FindIntermediateLinkChain(chains, member));
        }

        // ---- EVA construction on a foreign vessel (EVA-CONSTRUCTION-ON-FOREIGN-VESSEL-UNCLAIMED) ----

        private const uint BuiltPartPid = 7777u;
        private const string BuiltPart = "solarPanels5";

        [Fact]
        public void BuildConstructionSegmentEvent_AttachAddsDetachRemoves_WithInvariantDetails()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                SegmentEvent attach = ForeignInventoryTransfer.BuildConstructionSegmentEvent(
                    1200.5, attached: true, partName: BuiltPart, partPid: BuiltPartPid);
                Assert.Equal(1200.5, attach.ut);
                Assert.Equal(SegmentEventType.PartAdded, attach.type);
                Assert.Equal("construction part=solarPanels5 action=attach pid=7777", attach.details);
                Assert.True(GhostingTriggerClassifier.IsGhostingSegmentEvent(attach.type));

                SegmentEvent detach = ForeignInventoryTransfer.BuildConstructionSegmentEvent(
                    1201.0, attached: false, partName: null, partPid: 0u);
                Assert.Equal(SegmentEventType.PartRemoved, detach.type);
                Assert.Equal("construction part=unknown action=detach pid=0", detach.details);
                Assert.True(GhostingTriggerClassifier.IsGhostingSegmentEvent(detach.type));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void FormatConstructionSkipLog_NamesTheReasonAndTheAction()
        {
            string line = ForeignInventoryTransfer.FormatConstructionSkipLog(
                ForeignInventoryTransferVerdict.ChangedVesselIsTreeMember, attached: false,
                changedVesselPid: LanderPid, activeVesselPid: KerbalPid,
                partName: BuiltPart, partPid: BuiltPartPid);
            Assert.Equal(
                "Foreign construction change not recorded: reason=ChangedVesselIsTreeMember action=detach " +
                "changedVesselPid=50 activePid=60 part='solarPanels5' partPid=7777",
                line);
        }

        // A part the recorded kerbal attached to a parked rover in EVA construction adopts the
        // rover exactly as an inventory transfer does: the rover joins the tree under an
        // InventoryTransfer branch point, the chain walker claims it through the member's own
        // PartAdded event, and the member is the tip (it spawns in its built form).
        [Fact]
        public void ConstructionAttach_ClaimsTheForeignVessel_AndItsOwnRecordingIsTheTip()
        {
            var (tree, _, kerbal) = BuildEvaTree();
            var (bp, member) = ForeignInventoryTransfer.BuildMemberBranchData(
                kerbal.RecordingId, tree.Id, 1100.0, RoverPid, "Rover", kerbal.Generation);
            member.VesselSnapshot = new ConfigNode("VESSEL");
            member.Points = new List<TrajectoryPoint>();
            ForeignInventoryTransfer.AttachMember(tree, kerbal, bp, member,
                ForeignInventoryTransfer.BuildConstructionSegmentEvent(
                    1100.0, attached: true, partName: BuiltPart, partPid: BuiltPartPid));
            member.ExplicitEndUT = 1300;
            member.TerminalStateValue = TerminalState.Landed;

            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Foreign inventory transfer: vessel joined the tree")
                && l.Contains("event=PartAdded")
                && l.Contains("construction part=solarPanels5 action=attach"));

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900.0, supersedes: null);
            Assert.True(chains.ContainsKey(RoverPid));
            Assert.Equal(member.RecordingId, chains[RoverPid].TipRecordingId);
            Assert.Equal(1100.0, chains[RoverPid].GhostStartUT);
            Assert.False(chains.ContainsKey(KerbalPid));

            // A later detach from the same rover appends to its member and keeps UT order.
            ForeignInventoryTransfer.AppendTransferEvent(member,
                ForeignInventoryTransfer.BuildConstructionSegmentEvent(
                    1150.0, attached: false, partName: BuiltPart, partPid: BuiltPartPid));
            Assert.Equal(
                new[] { SegmentEventType.PartAdded, SegmentEventType.PartRemoved },
                member.SegmentEvents.Select(e => e.type).ToArray());
            Assert.Equal(ForeignInventoryMembership.TransferMember,
                ForeignInventoryTransfer.ResolveMembership(tree, RoverPid));
        }

        // ---- the enum member's consumers ----

        [Fact]
        public void InventoryTransfer_RoundTripsThroughTheBranchPointCodec()
        {
            var bp = new BranchPoint
            {
                Id = "bp_cargo",
                UT = 1100.0,
                Type = BranchPointType.InventoryTransfer,
                ParentRecordingIds = new List<string> { "kerbal" },
                ChildRecordingIds = new List<string> { "rover" }
            };
            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);
            Assert.Equal("10", node.GetValue("type"));

            BranchPoint restored = RecordingTree.LoadBranchPointFrom(node);
            Assert.Equal(BranchPointType.InventoryTransfer, restored.Type);
            Assert.Equal(new[] { "kerbal" }, restored.ParentRecordingIds);
            Assert.Equal(new[] { "rover" }, restored.ChildRecordingIds);
        }

        [Fact]
        public void InventoryTransfer_IsNonClaiming_AndReadsCargo()
        {
            // The claim comes from the member's own SegmentEvent (background-event scan);
            // the branch point itself names no target vessel.
            Assert.False(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.InventoryTransfer));
            Assert.DoesNotContain(logLines, l => l.Contains("unknown BranchPointType"));
            Assert.Equal("Cargo",
                MissionCompositionBuilder.BranchEventName(BranchPointType.InventoryTransfer, null));
        }

        [Fact]
        public void Generators_AuthorTheTransferTreeShape_AndItSurvivesTheTreeCodec()
        {
            var kerbal = new RecordingBuilder("Jebediah Kerman")
                .WithRecordingId("kerbalRec")
                .WithVesselPersistentId(KerbalPid)
                .AddPoint(100, -0.0972, -74.5575, 70)
                .AddPoint(160, -0.0972, -74.5575, 70);
            var rover = new RecordingBuilder("Rover")
                .WithRecordingId("roverRec")
                .WithVesselPersistentId(RoverPid)
                .AddPoint(110, -0.0971, -74.5574, 70)
                .AddPoint(150, -0.0971, -74.5574, 70)
                .AsForeignCargoPartner(110, SegmentEventType.PartRemoved, CargoPart);
            RecordingTree tree = ScenarioWriter.MaterializeTree(
                new[] { kerbal, rover }, "kerbalRec",
                new[] { ScenarioWriter.InventoryTransferBranch("bpCargo", "kerbalRec", "roverRec", 110) });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            RecordingTree loaded = RecordingTree.Load(node);

            BranchPoint bp = Assert.Single(loaded.BranchPoints);
            Assert.Equal(BranchPointType.InventoryTransfer, bp.Type);
            Assert.Equal("bpCargo", loaded.Recordings["roverRec"].ParentBranchPointId);
            Assert.Null(loaded.Recordings["kerbalRec"].ChildBranchPointId);
            Assert.True(ForeignInventoryTransfer.IsTransferMember(loaded, loaded.Recordings["roverRec"]));

            // The transfer event is written to the trajectory node and read back.
            var rec = new Recording { RecordingId = "roverRec" };
            RecordingStore.DeserializeSegmentEvents(rover.BuildTrajectoryNode(), rec.SegmentEvents);
            SegmentEvent e = Assert.Single(rec.SegmentEvents);
            Assert.Equal(SegmentEventType.PartRemoved, e.type);
            Assert.Equal(110.0, e.ut);
            Assert.StartsWith("inventory part=" + CargoPart, e.details);
        }
    }
}
