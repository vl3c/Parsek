using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CONSTRUCTION-PICKUP-OF-FOREIGN-LOOSE-PART-UNCLAIMED: in EVA construction the recorded
    /// kerbal picks up the last part of a loose DroppedPart / Debris vessel that is NOT a member
    /// of his tree (a part an earlier flight left). The pocket seam joins that vessel to the
    /// tree as his InventoryTransfer member, stamped Disassembled at the pick-up, so a later
    /// rewind to before the pick-up holds the real loose part back (positive launch identity,
    /// evidence <c>flies</c>) and nothing spawns it back. The chain walker's claim on it is a
    /// TERMINATED chain, which holds nothing on its own (FilterAndGhostChains and
    /// RewindVesselHold.CollectChainHolds both skip it, #174), so the hold must come from the
    /// member's recorded launch.
    /// </summary>
    [Collection("Sequential")]
    public class ForeignConstructionPickupTests : IDisposable
    {
        private const uint KerbalPid = 60u;
        private const string KerbalGuid = "60606060606060606060606060606060";
        private const uint LoosePid = 3141592u;
        private const string LooseGuid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        private const string OtherLaunchGuid = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
        private const uint LoosePartPid = 2718281u;
        private const string LoosePart = "solarPanels5";
        private const double LaunchUT = 1000.0;
        private const double PickupUT = 1100.0;

        private readonly List<string> logLines = new List<string>();

        public ForeignConstructionPickupTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
        }

        // The kerbal's own tree: one EVA recording, active, from the launch UT on.
        private static (RecordingTree tree, Recording kerbal) BuildKerbalTree()
        {
            var tree = new RecordingTree { Id = "tree-k", TreeName = "Bill Kerman", RootRecordingId = "kerbal" };
            var kerbal = new Recording
            {
                RecordingId = "kerbal", TreeId = tree.Id, VesselPersistentId = KerbalPid,
                RecordedVesselGuid = KerbalGuid, VesselName = "Bill Kerman", EvaCrewName = "Bill Kerman",
                ExplicitStartUT = LaunchUT, ExplicitEndUT = 1200.0, Generation = 0,
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL"), Points = new List<TrajectoryPoint>()
            };
            tree.AddOrReplaceRecording(kerbal);
            tree.ActiveRecordingId = kerbal.RecordingId;
            return (tree, kerbal);
        }

        private static TrajectoryPoint Pose(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, latitude = -0.097, longitude = -74.557, altitude = 70.0,
                rotation = UnityEngine.Quaternion.identity, bodyName = "Kerbin"
            };
        }

        // The seam's pure half, as TryAdoptForeignConstructionPickup drives it.
        private static Recording PickUp(RecordingTree tree, Recording kerbal)
        {
            var (bp, member) = ForeignInventoryTransfer.BuildMemberBranchData(
                kerbal.RecordingId, tree.Id, PickupUT, LoosePid, "Loose panel", kerbal.Generation);
            member.VesselSnapshot = new ConfigNode("VESSEL");
            member.GhostVisualSnapshot = new ConfigNode("VESSEL");
            member.RecordedVesselGuid = LooseGuid;
            ForeignInventoryTransfer.AttachPickedUpMember(tree, kerbal, bp, member,
                ForeignInventoryTransfer.BuildConstructionPickupSegmentEvent(PickupUT, LoosePart, LoosePartPid),
                Pose(PickupUT), PickupUT);
            return member;
        }

        // ---- the event ----

        [Fact]
        public void BuildConstructionPickupSegmentEvent_IsAPartRemoval_WithInvariantDetails()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                SegmentEvent e = ForeignInventoryTransfer.BuildConstructionPickupSegmentEvent(
                    1100.5, LoosePart, LoosePartPid);
                Assert.Equal(1100.5, e.ut);
                Assert.Equal(SegmentEventType.PartRemoved, e.type);
                Assert.Equal("construction part=solarPanels5 action=pickup pid=2718281", e.details);

                SegmentEvent unnamed = ForeignInventoryTransfer.BuildConstructionPickupSegmentEvent(
                    1101.0, null, 0u);
                Assert.Equal("construction part=unknown action=pickup pid=0", unnamed.details);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---- the gate ----

        [Fact]
        public void ShouldAdoptConstructionPickup_OnlyAPickupTheTransferGateAdopts()
        {
            Assert.True(ForeignInventoryTransfer.ShouldAdoptConstructionPickup(
                VesselDeathKind.Disassembled, ForeignInventoryTransferVerdict.Adopt));

            // An ordinary death of a foreign vessel is never adopted.
            Assert.False(ForeignInventoryTransfer.ShouldAdoptConstructionPickup(
                VesselDeathKind.Destroyed, ForeignInventoryTransferVerdict.Adopt));

            // Every refusal of the transfer gate refuses the pick-up too.
            foreach (ForeignInventoryTransferVerdict v in Enum.GetValues(typeof(ForeignInventoryTransferVerdict)))
            {
                if (v == ForeignInventoryTransferVerdict.Adopt) continue;
                Assert.False(ForeignInventoryTransfer.ShouldAdoptConstructionPickup(
                    VesselDeathKind.Disassembled, v), v.ToString());
            }
        }

        [Fact]
        public void TheGate_RefusesAGhostAndAVesselTheTreeAlreadyCarries()
        {
            var (tree, kerbal) = BuildKerbalTree();
            // A ghost map vessel: refused before membership is read.
            Assert.Equal(ForeignInventoryTransferVerdict.ChangedVesselIsGhost,
                ForeignInventoryTransfer.Evaluate(true, true, false, true, KerbalPid, KerbalPid,
                    LoosePid, changedVesselIsGhost: true,
                    membership: ForeignInventoryTransfer.ResolveMembership(tree, LoosePid)));

            // A recording of the tree that flew the pid (no longer in the BackgroundMap):
            // another tree vessel, left to its own recording.
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "earlier", TreeId = tree.Id, VesselPersistentId = LoosePid,
                ExplicitStartUT = LaunchUT, ExplicitEndUT = 1050.0
            });
            Assert.Equal(ForeignInventoryTransferVerdict.ChangedVesselIsTreeMember,
                ForeignInventoryTransfer.Evaluate(true, true, false, true, KerbalPid, KerbalPid,
                    LoosePid, false, ForeignInventoryTransfer.ResolveMembership(tree, LoosePid)));
        }

        [Fact]
        public void FormatConstructionPickupSkipLog_NamesTheReason()
        {
            string line = ForeignInventoryTransfer.FormatConstructionPickupSkipLog(
                ForeignInventoryTransferVerdict.ActiveVesselNotEva, LoosePid, KerbalPid, LoosePart, LoosePartPid);
            Assert.Equal(
                "Foreign construction pick-up not recorded: reason=ActiveVesselNotEva changedVesselPid=3141592 " +
                "activePid=60 part='solarPanels5' partPid=2718281",
                line);
            Assert.Contains("part='(none)'", ForeignInventoryTransfer.FormatConstructionPickupSkipLog(
                ForeignInventoryTransferVerdict.NotRecording, LoosePid, KerbalPid, null, 0u));
        }

        // ---- the member ----

        [Fact]
        public void AttachPickedUpMember_JoinsTheTree_EndingDisassembledAtThePickup()
        {
            var (tree, kerbal) = BuildKerbalTree();
            Recording member = PickUp(tree, kerbal);

            Assert.True(ForeignInventoryTransfer.IsTransferMember(tree, member));
            Assert.Equal(member.RecordingId, tree.BackgroundMap[LoosePid]);
            Assert.Null(kerbal.ChildBranchPointId);
            Assert.Equal(kerbal.RecordingId, tree.ActiveRecordingId);

            Assert.Equal(TerminalState.Disassembled, member.TerminalStateValue);
            Assert.Equal(PickupUT, member.ExplicitEndUT);
            Assert.Equal(PickupUT, member.StartUT);
            Assert.Equal(PickupUT, member.EndUT);
            // The destroy seam's later handlers key on this flag; the stamp leaves it false.
            Assert.False(member.VesselDestroyed);

            // One trajectory point, so the finalize prune does not drop it as a zero-point leaf.
            Assert.Single(member.Points);
            Assert.False(ParsekFlight.IsZeroPointLeaf(member));

            SegmentEvent e = Assert.Single(member.SegmentEvents);
            Assert.Equal(SegmentEventType.PartRemoved, e.type);
            Assert.Equal(PickupUT, e.ut);

            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Foreign inventory transfer: vessel joined the tree vesselPid=3141592")
                && l.Contains("event=PartRemoved")
                && l.Contains("construction part=solarPanels5 action=pickup pid=2718281"));
        }

        [Fact]
        public void AttachPickedUpMember_WithoutAPose_IsAZeroPointLeaf()
        {
            // Control for the pose: without it the finalize prune would drop the member, and
            // with it the evidence the rewind hold reads.
            var (tree, kerbal) = BuildKerbalTree();
            var (bp, member) = ForeignInventoryTransfer.BuildMemberBranchData(
                kerbal.RecordingId, tree.Id, PickupUT, LoosePid, "Loose panel", kerbal.Generation);
            ForeignInventoryTransfer.AttachPickedUpMember(tree, kerbal, bp, member,
                ForeignInventoryTransfer.BuildConstructionPickupSegmentEvent(PickupUT, LoosePart, LoosePartPid),
                null, PickupUT);
            Assert.True(ParsekFlight.IsZeroPointLeaf(member));
        }

        [Fact]
        public void TheStampLine_CarriesThePickupReason()
        {
            string line = ParsekFlight.FormatDisassembledTerminalLog(
                "Loose panel", LoosePid, 1, "m1", PickupUT, ForeignInventoryTransfer.ConstructionPickupLogReason);
            Assert.Equal(
                "Recording terminal: kind=Disassembled reason=foreign-part-picked-up vessel='Loose panel' " +
                "pid=3141592 parts=1 rec=m1 ut=1100.000",
                line);
        }

        // ---- after a rewind to before the pick-up ----

        [Fact]
        public void RewindToBeforeThePickup_HoldsTheRealLoosePart_AndNothingSpawnsItBack()
        {
            var (tree, kerbal) = BuildKerbalTree();
            Recording member = PickUp(tree, kerbal);
            var committed = new List<Recording> { kerbal, member };

            int matchedWithoutFuture;
            var held = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (LoosePid, LooseGuid), (KerbalPid, KerbalGuid) },
                null, committed, new List<GameAction>(), null, LaunchUT, out matchedWithoutFuture);

            ReFlyInvokeHold.Entry entry = held.Find(h => h.Pid == LoosePid);
            Assert.NotNull(entry);
            Assert.Equal(ReFlyInvokeHold.EvidenceFlies, entry.Evidence);
            Assert.Equal(member.RecordingId, entry.RecordingId);
            // The hold ghost shows the rewind save's loose part until the pick-up.
            Assert.Equal(PickupUT, entry.FirstClaimUT);
            Assert.Contains(member.RecordingId, entry.FlyingRecordingIds);
            Assert.True(RewindSpawnScope.WillReplayAfterRewind(member, LaunchUT));

            // The member ends Disassembled: never a spawnable leaf, so the hold is never undone.
            Assert.False(GhostPlaybackLogic.IsSpawnableTerminal(member.TerminalStateValue.Value));
        }

        [Fact]
        public void Control_WithoutTheMember_TheRewindHoldsNothing()
        {
            // The bug: no recording of the loose part's launch, so the rewind keeps it live
            // beside whatever the replayed kerbal makes of it.
            var (_, kerbal) = BuildKerbalTree();
            int matchedWithoutFuture;
            var held = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (LoosePid, LooseGuid) },
                null, new List<Recording> { kerbal }, new List<GameAction>(), null, LaunchUT,
                out matchedWithoutFuture);
            Assert.Empty(held);
        }

        [Fact]
        public void AnotherLaunchOnTheSamePid_IsNeverHeld()
        {
            // persistentId is craft-baked: a loose part of another launch that carries the same
            // pid is not the picked-up one and stays.
            var (tree, kerbal) = BuildKerbalTree();
            PickUp(tree, kerbal);
            int matchedWithoutFuture;
            var held = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (LoosePid, OtherLaunchGuid) },
                null, new List<Recording>(tree.Recordings.Values), new List<GameAction>(), null,
                LaunchUT, out matchedWithoutFuture);
            Assert.DoesNotContain(held, h => h.Pid == LoosePid);
        }

        [Fact]
        public void ARewindAfterThePickup_HoldsNothing()
        {
            var (tree, kerbal) = BuildKerbalTree();
            PickUp(tree, kerbal);
            int matchedWithoutFuture;
            var held = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (LoosePid, LooseGuid) },
                null, new List<Recording>(tree.Recordings.Values), new List<GameAction>(), null,
                PickupUT + 10.0, out matchedWithoutFuture);
            Assert.Empty(held);
        }

        [Fact]
        public void TheChainClaim_IsTerminated_SoTheHoldComesFromTheMembersLaunch()
        {
            var (tree, kerbal) = BuildKerbalTree();
            Recording member = PickUp(tree, kerbal);

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, LaunchUT, supersedes: null);
            Assert.True(chains.ContainsKey(LoosePid));
            Assert.Equal(member.RecordingId, chains[LoosePid].TipRecordingId);
            Assert.True(chains[LoosePid].IsTerminated);
            Assert.False(chains.ContainsKey(KerbalPid));
            Assert.Empty(RewindVesselHold.CollectChainHolds(chains, LaunchUT, pid => LooseGuid));
        }

        [Fact]
        public void AnEarlierFlightsSpawnedLoosePart_IsKeptForTheHold_NotStripped()
        {
            // The loose part is the end spawn of an earlier flight's dropped-part member (a
            // KSP-unique spawn pid). The pick-up member flies that pid but is a transfer member,
            // so the rewind's strip scope keeps the part in the rewind save for the hold.
            const uint DroppedPid = 777u;
            var earlierTree = new RecordingTree { Id = "tree-a", TreeName = "Earlier", RootRecordingId = "drop" };
            var drop = new Recording
            {
                RecordingId = "drop", TreeId = earlierTree.Id, VesselPersistentId = DroppedPid,
                VesselName = "Loose panel", ExplicitStartUT = 500.0, ExplicitEndUT = 600.0,
                TerminalStateValue = TerminalState.Landed,
                SpawnedVesselPersistentId = LoosePid, VesselSpawned = true
            };
            earlierTree.AddOrReplaceRecording(drop);

            var (tree, kerbal) = BuildKerbalTree();
            PickUp(tree, kerbal);

            var trees = new List<RecordingTree> { earlierTree, tree };
            var all = new List<Recording> { drop };
            all.AddRange(tree.Recordings.Values);
            var scope = RewindSpawnScope.ResolveRewindSpawnStripScope(
                all, LaunchUT, ForeignInventoryTransfer.CollectTransferMemberRecordingIds(trees));

            Assert.DoesNotContain(LoosePid, scope.StripPids);
            Assert.Contains(LoosePid, scope.KeptPids);
        }

        // ---- the live seam's wiring ----

        [Fact]
        public void ThePocketSeam_RoutesAVesselOutsideTheTreeToTheAdoption()
        {
            string terminal = ReadSource("ParsekFlight.TerminalEvents.cs");
            Assert.Contains(
                "if (!activeTree.BackgroundMap.TryGetValue(v.persistentId, out recordingId))\n" +
                "                return TryAdoptForeignConstructionPickup(v);",
                terminal.Replace("\r\n", "\n"));

            string seam = ReadSource("ParsekFlight.ForeignInventoryTransfer.cs");
            Assert.Contains("ForeignInventoryTransfer.ShouldAdoptConstructionPickup(deathKind, verdict)", seam);
            Assert.Contains("GhostMapPresence.IsGhostMapVessel(changedPid)", seam);
            Assert.Contains("ForeignInventoryTransfer.AttachPickedUpMember(", seam);
        }

        private static string ReadSource(string fileName)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = System.IO.Path.Combine(dir, "Source", "Parsek", fileName);
                if (System.IO.File.Exists(candidate))
                    return System.IO.File.ReadAllText(candidate);
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            throw new System.IO.FileNotFoundException(fileName + " not found above " +
                AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
