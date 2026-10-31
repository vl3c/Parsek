using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REFLY-MERGED-DEATH-PERMANENT-VIA-SUPERSEDED-CO-ROW (live lane RW-7): a crewed
    /// flight whose recording spans the rewind point (crew boarded at launch, the pod
    /// flew on past the decouple and ended Landed with the kerbal aboard) is re-flown
    /// from the separation and the re-fly kills the kerbal with stock crew respawn on.
    /// The merge must retire the replaced flight's open-ended Aboard row (a loss row,
    /// design section 2.12), so the death keeps its stock respawn instead of reading
    /// as a permanent loss through a co-row of a flight that no longer exists, in
    /// session and after the next load's re-derivation. Drives the real split, the
    /// real tombstone pass, the real re-derivation and the real reservation walk.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyMergedDeathCoRowTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private const string Jeb = "Jebediah Kerman";
        private const double LaunchUT = 8.0;
        private const double RewindUT = 34.0;
        private const double OriginEndUT = 53.0;
        private const double DeathUT = 144.0;
        private const double Timer = 7200.0;

        public ReFlyMergedDeathCoRowTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.SuppressLogging = true;
        }

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, altitude = 50.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
        }

        private static ConfigNode CrewSnapshot(string crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", crew);
            return snapshot;
        }

        private static Recording Flight(string id, string treeId, double[] uts,
            TerminalState terminal, KerbalEndState endState)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "G " + id,
                TreeId = treeId,
                MergeState = MergeState.Immutable,
                TerminalStateValue = terminal,
            };
            var frames = new List<TrajectoryPoint>();
            foreach (var ut in uts) { rec.Points.Add(PointAt(ut)); frames.Add(PointAt(ut)); }
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = uts[0], endUT = uts[uts.Length - 1], sampleRateHz = 1f,
                minAltitude = float.NaN, maxAltitude = float.NaN, frames = frames,
            });
            rec.GhostVisualSnapshot = CrewSnapshot(Jeb);
            if (endState != KerbalEndState.Dead)
                rec.VesselSnapshot = CrewSnapshot(Jeb);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState> { [Jeb] = endState };
            rec.CrewEndStatesResolved = true;
            return rec;
        }

        private static void Migrate()
        {
            MethodInfo method = typeof(LedgerOrchestrator).GetMethod(
                "MigrateKerbalAssignments", BindingFlags.Static | BindingFlags.NonPublic);
            method.Invoke(null, null);
            EffectiveState.ResetCachesForTesting();
        }

        private static void UseRespawnOn()
        {
            KerbalsModule.CrewRespawnPolicyProviderForTesting =
                (out bool respawns, out double timer) => { respawns = true; timer = Timer; return true; };
        }

        private static RecordingTree InstallOriginalFlight(TerminalState terminal, KerbalEndState endState)
        {
            var origin = Flight("rec_pod", "tree_g",
                new[] { LaunchUT, RewindUT - 0.08, RewindUT, OriginEndUT }, terminal, endState);
            var tree = new RecordingTree
            {
                Id = "tree_g", TreeName = "tree_g",
                RootRecordingId = origin.RecordingId, ActiveRecordingId = origin.RecordingId,
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedTreeForTesting(tree);
            Migrate(); // the original flight's rows as a real save holds them
            return tree;
        }

        private static void AddReFly(RecordingTree tree, TerminalState terminal, KerbalEndState endState,
            double endUT)
        {
            var fork = Flight("rec_fork", "tree_g", new[] { RewindUT, 80.0, endUT }, terminal, endState);
            if (endState == KerbalEndState.Dead)
            {
                fork.CrewDeathRespawns = true;
                fork.CrewDeathRespawnSeconds = Timer;
            }
            tree.AddOrReplaceRecording(fork);
            RecordingStore.AddCommittedInternal(fork);
            Migrate(); // the tree commit files the attempt's own rows
        }

        /// <summary>The merge tail: split the origin at the rewind point, supersede the
        /// TIP with the re-fly, tombstone the retire set, end the session.</summary>
        private static string MergeReFly(out ParsekScenario scenario)
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_rw7", TreeId = "tree_g", ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = "rec_pod", SupersedeTargetId = "rec_pod",
                RewindPointUT = RewindUT, InvokedUT = RewindUT,
            };
            scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();

            var split = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, scenario);
            Assert.False(split.Skipped);
            string tipId = split.TipRecordingId;
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_rw7", OldRecordingId = tipId, NewRecordingId = "rec_fork", UT = RewindUT,
            });
            SupersedeCommit.CommitTombstones(marker, new List<string> { tipId },
                "rec_fork", RewindUT, "2026-10-28T00:00:00Z", scenario);
            scenario.ActiveReFlySessionMarker = null;
            EffectiveState.ResetCachesForTesting();
            return tipId;
        }

        private KerbalsModule.KerbalReservation WalkJeb(double clockUT)
        {
            logLines.Clear();
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(clockUT, "rw7-test");
            var kerbals = LedgerOrchestrator.Kerbals;
            Assert.NotNull(kerbals);
            Assert.True(kerbals.Reservations.ContainsKey(Jeb), "no reservation for Jeb");
            return kerbals.Reservations[Jeb];
        }

        private void AssertRespawnPending(KerbalsModule.KerbalReservation r, string phase)
        {
            Assert.False(r.IsPermanent, "respawn-on re-fly death read permanent " + phase);
            Assert.Equal(DeathUT + Timer, r.DeathRespawnUT);
            Assert.Equal(DeathUT + Timer, r.ReservedUntilUT);
            Assert.True(double.IsNaN(r.OpenEndedCoRowStartUT),
                "open-ended co-row survived the merge " + phase + ": " + r.OpenEndedCoRowStartUT);
            Assert.True(KerbalsModule.IsRespawnPendingHold(r));
            Assert.True(KerbalsModule.IsLossHold(r), "the re-fly death is a loss " + phase);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Death hold: 'Jebediah Kerman' recording 'rec_fork' respawn scheduled"));
            Assert.DoesNotContain(logLines, l => l.Contains("respawn suppressed: open-ended co-row"));
        }

        private static List<GameAction> CrewRows(string recordingId)
        {
            return Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment
                && a.RecordingId == recordingId).ToList();
        }

        private static List<GameAction> EffectiveOpenEndedRows()
        {
            return EffectiveState.ComputeELS().Where(a => a.Type == GameActionType.KerbalAssignment
                && a.KerbalName == Jeb
                && (a.KerbalEndStateField == KerbalEndState.Aboard
                    || a.KerbalEndStateField == KerbalEndState.Unknown)).ToList();
        }

        // catches: the replaced flight's Aboard row (boarded at launch, before the rewind)
        // staying on HEAD, outside the closure, so the merge could not retire it and its
        // open-ended co-row turned the re-fly's respawn-on death into a permanent loss.
        [Fact]
        public void RW7Shape_MergedReFlyDeath_RespawnOn_InSession_SchedulesTheRespawn()
        {
            UseRespawnOn();
            var tree = InstallOriginalFlight(TerminalState.Landed, KerbalEndState.Aboard);
            string boardedRowId = CrewRows("rec_pod").Single().ActionId;
            AddReFly(tree, TerminalState.Destroyed, KerbalEndState.Dead, DeathUT);
            logLines.Clear();

            ParsekScenario scenario;
            string tipId = MergeReFly(out scenario);

            // The crew's end-of-flight fate followed the terminal to TIP and was retired.
            Assert.Empty(CrewRows("rec_pod"));
            Assert.Equal(tipId, Ledger.Actions.Single(a => a.ActionId == boardedRowId).RecordingId);
            Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == boardedRowId);
            Assert.Empty(EffectiveOpenEndedRows());
            Assert.Contains(logLines, l => l.Contains("[Splitter]")
                && l.Contains("pre-rewind-boarded open-ended crew row(s) retagged to TIP by endUT"));
            Assert.Contains(logLines, l => l.Contains("[LedgerSwap]")
                && l.Contains("open-ended crew interval screened by endUT")
                && l.Contains(boardedRowId) && l.Contains("-> in scope"));

            AssertRespawnPending(WalkJeb(200.0), "in session");
        }

        // catches: the next load's re-derivation filing a fresh, untombstoned Aboard row
        // on the superseded TIP (the RW-7 save: act_... recordingId=<TIP> endState=Aboard
        // ut=<TIP start>), which made the death permanent again after every load.
        [Fact]
        public void RW7Shape_MergedReFlyDeath_RespawnOn_AfterTwoReloads_StillSchedulesTheRespawn()
        {
            UseRespawnOn();
            var tree = InstallOriginalFlight(TerminalState.Landed, KerbalEndState.Aboard);
            string boardedRowId = CrewRows("rec_pod").Single().ActionId;
            AddReFly(tree, TerminalState.Destroyed, KerbalEndState.Dead, DeathUT);
            ParsekScenario scenario;
            string tipId = MergeReFly(out scenario);

            Migrate();
            Migrate();

            // TIP re-derives its own Aboard row under the retired row's id; HEAD hands off
            // Recovered at the cut (a finite hold) under a fresh id.
            var tipRow = CrewRows(tipId).Single();
            Assert.Equal(boardedRowId, tipRow.ActionId);
            Assert.Equal(KerbalEndState.Aboard, tipRow.KerbalEndStateField);
            var headRow = CrewRows("rec_pod").Single();
            Assert.Equal(KerbalEndState.Recovered, headRow.KerbalEndStateField);
            Assert.Empty(EffectiveOpenEndedRows());

            AssertRespawnPending(WalkJeb(200.0), "after two reloads");
            // Past the stock respawn he is free again (stock returns him Available).
            var later = WalkJeb(DeathUT + Timer + 10.0);
            Assert.False(LedgerOrchestrator.Kerbals.IsReservedNow(Jeb), "still held after the respawn");
            Assert.False(later.IsPermanent);
        }

        // catches: the same retirement gap without a death - a crewed slot re-flown to a
        // recovery left the kerbal held forever by the replaced flight's Aboard row.
        [Fact]
        public void CrewedSlotReFlyRecovered_ReplacedFlightDoesNotHoldTheKerbalForever()
        {
            UseRespawnOn();
            var tree = InstallOriginalFlight(TerminalState.Landed, KerbalEndState.Aboard);
            AddReFly(tree, TerminalState.Recovered, KerbalEndState.Recovered, 90.0);
            ParsekScenario scenario;
            MergeReFly(out scenario);

            foreach (string phase in new[] { "in session", "after a reload" })
            {
                if (phase != "in session") Migrate();
                var r = WalkJeb(500.0);
                Assert.False(r.IsPermanent, phase);
                Assert.Equal(90.0, r.ReservedUntilUT);
                Assert.False(LedgerOrchestrator.Kerbals.IsReservedNow(Jeb), "held forever " + phase);
                Assert.Empty(EffectiveOpenEndedRows());
            }
        }

        // Mirror: a GENUINELY live co-row - another committed flight, not superseded,
        // that carries the same kerbal open-ended from before the respawn - still makes
        // the respawn-on death a permanent loss (owner ruling 2026-09-26).
        [Fact]
        public void MirrorLiveCoRow_AnotherCommittedFlightAboardBeforeTheRespawn_StaysPermanent()
        {
            UseRespawnOn();
            var tree = InstallOriginalFlight(TerminalState.Landed, KerbalEndState.Aboard);
            AddReFly(tree, TerminalState.Destroyed, KerbalEndState.Dead, DeathUT);
            ParsekScenario scenario;
            MergeReFly(out scenario);

            var station = Flight("rec_station", "tree_s", new[] { 2000.0, 2100.0, 2200.0 },
                TerminalState.Orbiting, KerbalEndState.Aboard);
            var stree = new RecordingTree
            {
                Id = "tree_s", TreeName = "tree_s",
                RootRecordingId = station.RecordingId, ActiveRecordingId = station.RecordingId,
            };
            stree.AddOrReplaceRecording(station);
            RecordingStore.AddCommittedInternal(station);
            RecordingStore.AddCommittedTreeForTesting(stree);
            Migrate();

            var r = WalkJeb(3000.0);
            Assert.True(r.IsPermanent, "a live open-ended co-row across the dead window must be permanent");
            Assert.True(double.IsPositiveInfinity(r.ReservedUntilUT));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Death hold: 'Jebediah Kerman' respawn suppressed: open-ended co-row")
                && l.Contains("coRowStartUT=2000.0"));
        }

        // The attribution key (one key for both sides of the seam): an open-ended crew row
        // is keyed by its EndUT like a death; a Recovered row and a NaN EndUT keep the UT.
        [Theory]
        [InlineData(KerbalEndState.Aboard, 8f, 53f, 53.0)]
        [InlineData(KerbalEndState.Unknown, 8f, 53f, 53.0)]
        [InlineData(KerbalEndState.Dead, 8f, 53f, 53.0)]
        [InlineData(KerbalEndState.Recovered, 8f, 53f, 8.0)]
        [InlineData(KerbalEndState.Aboard, 8f, float.NaN, 8.0)]
        public void ComputeAttributionUT_OpenEndedCrewRowsAreKeyedByTheirEnd(
            KerbalEndState endState, float startUT, float endUT, double expected)
        {
            var row = new GameAction
            {
                ActionId = "act_key", Type = GameActionType.KerbalAssignment, RecordingId = "rec_x",
                KerbalName = Jeb, UT = startUT, StartUT = startUT, EndUT = endUT,
                KerbalEndStateField = endState,
            };
            Assert.Equal(expected, TombstoneAttributionHelper.ComputeAttributionUT(row));
            Assert.Equal(expected, TombstoneAttributionHelper.ComputeAttributionUT(row, new List<GameAction> { row }));
            bool retag = RecordingTreeSplitter.ShouldRetagLedgerActionToTip(row, "rec_x", RewindUT, new List<GameAction> { row });
            bool kept = TombstoneAttributionHelper.IsPreRewindAttributedAction(row, RewindUT, new List<GameAction> { row });
            Assert.Equal(expected >= RewindUT, retag);
            Assert.Equal(!retag, kept);
        }

        // The tail-overlap shape of RW-7 (the TIP's samples start 0.08 s before the rewind
        // point, so a row dated at the TIP's start sits before the cutoff): an open-ended
        // crew row of a closure recording is in the retire set whenever the flight ends
        // after the cutoff, and one whose flight ended before it is still kept.
        [Fact]
        public void BuildRetireSet_OpenEndedCrewRowBoardedBeforeTheCutoff_IsInScopeByItsEnd()
        {
            var straddling = new GameAction
            {
                ActionId = "act_straddle", Type = GameActionType.KerbalAssignment, RecordingId = "rec_tip",
                KerbalName = Jeb, UT = RewindUT - 0.08, StartUT = (float)(RewindUT - 0.08), EndUT = 53f,
                KerbalEndStateField = KerbalEndState.Aboard,
            };
            var endedBefore = new GameAction
            {
                ActionId = "act_before", Type = GameActionType.KerbalAssignment, RecordingId = "rec_tip",
                KerbalName = "Bill Kerman", UT = 8.0, StartUT = 8f, EndUT = 20f,
                KerbalEndStateField = KerbalEndState.Aboard,
            };
            var scan = SupersedeCommit.BuildRetireSet(new List<string> { "rec_tip" }, RewindUT,
                new List<GameAction> { straddling, endedBefore }, null, logDecisions: true);
            Assert.Contains(scan.Rows, a => a.ActionId == "act_straddle");
            Assert.DoesNotContain(scan.Rows, a => a.ActionId == "act_before");
            Assert.Equal(1, scan.OpenCrewIntervalsInScopeByEndUT);
            Assert.Equal(1, scan.PreRewindKept);
            Assert.True(SupersedeCommit.IsLossRow(straddling));
        }
    }
}
