using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECOVERED-AFTER-COMMIT-READS-LANDED-ELSEWHERE (operator rulings 2026-10-01): a
    /// recovered flight ends with no vessel and is never offered for Stash / Re-Fly, and the
    /// in-flight Recover commits it Recovered. The auto-merge shape these pin: the scene-exit
    /// commit stored the flight Landed, and the recovery lives only in the ledger as a
    /// FundsEarning(Recovery) row (or a KerbalRecovered crew-close row).
    /// </summary>
    [Collection("Sequential")]
    public class RecoveredAfterCommitTests : IDisposable
    {
        private const string Name = "Jumping Flea";
        private const uint Pid = 2905720181u;
        private const string LaunchGuid = "0123456789abcdef0123456789abcdef";

        private readonly List<string> logLines = new List<string>();

        public RecoveredAfterCommitTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            RecoveredRecordingEvidence.ResetForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            InFlightRecoveryRequest.ResetForTesting();
            RecoveredRecordingEvidence.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static ConfigNode LandedSnapshot()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", Name);
            return snapshot;
        }

        /// <summary>A committed, spawnable, landed-away-from-KSC flight.</summary>
        private static Recording LandedFlight(string id, TerminalState terminal = TerminalState.Landed)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = Name,
                VesselPersistentId = Pid,
                RecordedVesselGuid = LaunchGuid,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 300.0,
                VesselSnapshot = LandedSnapshot(),
                TerminalStateValue = terminal,
                PlaybackEnabled = true,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", latitude = 1.5, longitude = -70.0, altitude = 120, funds = 40000 },
                    new TrajectoryPoint { ut = 300.0, bodyName = "Kerbin", latitude = 1.5, longitude = -70.0, altitude = 120, funds = 40000 },
                },
            };
        }

        private static void AddRecoveryFundsRow(string recordingId, double ut = 347.4, float amount = 4558f)
        {
            Ledger.AddAction(new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Recovery,
                FundsAwarded = amount,
                RecordingId = recordingId,
            });
        }

        private static void AddCrewCloseRow(string recordingId, double ut = 347.4)
        {
            Ledger.AddAction(new GameAction
            {
                UT = ut,
                Type = GameActionType.KerbalRecovered,
                KerbalName = "Jebediah Kerman",
                RecordingId = recordingId,
            });
        }

        // ------------------------------------------------------------------
        // Shared predicate
        // ------------------------------------------------------------------

        [Fact]
        public void Predicate_RecoveredTerminal_IsTerminalEvidence()
        {
            var rec = LandedFlight("rec-a", TerminalState.Recovered);
            Assert.True(RecoveredRecordingEvidence.IsRecovered(rec, new List<GameAction>(), out string evidence));
            Assert.Equal(RecoveredRecordingEvidence.EvidenceTerminal, evidence);
            Assert.False(RecoveredRecordingEvidence.IsRecoveredByLedgerRow(rec, new List<GameAction>()));
        }

        [Theory]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Splashed)]
        [InlineData(TerminalState.Orbiting)]
        [InlineData(TerminalState.SubOrbital)]
        public void Predicate_OutlivedTerminalWithRecoveryRow_IsRowEvidence(TerminalState terminal)
        {
            var rec = LandedFlight("rec-a", terminal);
            var ledger = new List<GameAction>
            {
                new GameAction { UT = 400, Type = GameActionType.FundsEarning,
                    FundsSource = FundsEarningSource.Recovery, RecordingId = "rec-a" },
            };
            Assert.True(RecoveredRecordingEvidence.IsRecovered(rec, ledger, out string evidence));
            Assert.Equal(RecoveredRecordingEvidence.EvidenceLedgerRow, evidence);
        }

        // catches: a recovery row misattributed to a recording whose vessel ended otherwise
        // (destroyed, docked) being read as a recovery.
        [Theory]
        [InlineData(TerminalState.Destroyed)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void Predicate_VesselEndingTerminalWithRow_IsNotRecovered(TerminalState terminal)
        {
            var rec = LandedFlight("rec-a", terminal);
            var ledger = new List<GameAction>
            {
                new GameAction { UT = 400, Type = GameActionType.KerbalRecovered, RecordingId = "rec-a" },
            };
            Assert.False(RecoveredRecordingEvidence.IsRecovered(rec, ledger, out _));
        }

        // catches: keying on any funds earning, or on another recording's recovery.
        [Fact]
        public void Predicate_NonRecoveryFundsOrOtherRecording_IsNotRecovered()
        {
            var rec = LandedFlight("rec-a");
            var ledger = new List<GameAction>
            {
                new GameAction { UT = 400, Type = GameActionType.FundsEarning,
                    FundsSource = FundsEarningSource.ContractComplete, RecordingId = "rec-a" },
                new GameAction { UT = 400, Type = GameActionType.FundsEarning,
                    FundsSource = FundsEarningSource.Recovery, RecordingId = "rec-other" },
            };
            Assert.False(RecoveredRecordingEvidence.IsRecovered(rec, ledger, out _));
        }

        // ------------------------------------------------------------------
        // Spawn at the recording's end
        // ------------------------------------------------------------------

        [Fact]
        public void Spawn_LandedWithoutRecoveryRow_StillSpawns()
        {
            // Mirror direction: a genuinely Landed, unrecovered flight keeps its vessel.
            var rec = LandedFlight("rec-landed");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            AddRecoveryFundsRow("rec-unrelated");

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);

            Assert.True(needsSpawn, reason);
        }

        // catches: the auto-merge shape (committed Landed, recovered afterwards) materializing
        // the vessel at the landing site after a rewind, so it can be recovered twice.
        [Fact]
        public void Spawn_AutoMergeShapedRecoveredFlight_DoesNotSpawn()
        {
            var rec = LandedFlight("rec-recovered");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            AddRecoveryFundsRow("rec-recovered");

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);

            Assert.False(needsSpawn);
            Assert.Contains("recovered after commit", reason);
            Assert.Contains("terminal Landed", reason);
        }

        // catches: a zero-value crewed recovery (no funds row, only the crew close) spawning.
        [Fact]
        public void Spawn_CrewCloseRowOnly_DoesNotSpawn()
        {
            var rec = LandedFlight("rec-recovered");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            AddCrewCloseRow("rec-recovered");

            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);
        }

        // catches: a stale index keyed on the ledger contents at first read (the row arriving
        // after the first spawn decision must still count).
        [Fact]
        public void Spawn_RowAddedAfterFirstDecision_IsSeen()
        {
            var rec = LandedFlight("rec-recovered");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.True(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);

            AddRecoveryFundsRow("rec-recovered");

            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);
        }

        [Fact]
        public void Spawn_KscEnd_AutoMergeShapedRecoveredFlight_DoesNotSpawn()
        {
            var rec = LandedFlight("rec-recovered");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            AddRecoveryFundsRow("rec-recovered");

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, currentUT: 500.0);

            Assert.False(needsSpawn);
            Assert.Contains("recovered after commit", reason);
        }

        // ------------------------------------------------------------------
        // Stash / Re-Fly
        // ------------------------------------------------------------------

        private static Recording StashCandidate(string id)
        {
            var rec = LandedFlight(id);
            rec.MergeState = MergeState.Immutable;
            rec.ParentBranchPointId = "bp-sep";
            return rec;
        }

        [Fact]
        public void Stash_LandedWithoutRecoveryRow_IsStashShape()
        {
            // Mirror direction: the existing Landed stash shape is unchanged.
            var rec = StashCandidate("rec-landed");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.True(UnfinishedFlightClassifier.IsPotentialManualStashShape(rec));
        }

        // catches: a recovered flight committed Landed being offered for Stash and re-flown.
        [Fact]
        public void Stash_AutoMergeShapedRecoveredFlight_IsNotStashShape()
        {
            var rec = StashCandidate("rec-recovered");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            AddRecoveryFundsRow("rec-recovered");

            Assert.False(UnfinishedFlightClassifier.IsPotentialManualStashShape(rec));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("Stash terminal rejected: rec=rec-recovered")
                && l.Contains("recoveredAfterCommit=true"));
        }

        // ------------------------------------------------------------------
        // In-flight Recover commits Recovered
        // ------------------------------------------------------------------

        private static RecordingTree TreeOf(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree-flea", TreeName = Name, RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.AddOrReplaceRecording(r);
            }
            return tree;
        }

        [Fact]
        public void InFlightRecover_SceneExitToSpaceCenter_StampsRecovered()
        {
            var rec = LandedFlight("rec-flea");
            rec.GhostVisualSnapshot = null;
            var tree = TreeOf(rec);

            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 310.0);
            int stamped = InFlightRecoveryRequest.ApplyAtSceneExit(
                tree, GameScenes.SPACECENTER, "test");

            Assert.Equal(1, stamped);
            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.Equal(310.0, rec.ExplicitEndUT);
            Assert.Null(rec.VesselSnapshot);
            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.Null(InFlightRecoveryRequest.Armed);
            Assert.Contains(logLines, l =>
                l.Contains("[Recovery]")
                && l.Contains("In-flight recovery: recording 'rec-flea'")
                && l.Contains("terminal Landed -> Recovered"));

            // The committed shape now reads recovered on its own terminal, so both readers
            // agree without any ledger row.
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var spawn = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);
            Assert.False(spawn.needsSpawn);
            Assert.Contains("no vessel snapshot", spawn.reason);
        }

        // catches: a request outliving its scene change and stamping a later, unrelated exit.
        [Fact]
        public void InFlightRecover_FlightDestination_DropsRequestUnapplied()
        {
            var rec = LandedFlight("rec-flea");
            var tree = TreeOf(rec);

            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 310.0);
            Assert.Equal(0, InFlightRecoveryRequest.ApplyAtSceneExit(tree, GameScenes.FLIGHT, "test"));

            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.NotNull(rec.VesselSnapshot);
            Assert.Null(InFlightRecoveryRequest.Armed);
        }

        // catches: a name / pid-only match stamping a different launch of the same craft.
        [Fact]
        public void InFlightRecover_DifferentLaunchGuid_IsNotStamped()
        {
            var rec = LandedFlight("rec-flea");
            var tree = TreeOf(rec);

            InFlightRecoveryRequest.Arm(Pid, "fedcba9876543210fedcba9876543210", Name, 310.0);
            Assert.Equal(0, InFlightRecoveryRequest.ApplyAtSceneExit(tree, GameScenes.SPACECENTER, "test"));

            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.NotNull(rec.VesselSnapshot);
        }

        // catches: the earlier segment of the same vessel (split parent) being stamped too.
        [Fact]
        public void InFlightRecover_OnlyTheVesselTipIsStamped()
        {
            var parent = LandedFlight("rec-parent");
            parent.TerminalStateValue = null;
            parent.ExplicitEndUT = 200.0;
            var child = LandedFlight("rec-child");
            var tree = TreeOf(parent, child);
            var bp = new BranchPoint { Id = "bp-1", Type = BranchPointType.JointBreak, UT = 200.0 };
            parent.ChildBranchPointId = bp.Id;
            child.ParentBranchPointId = bp.Id;
            bp.ParentRecordingIds.Add(parent.RecordingId);
            bp.ChildRecordingIds.Add(child.RecordingId);
            tree.BranchPoints.Add(bp);

            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 310.0);
            Assert.Equal(1, InFlightRecoveryRequest.ApplyAtSceneExit(tree, GameScenes.SPACECENTER, "test"));

            Assert.Equal(TerminalState.Recovered, child.TerminalStateValue);
            Assert.Null(parent.TerminalStateValue);
            Assert.NotNull(parent.VesselSnapshot);
        }

        // catches: the late-listener path consuming the request on an unrelated pending tree,
        // leaving nothing for the real finalize.
        [Fact]
        public void InFlightRecover_UnrelatedPendingTree_KeepsRequestArmed()
        {
            var other = LandedFlight("rec-other");
            other.VesselPersistentId = 42u;
            other.RecordedVesselGuid = "11111111111111111111111111111111";
            var tree = TreeOf(other);

            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 310.0);
            Assert.Equal(0, InFlightRecoveryRequest.TryApplyToFinalizedPendingTree(tree, "test"));

            Assert.NotNull(InFlightRecoveryRequest.Armed);
            Assert.Equal(TerminalState.Landed, other.TerminalStateValue);
        }

        [Fact]
        public void InFlightRecover_LateListener_StampsMatchingPendingTree()
        {
            var rec = LandedFlight("rec-flea");
            var tree = TreeOf(rec);

            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 310.0);
            Assert.Equal(1, InFlightRecoveryRequest.TryApplyToFinalizedPendingTree(tree, "test"));

            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.Null(InFlightRecoveryRequest.Armed);
        }

        // catches: the commit-time pairing finding no FundsChanged(VesselRecovery) (stock pays
        // at the Space Center after the commit) and booking the last-two-points funds rise (a
        // contract paid on landing) as a recovery payout, on top of the #444 row that follows.
        [Fact]
        public void InFlightRecover_CommitCostActions_SkipTheFundsHeuristic()
        {
            var rec = LandedFlight("rec-flea");
            rec.PreLaunchFunds = 40000;
            var last = rec.Points[1];
            last.funds = 52000;   // in-flight contract payout on landing
            rec.Points[1] = last;
            var tree = TreeOf(rec);

            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 310.0);
            InFlightRecoveryRequest.ApplyAtSceneExit(tree, GameScenes.SPACECENTER, "test");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-flea", 100.0, 310.0);

            Assert.DoesNotContain(actions, a =>
                a.Type == GameActionType.FundsEarning && a.FundsSource == FundsEarningSource.Recovery);
            Assert.Contains(logLines, l => l.Contains("recovered in flight - payout not yet paid"));
        }

        [Fact]
        public void ManualPendingStamp_CommitCostActions_KeepTheHeuristic()
        {
            // Mirror direction: a Recovered terminal stamped by the pending-tree path (no
            // in-flight request) keeps the legacy fallback.
            var rec = LandedFlight("rec-flea", TerminalState.Recovered);
            rec.PreLaunchFunds = 40000;
            var last = rec.Points[1];
            last.funds = 52000;   // in-flight contract payout on landing
            rec.Points[1] = last;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-flea", 100.0, 300.0);

            Assert.Contains(actions, a =>
                a.Type == GameActionType.FundsEarning && a.FundsSource == FundsEarningSource.Recovery);
        }
    }
}
