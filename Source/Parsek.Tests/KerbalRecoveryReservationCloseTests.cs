using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KERBAL-ABOARD-RESERVATION-OUTLIVES-THE-REAL-VESSEL: a recovery of the REAL vessel a
    /// committed flight continues ends that flight's open-ended (Aboard / Unknown) crew
    /// hold at the recovery UT (design 9.3: STRANDED stays open "until a rescue recording
    /// closes it"; RECOVERED is "available after recovery").
    ///
    /// <para>The everyday shape is the one <c>L3-career-science-recover</c> measured: with
    /// auto-merge on, stock's in-flight Recover commits the flight at the scene change
    /// (the recording ends Landed with the crew aboard, split by the optimizer into a
    /// Recovered HEAD and an Aboard TIP) and only THEN recovers the vessel. The
    /// <see cref="GameActionType.KerbalRecovered"/> row the recovery writes makes the
    /// reservation UT 0 -> recovery UT, so #1767's time-based release frees him at once
    /// and a rewind to before the recovery holds him again.</para>
    /// </summary>
    [Collection("Sequential")]
    public class KerbalRecoveryReservationCloseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string Bill = "Bill Kerman";
        private const uint CraftPid = 2104817653u;
        private const string LaunchGuid = "5d1f0b6a2c7e4e0f9a3b8c1d2e4f6a7b";
        private const string OtherLaunchGuid = "0f0e0d0c0b0a09080706050403020100";
        private const string HeadId = "rec-l3-head";
        private const string TipId = "rec-l3-tip";
        private const double HeadEndUT = 341.8;
        private const double TipEndUT = 347.7;
        private const double RecoveryUT = 347.7;

        public KerbalRecoveryReservationCloseTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.LiveClockUTProviderForTesting = null;
            KerbalsModule.LoadedSaveUTProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixture helpers
        // ------------------------------------------------------------------

        private static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        private static Recording MakeRecording(
            string id, string treeId, double startUT, double endUT,
            uint pid = CraftPid, string guid = LaunchGuid, params string[] crew)
        {
            var snapshot = CrewSnapshot(crew);
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "Jumping Flea",
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                CrewEndStatesResolved = true
            };
        }

        /// <summary>Commits <paramref name="rec"/> (a fresh tree unless it names one).</summary>
        private static Recording Commit(Recording rec)
        {
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        private static GameAction Assignment(
            string recordingId, string kerbal, double startUT, double endUT,
            KerbalEndState endState, int sequence, string role = "Pilot")
        {
            return new GameAction
            {
                UT = startUT,
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbal,
                KerbalRole = role,
                StartUT = (float)startUT,
                EndUT = (float)endUT,
                KerbalEndStateField = endState,
                Sequence = sequence
            };
        }

        /// <summary>
        /// The L3 shape: one launch, split by the optimizer into a HEAD whose ghost-only
        /// chain hand-off reads Recovered and a TIP that ended Landed with Jeb aboard.
        /// Returns the shared tree id.
        /// </summary>
        private static string CommitL3Flight()
        {
            var head = Commit(MakeRecording(HeadId, null, 10.0, HeadEndUT, crew: Jeb));
            Commit(MakeRecording(TipId, head.TreeId, HeadEndUT, TipEndUT, crew: Jeb));
            Ledger.AddAction(Assignment(HeadId, Jeb, 10.0, HeadEndUT, KerbalEndState.Recovered, 1));
            Ledger.AddAction(Assignment(TipId, Jeb, HeadEndUT, TipEndUT, KerbalEndState.Aboard, 2));
            return head.TreeId;
        }

        private static int CountRows(GameActionType type)
        {
            int n = 0;
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
                if (actions[i].Type == type) n++;
            return n;
        }

        private static KerbalsModule Kerbals => LedgerOrchestrator.Kerbals;

        // ------------------------------------------------------------------
        // The production path: recovery -> ledger row -> walk
        // ------------------------------------------------------------------

        // catches: the filed bug - the L3 in-flight Recover leaving Jeb reserved forever
        // with a stand-in, because the TIP's Aboard row is +inf and nothing bounds it.
        [Fact]
        public void L3Shape_RecoveryOfTheVessel_EndsTheOpenEndedHold_AndFreesJeb()
        {
            CommitL3Flight();
            double clock = RecoveryUT;
            KerbalsModule.LiveClockUTProviderForTesting = () => clock;

            // The commit walk, before the recovery: the TIP's Aboard row holds him forever.
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(Kerbals.IsReservedNow(Jeb));

            int written = LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, LaunchGuid, "Jumping Flea", new List<string> { Jeb });

            Assert.Equal(1, written);
            Assert.Equal(1, CountRows(GameActionType.KerbalRecovered));
            Assert.Equal(RecoveryUT, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(Kerbals.IsReservedNow(Jeb));
            Assert.True(Kerbals.IsKerbalAvailable(Jeb));
            Assert.False(Kerbals.ShouldFilterFromCrewDialog(Jeb));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Crew reservation closed by recovery: 'Jebediah Kerman' recoveryUT=347.7 recordingId=rec-l3-tip")
                && l.Contains("openHolds=1"));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation released: 'Jebediah Kerman' endUT=347.7"));
        }

        // catches: the recovery event (or a replayed delivery of it) writing a second row.
        [Fact]
        public void SameRecoveryDeliveredTwice_WritesOneRow()
        {
            CommitL3Flight();
            var crew = new List<string> { Jeb };

            Assert.Equal(1, LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, LaunchGuid, "Jumping Flea", crew));
            Assert.Equal(0, LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT + 0.05, CraftPid, LaunchGuid, "Jumping Flea", crew));

            Assert.Equal(1, CountRows(GameActionType.KerbalRecovered));
            Assert.Single(logLines.FindAll(l => l.Contains("Crew reservation closed by recovery:")));
            Assert.Contains(logLines, l => l.Contains("already in the ledger"));
        }

        // catches: the closure being baked into state instead of derived from the ledger -
        // a rewind to before the recovery must hold him again, and crossing it again
        // must release him again (design 9.2 "recomputed on rewind", :1780-1795).
        [Fact]
        public void RewindBeforeTheRecovery_ReReserves_ThenReleasesAtIt()
        {
            CommitL3Flight();
            LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, LaunchGuid, "Jumping Flea", new List<string> { Jeb });

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(200.0, "rewind-test");
            Assert.True(Kerbals.IsReservedNow(Jeb));
            Assert.Equal(RecoveryUT, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.Equal(RecoveryUT, Kerbals.NextReservationReleaseUT);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(400.0, "rewind-test");
            Assert.False(Kerbals.IsReservedNow(Jeb));
            Assert.True(Kerbals.IsKerbalAvailable(Jeb));
        }

        // catches: a recovery shortening the hold of a LATER committed flight (another
        // mission) - the reservation keeps its max-end merge.
        [Fact]
        public void ALaterFlightInAnotherMission_KeepsMaxEndSemantics()
        {
            CommitL3Flight();
            Commit(MakeRecording("rec-second-mission", null, 400.0, 500.0,
                guid: OtherLaunchGuid, crew: Jeb));
            Ledger.AddAction(Assignment("rec-second-mission", Jeb, 400.0, 500.0,
                KerbalEndState.Recovered, 3));

            LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, LaunchGuid, "Jumping Flea", new List<string> { Jeb });

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(450.0, "maxend-test");
            Assert.Equal(500.0, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.True(Kerbals.IsReservedNow(Jeb));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(500.0, "maxend-test");
            Assert.False(Kerbals.IsReservedNow(Jeb));
        }

        // catches: a recovery ending the open-ended hold of a LATER flight in another
        // mission (the held flight ends after the recovery, so it is out of scope).
        [Fact]
        public void ALaterAboardFlightInAnotherMission_KeepsItsOpenEndedHold()
        {
            CommitL3Flight();
            Commit(MakeRecording("rec-mun-stranding", null, 400.0, 900.0,
                guid: OtherLaunchGuid, crew: Jeb));
            Ledger.AddAction(Assignment("rec-mun-stranding", Jeb, 400.0, 900.0,
                KerbalEndState.Aboard, 3));

            LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, LaunchGuid, "Jumping Flea", new List<string> { Jeb });

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10000.0, "stranded-test");
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(Kerbals.IsReservedNow(Jeb));
        }

        // Pins CURRENT behaviour (todo Residual 4): a cross-mission rescue is not closed.
        // Jeb ended mission T aboard its vessel (+inf), then came home aboard mission U's
        // vessel; U's recovery closes U's hold only, because RecoveryClosesHold never
        // reaches across trees, so T's hold stays open-ended. Invert this cell when the
        // residual is fixed.
        [Fact]
        public void CrossMissionRescue_ClosesOnlyTheRecoveredMissionsHold()
        {
            Commit(MakeRecording("rec-mission-t", null, 10.0, 200.0,
                guid: OtherLaunchGuid, crew: Jeb));
            Ledger.AddAction(Assignment("rec-mission-t", Jeb, 10.0, 200.0,
                KerbalEndState.Aboard, 1));
            CommitL3Flight();

            Assert.Equal(1, LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, LaunchGuid, "Jumping Flea", new List<string> { Jeb }));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10000.0, "cross-mission-test");
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(Kerbals.IsReservedNow(Jeb));
        }

        // catches: the writer disagreeing with the walk on a chain with a looping segment:
        // the walk keeps that hold +inf, so a row (and its "closed by recovery" line) would
        // change nothing.
        [Fact]
        public void BuildClosureRows_SkipsAHoldInAChainWithALoopingSegment()
        {
            var tip = MakeRecording("tip", "t", 100, 200);
            tip.ChainId = "chain-1";
            var loopSegment = MakeRecording("loop-seg", "t", 0, 100);
            loopSegment.ChainId = "chain-1";
            loopSegment.LoopPlayback = true;
            var actions = new List<GameAction>
            {
                Assignment("tip", Jeb, 100, 200, KerbalEndState.Aboard, 1),
            };

            var rows = CrewRecoveryReservationClose.BuildClosureRows(
                new List<Recording> { tip }, new List<string> { Jeb }, actions,
                new List<Recording> { tip, loopSegment }, 250.0);
            Assert.Empty(rows);

            // Control: the same hold without the looping segment is closed.
            rows = CrewRecoveryReservationClose.BuildClosureRows(
                new List<Recording> { tip }, new List<string> { Jeb }, actions,
                new List<Recording> { tip }, 250.0);
            Assert.Single(rows);
        }

        // catches: the craft-baked pid alone identifying the vessel. A fresh launch of the
        // same craft (same pid, different guid) must not close the recorded launch's hold,
        // and neither may a vessel whose guid is unknown (the direction is a RELEASE).
        [Theory]
        [InlineData(OtherLaunchGuid)]
        [InlineData(null)]
        public void OnlyAPositiveLaunchMatchClosesTheHold(string liveGuid)
        {
            CommitL3Flight();

            Assert.Equal(0, LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, liveGuid, "Jumping Flea", new List<string> { Jeb }));

            Assert.Equal(0, CountRows(GameActionType.KerbalRecovered));
            Assert.Contains(logLines, l => l.Contains("no committed recording continues this vessel"));
        }

        // catches: the vessel Parsek SPAWNED at the recording's end (fresh guid, KSP-unique
        // spawn pid) not counting as the flight's continuation when the player recovers it
        // from the Tracking Station.
        [Fact]
        public void RecoveryOfTheParsekSpawnedVessel_EndsTheHold()
        {
            var rec = Commit(MakeRecording("rec-spawned", null, 10.0, 300.0, crew: Jeb));
            rec.SpawnedVesselPersistentId = 777001u;
            Ledger.AddAction(Assignment("rec-spawned", Jeb, 10.0, 300.0, KerbalEndState.Aboard, 1));

            Assert.Equal(1, LedgerOrchestrator.OnRealVesselCrewRecovered(
                5000.0, 777001u, "ffeeddccbbaa99887766554433221100", "Jumping Flea",
                new List<string> { Jeb }));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(5000.0, "spawn-test");
            Assert.Equal(5000.0, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(Kerbals.IsReservedNow(Jeb));
        }

        // catches: a seated stand-in's recovery not closing the hold the assignment row
        // files under the OWNER's name (#254 reverse-map).
        [Fact]
        public void RecoveredStandIn_ClosesTheOwnersHold()
        {
            CommitL3Flight();
            CrewReservationManager.SetReplacement(Jeb, "Rosted Kerman");

            Assert.Equal(1, LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, LaunchGuid, "Jumping Flea",
                new List<string> { "Rosted Kerman" }));

            var row = Ledger.Actions[Ledger.Actions.Count - 1];
            Assert.Equal(GameActionType.KerbalRecovered, row.Type);
            Assert.Equal(Jeb, row.KerbalName);
            Assert.Equal(TipId, row.RecordingId);
        }

        // catches: a recovered crew member with nothing open-ended (the Recovered HEAD
        // only, a kerbal the flight never held) getting a pointless row.
        [Fact]
        public void NothingOpenEnded_NoRow()
        {
            var rec = Commit(MakeRecording("rec-recovered-only", null, 10.0, 300.0, crew: Jeb));
            Ledger.AddAction(Assignment(rec.RecordingId, Jeb, 10.0, 300.0, KerbalEndState.Recovered, 1));

            Assert.Equal(0, LedgerOrchestrator.OnRealVesselCrewRecovered(
                300.0, CraftPid, LaunchGuid, "Jumping Flea", new List<string> { Jeb, Bill }));
            Assert.Equal(0, CountRows(GameActionType.KerbalRecovered));
            Assert.Contains(logLines, l => l.Contains("no open-ended hold in scope"));
        }

        // catches: a re-fly supersede leaving the closure alive after it deleted the flight
        // the recovered vessel continued - the tombstoned row must stop bounding the hold.
        [Fact]
        public void TombstonedClosure_TheHoldIsOpenEndedAgain()
        {
            CommitL3Flight();
            LedgerOrchestrator.OnRealVesselCrewRecovered(
                RecoveryUT, CraftPid, LaunchGuid, "Jumping Flea", new List<string> { Jeb });
            GameAction row = null;
            foreach (var a in Ledger.Actions)
                if (a.Type == GameActionType.KerbalRecovered) row = a;
            Assert.NotNull(row);
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(row));

            // The walk over an action list without the row (what ELS yields once it is
            // tombstoned) is the open-ended hold again.
            var module = new KerbalsModule();
            var withoutRow = new List<GameAction>();
            foreach (var a in Ledger.Actions)
                if (a.Type != GameActionType.KerbalRecovered) withoutRow.Add(a);
            module.Reset();
            module.PrePass(withoutRow, 10000.0);
            foreach (var a in withoutRow) module.ProcessAction(a);
            module.PostWalk();
            Assert.True(double.IsPositiveInfinity(module.Reservations[Jeb].ReservedUntilUT));
        }

        // ------------------------------------------------------------------
        // Pure decisions
        // ------------------------------------------------------------------

        [Fact]
        public void IsRecoveredVesselRecording_IdentityRules()
        {
            var rec = MakeRecording("r", "t", 0, 10, CraftPid, LaunchGuid);
            Assert.True(CrewRecoveryReservationClose.IsRecoveredVesselRecording(rec, CraftPid, LaunchGuid));
            // Guid formatting is normalized.
            Assert.True(CrewRecoveryReservationClose.IsRecoveredVesselRecording(
                rec, CraftPid, new Guid(LaunchGuid).ToString("D", CultureInfo.InvariantCulture)));
            Assert.False(CrewRecoveryReservationClose.IsRecoveredVesselRecording(rec, CraftPid, OtherLaunchGuid));
            Assert.False(CrewRecoveryReservationClose.IsRecoveredVesselRecording(rec, CraftPid, null));
            Assert.False(CrewRecoveryReservationClose.IsRecoveredVesselRecording(rec, CraftPid + 1, LaunchGuid));
            Assert.False(CrewRecoveryReservationClose.IsRecoveredVesselRecording(rec, 0, LaunchGuid));

            var legacy = MakeRecording("l", "t", 0, 10, CraftPid, null);
            Assert.False(CrewRecoveryReservationClose.IsRecoveredVesselRecording(legacy, CraftPid, LaunchGuid));

            // Genuine spawn: KSP-unique spawn pid, conclusive whatever the guid.
            rec.SpawnedVesselPersistentId = 55u;
            Assert.True(CrewRecoveryReservationClose.IsRecoveredVesselRecording(rec, 55u, OtherLaunchGuid));
            // Adoption stamp (spawn pid == craft pid) is the baked pid: guid must match.
            legacy.SpawnedVesselPersistentId = CraftPid;
            Assert.False(CrewRecoveryReservationClose.IsRecoveredVesselRecording(legacy, CraftPid, LaunchGuid));
        }

        [Fact]
        public void SelectOwnerRecordings_LatestEndPerTree_EndedByTheRecovery()
        {
            var head = MakeRecording("head", "tree-a", 0, 100);
            var tip = MakeRecording("tip", "tree-a", 100, 200);
            var later = MakeRecording("later", "tree-a", 200, 900);      // ends after the recovery
            var ghost = MakeRecording("ghost", "tree-b", 0, 150);
            ghost.IsGhostOnly = true;
            var otherTree = MakeRecording("cont", "tree-c", 200, 250);
            var otherLaunch = MakeRecording("relaunch", "tree-d", 0, 100, guid: OtherLaunchGuid);

            var owners = CrewRecoveryReservationClose.SelectOwnerRecordings(
                new List<Recording> { head, tip, later, ghost, otherTree, otherLaunch },
                CraftPid, LaunchGuid, 300.0);

            Assert.Equal(2, owners.Count);
            Assert.Equal("tip", owners[0].RecordingId);
            Assert.Equal("cont", owners[1].RecordingId);
        }

        [Theory]
        // hold, holdTree, holdEnd, owner, ownerTree, recoveryUT, expected
        [InlineData("tip", "t", 347.7, "tip", "t", 347.7, true)]     // the owner itself
        [InlineData("eva", "t", 300.0, "tip", "t", 347.7, true)]     // same mission, ended before
        [InlineData("eva", "t", 348.5, "tip", "t", 347.7, true)]     // inside the end tolerance
        [InlineData("later", "t", 400.0, "tip", "t", 347.7, false)]  // a later flight keeps its hold
        [InlineData("mun", "u", 300.0, "tip", "t", 347.7, false)]    // another mission
        [InlineData("x", null, 300.0, "tip", null, 347.7, false)]    // no tree: only the owner itself
        [InlineData("tip", null, 300.0, "tip", null, 347.7, true)]
        [InlineData(null, "t", 300.0, "tip", "t", 347.7, false)]
        public void RecoveryClosesHold_Scope(
            string hold, string holdTree, double holdEnd,
            string owner, string ownerTree, double recoveryUT, bool expected)
        {
            Assert.Equal(expected, KerbalsModule.RecoveryClosesHold(
                hold, holdTree, holdEnd, owner, ownerTree, recoveryUT));
        }

        [Fact]
        public void RecoveryClosesHold_NonFiniteInputsCloseNothing()
        {
            Assert.False(KerbalsModule.RecoveryClosesHold("a", "t", 1.0, "a", "t", double.NaN));
            Assert.False(KerbalsModule.RecoveryClosesHold("a", "t", 1.0, "a", "t", double.PositiveInfinity));
            Assert.False(KerbalsModule.RecoveryClosesHold("a", "t", double.NaN, "a", "t", 5.0));
        }

        [Fact]
        public void BuildClosureRows_OnlyRecoveredCrewWithAnOpenEndedHoldInScope()
        {
            var tip = MakeRecording("tip", "t", 100, 200);
            var eva = MakeRecording("eva", "t", 150, 180, pid: 9u, guid: OtherLaunchGuid);
            var loop = MakeRecording("loop", "t", 0, 100);
            loop.LoopPlayback = true;
            var mun = MakeRecording("mun", "u", 0, 150, guid: OtherLaunchGuid);
            var recs = new List<Recording> { tip, eva, loop, mun };
            var actions = new List<GameAction>
            {
                Assignment("tip", Jeb, 100, 200, KerbalEndState.Aboard, 1),
                Assignment("eva", Jeb, 150, 180, KerbalEndState.Unknown, 2),
                Assignment("tip", Bill, 100, 200, KerbalEndState.Aboard, 3),
                Assignment("tip", "Val Kerman", 100, 200, KerbalEndState.Recovered, 4),
                Assignment("tip", "Tourist Kerman", 100, 200, KerbalEndState.Aboard, 5, "Tourist"),
                Assignment("loop", "Bob Kerman", 0, 100, KerbalEndState.Aboard, 6),
                Assignment("mun", "Gene Kerman", 0, 150, KerbalEndState.Aboard, 7),
                Assignment("tip", "Dead Kerman", 100, 200, KerbalEndState.Dead, 8),
            };
            var names = new List<string>
            {
                Jeb, Bill, "Val Kerman", "Tourist Kerman", "Bob Kerman", "Gene Kerman", "Dead Kerman"
            };

            var rows = CrewRecoveryReservationClose.BuildClosureRows(
                new List<Recording> { tip }, names, actions, recs, 250.0);

            Assert.Equal(2, rows.Count);
            Assert.Equal(Bill, rows[0].Action.KerbalName);   // name order
            Assert.Equal(1, rows[0].ClosedHolds);
            Assert.Equal(Jeb, rows[1].Action.KerbalName);
            Assert.Equal(2, rows[1].ClosedHolds);            // the TIP and the EVA
            foreach (var r in rows)
            {
                Assert.Equal(GameActionType.KerbalRecovered, r.Action.Type);
                Assert.Equal("tip", r.Action.RecordingId);
                Assert.Equal(250.0, r.Action.UT);
                Assert.Equal("Pilot", r.Action.KerbalRole);
            }
        }

        // catches: the walk closing a hold with a row whose owner is not committed, or
        // taking a later closure over an earlier one.
        [Fact]
        public void Walk_TakesTheEarliestClosureWhoseOwnerIsCommitted()
        {
            var tip = Commit(MakeRecording("tip", null, 100, 200, crew: Jeb));
            var actions = new List<GameAction>
            {
                Assignment("tip", Jeb, 100, 200, KerbalEndState.Aboard, 1),
                new GameAction { UT = 250.0, Type = GameActionType.KerbalRecovered,
                    RecordingId = "not-committed", KerbalName = Jeb, Sequence = 2 },
                new GameAction { UT = 400.0, Type = GameActionType.KerbalRecovered,
                    RecordingId = "tip", KerbalName = Jeb, Sequence = 3 },
                new GameAction { UT = 300.0, Type = GameActionType.KerbalRecovered,
                    RecordingId = "tip", KerbalName = Jeb, Sequence = 4 },
            };
            var module = new KerbalsModule();
            module.Reset();
            module.PrePass(actions, 1000.0);
            foreach (var a in actions) module.ProcessAction(a);
            module.PostWalk();

            Assert.Equal(300.0, module.Reservations[Jeb].ReservedUntilUT);
            Assert.False(module.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("Reservation bounded by recovery: 'Jebediah Kerman' recording 'tip'"));
            Assert.NotNull(tip);
        }

        [Fact]
        public void Walk_DeadAndLoopChainHoldsAreNotBounded()
        {
            Commit(MakeRecording("dead", null, 100, 200, crew: Jeb));
            var actions = new List<GameAction>
            {
                Assignment("dead", Jeb, 100, 200, KerbalEndState.Dead, 1),
                new GameAction { UT = 300.0, Type = GameActionType.KerbalRecovered,
                    RecordingId = "dead", KerbalName = Jeb, Sequence = 2 },
            };
            var module = new KerbalsModule();
            module.Reset();
            module.PrePass(actions, 1000.0);
            foreach (var a in actions) module.ProcessAction(a);
            module.PostWalk();

            Assert.True(module.Reservations[Jeb].IsPermanent);
            Assert.True(module.IsReservedNow(Jeb));
        }

        [Fact]
        public void CollectRecoveryClosures_IgnoresIncompleteRows()
        {
            var into = new Dictionary<string, List<KerbalsModule.RecoveryClosure>>();
            int n = KerbalsModule.CollectRecoveryClosures(new List<GameAction>
            {
                new GameAction { UT = 5, Type = GameActionType.KerbalRecovered, RecordingId = "r", KerbalName = Jeb },
                new GameAction { UT = 6, Type = GameActionType.KerbalRecovered, RecordingId = "r", KerbalName = null },
                new GameAction { UT = 7, Type = GameActionType.KerbalRecovered, RecordingId = null, KerbalName = Jeb },
                new GameAction { UT = double.NaN, Type = GameActionType.KerbalRecovered, RecordingId = "r", KerbalName = Jeb },
                new GameAction { UT = 8, Type = GameActionType.KerbalExperience, RecordingId = "r", KerbalName = Jeb },
                null,
            }, into);

            Assert.Equal(1, n);
            Assert.Single(into[Jeb]);
            Assert.Equal(5.0, into[Jeb][0].RecoveryUT);
        }

        // ------------------------------------------------------------------
        // Persistence and ledger plumbing
        // ------------------------------------------------------------------

        [Fact]
        public void KerbalRecovered_SerializationRoundTrip()
        {
            var original = new GameAction
            {
                UT = 347.7,
                Type = GameActionType.KerbalRecovered,
                RecordingId = TipId,
                KerbalName = Jeb,
                KerbalRole = "Pilot",
                Sequence = 12
            };
            var parent = new ConfigNode("ROOT");
            original.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");
            Assert.NotNull(node);
            var back = GameAction.DeserializeFrom(node);

            Assert.Equal(GameActionType.KerbalRecovered, back.Type);
            Assert.Equal(347.7, back.UT);
            Assert.Equal(TipId, back.RecordingId);
            Assert.Equal(Jeb, back.KerbalName);
            Assert.Equal("Pilot", back.KerbalRole);
            Assert.Equal(12, back.Sequence);
            Assert.Equal(original.ActionId, back.ActionId);
            Assert.Equal("34", node.GetValue("type"));
        }

        [Fact]
        public void KerbalRecovered_DedupKeyIsPerOwnerAndKerbal()
        {
            var a = new GameAction { Type = GameActionType.KerbalRecovered, RecordingId = "r", KerbalName = Jeb };
            var b = new GameAction { Type = GameActionType.KerbalRecovered, RecordingId = "r", KerbalName = Bill };
            Assert.Equal("r|" + Jeb, LedgerOrchestrator.GetActionKey(a));
            Assert.NotEqual(LedgerOrchestrator.GetActionKey(a), LedgerOrchestrator.GetActionKey(b));
        }
    }
}
