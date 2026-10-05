using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REFLY-CONCLUSION-FINALIZES-COMMITTED-CHAIN-HEAD. Shape of CI-2 on bdock-recorded: the
    /// Re-Fly tree (spliced from the committed tree) carries the first launch's Kerbal X as an
    /// optimizer chain HEAD (no terminal, no child branch point) plus its chain TAIL (Orbiting,
    /// parent of the JointBreak that made the probe). The second tree docked into that same
    /// Kerbal X and undocked it; its tip recording's chain-tip spawn re-creates the vessel with
    /// the SAME craft-baked pid and the SAME launch guid. At the conclusion's scene exit the
    /// head used to match that spawned vessel by pid, read Destroyed off its live orbit at the
    /// jump UT and carry Dead crew into the merge.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyConclusionChainHeadFinalizeTests : IDisposable
    {
        private const uint KerbalXPid = 3620499050;
        private const uint ProbePid = 3702669050;
        private const string KerbalXGuid = "97813bb6000040008000000000000001";
        private const string OtherLaunchGuid = "270d1da5000040008000000000000002";
        private const string ChainId = "73f4a80bcb724bfeb7d6a91ce0a5c91e";
        private const double CutUT = 196.25988220214066;
        private const double TailEndUT = 386.9117108154017;
        private const double CommitUT = 8960.24;

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> refreshedSnapshotIds = new List<string>();
        private readonly List<string> sceneExitFinalizerIds = new List<string>();

        public ReFlyConclusionChainHeadFinalizeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            // Mirrors the flight: a no-solver live-orbit fallback over the spawned Kerbal X
            // classifies Destroyed at the jump UT; nothing else is finalized here.
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT,
                    out IncompleteBallisticFinalizationResult result) =>
                {
                    sceneExitFinalizerIds.Add(recording.RecordingId);
                    result = default(IncompleteBallisticFinalizationResult);
                    if (ReferenceEquals(vessel, null) || vessel.persistentId != KerbalXPid)
                        return false;
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = commitUT,
                    };
                    return true;
                };
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            CrewReservationManager.ResetReplacementsForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // The repro: the whole conclusion finalize over the Re-Fly tree
        // ------------------------------------------------------------------

        // catches: the defect itself - the committed chain head stamped Destroyed at the jump UT
        // through the other tree's chain-tip spawn, with its crew read Dead.
        [Fact]
        public void ReFlyConclusion_CommittedChainHead_KeepsItsCommittedEnd()
        {
            var fx = BuildReFlyTree();
            AddLaterChainTipOwner(KerbalXGuid);

            ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                fx.Tree, CommitUT, isSceneExit: true,
                findVesselByPid: fx.FindVessel,
                liveVesselAccess: LiveAccess());

            Assert.False(fx.Head.TerminalStateValue.HasValue);
            Assert.Equal(CutUT, fx.Head.EndUT, 6);
            Assert.DoesNotContain(fx.Head.RecordingId, sceneExitFinalizerIds);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("rec='" + fx.Head.RecordingId + "'")
                && l.Contains("leaf=False"));

            // The tail keeps its committed terminal and is not re-snapshotted from the
            // spawned vessel's state at the jump UT.
            Assert.Equal(TerminalState.Orbiting, fx.Tail.TerminalStateValue);
            Assert.Equal(TailEndUT, fx.Tail.EndUT, 6);
            Assert.DoesNotContain(fx.Tail.RecordingId, refreshedSnapshotIds);

            // The re-flown provisional (the tree's active recording) still finalizes live.
            Assert.Contains(fx.Provisional.RecordingId, refreshedSnapshotIds);
        }

        // catches: the crew half of the defect - Dead x3 and a death-respawn stamp on a head
        // whose crew flew on into the tail.
        [Fact]
        public void ReFlyConclusion_CommittedChainHead_CrewEndStatesHaveNoDeath()
        {
            var fx = BuildReFlyTree();
            AddLaterChainTipOwner(KerbalXGuid);

            ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                fx.Tree, CommitUT, isSceneExit: true,
                findVesselByPid: fx.FindVessel,
                liveVesselAccess: LiveAccess());
            KerbalsModule.PopulateCrewEndStates(fx.Head);

            Assert.NotNull(fx.Head.CrewEndStates);
            Assert.Equal(3, fx.Head.CrewEndStates.Count);
            Assert.DoesNotContain(KerbalEndState.Dead, fx.Head.CrewEndStates.Values);
            Assert.False(fx.Head.CrewDeathRespawns.HasValue);
        }

        // ------------------------------------------------------------------
        // The leaf-classification guard
        // ------------------------------------------------------------------

        // catches: the chain gate missing - with NO spawn owner in the store (the identity gate
        // cannot fire), the head is still not finalized as a leaf.
        [Fact]
        public void FinalizeIndividualRecording_NonFinalChainSegment_IsNotALeaf()
        {
            var fx = BuildReFlyTree();

            ParsekFlight.FinalizeIndividualRecording(
                fx.Head, CommitUT, isSceneExit: true,
                treeContext: fx.Tree,
                findVesselByPid: fx.FindVessel,
                liveVesselAccess: LiveAccess());

            Assert.False(fx.Head.TerminalStateValue.HasValue);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("non-final chain segment")
                && l.Contains("successor=" + fx.Tail.RecordingId));
        }

        [Fact]
        public void IsNonFinalChainSegmentInTree_HeadWithContiguousSuccessor_True()
        {
            var fx = BuildReFlyTree();
            Assert.True(ParsekFlight.IsNonFinalChainSegmentInTree(fx.Head, fx.Tree, out string successor));
            Assert.Equal(fx.Tail.RecordingId, successor);
        }

        [Fact]
        public void IsNonFinalChainSegmentInTree_FinalSegment_False()
        {
            var fx = BuildReFlyTree();
            Assert.False(ParsekFlight.IsNonFinalChainSegmentInTree(fx.Tail, fx.Tree, out _));
        }

        // Mirror: a head the session is flying (the tree's active recording) still finalizes.
        [Fact]
        public void IsNonFinalChainSegmentInTree_ActiveRecording_False()
        {
            var fx = BuildReFlyTree();
            fx.Tree.ActiveRecordingId = fx.Head.RecordingId;
            Assert.False(ParsekFlight.IsNonFinalChainSegmentInTree(fx.Head, fx.Tree, out _));
        }

        // Mirror: a head recorded past its successor's start is being flown, not replayed.
        [Fact]
        public void IsNonFinalChainSegmentInTree_HeadExtendedPastSuccessorStart_False()
        {
            var fx = BuildReFlyTree();
            fx.Head.Points.Add(new TrajectoryPoint { ut = CutUT + 30.0, altitude = 80000.0, bodyName = "Kerbin" });
            Assert.False(ParsekFlight.IsNonFinalChainSegmentInTree(fx.Head, fx.Tree, out _));
        }

        [Fact]
        public void IsNonFinalChainSegmentInTree_SuccessorNotInThisTree_False()
        {
            var fx = BuildReFlyTree();
            fx.Tree.Recordings.Remove(fx.Tail.RecordingId);
            Assert.False(ParsekFlight.IsNonFinalChainSegmentInTree(fx.Head, fx.Tree, out _));
        }

        [Fact]
        public void IsNonFinalChainSegmentInTree_ParallelBranchOrUnchained_False()
        {
            var fx = BuildReFlyTree();
            fx.Head.ChainBranch = 1;
            Assert.False(ParsekFlight.IsNonFinalChainSegmentInTree(fx.Head, fx.Tree, out _));

            var fx2 = BuildReFlyTree();
            fx2.Head.ChainId = null;
            fx2.Head.ChainIndex = -1;
            Assert.False(ParsekFlight.IsNonFinalChainSegmentInTree(fx2.Head, fx2.Tree, out _));
        }

        // ------------------------------------------------------------------
        // The identity gate: a live vessel that later committed history spawned
        // ------------------------------------------------------------------

        [Fact]
        public void FindLaterCommittedSpawnOwner_SameLaunchChainTipSpawn_ReturnsOwner()
        {
            var fx = BuildReFlyTree();
            Recording owner = AddLaterChainTipOwner(KerbalXGuid);
            Assert.Same(owner, RecordingStore.FindLaterCommittedSpawnOwner(fx.Tail, KerbalXPid, KerbalXGuid));
        }

        // A relaunch of the same craft shares the baked pid but not the guid: the live vessel is
        // not that owner's spawn, so the owner does not claim it.
        [Fact]
        public void FindLaterCommittedSpawnOwner_AdoptionStampDifferentGuid_Null()
        {
            var fx = BuildReFlyTree();
            AddLaterChainTipOwner(KerbalXGuid);
            Assert.Null(RecordingStore.FindLaterCommittedSpawnOwner(fx.Tail, KerbalXPid, OtherLaunchGuid));
        }

        // Unknown live guid: pid-only fallback per the identity contract.
        [Fact]
        public void FindLaterCommittedSpawnOwner_UnknownLiveGuid_FallsBackToPid()
        {
            var fx = BuildReFlyTree();
            Recording owner = AddLaterChainTipOwner(KerbalXGuid);
            Assert.Same(owner, RecordingStore.FindLaterCommittedSpawnOwner(fx.Tail, KerbalXPid, null));
        }

        // A genuine Parsek spawn carries a KSP-unique spawn pid: the pid match alone is conclusive.
        [Fact]
        public void FindLaterCommittedSpawnOwner_UniqueSpawnPid_MatchesWithoutGuid()
        {
            var fx = BuildReFlyTree();
            Recording owner = AddLaterChainTipOwner(KerbalXGuid);
            owner.SpawnedVesselPersistentId = 4111222333;
            Assert.Same(owner, RecordingStore.FindLaterCommittedSpawnOwner(fx.Tail, 4111222333, OtherLaunchGuid));
            Assert.Null(RecordingStore.FindLaterCommittedSpawnOwner(fx.Tail, KerbalXPid, KerbalXGuid));
        }

        // Mirror: a recording that continues an EARLIER recording's spawned vessel (a Switch-To
        // resume of a materialized endpoint) is that vessel's later history, so it still reads it.
        [Fact]
        public void FindLaterCommittedSpawnOwner_OwnerEndsBeforeRecording_Null()
        {
            var fx = BuildReFlyTree();
            Recording owner = AddLaterChainTipOwner(KerbalXGuid);
            owner.ExplicitStartUT = 100.0;
            owner.ExplicitEndUT = 150.0;
            Assert.Null(RecordingStore.FindLaterCommittedSpawnOwner(fx.Tail, KerbalXPid, KerbalXGuid));
        }

        [Fact]
        public void FindLaterCommittedSpawnOwner_SameRecordingId_Null()
        {
            var fx = BuildReFlyTree();
            Recording owner = AddLaterChainTipOwner(KerbalXGuid);
            owner.RecordingId = fx.Tail.RecordingId;
            Assert.Null(RecordingStore.FindLaterCommittedSpawnOwner(fx.Tail, KerbalXPid, KerbalXGuid));
        }

        // catches: the identity gate missing - the committed tail re-snapshotted from the vessel
        // the other tree's chain tip spawned.
        [Fact]
        public void FinalizeIndividualRecording_LiveVesselOwnedByLaterSpawn_LeavesRecordingAlone()
        {
            var fx = BuildReFlyTree();
            AddLaterChainTipOwner(KerbalXGuid);
            double orbitSmaBefore = fx.Tail.TerminalOrbitSemiMajorAxis;

            ParsekFlight.FinalizeIndividualRecording(
                fx.Tail, CommitUT, isSceneExit: true,
                treeContext: fx.Tree,
                findVesselByPid: fx.FindVessel,
                liveVesselAccess: LiveAccess());

            Assert.Equal(TerminalState.Orbiting, fx.Tail.TerminalStateValue);
            Assert.Equal(orbitSmaBefore, fx.Tail.TerminalOrbitSemiMajorAxis);
            Assert.DoesNotContain(fx.Tail.RecordingId, refreshedSnapshotIds);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("is the terminal spawn of later committed recording 'tip-37d0dc07'"));
        }

        // Mirror: a different launch of the craft (different guid) is not the owner's spawn,
        // so finalization proceeds from the live vessel exactly as before.
        [Fact]
        public void FinalizeIndividualRecording_LiveVesselOfDifferentLaunch_FinalizesAsBefore()
        {
            var fx = BuildReFlyTree();
            AddLaterChainTipOwner(KerbalXGuid);
            fx.KerbalXVessel.id = new Guid(OtherLaunchGuid);

            ParsekFlight.FinalizeIndividualRecording(
                fx.Tail, CommitUT, isSceneExit: true,
                treeContext: fx.Tree,
                findVesselByPid: fx.FindVessel,
                liveVesselAccess: LiveAccess());

            Assert.Contains(fx.Tail.RecordingId, refreshedSnapshotIds);
        }

        // Mirror: the tree's active recording is the one flown this session; a later committed
        // spawn sharing its pid never blocks its own finalization.
        [Fact]
        public void FinalizeIndividualRecording_ActiveRecording_IsNotGatedByLaterSpawnOwner()
        {
            var fx = BuildReFlyTree();
            AddLaterChainTipOwner(KerbalXGuid);
            fx.Tree.ActiveRecordingId = fx.Tail.RecordingId;

            ParsekFlight.FinalizeIndividualRecording(
                fx.Tail, CommitUT, isSceneExit: true,
                treeContext: fx.Tree,
                findVesselByPid: fx.FindVessel,
                liveVesselAccess: LiveAccess());

            Assert.Contains(fx.Tail.RecordingId, refreshedSnapshotIds);
        }

        // Mirror: with no committed spawn owner, a leaf whose own vessel is live still takes its
        // terminal from that vessel (a genuine own continuation).
        [Fact]
        public void FinalizeIndividualRecording_OwnLiveVesselNoLaterOwner_StampsTerminal()
        {
            var fx = BuildReFlyTree();
            var standalone = new Recording
            {
                RecordingId = "own-continuation",
                TreeId = fx.Tree.Id,
                VesselName = "Kerbal X",
                VesselPersistentId = KerbalXPid,
                RecordedVesselGuid = KerbalXGuid,
            };
            standalone.Points.Add(new TrajectoryPoint { ut = 8000.0, altitude = 90000.0, bodyName = "Kerbin" });
            standalone.Points.Add(new TrajectoryPoint { ut = CommitUT, altitude = 90000.0, bodyName = "Kerbin" });

            ParsekFlight.FinalizeIndividualRecording(
                standalone, CommitUT, isSceneExit: false,
                treeContext: null,
                findVesselByPid: fx.FindVessel,
                liveVesselAccess: LiveAccess());

            Assert.Equal(TerminalState.SubOrbital, standalone.TerminalStateValue);
        }

        // ------------------------------------------------------------------
        // Fixture
        // ------------------------------------------------------------------

        private sealed class ReFlyFixture
        {
            public RecordingTree Tree;
            public Recording Head;
            public Recording Tail;
            public Recording Provisional;
            public Vessel KerbalXVessel;
            public Vessel ProbeVessel;

            public Vessel FindVessel(uint pid)
            {
                if (pid == KerbalXPid) return KerbalXVessel;
                if (pid == ProbePid) return ProbeVessel;
                return null;
            }
        }

        private static ReFlyFixture BuildReFlyTree()
        {
            var tree = new RecordingTree
            {
                Id = "788554a928324f148dd313dd26322638",
                TreeName = "Kerbal X",
                ActiveRecordingId = "rec_f57a-provisional",
            };

            ConfigNode crewed = VesselSnapshotBuilder
                .CrewedShip("Kerbal X", "Jebediah Kerman", KerbalXPid)
                .AddPart("crewCabin", "Bill Kerman")
                .AddPart("crewCabin", "Bob Kerman")
                .Build();

            var head = new Recording
            {
                RecordingId = "a32f62f5-head",
                TreeId = tree.Id,
                VesselName = "Kerbal X",
                VesselPersistentId = KerbalXPid,
                RecordedVesselGuid = KerbalXGuid,
                ChainId = ChainId,
                ChainIndex = 0,
                GhostVisualSnapshot = crewed.CreateCopy(),
                VesselSnapshot = crewed.CreateCopy(),
                ExplicitStartUT = 25.96,
                ExplicitEndUT = CutUT,
            };
            head.Points.Add(new TrajectoryPoint { ut = 25.96, altitude = 70.0, bodyName = "Kerbin" });
            head.Points.Add(new TrajectoryPoint { ut = CutUT, altitude = 70500.0, bodyName = "Kerbin" });

            var tail = new Recording
            {
                RecordingId = "990ed615-tail",
                TreeId = tree.Id,
                VesselName = "Kerbal X",
                VesselPersistentId = KerbalXPid,
                RecordedVesselGuid = KerbalXGuid,
                ChainId = ChainId,
                ChainIndex = 1,
                ChildBranchPointId = "384533f3-jointbreak",
                GhostVisualSnapshot = crewed.CreateCopy(),
                VesselSnapshot = crewed.CreateCopy(),
                ExplicitStartUT = CutUT,
                ExplicitEndUT = TailEndUT,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 512362.0,
            };
            tail.Points.Add(new TrajectoryPoint { ut = CutUT, altitude = 70500.0, bodyName = "Kerbin" });
            tail.Points.Add(new TrajectoryPoint { ut = TailEndUT, altitude = 110000.0, bodyName = "Kerbin" });

            var origin = new Recording
            {
                RecordingId = "b07cfd6c-probe",
                TreeId = tree.Id,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = ProbePid,
                ParentBranchPointId = "384533f3-jointbreak",
                ExplicitStartUT = 382.21,
                ExplicitEndUT = 387.0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
            };
            origin.Points.Add(new TrajectoryPoint { ut = 382.21, altitude = 110000.0, bodyName = "Kerbin" });
            origin.Points.Add(new TrajectoryPoint { ut = 387.0, altitude = 110000.0, bodyName = "Kerbin" });

            var provisional = new Recording
            {
                RecordingId = "rec_f57a-provisional",
                TreeId = tree.Id,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = ProbePid,
                ParentBranchPointId = "384533f3-jointbreak",
                MergeState = MergeState.NotCommitted,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
            };
            provisional.Points.Add(new TrajectoryPoint { ut = 382.75, altitude = 110000.0, bodyName = "Kerbin" });
            provisional.Points.Add(new TrajectoryPoint { ut = CommitUT, altitude = 110000.0, bodyName = "Kerbin" });

            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tail);
            tree.AddOrReplaceRecording(origin);
            tree.AddOrReplaceRecording(provisional);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "384533f3-jointbreak",
                Type = BranchPointType.JointBreak,
                UT = 382.21,
                ParentRecordingIds = new List<string> { tail.RecordingId },
                ChildRecordingIds = new List<string> { origin.RecordingId, provisional.RecordingId },
            });

            return new ReFlyFixture
            {
                Tree = tree,
                Head = head,
                Tail = tail,
                Provisional = provisional,
                KerbalXVessel = TestVessel(KerbalXPid, Vessel.Situations.SUB_ORBITAL, KerbalXGuid),
                ProbeVessel = TestVessel(ProbePid, Vessel.Situations.ORBITING, "dfaa928c000040008000000000000003"),
            };
        }

        /// <summary>The second tree's chain tip 37d0dc07: same vessel (pid AND guid), committed,
        /// already spawned by its chain-tip spawn with the original pid (an adoption stamp).</summary>
        private static Recording AddLaterChainTipOwner(string guid)
        {
            var owner = new Recording
            {
                RecordingId = "tip-37d0dc07",
                VesselName = "Kerbal X",
                VesselPersistentId = KerbalXPid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = 8950.59,
                ExplicitEndUT = 8950.61,
                TerminalStateValue = TerminalState.Orbiting,
                VesselSpawned = true,
                SpawnedVesselPersistentId = KerbalXPid,
            };
            RecordingStore.AddRecordingWithTreeForTesting(owner, "Kerbal X (docking)");
            return owner;
        }

        private static Vessel TestVessel(uint persistentId, Vessel.Situations situation, string guid)
        {
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            vessel.persistentId = persistentId;
            vessel.situation = situation;
            vessel.id = new Guid(guid);
            return vessel;
        }

        private ParsekFlight.FinalizationLiveVesselAccess LiveAccess() =>
            new ParsekFlight.FinalizationLiveVesselAccess(
                isFound: vessel => !ReferenceEquals(vessel, null),
                determineTerminalState: vessel =>
                    RecordingTree.DetermineTerminalState((int)vessel.situation),
                captureTerminalOrbit: (rec, vessel) => { },
                captureTerminalPosition: (rec, vessel) => { },
                tryBackupSnapshot: vessel => null,
                tryRefreshStableTerminalSnapshot: (rec, vessel, isSceneExit, logPrefix) =>
                {
                    refreshedSnapshotIds.Add(rec.RecordingId);
                    return false;
                });
    }
}
