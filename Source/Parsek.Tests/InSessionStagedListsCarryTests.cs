using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// QUICKLOAD-REFLY-LISTS-REVERT-WHILE-RECORDINGS-STAY (roadmap TA-1): an in-session load keeps
    /// the committed recordings and the ledger in memory, so the Re-Fly bookkeeping that indexes
    /// them (supersede rows, rewind retirements, tombstones, the merge journal, committed trees'
    /// rewind points, and on a Discard Re-fly the marker) comes from memory too, through
    /// <see cref="InSessionStagedStateHandoff"/>. Each cell drives the production OnLoad sequence
    /// headlessly: the old instance's capture (the method <c>OnDestroy</c> calls), the staging
    /// load of the loaded node, the plain-rewind carries, step A, the tree restore's detach where
    /// the case has one, step B and, where the case needs it, <see cref="LoadTimeSweep.Run"/>.
    /// The OnLoad order itself is pinned by <see cref="InSessionHandoffWiringGateTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class InSessionStagedListsCarryTests : IDisposable
    {
        private const string SaveFolder = "insession-carry-test";
        private const string TreeId = "tree_t";
        private const string OriginId = "rec_a";
        private const string ForkId = "rec_a_prime";
        private const string DeathActionId = "act_a_death";
        private const string SessionS = "sess_s";

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> deletedRpIds = new List<string>();
        private readonly GameScenes previousScene;
        private string currentSaveFolder;
        private readonly bool previousInitialLoadDone;
        private readonly bool priorStoreSuppress;

        public InSessionStagedListsCarryTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = false;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            ResetAll();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            RewindPointReaper.DeleteQuicksaveForTesting = rpId =>
            {
                deletedRpIds.Add(rpId);
                return true;
            };

            previousScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            currentSaveFolder = SaveFolder;
            InSessionStagedStateHandoff.SaveFolderProviderForTesting = () => currentSaveFolder;
            previousInitialLoadDone = InitialLoadDone;
            InitialLoadDone = true;
        }

        public void Dispose()
        {
            InitialLoadDone = previousInitialLoadDone;
            HighLogic.LoadedScene = previousScene;
            ResetAll();
            KspStatePatcher.ResetForTesting();
            RecordingsTableUI.ClearAllRewindSlotCanInvokeLogState();
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.ResetRewindFlags();
            RewindContext.ResetForTesting();
            RewindInvokeContext.Clear();
            InSessionStagedStateHandoff.ResetForTesting();
            DiscardReFlyLoadIntent.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindPointReaper.ResetTestOverrides();
            MarkerValidator.ResetTestOverrides();
            RevertInterceptor.ResetTestOverrides();
            ReFlyRevertDialog.ResetForTesting();
            TreeDiscardPurge.ResetTestOverrides();
            RecordingStore.SaveGameForTesting = null;
        }

        private static bool InitialLoadDone
        {
            get => (bool)InitialLoadDoneField.GetValue(null);
            set => InitialLoadDoneField.SetValue(null, value);
        }

        private static FieldInfo InitialLoadDoneField
        {
            get
            {
                FieldInfo f = typeof(ParsekScenario).GetField(
                    "initialLoadDone", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(f);
                return f;
            }
        }

        // ---------- fixture builders ----------------------------------------

        private static Recording Rec(string id, double startUT, MergeState state, string treeId = TreeId)
        {
            var rec = new Recording { RecordingId = id, VesselName = id, MergeState = state, TreeId = treeId };
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            rec.Points.Add(new TrajectoryPoint { ut = startUT + 10.0 });
            return rec;
        }

        /// <summary>A committed tree holding <paramref name="recs"/> and one branch point per rewind point id.</summary>
        private static RecordingTree InstallCommittedTree(string treeId, string[] rewindPointIds, params Recording[] recs)
        {
            var tree = BuildTree(treeId, rewindPointIds, recs);
            foreach (var rec in recs)
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static RecordingTree BuildTree(string treeId, string[] rewindPointIds, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null,
            };
            foreach (var rec in recs)
            {
                rec.TreeId = treeId;
                tree.AddOrReplaceRecording(rec);
            }
            if (rewindPointIds != null)
            {
                foreach (string rpId in rewindPointIds)
                {
                    tree.BranchPoints.Add(new BranchPoint
                    {
                        Id = BpFor(rpId),
                        Type = BranchPointType.JointBreak,
                        RewindPointId = rpId,
                    });
                }
            }
            return tree;
        }

        private static string BpFor(string rpId) => "bp_" + rpId;

        private static RewindPoint Rp(string id, bool sessionProvisional = false, string creatingSessionId = null,
            double ut = 50.0, string originId = OriginId)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = BpFor(id),
                UT = ut,
                QuicksaveFilename = id + ".sfs",
                SessionProvisional = sessionProvisional,
                CreatingSessionId = creatingSessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = originId, Controllable = true },
                },
            };
        }

        private static RecordingSupersedeRelation Rel(string id, string oldId, string newId)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = id,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = 60.0,
                CreatedRealTime = "2026-10-06T00:00:00.0000000Z",
            };
        }

        private static LedgerTombstone Tomb(string id, string actionId, string retiringId)
        {
            return new LedgerTombstone
            {
                TombstoneId = id,
                ActionId = actionId,
                RetiringRecordingId = retiringId,
                UT = 60.0,
                CreatedRealTime = "2026-10-06T00:00:00.0000000Z",
            };
        }

        private static ReFlySessionMarker Marker(string sessionId, string activeId, string originId, string rpId)
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = TreeId,
                ActiveReFlyRecordingId = activeId,
                OriginChildRecordingId = originId,
                RewindPointId = rpId,
                InvokedUT = 55.0,
                InvokedRealTime = "2026-10-06T00:00:00.000Z",
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

        /// <summary>The loaded save's scenario node, written by the real staging writer.</summary>
        private static ConfigNode SaveNode(Action<ParsekScenario> fill)
        {
            var saved = NewScenario();
            fill?.Invoke(saved);
            var node = new ConfigNode("SCENARIO");
            Invoke(saved, "SaveRewindStagingState", node);
            return node;
        }

        /// <summary>The live (memory) scenario, installed as Instance.</summary>
        private static ParsekScenario Memory(Action<ParsekScenario> fill)
        {
            var memory = NewScenario();
            fill?.Invoke(memory);
            ParsekScenario.SetInstanceForTesting(memory);
            return memory;
        }

        /// <summary>
        /// One OnLoad in production order: the old instance's capture (what OnDestroy calls),
        /// the new instance, <c>LoadRewindStagingState</c>, the two plain-rewind carries, step A,
        /// the tree restore (<paramref name="treeRestore"/>: the detach a resumed tree does),
        /// step B, and the sweep.
        /// </summary>
        private static ParsekScenario Load(ParsekScenario memory, ConfigNode saved, EarlyLoadKind early,
            Action treeRestore = null, bool sweep = false, bool capture = true)
        {
            if (capture && !ReferenceEquals(memory, null))
                memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);
            var fresh = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(fresh);
            Invoke(fresh, "LoadRewindStagingState", saved);
            RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(fresh);
            RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad(fresh);
            fresh.ApplyInSessionStagedStateHandoffStepA(early);
            treeRestore?.Invoke();
            fresh.ApplyInSessionRewindPointPartitionStepB();
            if (sweep)
                LoadTimeSweep.Run();
            EffectiveState.ResetCachesForTesting();
            return fresh;
        }

        private static void AddDeathAction()
        {
            Ledger.AddAction(new GameAction
            {
                ActionId = DeathActionId,
                UT = 58.0,
                Type = GameActionType.ReputationPenalty,
                RecordingId = OriginId,
            });
        }

        private static bool ElsHas(string actionId)
            => EffectiveState.ComputeELS().Any(a => a.ActionId == actionId);

        private static bool ErsHas(string recordingId)
            => EffectiveState.ComputeERS().Any(r => r.RecordingId == recordingId);

        /// <summary>Tree T: origin A and its merged Re-Fly A', both committed, rp_t on its split.</summary>
        private static void InstallMergedReFlyTree()
        {
            InstallCommittedTree(TreeId, new[] { "rp_t" },
                Rec(OriginId, 50.0, MergeState.Immutable),
                Rec(ForkId, 55.0, MergeState.Immutable));
        }

        // =====================================================================
        // F9 into a save from before a Re-Fly merge (design 3.4 case 1)
        // =====================================================================

        [Fact]
        public void Quickload_SaveBeforeReFlyMerge_OriginStaysInvisible()
        {
            InstallMergedReFlyTree();
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_a", OriginId, ForkId)));
            ConfigNode saved = SaveNode(null);

            var loaded = Load(memory, saved, EarlyLoadKind.InSession);

            Assert.Equal(new[] { "rel_a" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
            Assert.False(ErsHas(OriginId), "the re-flown origin must stay superseded after the quickload");
            Assert.True(ErsHas(ForkId));
            Assert.Contains(logLines, l => l.Contains("[INFO][Rewind]")
                && l.Contains("In-session staged lists from memory:")
                && l.Contains("supersedes installed=1 loadedFromSave=0 restored=1 staleDropped=0"));
        }

        [Fact]
        public void Quickload_SaveBeforeReFlyMerge_TombstonedDeathStaysOutOfEls()
        {
            InstallMergedReFlyTree();
            AddDeathAction();
            var memory = Memory(s =>
            {
                s.RecordingSupersedes.Add(Rel("rel_a", OriginId, ForkId));
                s.LedgerTombstones.Add(Tomb("tomb_a", DeathActionId, ForkId));
            });
            ConfigNode saved = SaveNode(null);

            var loaded = Load(memory, saved, EarlyLoadKind.InSession);

            Assert.Equal(new[] { "tomb_a" }, loaded.LedgerTombstones.Select(t => t.TombstoneId));
            Assert.False(ElsHas(DeathActionId), "the retired crew death must stay retired after the quickload");
        }

        [Fact]
        public void Quickload_RowsOnlyInTheSave_AreNotResurrected()
        {
            // Mirror: a row memory already removed (a tree discard after the save) stays removed.
            InstallMergedReFlyTree();
            var memory = Memory(null);
            ConfigNode saved = SaveNode(s =>
            {
                s.RecordingSupersedes.Add(Rel("rel_gone", "rec_deleted_origin", "rec_deleted_fork"));
                s.RecordingRewindRetirements.Add(new RecordingRewindRetirement
                {
                    RetirementId = "rrt_gone",
                    RecordingId = "rec_deleted_fork",
                    RestoredRecordingId = "rec_deleted_origin",
                    Reason = RecordingRewindRetirement.DefaultReason,
                });
            });

            var loaded = Load(memory, saved, EarlyLoadKind.InSession);

            Assert.Empty(loaded.RecordingSupersedes);
            Assert.Empty(loaded.RecordingRewindRetirements);
        }

        // =====================================================================
        // Rewind points: owner partition (design 3.3)
        // =====================================================================

        [Fact]
        public void Quickload_CommittedTreeRpCreatedAfterSave_StaysListed()
        {
            InstallCommittedTree(TreeId, new[] { "rp_new" }, Rec(OriginId, 50.0, MergeState.CommittedProvisional));
            var rpNew = Rp("rp_new");
            var memory = Memory(s => s.RewindPoints.Add(rpNew));
            ConfigNode saved = SaveNode(null);

            var loaded = Load(memory, saved, EarlyLoadKind.InSession, sweep: true);

            Assert.Same(rpNew, Assert.Single(loaded.RewindPoints));
            Assert.Empty(deletedRpIds);
            Assert.Contains(logLines, l => l.Contains("[INFO][Rewind]")
                && l.Contains("In-session RP owner partition:")
                && l.Contains("committedOwner kept=0 memoryOnlyKept=1 saveOnlyDropped=0"));
        }

        [Fact]
        public void StepB_EitherLinkMakesTheCommittedTreeTheOwner()
        {
            // A point belongs to a committed tree through its own BranchPointId or through the
            // tree's branch point naming it (RewindPointId, the link the commit-time promotion
            // reads); each link alone must keep a memory-only point.
            var tree = BuildTree(TreeId, null, Rec(OriginId, 50.0, MergeState.Immutable));
            tree.BranchPoints.Add(new BranchPoint { Id = "bp_by_id", Type = BranchPointType.JointBreak });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_backref", Type = BranchPointType.JointBreak, RewindPointId = "rp_by_backref",
            });
            RecordingStore.AddRecordingWithTreeForTesting(tree.Recordings[OriginId], TreeId);
            RecordingStore.AddCommittedTreeForTesting(tree);
            var byId = Rp("rp_by_id");
            byId.BranchPointId = "bp_by_id";
            var byBackref = Rp("rp_by_backref");
            byBackref.BranchPointId = "bp_not_in_any_tree";
            var memory = Memory(s =>
            {
                s.RewindPoints.Add(byId);
                s.RewindPoints.Add(byBackref);
            });

            var loaded = Load(memory, SaveNode(null), EarlyLoadKind.InSession);

            Assert.Equal(new[] { "rp_by_id", "rp_by_backref" }, loaded.RewindPoints.Select(r => r.RewindPointId));
        }

        [Fact]
        public void Quickload_RpReapedAfterSave_NotResurrected()
        {
            InstallCommittedTree(TreeId, new[] { "rp_old" }, Rec(OriginId, 50.0, MergeState.Immutable));
            var memory = Memory(null);
            ConfigNode saved = SaveNode(s => s.RewindPoints.Add(Rp("rp_old")));

            var loaded = Load(memory, saved, EarlyLoadKind.InSession);

            Assert.Empty(loaded.RewindPoints);
            Assert.Empty(deletedRpIds);
            Assert.Contains(logLines, l => l.Contains("In-session RP owner partition:")
                && l.Contains("saveOnlyDropped=1"));
        }

        [Fact]
        public void Quickload_ResumedTreeRp_FollowsTheSave()
        {
            // The quicksave's own flight (tree R) is committed in memory until the active-tree
            // restore detaches it; its rewind points then follow the save, and a point memory
            // created after the quicksave drops out of the list with its file left on disk.
            InstallCommittedTree("tree_r", new[] { "rp_r_shared", "rp_r_mem_only", "rp_r_save_only" },
                Rec("rec_r", 40.0, MergeState.Immutable, "tree_r"));
            var memShared = Rp("rp_r_shared");
            var memory = Memory(s =>
            {
                s.RewindPoints.Add(memShared);
                s.RewindPoints.Add(Rp("rp_r_mem_only"));
            });
            ConfigNode saved = SaveNode(s =>
            {
                s.RewindPoints.Add(Rp("rp_r_shared", sessionProvisional: true));
                s.RewindPoints.Add(Rp("rp_r_save_only", sessionProvisional: true));
            });

            var loaded = Load(memory, saved, EarlyLoadKind.InSession,
                treeRestore: () => RecordingStore.RemoveCommittedTreeById("tree_r", "test-detach"));

            Assert.Equal(new[] { "rp_r_shared", "rp_r_save_only" }, loaded.RewindPoints.Select(r => r.RewindPointId));
            Assert.NotSame(memShared, loaded.RewindPoints[0]);
            Assert.True(loaded.RewindPoints[0].SessionProvisional, "the loaded instance stands for a resumed flight");
            Assert.Empty(deletedRpIds);
            Assert.Contains(logLines, l => l.Contains("In-session RP owner partition:")
                && l.Contains("followSave kept=2 memoryOnlyDropped=1"));
        }

        [Fact]
        public void StockRevert_RevertedFlightRps_FollowTheSave()
        {
            // A reverted flight's tree is the pending Limbo tree, never committed: its staging
            // rewind points follow the launch-state save (today's behaviour), files untouched.
            var reverted = BuildTree("tree_reverted", new[] { "rp_flight" },
                Rec("rec_flight", 10.0, MergeState.NotCommitted, "tree_reverted"));
            RecordingStore.StashPendingTree(reverted, PendingTreeState.Limbo);
            InstallMergedReFlyTree();
            var memory = Memory(s =>
            {
                s.RewindPoints.Add(Rp("rp_flight", sessionProvisional: true));
                s.RecordingSupersedes.Add(Rel("rel_a", OriginId, ForkId));
            });
            ConfigNode saved = SaveNode(null);

            var loaded = Load(memory, saved, EarlyLoadKind.InSession, sweep: true);

            Assert.Empty(loaded.RewindPoints);
            Assert.Empty(deletedRpIds);
            Assert.Equal(new[] { "rel_a" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
        }

        // =====================================================================
        // Re-Fly sessions (design 3.4 cases 2-4)
        // =====================================================================

        [Fact]
        public void Quickload_SaveBeforeSessionStarted_SessionRpsPurgedWithFiles()
        {
            InstallCommittedTree(TreeId, new[] { "rp_t" }, Rec(OriginId, 50.0, MergeState.CommittedProvisional));
            var provisional = Rec("rec_provisional", 55.0, MergeState.NotCommitted);
            provisional.CreatingSessionId = SessionS;
            RecordingStore.AddProvisional(provisional);
            var memory = Memory(s =>
            {
                s.RewindPoints.Add(Rp("rp_t"));
                s.RewindPoints.Add(Rp("rp_s_child", sessionProvisional: true, creatingSessionId: SessionS));
                s.ActiveReFlySessionMarker = Marker(SessionS, "rec_provisional", OriginId, "rp_t");
            });
            ConfigNode saved = SaveNode(s => s.RewindPoints.Add(Rp("rp_t")));

            var loaded = Load(memory, saved, EarlyLoadKind.InSession, sweep: true);

            Assert.Null(loaded.ActiveReFlySessionMarker);
            Assert.Equal(new[] { "rp_t" }, loaded.RewindPoints.Select(r => r.RewindPointId));
            Assert.Equal(new[] { "rp_s_child" }, deletedRpIds);
            Assert.Contains(logLines, l => l.Contains("Purged session-prov rp=rp_s_child"));
        }

        [Fact]
        public void Quickload_SameInstantDuringSession_NoChange()
        {
            InstallCommittedTree(TreeId, new[] { "rp_t" }, Rec(OriginId, 50.0, MergeState.CommittedProvisional));
            InstallCommittedTree("tree_x", null,
                Rec("rec_x_origin", 20.0, MergeState.Immutable, "tree_x"),
                Rec("rec_x_fork", 25.0, MergeState.Immutable, "tree_x"));
            var provisional = Rec("rec_provisional", 55.0, MergeState.NotCommitted);
            provisional.CreatingSessionId = SessionS;
            RecordingStore.AddProvisional(provisional);
            Action<ParsekScenario> state = s =>
            {
                s.RewindPoints.Add(Rp("rp_t"));
                s.RewindPoints.Add(Rp("rp_s_child", sessionProvisional: true, creatingSessionId: SessionS));
                s.RecordingSupersedes.Add(Rel("rel_other", "rec_x_origin", "rec_x_fork"));
                s.ActiveReFlySessionMarker = Marker(SessionS, "rec_provisional", OriginId, "rp_t");
            };
            var memory = Memory(state);
            ConfigNode saved = SaveNode(state);

            var loaded = Load(memory, saved, EarlyLoadKind.InSession, sweep: true);

            Assert.NotNull(loaded.ActiveReFlySessionMarker);
            Assert.Equal(SessionS, loaded.ActiveReFlySessionMarker.SessionId);
            Assert.Equal(new[] { "rp_t", "rp_s_child" }, loaded.RewindPoints.Select(r => r.RewindPointId));
            Assert.Equal(new[] { "rel_other" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Empty(deletedRpIds);
        }

        /// <summary>
        /// Owner ruling OQ-1, first half: F9 into a quicksave taken during a Re-Fly session that
        /// has since merged resumes that session. Memory holds the merge (A->A' row, A's death
        /// tombstoned, the session's rewind point promoted); the restore detaches the committed
        /// tree and stashes the save's copy, whose A' is the session's NotCommitted provisional.
        /// The rows stay (from memory), the save's marker validates against the pending tree, and
        /// the session's rewind point comes back session-scoped from the save.
        /// </summary>
        [Fact]
        public void Quickload_SaveDuringMergedSession_ResumesSession_RowsKept()
        {
            InstallCommittedTree(TreeId, new[] { "rp_t", "rp_s_child" },
                Rec(OriginId, 50.0, MergeState.Immutable),
                Rec(ForkId, 55.0, MergeState.Immutable));
            AddDeathAction();
            var memory = Memory(s =>
            {
                s.RewindPoints.Add(Rp("rp_t"));
                s.RewindPoints.Add(Rp("rp_s_child"));
                s.RecordingSupersedes.Add(Rel("rel_a", OriginId, ForkId));
                s.LedgerTombstones.Add(Tomb("tomb_a", DeathActionId, ForkId));
            });
            ConfigNode saved = SaveNode(s =>
            {
                s.RewindPoints.Add(Rp("rp_t"));
                s.RewindPoints.Add(Rp("rp_s_child", sessionProvisional: true, creatingSessionId: SessionS));
                s.ActiveReFlySessionMarker = Marker(SessionS, ForkId, OriginId, "rp_t");
            });

            var loaded = Load(memory, saved, EarlyLoadKind.InSession, sweep: true,
                treeRestore: () =>
                {
                    RecordingStore.RemoveCommittedTreeById(TreeId, "test-detach");
                    var resumed = BuildTree(TreeId, new[] { "rp_t", "rp_s_child" },
                        Rec(OriginId, 50.0, MergeState.Immutable),
                        Rec(ForkId, 55.0, MergeState.NotCommitted));
                    RecordingStore.StashPendingTree(resumed, PendingTreeState.Limbo);
                });

            Assert.NotNull(loaded.ActiveReFlySessionMarker);
            Assert.Equal(SessionS, loaded.ActiveReFlySessionMarker.SessionId);
            Assert.Contains(logLines, l => l.Contains("Marker valid sess=" + SessionS));
            Assert.Equal(new[] { "rel_a" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Equal(new[] { "tomb_a" }, loaded.LedgerTombstones.Select(t => t.TombstoneId));
            var sessionRp = loaded.RewindPoints.Single(r => r.RewindPointId == "rp_s_child");
            Assert.True(sessionRp.SessionProvisional);
            Assert.Equal(SessionS, sessionRp.CreatingSessionId);
            Assert.Empty(deletedRpIds);
        }

        // =====================================================================
        // Discard Re-fly (design 3.4 case 6; DISCARD-REFLY-PRELAUNCH-PURGES-NESTED-ORIGIN-RP)
        // =====================================================================

        private void WireDiscardSeams()
        {
            RevertInterceptor.DiscardReFlyLoadGameForTesting = (rp, name) => { };
            RevertInterceptor.DiscardReFlyLoadSceneForTesting = (scene, facility) => { };
            RevertInterceptor.DiscardReFlyQuicksaveExistsForTesting = _ => true;
            RevertInterceptor.ScreenMessagePostForTesting = _ => { };
        }

        /// <summary>
        /// The Discard Re-fly load lands on ONE OnLoad: the Space Center's (Launch) or the
        /// editor's (Prelaunch) scene start reloads persistent.sfs (decompiled
        /// SpaceCenterMain.Start / EditorDriver.Start), not the rewind-point quicksave the handler
        /// loaded. Whatever that node carries, the handoff the old instance captured after the
        /// handler mutated memory supplies the marker, the journal, the rows and the committed
        /// tree's rewind points.
        /// </summary>
        private ParsekScenario DiscardThenLoad(RevertTarget target, ConfigNode saved, ParsekScenario memory)
        {
            WireDiscardSeams();
            RevertInterceptor.DiscardReFlyHandler(memory.ActiveReFlySessionMarker, target, EditorFacility.VAB);
            Assert.Null(memory.ActiveReFlySessionMarker);
            GameScenes scene = DiscardReFlyLoadIntent.ExpectedSceneFor(target);
            HighLogic.LoadedScene = scene;
            bool discard = DiscardReFlyLoadIntent.TryConsume(scene, "test", out _);
            Assert.True(discard);
            EarlyLoadKind early = LoadReconcilePolicy.ClassifyEarly(true, false, false, discard);
            Assert.Equal(EarlyLoadKind.DiscardReFly, early);
            return Load(memory, saved, early, sweep: true);
        }

        [Fact]
        public void DiscardReFly_PromotedOriginRpAndNullMarker_SurviveTheLoad()
        {
            // The origin RP carries the session's stamp (the invariant violation the handler
            // defends against by always clearing CreatingSessionId); the node the load reads
            // still has it stamped and the session's marker live.
            InstallCommittedTree(TreeId, new[] { "rp_origin" }, Rec(OriginId, 50.0, MergeState.CommittedProvisional));
            var provisional = Rec("rec_provisional", 55.0, MergeState.NotCommitted);
            provisional.CreatingSessionId = SessionS;
            RecordingStore.AddProvisional(provisional);
            var marker = Marker(SessionS, "rec_provisional", OriginId, "rp_origin");
            var memory = Memory(s =>
            {
                s.RewindPoints.Add(Rp("rp_origin", sessionProvisional: true, creatingSessionId: SessionS));
                s.ActiveReFlySessionMarker = marker;
            });
            ConfigNode saved = SaveNode(s =>
            {
                s.RewindPoints.Add(Rp("rp_origin", sessionProvisional: true, creatingSessionId: SessionS));
                s.ActiveReFlySessionMarker = Marker(SessionS, "rec_provisional", OriginId, "rp_origin");
                s.ActiveMergeJournal = new MergeJournal { JournalId = "mj_stale", SessionId = SessionS, Phase = MergeJournal.Phases.Begin };
            });

            var loaded = DiscardThenLoad(RevertTarget.Launch, saved, memory);

            Assert.Null(loaded.ActiveReFlySessionMarker);
            Assert.Null(loaded.ActiveMergeJournal);
            var origin = Assert.Single(loaded.RewindPoints);
            Assert.Equal("rp_origin", origin.RewindPointId);
            Assert.False(origin.SessionProvisional);
            Assert.Null(origin.CreatingSessionId);
            Assert.Empty(deletedRpIds);
            Assert.Contains(logLines, l => l.Contains("In-session staged lists from memory:")
                && l.Contains("marker source=memory"));
        }

        [Fact]
        public void DiscardReFlyPrelaunch_NestedOriginRp_NotPurged()
        {
            // rp_nested was authored inside session S0, which merged (its point promoted in
            // memory); the node the editor load reads carries S0's stale marker and the point
            // still stamped S0. S1 re-flies rp_nested and is discarded to the editor.
            InstallCommittedTree(TreeId, new[] { "rp_nested" },
                Rec(OriginId, 50.0, MergeState.CommittedProvisional),
                Rec("rec_s0_fork", 52.0, MergeState.Immutable));
            var provisional = Rec("rec_s1_provisional", 55.0, MergeState.NotCommitted);
            provisional.CreatingSessionId = "sess_s1";
            RecordingStore.AddProvisional(provisional);
            var memory = Memory(s =>
            {
                s.RewindPoints.Add(Rp("rp_nested"));
                s.ActiveReFlySessionMarker = Marker("sess_s1", "rec_s1_provisional", OriginId, "rp_nested");
            });
            ConfigNode saved = SaveNode(s =>
            {
                s.RewindPoints.Add(Rp("rp_nested", sessionProvisional: true, creatingSessionId: "sess_s0"));
                s.ActiveReFlySessionMarker = Marker("sess_s0", "rec_s0_fork", OriginId, "rp_nested");
            });

            var loaded = DiscardThenLoad(RevertTarget.Prelaunch, saved, memory);

            Assert.Null(loaded.ActiveReFlySessionMarker);
            var nested = Assert.Single(loaded.RewindPoints);
            Assert.Equal("rp_nested", nested.RewindPointId);
            Assert.False(nested.SessionProvisional);
            Assert.Empty(deletedRpIds);
            Assert.DoesNotContain(logLines, l => l.Contains("Purged session-prov rp=rp_nested"));
        }

        // =====================================================================
        // OQ-1 second half: Discard of a resumed merged session
        // =====================================================================

        /// <summary>
        /// The state the first-half load leaves: tree T detached into the pending slot with the
        /// session's provisional A' (NotCommitted) and the origin A, memory's first-merge rows
        /// naming A' (supersede A->A', A's death tombstoned by A', a rewind retirement of A'), an
        /// unrelated committed tree whose rows must stay, and the session's live marker.
        /// </summary>
        private (ParsekScenario Scenario, RecordingTree Tree, ReFlySessionMarker Marker) BuildResumedMergedSession()
        {
            InstallCommittedTree("tree_x", null,
                Rec("rec_x_origin", 20.0, MergeState.Immutable, "tree_x"),
                Rec("rec_x_fork", 25.0, MergeState.Immutable, "tree_x"));
            var fork = Rec(ForkId, 55.0, MergeState.NotCommitted);
            fork.CreatingSessionId = SessionS;
            fork.SupersedeTargetId = OriginId;
            var tree = BuildTree(TreeId, new[] { "rp_t" }, Rec(OriginId, 50.0, MergeState.Immutable), fork);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            AddDeathAction();
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_x_penalty",
                UT = 22.0,
                Type = GameActionType.ReputationPenalty,
                RecordingId = "rec_x_origin",
            });
            var marker = Marker(SessionS, ForkId, OriginId, "rp_t");
            var scenario = Memory(s =>
            {
                s.RewindPoints.Add(Rp("rp_t"));
                s.RecordingSupersedes.Add(Rel("rel_a", OriginId, ForkId));
                s.RecordingSupersedes.Add(Rel("rel_x", "rec_x_origin", "rec_x_fork"));
                s.LedgerTombstones.Add(Tomb("tomb_a", DeathActionId, ForkId));
                s.LedgerTombstones.Add(Tomb("tomb_x", "act_x_penalty", "rec_x_fork"));
                s.RecordingRewindRetirements.Add(new RecordingRewindRetirement
                {
                    RetirementId = "rrt_a",
                    RecordingId = ForkId,
                    RestoredRecordingId = OriginId,
                    Reason = RecordingRewindRetirement.DefaultReason,
                });
                s.ActiveReFlySessionMarker = marker;
            });
            EffectiveState.ResetCachesForTesting();
            Assert.False(EffectiveState.IsVisible(tree.Recordings[OriginId], scenario.RecordingSupersedes));
            Assert.False(ElsHas(DeathActionId));
            return (scenario, tree, marker);
        }

        private void AssertFirstMergeRowsNamingTheAttemptAreGone(ParsekScenario scenario, Recording origin)
        {
            Assert.Equal(new[] { "rel_x" }, scenario.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Equal(new[] { "tomb_x" }, scenario.LedgerTombstones.Select(t => t.TombstoneId));
            Assert.Empty(scenario.RecordingRewindRetirements);
            // No cache reset: the prune's tombstone-version bump must invalidate the ELS the
            // fixture computed.
            Assert.True(EffectiveState.IsVisible(origin, scenario.RecordingSupersedes),
                "the origin must be visible again once the resumed attempt is discarded");
            Assert.True(ElsHas(DeathActionId), "the origin's crew death counts again");
            Assert.False(ElsHas("act_x_penalty"), "another tree's tombstone stays");
            Assert.Contains(logLines, l => l.Contains("[INFO][MergeDialog]")
                && l.Contains("PruneStagedRowsNamingAttempt")
                && l.Contains("supersedes=1 tombstones=1 retirements=1"));
        }

        [Fact]
        public void DiscardOfResumedMergedSession_EscMenu_DropsTheFirstMergeRowsNamingTheAttempt()
        {
            var resumed = BuildResumedMergedSession();
            Recording origin = resumed.Tree.Recordings[OriginId];
            WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(resumed.Marker, RevertTarget.Launch, EditorFacility.VAB);

            Assert.Null(resumed.Scenario.ActiveReFlySessionMarker);
            AssertFirstMergeRowsNamingTheAttemptAreGone(resumed.Scenario, origin);
        }

        [Fact]
        public void DiscardOfResumedMergedSession_MergeDialog_OriginBackInTheTimeline()
        {
            var resumed = BuildResumedMergedSession();
            Recording origin = resumed.Tree.Recordings[OriginId];

            MergeDialog.MergeDiscard(resumed.Tree);

            Assert.Null(resumed.Scenario.ActiveReFlySessionMarker);
            AssertFirstMergeRowsNamingTheAttemptAreGone(resumed.Scenario, origin);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == TreeId);
            Assert.True(ErsHas(OriginId), "the sanitized tree is committed again with its origin visible");
            Assert.False(ErsHas(ForkId));
        }

        [Fact]
        public void DiscardOfFreshSession_PrunesNoStagedRow()
        {
            // A session that never merged has no row naming its provisional; the prune is a no-op
            // and an unrelated tree's rows stay.
            InstallCommittedTree("tree_x", null,
                Rec("rec_x_origin", 20.0, MergeState.Immutable, "tree_x"),
                Rec("rec_x_fork", 25.0, MergeState.Immutable, "tree_x"));
            InstallCommittedTree(TreeId, new[] { "rp_t" }, Rec(OriginId, 50.0, MergeState.CommittedProvisional));
            var provisional = Rec("rec_provisional", 55.0, MergeState.NotCommitted);
            provisional.CreatingSessionId = SessionS;
            RecordingStore.AddProvisional(provisional);
            var marker = Marker(SessionS, "rec_provisional", OriginId, "rp_t");
            var scenario = Memory(s =>
            {
                s.RewindPoints.Add(Rp("rp_t"));
                s.RecordingSupersedes.Add(Rel("rel_x", "rec_x_origin", "rec_x_fork"));
                s.LedgerTombstones.Add(Tomb("tomb_x", "act_x_penalty", "rec_x_fork"));
                s.ActiveReFlySessionMarker = marker;
            });
            WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch, EditorFacility.VAB);

            Assert.Equal(new[] { "rel_x" }, scenario.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Equal(new[] { "tomb_x" }, scenario.LedgerTombstones.Select(t => t.TombstoneId));
            Assert.Contains(logLines, l => l.Contains("PruneStagedRowsNamingAttempt")
                && l.Contains("no row names the 1 pruned attempt recording(s)"));
        }

        [Fact]
        public void RemoveRowsNaming_Pure_RemovesOnlyRowsWhoseNamedSideIsInTheSet()
        {
            var rows = new List<RecordingSupersedeRelation>
            {
                Rel("r1", "o1", "attempt"),
                null,
                Rel("r2", "attempt", "n2"),
                Rel("r3", "o3", "n3"),
                Rel("r4", "o4", "attempt_child"),
            };
            var removed = new List<string>();

            int count = MergeDialog.RemoveRowsNaming(
                rows, r => r.NewRecordingId, r => r.RelationId,
                new HashSet<string>(StringComparer.Ordinal) { "attempt", "attempt_child" }, removed);

            Assert.Equal(2, count);
            Assert.Equal(new[] { null, "r2", "r3" }, rows.Select(r => r?.RelationId));
            Assert.Equal(new[] { "r4", "r1" }, removed);
            Assert.Equal(0, MergeDialog.RemoveRowsNaming(rows, r => r.NewRecordingId, r => r.RelationId,
                new HashSet<string>(StringComparer.Ordinal), removed));
            Assert.Equal(0, MergeDialog.RemoveRowsNaming<RecordingSupersedeRelation>(null, r => r.NewRecordingId,
                r => r.RelationId, new HashSet<string> { "x" }, removed));
        }

        [Fact]
        public void PruneStagedRowsNamingAttempt_NoScenarioOrNoIds_IsANoOp()
        {
            var result = MergeDialog.PruneStagedRowsNamingAttempt(null, new HashSet<string> { "a" }, "test");
            Assert.Equal(0, result.Total);
            var scenario = Memory(s => s.RecordingSupersedes.Add(Rel("r", "o", "a")));
            result = MergeDialog.PruneStagedRowsNamingAttempt(scenario, new HashSet<string>(), "test");
            Assert.Equal(0, result.Total);
            Assert.Single(scenario.RecordingSupersedes);
        }

        [Fact]
        public void StepA_BumpsBothStateVersions_SoCachedViewsRebuild()
        {
            InstallMergedReFlyTree();
            AddDeathAction();
            var memory = Memory(s =>
            {
                s.RecordingSupersedes.Add(Rel("rel_a", OriginId, ForkId));
                s.LedgerTombstones.Add(Tomb("tomb_a", DeathActionId, ForkId));
            });
            memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);
            var fresh = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(fresh);
            Invoke(fresh, "LoadRewindStagingState", SaveNode(null));
            EffectiveState.ResetCachesForTesting();
            Assert.True(ErsHas(OriginId));
            Assert.True(ElsHas(DeathActionId));
            int supersedeVersion = fresh.SupersedeStateVersion;
            int tombstoneVersion = fresh.TombstoneStateVersion;

            fresh.ApplyInSessionStagedStateHandoffStepA(EarlyLoadKind.InSession);

            Assert.True(fresh.SupersedeStateVersion > supersedeVersion);
            Assert.True(fresh.TombstoneStateVersion > tombstoneVersion);
            Assert.False(ErsHas(OriginId), "the cached ERS must rebuild from the installed rows");
            Assert.False(ElsHas(DeathActionId), "the cached ELS must rebuild from the installed tombstones");
        }

        [Fact]
        public void DiscardReFly_StepA_ClearsTheLoadedMarkerBeforeTheSweep()
        {
            // The marker half of step A, observed before LoadTimeSweep.Run could clear a loaded
            // marker on its own: a Discard Re-fly load ends the session whatever the node holds.
            var memory = Memory(null);
            ConfigNode saved = SaveNode(s => s.ActiveReFlySessionMarker =
                Marker(SessionS, "rec_provisional", OriginId, "rp_t"));
            memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);
            var fresh = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(fresh);
            Invoke(fresh, "LoadRewindStagingState", saved);
            Assert.NotNull(fresh.ActiveReFlySessionMarker);

            fresh.ApplyInSessionStagedStateHandoffStepA(EarlyLoadKind.DiscardReFly);

            Assert.Null(fresh.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l => l.Contains("In-session staged lists from memory:")
                && l.Contains("marker source=memory(none; loaded=" + SessionS + ")"));
        }

        [Fact]
        public void InSessionLoad_StepA_LeavesTheLoadedMarkerToTheSweep()
        {
            // Mirror: on the in-session kinds the marker follows the save (step 1 of the sweep
            // validates it); memory's marker never replaces it.
            var memory = Memory(s => s.ActiveReFlySessionMarker = Marker("sess_memory", "rec_m", OriginId, "rp_t"));
            ConfigNode saved = SaveNode(s => s.ActiveReFlySessionMarker =
                Marker(SessionS, "rec_provisional", OriginId, "rp_t"));
            memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);
            var fresh = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(fresh);
            Invoke(fresh, "LoadRewindStagingState", saved);

            fresh.ApplyInSessionStagedStateHandoffStepA(EarlyLoadKind.InSession);

            Assert.Equal(SessionS, fresh.ActiveReFlySessionMarker.SessionId);
            Assert.Contains(logLines, l => l.Contains("In-session staged lists from memory:")
                && l.Contains("marker source=save;"));
        }

        // =====================================================================
        // The merge journal
        // =====================================================================

        [Fact]
        public void Quickload_StaleJournalOnDisk_MemoryFinishedIt_IsDropped()
        {
            var memory = Memory(null);
            ConfigNode saved = SaveNode(s => s.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "mj_stale",
                SessionId = "sess_done",
                Phase = MergeJournal.Phases.Durable1Done,
            });

            var loaded = Load(memory, saved, EarlyLoadKind.InSession);

            Assert.Null(loaded.ActiveMergeJournal);
            Assert.Contains(logLines, l => l.Contains("In-session staged lists from memory:")
                && l.Contains("journal installed=none loadedFromSave=mj_stale@Durable1Done"));
        }

        [Fact]
        public void InFlightJournal_LoadedJournalStands_Warns()
        {
            var memory = Memory(s => s.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "mj_live",
                SessionId = "sess_live",
                Phase = MergeJournal.Phases.Supersede,
            });
            ConfigNode saved = SaveNode(s => s.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "mj_disk",
                SessionId = "sess_live",
                Phase = MergeJournal.Phases.Begin,
            });

            var loaded = Load(memory, saved, EarlyLoadKind.InSession);

            Assert.Equal("mj_disk", loaded.ActiveMergeJournal.JournalId);
            Assert.Contains(logLines, l => l.Contains("[WARN][Rewind]")
                && l.Contains("In-session handoff kept the loaded merge journal")
                && l.Contains("mj_live@Supersede"));
        }

        // =====================================================================
        // Drops: every reason, and consume-once
        // =====================================================================

        [Fact]
        public void ColdLoad_DropsHandoff_ListsFollowTheSave()
        {
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_mem", "o", "n")));
            ConfigNode saved = SaveNode(s => s.RecordingSupersedes.Add(Rel("rel_disk", "o2", "n2")));

            var loaded = Load(memory, saved, EarlyLoadKind.Cold);

            Assert.Equal(new[] { "rel_disk" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
            Assert.False(InSessionStagedStateHandoff.HasPending);
            Assert.False(loaded.InSessionRewindPointPartitionPendingForTesting);
            Assert.Contains(logLines, l => l.Contains("Staged-list handoff dropped reason=cold-load"));
        }

        [Fact]
        public void PlainRewind_RewindCarryOwns()
        {
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_mem", "o", "n")));
            ConfigNode saved = SaveNode(null);
            RewindContext.BeginRewind(30.0, default(BudgetSummary), 0, 0, 0f);
            RecordingStore.CaptureRewindStagedListsForRewind(memory, "Rewind");

            var loaded = Load(memory, saved, EarlyLoadKind.PlainRewind);

            // The rewind carry installed memory's rows, not the handoff.
            Assert.Equal(new[] { "rel_mem" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Contains(logLines, l => l.Contains("Staged lists carried across rewind:"));
            Assert.Contains(logLines, l => l.Contains("Staged-list handoff dropped reason=rewind-carry-owns"));
            Assert.DoesNotContain(logLines, l => l.Contains("In-session staged lists from memory:"));
        }

        [Fact]
        public void ReFlyStart_BundleOwns()
        {
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_mem", "o", "n")));
            ConfigNode saved = SaveNode(s => s.RecordingSupersedes.Add(Rel("rel_disk", "o2", "n2")));

            var loaded = Load(memory, saved, EarlyLoadKind.ReFlyStart);

            // The bundle restore (RewindInvoker.ConsumePostLoad, later in OnLoad) owns the lists.
            Assert.Equal(new[] { "rel_disk" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Contains(logLines, l => l.Contains("Staged-list handoff dropped reason=refly-bundle-owns"));
        }

        [Fact]
        public void SaveFolderChanged_DropsHandoff()
        {
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_mem", "o", "n")));
            memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);
            currentSaveFolder = "some-other-save";
            ConfigNode saved = SaveNode(null);

            var loaded = Load(memory, saved, EarlyLoadKind.InSession, capture: false);

            Assert.Empty(loaded.RecordingSupersedes);
            Assert.Contains(logLines, l => l.Contains("Staged-list handoff dropped reason=save-folder-changed"));
        }

        [Fact]
        public void NoHandoff_InSessionLoad_KeepsTheSave_AndWarns()
        {
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_mem", "o", "n")));
            ConfigNode saved = SaveNode(s => s.RecordingSupersedes.Add(Rel("rel_disk", "o2", "n2")));

            var loaded = Load(memory, saved, EarlyLoadKind.InSession, capture: false);

            Assert.Equal(new[] { "rel_disk" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Contains(logLines, l => l.Contains("[WARN][Rewind]")
                && l.Contains("In-session load found no staged-list handoff"));
        }

        [Fact]
        public void TwoOnLoadsOneCapture_TheSecondFindsNone()
        {
            // Consume-once: a second OnLoad with no teardown in between (not a stock shape:
            // every scene load destroys the scenario first) must not re-apply the first
            // load's memory.
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_mem", "o", "n")));
            ConfigNode saved = SaveNode(null);

            var first = Load(memory, saved, EarlyLoadKind.InSession);
            Assert.Single(first.RecordingSupersedes);
            logLines.Clear();
            var second = Load(null, saved, EarlyLoadKind.InSession);

            Assert.Empty(second.RecordingSupersedes);
            Assert.Contains(logLines, l => l.Contains("In-session load found no staged-list handoff"));
        }

        [Fact]
        public void Capture_SkipsBeforeAnyCompletedLoad()
        {
            InitialLoadDone = false;
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_mem", "o", "n")));

            memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);

            Assert.False(InSessionStagedStateHandoff.HasPending);
            Assert.Contains(logLines, l => l.Contains("Staged-list handoff capture skipped reason=no-completed-load"));
        }

        [Fact]
        public void Capture_IsASnapshot_AndAReplacedCaptureWarns()
        {
            var memory = Memory(s => s.RecordingSupersedes.Add(Rel("rel_mem", "o", "n")));
            memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);
            memory.RecordingSupersedes.Add(Rel("rel_late", "o3", "n3"));
            memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnAwake);
            memory.RecordingSupersedes.Add(Rel("rel_later", "o4", "n4"));
            ConfigNode saved = SaveNode(null);

            var loaded = Load(memory, saved, EarlyLoadKind.InSession, capture: false);

            Assert.Equal(new[] { "rel_mem", "rel_late" }, loaded.RecordingSupersedes.Select(r => r.RelationId));
            Assert.Contains(logLines, l => l.Contains("[WARN][Rewind]")
                && l.Contains("Staged-list handoff replaced an unconsumed one (captured reason=OnDestroy"));
            Assert.Contains(logLines, l => l.Contains("Staged-list handoff captured reason=OnAwake")
                && l.Contains("supersedes=2"));
        }

        [Fact]
        public void Drop_EveryReasonLogsAndClears()
        {
            foreach (string reason in InSessionStagedStateHandoff.DropReasons)
            {
                var memory = Memory(null);
                memory.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);
                InSessionStagedStateHandoff.Drop(reason);
                Assert.False(InSessionStagedStateHandoff.HasPending, reason);
                Assert.Contains(logLines, l => l.Contains("Staged-list handoff dropped reason=" + reason));
            }
        }

        // =====================================================================
        // Pure decisions
        // =====================================================================

        [Theory]
        [InlineData((int)EarlyLoadKind.Cold, "f", "f", InSessionStagedStateHandoff.DropColdLoad)]
        [InlineData((int)EarlyLoadKind.PlainRewind, "f", "f", InSessionStagedStateHandoff.DropRewindCarryOwns)]
        [InlineData((int)EarlyLoadKind.ReFlyStart, "f", "f", InSessionStagedStateHandoff.DropReFlyBundleOwns)]
        [InlineData((int)EarlyLoadKind.DiscardReFly, "f", "f", null)]
        [InlineData((int)EarlyLoadKind.InSession, "f", "f", null)]
        [InlineData((int)EarlyLoadKind.InSession, "f", "g", InSessionStagedStateHandoff.DropSaveFolderChanged)]
        [InlineData((int)EarlyLoadKind.DiscardReFly, null, "g", InSessionStagedStateHandoff.DropSaveFolderChanged)]
        [InlineData((int)EarlyLoadKind.Cold, "f", "g", InSessionStagedStateHandoff.DropColdLoad)]
        public void DecideStepA_Pure(int early, string captured, string current, string expected)
        {
            Assert.Equal(expected, InSessionStagedStateHandoff.DecideStepA((EarlyLoadKind)early, captured, current));
        }

        [Theory]
        [InlineData(true, false, true, null)]
        [InlineData(false, false, false, "no-completed-load")]
        [InlineData(true, true, false, "inert-game-mode")]
        public void ShouldCapture_Pure(bool initialLoadDone, bool inert, bool expected, string skip)
        {
            Assert.Equal(expected, InSessionStagedStateHandoff.ShouldCapture(initialLoadDone, inert, out string reason));
            Assert.Equal(skip, reason);
        }

        public static IEnumerable<object[]> PartitionCases()
        {
            // id, inMemory, inSave, memorySessionScoped, saveSessionScoped, ownerCommitted, expectedKept, expectedSource
            yield return new object[] { "session-both", true, true, true, true, false, true, "memory" };
            yield return new object[] { "session-memory-only", true, false, true, false, true, true, "memory" };
            yield return new object[] { "session-save-only", false, true, false, true, false, true, "save" };
            yield return new object[] { "committed-both", true, true, false, true, true, true, "memory" };
            yield return new object[] { "committed-memory-only", true, false, false, false, true, true, "memory" };
            yield return new object[] { "committed-save-only", false, true, false, false, true, false, null };
            yield return new object[] { "follow-both", true, true, false, true, false, true, "save" };
            yield return new object[] { "follow-memory-only", true, false, false, false, false, false, null };
            yield return new object[] { "follow-save-only", false, true, false, false, false, true, "save" };
        }

        [Theory]
        [MemberData(nameof(PartitionCases))]
        public void MergeRewindPointsByOwner_Pure(string id, bool inMemory, bool inSave,
            bool memorySessionScoped, bool saveSessionScoped, bool ownerCommitted, bool expectedKept, string expectedSource)
        {
            var mem = inMemory ? Rp(id, memorySessionScoped, memorySessionScoped ? SessionS : null) : null;
            var disk = inSave ? Rp(id, saveSessionScoped, saveSessionScoped ? SessionS : null) : null;
            var memory = mem != null ? new List<RewindPoint> { mem } : new List<RewindPoint>();
            var loaded = disk != null ? new List<RewindPoint> { disk } : new List<RewindPoint>();
            var decisions = new List<string>();

            var merged = InSessionStagedStateHandoff.MergeRewindPointsByOwner(
                memory, loaded, rp => ownerCommitted, out RewindPointPartitionCounts counts, decisions);

            Assert.Equal(expectedKept, merged.Count == 1);
            if (expectedSource == "memory") Assert.Same(mem, merged[0]);
            if (expectedSource == "save") Assert.Same(disk, merged[0]);
            Assert.Single(decisions);
            Assert.StartsWith(id + ":", decisions[0]);
            int total = counts.SessionScoped + counts.CommittedKept + counts.CommittedMemoryOnlyKept
                + counts.CommittedSaveOnlyDropped + counts.FollowSaveKept + counts.FollowSaveMemoryOnlyDropped;
            Assert.Equal(1, total);
        }

        [Fact]
        public void MergeRewindPointsByOwner_KeepsSaveOrderThenMemoryOnly()
        {
            var memory = new List<RewindPoint> { Rp("m1"), Rp("s2"), null, Rp("m3") };
            var loaded = new List<RewindPoint> { Rp("s1"), Rp("s2"), new RewindPoint { RewindPointId = null } };

            var merged = InSessionStagedStateHandoff.MergeRewindPointsByOwner(
                memory, loaded, rp => true, out RewindPointPartitionCounts counts);

            Assert.Equal(new[] { "s2", null, "m1", "m3" }, merged.Select(r => r.RewindPointId));
            Assert.Same(memory[1], merged[0]);
            Assert.Equal(1, counts.CommittedKept);
            Assert.Equal(2, counts.CommittedMemoryOnlyKept);
            Assert.Equal(1, counts.CommittedSaveOnlyDropped);
            Assert.Equal(1, counts.FollowSaveKept);
        }

        [Fact]
        public void CollectCommittedTreeRewindLinks_ReadsBothLinks()
        {
            var tree = BuildTree("t", new[] { "rp_1" }, Rec("r", 0.0, MergeState.Immutable, "t"));
            tree.BranchPoints.Add(new BranchPoint { Id = "bp_without_rp" });

            InSessionStagedStateHandoff.CollectCommittedTreeRewindLinks(
                new[] { tree, null }, out HashSet<string> bps, out HashSet<string> rps);

            Assert.Equal(new[] { "bp_rp_1", "bp_without_rp" }, bps.OrderBy(s => s));
            Assert.Equal(new[] { "rp_1" }, rps);
        }
    }
}
