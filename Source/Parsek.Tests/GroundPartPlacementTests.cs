using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.Analyzer;
using Parsek.Analyzer.Rules;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The EVA ground-part placement tree member (docs/parsek-flight-recorder-design.md
    /// section 4.11; todos EVA-GROUND-SCIENCE-PLACED-PART-PID-NOT-ON-VESSEL and
    /// INVENTORY-PLACED-FIRES-ON-ANY-LANDED-EXPERIMENT-LOAD).
    /// </summary>
    [Collection("Sequential")]
    public class GroundPartPlacementTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const uint KerbalPid = 111u;
        private const uint PlacedVesselPid = 880280830u;
        private const uint PlacedPartPid = 4140861083u;
        private const string PartName = "DeployedSeismicSensor";

        public GroundPartPlacementTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---- the placement gate ----

        private static GroundPartPlacementVerdict Evaluate(
            bool hasActiveTree = true,
            bool recorderIsRecording = true,
            bool activeVesselIsEva = true,
            uint activeVesselPid = KerbalPid,
            uint activeRecordingVesselPid = KerbalPid,
            bool createdVesselPresent = true,
            bool createdThisFrame = true,
            int createdPartCount = 1,
            string createdPartName = PartName,
            string placedPartName = PartName,
            bool createdVesselIsTreeMember = false)
        {
            return GroundPartPlacement.EvaluatePlacement(
                hasActiveTree, recorderIsRecording, activeVesselIsEva, activeVesselPid,
                activeRecordingVesselPid, createdVesselPresent, createdThisFrame,
                createdPartCount, createdPartName, placedPartName, createdVesselIsTreeMember);
        }

        [Fact]
        public void EvaluatePlacement_RealPlacementByTheRecordedKerbal_Records()
        {
            Assert.Equal(GroundPartPlacementVerdict.Record, Evaluate());
        }

        [Fact]
        public void EvaluatePlacement_EachMissingCondition_SkipsWithItsOwnReason()
        {
            Assert.Equal(GroundPartPlacementVerdict.NoActiveTree, Evaluate(hasActiveTree: false));
            Assert.Equal(GroundPartPlacementVerdict.NotRecording, Evaluate(recorderIsRecording: false));
            Assert.Equal(GroundPartPlacementVerdict.ActiveVesselNotEva, Evaluate(activeVesselIsEva: false));
            Assert.Equal(GroundPartPlacementVerdict.ActiveVesselNotRecorded,
                Evaluate(activeRecordingVesselPid: 222u));
            Assert.Equal(GroundPartPlacementVerdict.ActiveVesselNotRecorded,
                Evaluate(activeVesselPid: 0u, activeRecordingVesselPid: 0u));
            Assert.Equal(GroundPartPlacementVerdict.NoCreatedVessel, Evaluate(createdVesselPresent: false));
            Assert.Equal(GroundPartPlacementVerdict.CreatedVesselNotThisFrame, Evaluate(createdThisFrame: false));
            Assert.Equal(GroundPartPlacementVerdict.CreatedVesselNotSinglePart, Evaluate(createdPartCount: 2));
            Assert.Equal(GroundPartPlacementVerdict.PartNameMismatch, Evaluate(createdPartName: "evaChute"));
            Assert.Equal(GroundPartPlacementVerdict.PartNameMismatch, Evaluate(placedPartName: null));
            Assert.Equal(GroundPartPlacementVerdict.AlreadyTreeMember, Evaluate(createdVesselIsTreeMember: true));
        }

        [Fact]
        public void EvaluatePlacement_ExperimentLoadWithoutADeployCall_HasNoCreatedVessel()
        {
            // An old unlinked experiment starting landed fires onGroundSciencePartDeployed,
            // which is not an input at all; with no onDeployGroundPart there is nothing to
            // evaluate, and a stale cached vessel from an earlier frame never matches.
            Assert.Equal(GroundPartPlacementVerdict.CreatedVesselNotThisFrame,
                Evaluate(createdThisFrame: false));
        }

        [Fact]
        public void FormatPlacementSkipLog_NamesReasonAndPids()
        {
            string line = GroundPartPlacement.FormatPlacementSkipLog(
                GroundPartPlacementVerdict.ActiveVesselNotEva, PartName, PlacedVesselPid, KerbalPid);
            Assert.Equal(
                "Ground part placement not recorded: reason=ActiveVesselNotEva part='DeployedSeismicSensor' " +
                "createdVesselPid=880280830 activePid=111 createdParts=0 createdPart='(null)'",
                line);
            Assert.EndsWith("createdParts=2 createdPart='evaChute'", GroundPartPlacement.FormatPlacementSkipLog(
                GroundPartPlacementVerdict.CreatedVesselNotSinglePart, PartName, 1u, 2u, 2, "evaChute"));
        }

        [Fact]
        public void ResolveCreatedPartShape_PrefersTheProtoVesselInThePlacementFrame()
        {
            // The placement frame: the ProtoVessel holds the one PART node, the live list is empty.
            GroundPartPlacement.ResolveCreatedPartShape(1, PartName, 0, null, out int count, out string name);
            Assert.Equal(1, count);
            Assert.Equal(PartName, name);
            Assert.Equal(GroundPartPlacementVerdict.Record,
                Evaluate(createdPartCount: count, createdPartName: name));

            // No proto snapshot: fall back to the live parts.
            GroundPartPlacement.ResolveCreatedPartShape(0, null, 1, "evaChute", out count, out name);
            Assert.Equal(1, count);
            Assert.Equal("evaChute", name);

            GroundPartPlacement.ResolveCreatedPartShape(0, null, 0, "stale", out count, out name);
            Assert.Equal(0, count);
            Assert.Null(name);
        }

        // ---- the tree shape ----

        private static (RecordingTree tree, Recording kerbal) BuildTreeWithKerbal()
        {
            var tree = new RecordingTree { Id = "tree1", TreeName = "Pad" };
            var kerbal = new Recording
            {
                RecordingId = "kerbalRec",
                TreeId = "tree1",
                VesselPersistentId = KerbalPid,
                VesselName = "Jebediah Kerman",
                Generation = 1
            };
            tree.AddOrReplaceRecording(kerbal);
            tree.ActiveRecordingId = kerbal.RecordingId;
            return (tree, kerbal);
        }

        private static (RecordingTree tree, Recording kerbal, Recording member, BranchPoint bp) BuildPlacedTree()
        {
            var (tree, kerbal) = BuildTreeWithKerbal();
            var (bp, member) = GroundPartPlacement.BuildPlacementBranchData(
                kerbal.RecordingId, tree.Id, 100.0, PlacedVesselPid, "Seismic Sensor", kerbal.Generation);
            member.VesselSnapshot = new ConfigNode("VESSEL");
            GroundPartPlacement.AttachMember(tree, kerbal, bp, member, PlacedPartPid, PartName, 100.0);
            return (tree, kerbal, member, bp);
        }

        [Fact]
        public void BuildPlacementBranchData_ParentIsTheKerbal_ChildIsAFreshNonDebrisMember()
        {
            var (bp, member) = GroundPartPlacement.BuildPlacementBranchData(
                "kerbalRec", "tree1", 123.5, PlacedVesselPid, "Seismic Sensor", parentGeneration: 2);

            Assert.Equal(BranchPointType.GroundPartPlaced, bp.Type);
            Assert.Equal(123.5, bp.UT);
            Assert.Equal(new[] { "kerbalRec" }, bp.ParentRecordingIds);
            Assert.Equal(new[] { member.RecordingId }, bp.ChildRecordingIds);
            Assert.Null(bp.SplitCause);
            Assert.Null(bp.MergeCause);

            Assert.Equal("tree1", member.TreeId);
            Assert.Equal(PlacedVesselPid, member.VesselPersistentId);
            Assert.Equal(bp.Id, member.ParentBranchPointId);
            Assert.Equal(123.5, member.ExplicitStartUT);
            Assert.Equal(3, member.Generation);
            Assert.False(member.IsDebris);
            Assert.Null(member.ParentAnchorRecordingId);
            Assert.Null(member.ChildBranchPointId);
        }

        [Fact]
        public void AttachMember_WiresTheTree_AndLeavesTheKerbalRecordingAlone()
        {
            var (tree, kerbal, member, bp) = BuildPlacedTree();

            Assert.Contains(bp, tree.BranchPoints);
            Assert.Same(member, tree.Recordings[member.RecordingId]);
            Assert.Equal(member.RecordingId, tree.BackgroundMap[PlacedVesselPid]);
            // The kerbal keeps recording: no child pointer, still the active recording.
            Assert.Null(kerbal.ChildBranchPointId);
            Assert.Equal(kerbal.RecordingId, tree.ActiveRecordingId);
            // Nothing lands on the kerbal's recording.
            Assert.Empty(kerbal.PartEvents);

            // The Placed event is on the MEMBER, keyed by the placed part's own pid.
            PartEvent placed = Assert.Single(member.PartEvents);
            Assert.Equal(PartEventType.InventoryPartPlaced, placed.eventType);
            Assert.Equal(PlacedPartPid, placed.partPersistentId);
            Assert.Equal(100.0, placed.ut);
            Assert.Equal(PartName, placed.partName);

            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Ground part placed: tree member created part='DeployedSeismicSensor' " +
                              "vesselPid=880280830 partPid=4140861083 rec=" + member.RecordingId +
                              " parentRec=kerbalRec bp=" + bp.Id + " ut=100.00 snapshot=True"));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Part event captured: InventoryPartPlaced 'DeployedSeismicSensor' " +
                              "pid=4140861083 via onDeployGroundPart rec=" + member.RecordingId));
        }

        [Fact]
        public void IsPlacedPartMember_OnlyForChildrenOfAPlacementBranchPoint()
        {
            var (tree, kerbal, member, _) = BuildPlacedTree();
            Assert.True(GroundPartPlacement.IsPlacedPartMember(tree, member));
            Assert.False(GroundPartPlacement.IsPlacedPartMember(tree, kerbal));
            Assert.False(GroundPartPlacement.IsPlacedPartMember(null, member));

            var evaChild = new Recording { RecordingId = "c", ParentBranchPointId = "evaBp" };
            tree.BranchPoints.Add(new BranchPoint { Id = "evaBp", Type = BranchPointType.EVA });
            Assert.False(GroundPartPlacement.IsPlacedPartMember(tree, evaChild));
        }

        [Fact]
        public void SwitchSegmentSubtree_IncludesAPartPlacedByTheSegmentsKerbal()
        {
            // The placed member hangs off a GroundPartPlaced point the kerbal does NOT
            // reference through ChildBranchPointId; scoped Discard and the no-op classifier
            // both read this walk, so a placement must be inside the segment's subtree.
            var (tree, kerbal, member, _) = BuildPlacedTree();
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                TreeId = tree.Id,
                ActiveSegmentRecordingId = kerbal.RecordingId
            };

            HashSet<string> ids = RecordingStore.CollectSwitchSegmentSubtreeRecordingIds(tree, session);

            Assert.Equal(2, ids.Count);
            Assert.Contains(kerbal.RecordingId, ids);
            Assert.Contains(member.RecordingId, ids);
        }

        // ---- the pick-up ----

        [Fact]
        public void HandleRemovedSignal_FirstSignalRecordsOnTheMember_SecondIsDeduplicated()
        {
            var (tree, kerbal, member, _) = BuildPlacedTree();
            var pending = new HashSet<uint>();

            var first = GroundPartPlacement.HandleRemovedSignal(
                tree, PlacedVesselPid, PlacedPartPid, PartName, 130.0, pending);
            var second = GroundPartPlacement.HandleRemovedSignal(
                tree, PlacedVesselPid, PlacedPartPid, PartName, 134.7, pending);

            Assert.Equal(GroundPartPlacement.RemovedSignalOutcome.Recorded, first);
            Assert.Equal(GroundPartPlacement.RemovedSignalOutcome.Deduplicated, second);
            Assert.Contains(PlacedVesselPid, pending);

            var removed = member.PartEvents.Where(e => e.eventType == PartEventType.InventoryPartRemoved).ToList();
            PartEvent only = Assert.Single(removed);
            Assert.Equal(130.0, only.ut);
            Assert.Equal(PlacedPartPid, only.partPersistentId);
            Assert.Empty(kerbal.PartEvents);

            Assert.Single(logLines, l => l.Contains("[Flight]")
                && l.Contains("Part event captured: InventoryPartRemoved 'DeployedSeismicSensor' " +
                              "pid=4140861083 via onGroundSciencePartRemoved rec=" + member.RecordingId));
            Assert.Contains(logLines, l => l.Contains("repeat pick-up signal") && l.Contains("deduplicated"));
        }

        [Fact]
        public void HandleRemovedSignal_ForAVesselOutsideTheTree_RecordsNothing()
        {
            var (tree, kerbal, member, _) = BuildPlacedTree();
            var pending = new HashSet<uint>();

            var outcome = GroundPartPlacement.HandleRemovedSignal(
                tree, 424242u, 99u, PartName, 130.0, pending);

            Assert.Equal(GroundPartPlacement.RemovedSignalOutcome.NotAMember, outcome);
            Assert.Empty(pending);
            Assert.Single(member.PartEvents); // just the Placed event
            Assert.Contains(logLines, l => l.Contains("is not a placed-part tree member, nothing recorded"));
        }

        [Fact]
        public void HandleRemovedSignal_ForANonPlacementMember_RecordsNothing()
        {
            // An EVA-split pod left in the BackgroundMap is a member, but not a placed part.
            var (tree, kerbal) = BuildTreeWithKerbal();
            tree.BranchPoints.Add(new BranchPoint { Id = "evaBp", Type = BranchPointType.EVA });
            var pod = new Recording
            {
                RecordingId = "podRec", TreeId = tree.Id, VesselPersistentId = 500u,
                ParentBranchPointId = "evaBp"
            };
            tree.AddOrReplaceRecording(pod);
            tree.BackgroundMap[500u] = pod.RecordingId;
            var pending = new HashSet<uint>();

            var outcome = GroundPartPlacement.HandleRemovedSignal(tree, 500u, 7u, PartName, 1.0, pending);

            Assert.Equal(GroundPartPlacement.RemovedSignalOutcome.NotAMember, outcome);
            Assert.Empty(pending);
            Assert.Empty(pod.PartEvents);
        }

        [Fact]
        public void AppendPartEventSorted_KeepsStableUtOrder()
        {
            var rec = new Recording { RecordingId = "r" };
            GroundPartPlacement.AppendPartEventSorted(rec,
                GroundPartPlacement.BuildInventoryEvent(PartEventType.InventoryPartRemoved, 50.0, 1u, "a"));
            GroundPartPlacement.AppendPartEventSorted(rec,
                GroundPartPlacement.BuildInventoryEvent(PartEventType.InventoryPartPlaced, 10.0, 1u, "a"));
            Assert.Equal(new[] { 10.0, 50.0 }, rec.PartEvents.Select(e => e.ut).ToArray());
            Assert.Equal("unknown",
                GroundPartPlacement.BuildInventoryEvent(PartEventType.InventoryPartPlaced, 0, 1u, null).partName);
        }

        [Theory]
        // member, removedSeen, deployedOnGround, expected
        [InlineData(true, true, null, true)]      // science part: Removed signal seen
        [InlineData(true, false, false, true)]    // Central Station: RetrievePart cleared deployedOnGround
        [InlineData(true, false, true, false)]    // crashed / destroyed while still deployed
        [InlineData(true, false, null, false)]    // no evidence at all
        [InlineData(false, true, false, false)]   // not a placed-part member: never
        public void IsRetrievalDeath_TruthTable(bool member, bool removedSeen, bool? deployed, bool expected)
        {
            Assert.Equal(expected, GroundPartPlacement.IsRetrievalDeath(member, removedSeen, deployed));
        }

        [Fact]
        public void ApplyDisassembledTerminal_WithRetrievalReason_StampsDisassembledAndEnd()
        {
            var rec = new Recording { RecordingId = "m" };
            ParsekFlight.ApplyDisassembledTerminal(rec, 134.7, GroundPartPlacement.RetrievedTerminalReason);
            Assert.Equal(TerminalState.Disassembled, rec.TerminalStateValue);
            Assert.Equal(134.7, rec.ExplicitEndUT);
            Assert.False(rec.VesselDestroyed);
        }

        [Fact]
        public void FormatDisassembledTerminalLog_CarriesTheRetrievalReason_UnderAnyCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = ParsekFlight.FormatDisassembledTerminalLog(
                    "Seismic Sensor", PlacedVesselPid, 1, "m", 134.75, GroundPartPlacement.RetrievedLogReason);
                Assert.Equal(
                    "Recording terminal: kind=Disassembled reason=ground-part-retrieved vessel='Seismic Sensor' " +
                    "pid=880280830 parts=1 rec=m ut=134.750",
                    line);
                // The pocket default is unchanged.
                Assert.Contains("reason=last-part-stored",
                    ParsekFlight.FormatDisassembledTerminalLog("v", 1u, 1, "r", 1.0));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---- spawn at end ----

        [Fact]
        public void PlacedMember_StillPlacedAtEnd_IsASpawnableLeaf_PickedUpIsNot()
        {
            var (tree, kerbal, member, _) = BuildPlacedTree();

            member.TerminalStateValue = TerminalState.Landed;
            Assert.True(RecordingTree.IsSpawnableLeaf(member));

            member.TerminalStateValue = TerminalState.Disassembled;
            Assert.False(RecordingTree.IsSpawnableLeaf(member));

            // The kerbal is still an ordinary leaf: placement did not take its child slot.
            kerbal.VesselSnapshot = new ConfigNode("VESSEL");
            kerbal.TerminalStateValue = TerminalState.Landed;
            Assert.True(RecordingTree.IsSpawnableLeaf(kerbal));
        }

        [Fact]
        public void AreAllLeavesTerminal_CountsAStillPlacedMemberAsAlive()
        {
            var (tree, kerbal, member, _) = BuildPlacedTree();
            kerbal.TerminalStateValue = TerminalState.Boarded;

            member.TerminalStateValue = TerminalState.Landed;
            Assert.False(RecordingTree.AreAllLeavesTerminal(tree.Recordings, null, false));

            member.TerminalStateValue = TerminalState.Disassembled;
            Assert.True(RecordingTree.AreAllLeavesTerminal(tree.Recordings, null, false));
        }

        // ---- synthetic generator support ----

        private static (RecordingBuilder kerbal, RecordingBuilder part) PlacementBuilders(double? pickedUpUT)
        {
            var kerbal = new RecordingBuilder("Jebediah Kerman")
                .WithRecordingId("kerbalRec")
                .WithVesselPersistentId(KerbalPid)
                .AddPoint(100, -0.0972, -74.5575, 70)
                .AddPoint(160, -0.0972, -74.5575, 70);
            var part = new RecordingBuilder("Seismic Sensor")
                .WithRecordingId("memberRec")
                .WithVesselPersistentId(PlacedVesselPid)
                .WithVesselSnapshot(new VesselSnapshotBuilder()
                    .WithName("Seismic Sensor")
                    .WithPersistentId(PlacedVesselPid)
                    .AsLanded(-0.0971, -74.5574, 70)
                    .AddPart(PartName))
                .AddPoint(110, -0.0971, -74.5574, 70)
                .AddPoint(150, -0.0971, -74.5574, 70)
                // VesselSnapshotBuilder gives the single part pid 100000.
                .AsPlacedGroundPart(100000u, PartName, placedUT: 110, pickedUpUT: pickedUpUT);
            return (kerbal, part);
        }

        [Fact]
        public void Generators_AuthorThePlacementTreeShape_AndItSurvivesTheTreeCodec()
        {
            var (kerbal, part) = PlacementBuilders(pickedUpUT: null);
            RecordingTree tree = ScenarioWriter.MaterializeTree(
                new[] { kerbal, part }, "kerbalRec",
                new[] { ScenarioWriter.GroundPartPlacedBranch("bpPlace", "kerbalRec", "memberRec", 110) });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            RecordingTree loaded = RecordingTree.Load(node);

            BranchPoint bp = Assert.Single(loaded.BranchPoints);
            Assert.Equal(BranchPointType.GroundPartPlaced, bp.Type);
            Recording member = loaded.Recordings["memberRec"];
            Recording kerbalRec = loaded.Recordings["kerbalRec"];
            Assert.Equal("bpPlace", member.ParentBranchPointId);
            Assert.Null(kerbalRec.ChildBranchPointId);
            Assert.True(GroundPartPlacement.IsPlacedPartMember(loaded, member));
            Assert.False(member.IsDebris);
        }

        [Fact]
        public void Generators_PlacedPartEventsResolveAgainstTheMembersOwnSnapshot()
        {
            var (_, part) = PlacementBuilders(pickedUpUT: 140);
            var rec = new Recording
            {
                RecordingId = "memberRec",
                VesselSnapshot = part.GetVesselSnapshot()
            };
            RecordingStore.DeserializePartEvents(part.BuildTrajectoryNode(), rec);

            Assert.Equal(
                new[] { PartEventType.InventoryPartPlaced, PartEventType.InventoryPartRemoved },
                rec.PartEvents.Select(e => e.eventType).ToArray());
            Assert.Equal(((int)TerminalState.Disassembled), part.GetTerminalState());

            var findings = new Inv4PartEventPid().Evaluate(new AnalyzerModel
            {
                SaveName = "placement",
                Recordings = new List<Recording> { rec }
            }).ToList();
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
        }

        // ---- the enum member's consumers ----

        [Fact]
        public void GroundPartPlaced_RoundTripsThroughTheBranchPointCodec()
        {
            var bp = new BranchPoint
            {
                Id = "bp_place",
                UT = 100.0,
                Type = BranchPointType.GroundPartPlaced,
                ParentRecordingIds = new List<string> { "kerbalRec" },
                ChildRecordingIds = new List<string> { "memberRec" }
            };
            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);
            Assert.Equal("9", node.GetValue("type"));

            BranchPoint restored = RecordingTree.LoadBranchPointFrom(node);
            Assert.Equal(BranchPointType.GroundPartPlaced, restored.Type);
            Assert.Equal(new[] { "kerbalRec" }, restored.ParentRecordingIds);
            Assert.Equal(new[] { "memberRec" }, restored.ChildRecordingIds);
        }

        [Fact]
        public void GroundPartPlaced_IsNonClaiming_AndReadsPlaced()
        {
            Assert.False(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.GroundPartPlaced));
            Assert.DoesNotContain(logLines, l => l.Contains("unknown BranchPointType"));
            Assert.Equal("Placed",
                MissionCompositionBuilder.BranchEventName(BranchPointType.GroundPartPlaced, null));
        }
    }
}
