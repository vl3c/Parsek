using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RP-REWIND-STAGED-LISTS-FROM-STALE-PERSISTENT: a plain rewind's OnLoad reads
    /// persistent.sfs as last written (<c>SpaceCenterMain.Start</c> reloads it), so
    /// <c>LoadRewindStagingState</c> rebuilt RECORDING_SUPERSEDES,
    /// RECORDING_REWIND_RETIREMENTS, LEDGER_TOMBSTONES and the merge journal from a file of
    /// unknown age while the recordings and the ledger stay in memory. These tests drive the
    /// real save / load of the staging node and the same OnLoad sequence
    /// (staging load, RP carry, staged-list carry, supersede re-apply).
    /// </summary>
    [Collection("Sequential")]
    public class RewindStagedListsCarryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;
        private readonly GameScenes previousScene;

        public RewindStagedListsCarryTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            previousScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = previousScene;
            RecordingStore.ResetRewindFlags();
            RewindContext.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingsTableUI.ClearAllRewindSlotCanInvokeLogState();
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------- helpers ----------------------------------------------

        private const double RewindAdjustedUT = 15.0;

        private static Recording Rec(string id, double startUT, MergeState state)
        {
            var rec = new Recording { RecordingId = id, VesselName = id, MergeState = state };
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            rec.Points.Add(new TrajectoryPoint { ut = startUT + 10.0 });
            return rec;
        }

        private static void InstallTree(string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree { Id = treeId, TreeName = "Test_" + treeId };
            foreach (var rec in recordings)
            {
                rec.TreeId = treeId;
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            RecordingStore.CommittedTrees.Add(tree);
        }

        private static RecordingSupersedeRelation Rel(string id, string oldId, string newId, double ut)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = id,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = ut,
                CreatedRealTime = "2026-09-26T00:00:00.0000000Z",
            };
        }

        private static LedgerTombstone Tomb(string id, string actionId, string retiringId, double ut)
        {
            return new LedgerTombstone
            {
                TombstoneId = id,
                ActionId = actionId,
                RetiringRecordingId = retiringId,
                UT = ut,
                CreatedRealTime = "2026-09-26T00:00:00.0000000Z",
            };
        }

        private static RecordingRewindRetirement Retire(string id, string recId, string restoredId)
        {
            return new RecordingRewindRetirement
            {
                RetirementId = id,
                RecordingId = recId,
                RestoredRecordingId = restoredId,
                SourceSupersedeRelationId = "rsr_prior",
                RewindUT = 5.0,
                CreatedUT = 6.0,
                CreatedRealTime = "2026-09-26T00:00:00.0000000Z",
                Reason = RecordingRewindRetirement.DefaultReason,
            };
        }

        private static ParsekScenario NewScenario()
        {
            return new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
        }

        private static void Invoke(ParsekScenario scenario, string method, ConfigNode node)
        {
            MethodInfo mi = typeof(ParsekScenario).GetMethod(
                method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            mi.Invoke(scenario, new object[] { node });
        }

        /// <summary>persistent.sfs as the last OnSave wrote it, from a separate scenario.</summary>
        private static ConfigNode WriteStalePersistent(ParsekScenario staleState)
        {
            var node = new ConfigNode("SCENARIO");
            Invoke(staleState, "SaveRewindStagingState", node);
            return node;
        }

        private static void ArmRewindToLaunch(string ownerRecordingId)
        {
            RewindContext.BeginRewind(30.0, default(BudgetSummary), 0, 0, 0f);
            RewindContext.SetAdjustedUT(RewindAdjustedUT);
            RecordingStore.RewindReplayTargetRecordingId = ownerRecordingId;
        }

        /// <summary>
        /// The rewind's side of the load, in production order: <c>ExecuteRewindSaveLoad</c>
        /// captures the RP list, logs the rewound tree's supersede rows (keeping all of them),
        /// captures the staged lists; then OnLoad loads the staging state from the (stale)
        /// node and runs the RP carry and the staged-list carry.
        /// </summary>
        private static void SimulateRewindLoad(ParsekScenario scenario, ConfigNode stalePersistent)
        {
            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");
            Recording owner = RecordingStore.CommittedRecordings
                .First(r => r.RecordingId == RecordingStore.RewindReplayTargetRecordingId);
            RecordingStore.LogSupersedesKeptAcrossRewind(owner, RewindAdjustedUT);
            RecordingStore.CaptureRewindStagedListsForRewind(scenario, "Rewind");
            Invoke(scenario, "LoadRewindStagingState", stalePersistent);
            RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario);
            RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad(scenario);
        }

        /// <summary>
        /// Tree A is rewound to launch; its own Re-Fly (rel_a, an open fork after the rewind
        /// UT, which the rewind keeps) is in memory AND on disk. With
        /// <paramref name="withDanglingRow"/> memory also holds rel_dangle, a tree-A row whose
        /// fork is in no committed list, which the rewind keeps too. Tree B's Re-Fly merged after the last persistent write, so
        /// only memory holds rel_b / tomb_b; a prior rewind's retirement rrt_prior is
        /// memory-only too. rel_gone is on disk but memory already removed it (a tree
        /// discard purge after the last write).
        /// </summary>
        private ParsekScenario BuildTwoTreeCase(out ConfigNode stalePersistent,
            bool withDanglingRow = false)
        {
            InstallTree("tree_a",
                Rec("rec_a_root", 0.0, MergeState.Immutable),
                Rec("rec_a_origin", 50.0, MergeState.Immutable),
                Rec("rec_a_fork", 60.0, MergeState.CommittedProvisional));
            InstallTree("tree_b",
                Rec("rec_b_origin", 200.0, MergeState.Immutable),
                Rec("rec_b_fork", 210.0, MergeState.Immutable));

            var stale = NewScenario();
            stale.RecordingSupersedes.Add(Rel("rel_a", "rec_a_origin", "rec_a_fork", 60.0));
            stale.RecordingSupersedes.Add(Rel("rel_gone", "rec_deleted_origin", "rec_deleted_fork", 70.0));
            stale.LedgerTombstones.Add(Tomb("tomb_gone", "act_deleted_death", "rec_deleted_fork", 70.0));
            stalePersistent = WriteStalePersistent(stale);

            var scenario = NewScenario();
            scenario.RecordingSupersedes.Add(Rel("rel_a", "rec_a_origin", "rec_a_fork", 60.0));
            scenario.RecordingSupersedes.Add(Rel("rel_b", "rec_b_origin", "rec_b_fork", 210.0));
            if (withDanglingRow)
                scenario.RecordingSupersedes.Add(Rel("rel_dangle", "rec_a_origin", "rec_a_lost", 65.0));
            scenario.LedgerTombstones.Add(Tomb("tomb_b", "act_b_death", "rec_b_fork", 210.0));
            scenario.RecordingRewindRetirements.Add(Retire("rrt_prior", "rec_old_fork", "rec_old_origin"));
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        // ---------- the defect -------------------------------------------

        [Fact]
        public void RewindToLaunch_OtherTreeReFlyAfterLastPersistentWrite_KeepsItsRowsFromMemory()
        {
            var scenario = BuildTwoTreeCase(out ConfigNode stalePersistent);
            ArmRewindToLaunch("rec_a_root");

            SimulateRewindLoad(scenario, stalePersistent);

            // Tree B's merge survives: its supersede row and its tombstone come from memory.
            // Tree A's own open re-fly survives the rewind too (a rewind undoes nothing).
            Assert.Equal(new[] { "rel_a", "rel_b" }, scenario.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Equal(new[] { "tomb_b" }, scenario.LedgerTombstones.Select(t => t.TombstoneId));
            // The prior rewind's retirement survives, and nothing new is retired.
            RecordingRewindRetirement prior = Assert.Single(scenario.RecordingRewindRetirements);
            Assert.Equal("rrt_prior", prior.RetirementId);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Rewind]")
                && l.Contains("Rewind: carrying staged lists across the rewind load: supersedes=2 retirements=1 tombstones=1 journal=none"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Rewind]")
                && l.Contains("Staged lists carried across rewind: supersedes installed=2 loadedFromSave=2 restored=1 staleDropped=1")
                && l.Contains("retirements installed=1 loadedFromSave=0 restored=1 staleDropped=0")
                && l.Contains("tombstones installed=1 loadedFromSave=1 restored=1 staleDropped=1")
                && l.Contains("journal installed=none loadedFromSave=none"));
            Assert.False(RecordingStore.HasRewindCarriedStagedLists);
        }

        [Fact]
        public void RewindToLaunch_RewoundTreeOpenForkAndDanglingRow_BothSurviveTheLoad()
        {
            // A rewind keeps every row of the rewound tree, the open re-fly (rel_a) and the
            // row whose fork is in no committed list (rel_dangle) alike, and the carry hands
            // them through the load unchanged.
            var scenario = BuildTwoTreeCase(out ConfigNode stalePersistent, withDanglingRow: true);
            ArmRewindToLaunch("rec_a_root");

            SimulateRewindLoad(scenario, stalePersistent);

            Assert.Equal(new[] { "rel_a", "rel_b", "rel_dangle" },
                scenario.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Equal(new[] { "rrt_prior" }, scenario.RecordingRewindRetirements.Select(r => r.RetirementId));
            Assert.Contains(logLines, l =>
                l.Contains("Rewind keeps supersede rows: rows=2 forks=2 sealed=0 open=1 dangling=1"));
        }

        [Fact]
        public void RewindToLaunch_RowsOnlyOnDisk_AreNotResurrected()
        {
            // Mirror of the loss: a row memory removed after the last write stays removed.
            var scenario = BuildTwoTreeCase(out ConfigNode stalePersistent);
            ArmRewindToLaunch("rec_a_root");

            SimulateRewindLoad(scenario, stalePersistent);

            Assert.DoesNotContain(scenario.RecordingSupersedes, r => r.RelationId == "rel_gone");
            Assert.DoesNotContain(scenario.LedgerTombstones, t => t.TombstoneId == "tomb_gone");
        }

        [Fact]
        public void RewindToLaunch_RowOnDiskAndInMemory_AppearsOnce_AsTheInMemoryInstance()
        {
            InstallTree("tree_b",
                Rec("rec_b_origin", 200.0, MergeState.Immutable),
                Rec("rec_b_fork", 210.0, MergeState.Immutable));
            InstallTree("tree_a", Rec("rec_a_root", 0.0, MergeState.Immutable));
            var stale = NewScenario();
            stale.RecordingSupersedes.Add(Rel("rel_b", "rec_b_origin", "rec_b_fork", 210.0));
            stale.LedgerTombstones.Add(Tomb("tomb_b", "act_b_death", "rec_b_fork", 210.0));
            ConfigNode stalePersistent = WriteStalePersistent(stale);

            var scenario = NewScenario();
            var liveRel = Rel("rel_b", "rec_b_origin", "rec_b_fork", 210.0);
            var liveTomb = Tomb("tomb_b", "act_b_death", "rec_b_fork", 210.0);
            scenario.RecordingSupersedes.Add(liveRel);
            scenario.LedgerTombstones.Add(liveTomb);
            ParsekScenario.SetInstanceForTesting(scenario);
            ArmRewindToLaunch("rec_a_root");

            SimulateRewindLoad(scenario, stalePersistent);

            Assert.Same(liveRel, Assert.Single(scenario.RecordingSupersedes));
            Assert.Same(liveTomb, Assert.Single(scenario.LedgerTombstones));
        }

        // ---------- the merge journal ------------------------------------

        [Fact]
        public void RewindToLaunch_StaleJournalOnDisk_MemoryFinishedIt_IsDropped()
        {
            // A load-time finisher completes a journal in memory with a deferred save, so
            // persistent.sfs still holds it mid-phase. Read back by the rewind, it would sit
            // in memory (the rewind branch runs no finisher) and be driven again on the
            // next load, after the rewind has cleared the Re-Fly marker it needs.
            var scenario = BuildTwoTreeCase(out _);
            var stale = NewScenario();
            stale.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "mj_stale",
                SessionId = "sess_done",
                Phase = MergeJournal.Phases.Durable1Done,
            };
            ConfigNode stalePersistent = WriteStalePersistent(stale);
            Assert.Null(scenario.ActiveMergeJournal);
            ArmRewindToLaunch("rec_a_root");

            SimulateRewindLoad(scenario, stalePersistent);

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Contains(logLines, l =>
                l.Contains("journal installed=none loadedFromSave=mj_stale@Durable1Done"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Rewind]")
                && l.Contains("Dropped stale merge journal mj_stale@Durable1Done read from the save"));
        }

        [Fact]
        public void InFlightJournalAtCapture_LoadedJournalStands_AndWarns()
        {
            // Unreachable in production (both plain-rewind entry points refuse an in-flight
            // journal); the carry then keeps the pre-carry behaviour for the journal only.
            var scenario = BuildTwoTreeCase(out ConfigNode stalePersistent);
            scenario.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "mj_live",
                SessionId = "sess_live",
                Phase = MergeJournal.Phases.Supersede,
            };
            ArmRewindToLaunch("rec_a_root");

            SimulateRewindLoad(scenario, stalePersistent);

            Assert.Null(scenario.ActiveMergeJournal);   // the stale node held none
            Assert.Equal(new[] { "rel_a", "rel_b" }, scenario.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Rewind]")
                && l.Contains("Staged-list carry kept the loaded merge journal")
                && l.Contains("mj_live@Supersede"));
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData(MergeJournal.Phases.Complete, true)]
        [InlineData(MergeJournal.Phases.Begin, false)]
        [InlineData(MergeJournal.Phases.Durable1Done, false)]
        public void ShouldCarryMergeJournal_Pure(string phase, bool expected)
        {
            MergeJournal journal = phase == null ? null : new MergeJournal { Phase = phase };
            Assert.Equal(expected, RecordingStore.ShouldCarryMergeJournal(journal));
        }

        // ---------- gating (mirrors the RP carry) -------------------------

        [Fact]
        public void NonRewindLoad_TheRewindCarryStaysOut_AndDropsAStrandedCapture()
        {
            // Mirror direction: the plain-rewind carry installs nothing on a load that is not a
            // rewind and drops a stranded capture. Such a load takes its lists from whatever owns
            // them on that load kind: the in-session handoff (InSessionStagedListsCarryTests), the
            // Re-Fly bundle, or the loaded node on a cold load, which is what this cell leaves.
            var scenario = BuildTwoTreeCase(out _);
            var stale = NewScenario();
            stale.ActiveMergeJournal = new MergeJournal { JournalId = "mj_disk", Phase = MergeJournal.Phases.Split };
            ConfigNode fromSave = WriteStalePersistent(stale);
            RecordingStore.CaptureRewindStagedListsForRewind(scenario, "Rewind");

            Invoke(scenario, "LoadRewindStagingState", fromSave);
            bool installed = RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad(scenario);

            Assert.False(installed);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Equal("mj_disk", scenario.ActiveMergeJournal.JournalId);
            Assert.False(RecordingStore.HasRewindCarriedStagedLists);
            Assert.Contains(logLines, l =>
                l.Contains("Dropped carried staged lists without reinstalling them (reason=load-is-not-a-rewind)"));
        }

        [Fact]
        public void FailedRewindLoad_ResetRewindFlags_DropsTheCapture()
        {
            var scenario = BuildTwoTreeCase(out ConfigNode stalePersistent);
            ArmRewindToLaunch("rec_a_root");
            RecordingStore.CaptureRewindStagedListsForRewind(scenario, "Rewind");

            RecordingStore.ResetRewindFlags();

            Assert.False(RecordingStore.HasRewindCarriedStagedLists);
            Invoke(scenario, "LoadRewindStagingState", stalePersistent);
            Assert.False(RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad(scenario));
            Assert.Equal(new[] { "rel_a", "rel_gone" }, scenario.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Contains(logLines, l =>
                l.Contains("Dropped carried staged lists without reinstalling them (reason=rewind-flags-reset)"));
        }

        [Fact]
        public void Capture_IsASnapshot_LaterListMutationDoesNotLeakIn()
        {
            var scenario = BuildTwoTreeCase(out ConfigNode stalePersistent);
            ArmRewindToLaunch("rec_a_root");
            RecordingStore.CaptureRewindStagedListsForRewind(scenario, "Rewind");
            scenario.LedgerTombstones.Add(Tomb("tomb_late", "act_late", "rec_x", 1.0));

            Invoke(scenario, "LoadRewindStagingState", stalePersistent);
            RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad(scenario);

            Assert.Equal(new[] { "tomb_b" }, scenario.LedgerTombstones.Select(t => t.TombstoneId));
        }

        [Fact]
        public void NoScenarioAtCapture_CapturesNothing()
        {
            ArmRewindToLaunch("rec_a_root");
            RecordingStore.CaptureRewindStagedListsForRewind(null, "Warp-to-game-start");
            Assert.False(RecordingStore.HasRewindCarriedStagedLists);
        }

        [Fact]
        public void MergeCarriedStagedList_Pure_CountsDriftBothWays_NoUnion()
        {
            var carried = new List<LedgerTombstone>
            {
                Tomb("a", "x", "r", 1), null, Tomb("b", "x", "r", 2), Tomb(null, "x", "r", 3),
            };
            var loaded = new List<LedgerTombstone>
            {
                Tomb("b", "x", "r", 2), Tomb("c", "x", "r", 3), Tomb("d", "x", "r", 4),
            };

            var merged = RecordingStore.MergeCarriedStagedList(
                carried, loaded, t => t.TombstoneId, out int restored, out int staleDropped);

            Assert.Equal(new[] { "a", "b", null }, merged.Select(t => t.TombstoneId));
            Assert.Same(carried[2], merged[1]);
            Assert.Equal(1, restored);        // a
            Assert.Equal(2, staleDropped);    // c, d
        }

        // ---------- source wiring -----------------------------------------

        [Fact]
        public void ExecuteRewindSaveLoad_CapturesTheStagedLists_AfterTheSupersedeAudit_BeforeTheSceneLoad()
        {
            string body = RewindPointSurvivesRewindTests.MethodBody(
                RewindPointSurvivesRewindTests.ReadSource("RecordingStore.cs"),
                "private static bool ExecuteRewindSaveLoad(");
            int audit = body.IndexOf("LogSupersedesKeptAcrossRewind(supersedeAuditOwner", StringComparison.Ordinal);
            int capture = body.IndexOf("CaptureRewindStagedListsForRewind(ParsekScenario.Instance", StringComparison.Ordinal);
            int scene = body.IndexOf("HighLogic.LoadScene(GameScenes.SPACECENTER)", StringComparison.Ordinal);
            Assert.True(audit >= 0, "the pre-load supersede audit moved");
            Assert.True(capture > audit, "the staged-list capture must follow the supersede audit");
            Assert.True(scene > capture, "the staged-list capture must precede the scene load");
            // A rewind keeps every supersede row: nothing in the load removes one.
            Assert.DoesNotContain("RecordingSupersedes.Remove", body);
        }

        [Fact]
        public void OnLoad_ReinstallsTheStagedLists_AfterTheStagingLoad_BeforeTheRewindBranch()
        {
            string body = RewindPointSurvivesRewindTests.MethodBody(
                RewindPointSurvivesRewindTests.ReadSource("ParsekScenario.cs"),
                "public override void OnLoad(ConfigNode node)");
            int staging = body.IndexOf("LoadRewindStagingState(node);", StringComparison.Ordinal);
            int reinstall = body.IndexOf("RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad(this);", StringComparison.Ordinal);
            int rewindBranch = body.IndexOf("HandleRewindOnLoad(node, recordings);", StringComparison.Ordinal);
            Assert.True(staging >= 0 && reinstall > staging,
                "OnLoad must reinstall the carried staged lists after LoadRewindStagingState");
            Assert.True(rewindBranch > reinstall);
        }

        [Fact]
        public void ResetRewindFlags_DropsTheStagedCapture()
        {
            string body = RewindPointSurvivesRewindTests.MethodBody(
                RewindPointSurvivesRewindTests.ReadSource("RecordingStore.cs"),
                "internal static void ResetRewindFlags()");
            Assert.Contains("ClearRewindCarriedStagedLists(\"rewind-flags-reset\");", body);
        }
    }
}
