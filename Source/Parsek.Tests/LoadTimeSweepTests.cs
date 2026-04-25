using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 13 of Rewind-to-Staging (design §6.9): guards
    /// <see cref="LoadTimeSweep.Run"/> and <see cref="MarkerValidator.Validate"/>.
    ///
    /// <para>
    /// Covers: marker-validation matrix (6 fields), spare-set preservation,
    /// discard-set zombie cleanup, orphan supersede + tombstone warnings,
    /// stray SupersedeTargetId clearing, nested session-provisional cleanup
    /// (§7.11), summary log, and cache-version bump.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class LoadTimeSweepTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> deletedRpIds = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public LoadTimeSweepTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            MarkerValidator.ResetTestOverrides();
            RewindPointReaper.ResetTestOverrides();

            // Keep the diagnostic "current UT" finite so marker-validation
            // log assertions are deterministic. Individual tests override it
            // when they need to exercise fresh-load clock behavior.
            MarkerValidator.NowUtProvider = () => 1_000_000.0;
        }

        public void Dispose()
        {
            MarkerValidator.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            RewindPointReaper.ResetTestOverrides();
        }

        // ---------- Helpers -----------------------------------------------

        private static Recording Rec(
            string id,
            MergeState state,
            string sessionId = null,
            string supersedeTarget = null,
            string treeId = "tree_1")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                CreatingSessionId = sessionId,
                SupersedeTargetId = supersedeTarget,
            };
        }

        private static BranchPoint Bp(string id, string rpId = null)
        {
            return new BranchPoint
            {
                Id = id,
                Type = BranchPointType.Undock,
                UT = 0.0,
                RewindPointId = rpId,
            };
        }

        private static ChildSlot Slot(int index, string originRecordingId)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = originRecordingId,
                Controllable = true,
            };
        }

        private static RewindPoint Rp(
            string id,
            string bpId,
            bool sessionProvisional,
            string creatingSessionId = null,
            params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = bpId,
                UT = 0.0,
                QuicksaveFilename = id + ".sfs",
                SessionProvisional = sessionProvisional,
                CreatingSessionId = creatingSessionId,
                ChildSlots = new List<ChildSlot>(slots ?? Array.Empty<ChildSlot>()),
            };
        }

        private static ReFlySessionMarker Marker(
            string sessId,
            string treeId,
            string activeId,
            string originId,
            string rpId,
            double invokedUt = 500.0)
        {
            return new ReFlySessionMarker
            {
                SessionId = sessId,
                TreeId = treeId,
                ActiveReFlyRecordingId = activeId,
                OriginChildRecordingId = originId,
                RewindPointId = rpId,
                InvokedUT = invokedUt,
                InvokedRealTime = "2026-04-18T00:00:00Z",
            };
        }

        private static ParsekScenario InstallMarkerValidationFixture(
            double invokedUt,
            double currentUt,
            double rewindPointUt = 577.5)
        {
            MarkerValidator.NowUtProvider = () => currentUt;
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active", MergeState.NotCommitted),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            rp.UT = rewindPointUt;
            var marker = Marker("sess_1", "tree_1", "rec_active", "rec_origin", "rp_1",
                invokedUt: invokedUt);
            return InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);
        }

        private static void InstallTree(string treeId, List<Recording> recordings,
            List<BranchPoint> branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                BranchPoints = branchPoints ?? new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);
        }

        private static ParsekScenario InstallScenario(
            List<RewindPoint> rps = null,
            List<RecordingSupersedeRelation> supersedes = null,
            List<LedgerTombstone> tombstones = null,
            ReFlySessionMarker marker = null,
            MergeJournal journal = null)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = rps ?? new List<RewindPoint>(),
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                LedgerTombstones = tombstones ?? new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = marker,
                ActiveMergeJournal = journal,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        // ---------- Marker validation matrix ------------------------------

        [Fact]
        public void MarkerValid_AllSixFieldsOK_Preserved()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active", MergeState.NotCommitted, sessionId: "sess_1",
                        supersedeTarget: "rec_origin"),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_1", slots: new[] { Slot(0, "rec_origin") });
            var marker = Marker("sess_1", "tree_1", "rec_active", "rec_origin", "rp_1",
                invokedUt: 500.0);
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Single(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("Marker valid sess=sess_1"));
        }

        [Fact]
        public void MarkerInvalid_SessionIdEmpty_ClearedLogsWarn()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active", MergeState.NotCommitted),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker(null, "tree_1", "rec_active", "rec_origin", "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=SessionId"));
        }

        [Fact]
        public void MarkerInvalid_TreeIdMissing_Cleared()
        {
            // Install no tree — marker references "tree_missing".
            RecordingStore.AddCommittedInternal(Rec("rec_active", MergeState.NotCommitted));
            RecordingStore.AddCommittedInternal(Rec("rec_origin", MergeState.CommittedProvisional));
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_missing", "rec_active", "rec_origin", "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=TreeId"));
        }

        [Fact]
        public void MarkerInvalid_RefRecordingMissing_Cleared()
        {
            // ActiveReFlyRecordingId points at a recording that was never added.
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_1", "rec_missing", "rec_origin", "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=ActiveReFlyRecordingId"));
        }

        [Fact]
        public void MarkerInvalid_OriginChildMissing_Cleared()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active", MergeState.NotCommitted),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_1", "rec_active", "origin_missing", "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=OriginChildRecordingId"));
        }

        [Fact]
        public void MarkerInvalid_RewindPointMissing_Cleared()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active", MergeState.NotCommitted),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            // marker references a non-existent rp id
            var marker = Marker("sess_1", "tree_1", "rec_active", "rec_origin", "rp_missing");
            var scenario = InstallScenario(
                rps: new List<RewindPoint>(),
                marker: marker);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=RewindPointId"));
        }

        [Fact]
        public void MarkerValid_PriorSessionInvokedUtAfterFreshLoadUt_PreservedAndLogged()
        {
            // Regression #577: on a fresh SPACECENTER load the scenario load
            // summary reported current UT 0 even though the durable marker
            // had been authored in an earlier session at UT ~= the RP time.
            // Current UT is therefore diagnostic-only; the persisted game UT
            // remains sane and the marker must survive.
            var scenario = InstallMarkerValidationFixture(
                invokedUt: 578.13180328350882,
                currentUt: 0.0,
                rewindPointUt: 577.5);

            LoadTimeSweep.Run();

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker valid sess=sess_1") &&
                l.Contains("invokedUT=578.13180328350882") &&
                l.Contains("currentUT=0") &&
                l.Contains("rpUT=577.5") &&
                l.Contains("legacyFutureUtCheck=triggered"));
        }

        [Fact]
        public void MarkerInvalid_InvokedUtNaN_ClearedWithDiagnostic()
        {
            var scenario = InstallMarkerValidationFixture(
                invokedUt: double.NaN,
                currentUt: 0.0,
                rewindPointUt: 577.5);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=InvokedUT") &&
                l.Contains("failure=not-finite") &&
                l.Contains("currentUT=0") &&
                l.Contains("rpUT=577.5"));
        }

        [Fact]
        public void MarkerInvalid_InvokedUtNegative_ClearedWithDiagnostic()
        {
            var scenario = InstallMarkerValidationFixture(
                invokedUt: -1.0,
                currentUt: 0.0,
                rewindPointUt: 577.5);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=InvokedUT") &&
                l.Contains("failure=negative") &&
                l.Contains("invokedUT=-1") &&
                l.Contains("rpUT=577.5"));
        }

        [Fact]
        public void MarkerInvalid_InvokedUtExtremeFuture_ClearedWithDiagnostic()
        {
            var scenario = InstallMarkerValidationFixture(
                invokedUt: 1.0e16,
                currentUt: 22_116.0,
                rewindPointUt: 577.5);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=InvokedUT") &&
                l.Contains("failure=exceeds-sanity-ceiling") &&
                l.Contains("currentUT=22116") &&
                l.Contains("maxReasonableInvokedUT=1E+15"));
        }

        [Fact]
        public void MarkerInvalid_ActiveRecordingNotInNotCommitted_Cleared()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    // Active is Immutable (wrong state) — should fail marker validation.
                    Rec("rec_active", MergeState.Immutable),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_1", "rec_active", "rec_origin", "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=ActiveReFlyRecordingId"));
        }

        [Fact]
        public void MarkerValid_InPlaceContinuation_CommittedProvisional_Preserved()
        {
            // Regression: 2026-04-25_1246 playtest. RewindInvoker.AtomicMarkerWrite's
            // in-place continuation path (item 11 in todo-and-known-bugs.md)
            // sets ActiveReFlyRecordingId == OriginChildRecordingId so the
            // marker points at the existing recording instead of a fresh
            // placeholder. When that recording was a previously-promoted
            // Unfinished Flight its MergeState is CommittedProvisional, NOT
            // NotCommitted. The validator MUST accept that state for the
            // in-place continuation case or the marker is silently wiped on
            // every save+load cycle (e.g. the FLIGHT->SPACECENTER scene
            // change for the merge dialog), and TryCommitReFlySupersede
            // falls through to the regular tree-merge path with
            // 'no active re-fly session marker'.
            InstallTree("tree_1",
                new List<Recording>
                {
                    // Active == origin (in-place continuation). MergeState is
                    // CommittedProvisional from the prior tree merge that
                    // promoted this recording out of NotCommitted into the
                    // crash-terminal RP-child slot.
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_1",
                activeId: "rec_origin", originId: "rec_origin", rpId: "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal("sess_1", scenario.ActiveReFlySessionMarker.SessionId);
        }

        [Fact]
        public void MarkerInvalid_PlaceholderPattern_CommittedProvisional_Cleared()
        {
            // Regression: the in-place CommittedProvisional carve-out only
            // applies when origin == active. A placeholder pattern (origin
            // != active) MUST still be NotCommitted — a placeholder
            // recording carries no committed history yet. Reject
            // CommittedProvisional in this shape so a corrupt save or
            // legacy migration leftover does not silently keep a stale
            // marker pointing at a finalized branch.
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active", MergeState.CommittedProvisional),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_1",
                activeId: "rec_active", originId: "rec_origin", rpId: "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Marker invalid field=ActiveReFlyRecordingId"));
        }

        [Fact]
        public void MarkerValid_InPlaceContinuation_Immutable_Preserved()
        {
            // Review follow-up: an Immutable recording IS a valid re-fly
            // target when it is also an Unfinished Flight (terminal=
            // Destroyed + matching RP). EffectiveState.IsUnfinishedFlight
            // accepts both Immutable and CommittedProvisional (line 156-
            // 157), and RewindInvoker.AtomicMarkerWrite has no MergeState
            // gate — so the validator must accept the same shape it can
            // legitimately produce. Without this carve-out, a save/load
            // during an in-place re-fly of an Immutable UF wipes the
            // marker and the merge falls through to the regular tree-
            // merge path (no force-Immutable, no RP reap). The
            // CommittedProvisional sister case is pinned by the test
            // immediately above.
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_origin", MergeState.Immutable),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_1",
                activeId: "rec_origin", originId: "rec_origin", rpId: "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal("rec_origin", scenario.ActiveReFlySessionMarker.ActiveReFlyRecordingId);
            Assert.Equal("rec_origin", scenario.ActiveReFlySessionMarker.OriginChildRecordingId);
        }

        // ---------- Spare + discard sets ----------------------------------

        [Fact]
        public void SpareSet_PreservesSessionProvisional_RP()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active", MergeState.NotCommitted, sessionId: "sess_1",
                        supersedeTarget: "rec_origin"),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var spareRp = Rp("rp_1", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_1", slots: new[] { Slot(0, "rec_origin") });
            var marker = Marker("sess_1", "tree_1", "rec_active", "rec_origin", "rp_1");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { spareRp },
                marker: marker);

            LoadTimeSweep.Run();

            // RP stays because the marker is valid and references it.
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", scenario.RewindPoints[0].RewindPointId);
            // The active re-fly recording stays too.
            Assert.NotNull(FindRecording("rec_active"));
        }

        [Fact]
        public void DiscardSet_ZombieProvisional_Removed()
        {
            // No marker; a leftover NotCommitted provisional recording
            // plus a session-provisional RP referencing a defunct session.
            var bp = Bp("bp_1", "rp_dead");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_zombie", MergeState.NotCommitted, sessionId: "sess_dead",
                        supersedeTarget: "rec_origin"),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { bp });
            var zombieRp = Rp("rp_dead", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_dead", slots: new[] { Slot(0, "rec_origin") });
            RewindPointReaper.DeleteQuicksaveForTesting = id =>
            {
                deletedRpIds.Add(id);
                return true;
            };
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { zombieRp },
                marker: null);

            LoadTimeSweep.Run();

            Assert.Null(FindRecording("rec_zombie"));
            Assert.Empty(scenario.RewindPoints);
            Assert.Contains("rp_dead", deletedRpIds);
            Assert.Null(bp.RewindPointId);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Zombie discarded rec=rec_zombie"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Purged session-prov rp=rp_dead"));
        }

        [Fact]
        public void DiscardSet_NormalStagingRpWithoutSessionScope_Preserved()
        {
            // Normal multi-controllable staging RPs are born SessionProvisional
            // before any re-fly session exists. They have no CreatingSessionId
            // and must survive the KSC/TrackingStation OnLoad that shows the
            // pending-tree merge dialog; otherwise the destroyed child cannot
            // appear in Unfinished Flights after merge.
            var bp = Bp("bp_stage", "rp_stage");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_upper", MergeState.Immutable),
                    Rec("rec_probe", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var normalRp = Rp("rp_stage", "bp_stage", sessionProvisional: true,
                creatingSessionId: null, slots: new[] { Slot(1, "rec_probe") });
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { normalRp },
                marker: null);

            LoadTimeSweep.Run();

            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_stage", scenario.RewindPoints[0].RewindPointId);
            Assert.Equal("rp_stage", bp.RewindPointId);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("Keeping session-prov rp=rp_stage") &&
                l.Contains("no session scope"));
        }

        // ---------- Orphan supersede + tombstone -------------------------

        [Fact]
        public void OrphanSupersede_WarnsButKeeps()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_present", MergeState.Immutable) },
                new List<BranchPoint>());
            var rel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_orphan",
                OldRecordingId = "rec_present",
                NewRecordingId = "rec_vanished",  // not in committedRecordings
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation> { rel });

            LoadTimeSweep.Run();

            // Relation survives per §3.5 invariant 7.
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Orphan relation=rsr_orphan"));
        }

        [Fact]
        public void OrphanTombstone_WarnsButKeeps()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", MergeState.Immutable) },
                new List<BranchPoint>());
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_orphan",
                ActionId = "act_vanished",  // no matching ledger action
                RetiringRecordingId = "rec_a",
                UT = 0.0,
            };
            var scenario = InstallScenario(
                tombstones: new List<LedgerTombstone> { tomb });

            LoadTimeSweep.Run();

            // Tombstone is append-only; sweep logs but does not remove.
            Assert.Single(scenario.LedgerTombstones);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerSwap]") && l.Contains("Orphan tombstone=tomb_orphan"));
        }

        // ---------- Stray SupersedeTargetId on committed ------------------

        [Fact]
        public void StrayField_OnImmutable_WarnsAndClears()
        {
            var immutable = Rec("rec_imm", MergeState.Immutable,
                supersedeTarget: "rec_some_target");
            var cp = Rec("rec_cp", MergeState.CommittedProvisional,
                supersedeTarget: "rec_some_target");
            var notCommitted = Rec("rec_nc", MergeState.NotCommitted,
                supersedeTarget: "rec_some_target");
            InstallTree("tree_1",
                new List<Recording> { immutable, cp, notCommitted },
                new List<BranchPoint>());
            var scenario = InstallScenario(marker: null);

            LoadTimeSweep.Run();

            Assert.Null(immutable.SupersedeTargetId);
            Assert.Null(cp.SupersedeTargetId);
            // NotCommitted keeps its field intact (it is a live provisional;
            // we only clear the stray-on-committed case).
            Assert.Equal("rec_some_target", notCommitted.SupersedeTargetId);
            Assert.Contains(logLines, l =>
                l.Contains("[Recording]") && l.Contains("Stray SupersedeTargetId on committed rec=rec_imm"));
            Assert.Contains(logLines, l =>
                l.Contains("[Recording]") && l.Contains("Stray SupersedeTargetId on committed rec=rec_cp"));
        }

        // ---------- Nested session-provisional cleanup (§7.11) -----------

        [Fact]
        public void NestedSessionProvCleanup_OnParentDiscard()
        {
            // Setup: marker is invalid (TreeId missing) so it will be cleared.
            // A session-provisional RP and a NotCommitted recording both
            // carry the same (dead) session id — they must be swept.
            RecordingStore.AddCommittedInternal(Rec("rec_dead_active",
                MergeState.NotCommitted, sessionId: "sess_dead"));
            RecordingStore.AddCommittedInternal(Rec("rec_dead_origin",
                MergeState.CommittedProvisional));
            var nestedRp = Rp("rp_nested", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_dead");
            var invalidMarker = Marker("sess_dead", "tree_gone", "rec_dead_active",
                "rec_dead_origin", "rp_nested");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { nestedRp },
                marker: invalidMarker);

            LoadTimeSweep.Run();

            // Marker cleared (invalid tree).
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // Nested session-prov RP gone.
            Assert.Empty(scenario.RewindPoints);
            // Nested NotCommitted recording gone.
            Assert.Null(FindRecording("rec_dead_active"));

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") &&
                l.Contains("Nested session-prov cleanup sess=sess_dead"));
        }

        // ---------- Load summary + cache bump -----------------------------

        [Fact]
        public void LoadSummary_LogsCounts()
        {
            // A mix: one zombie recording, one zombie RP, one orphan
            // supersede, one orphan tombstone, one stray field.
            var zombie = Rec("rec_z", MergeState.NotCommitted, sessionId: "sess_dead");
            var stray = Rec("rec_stray", MergeState.Immutable,
                supersedeTarget: "rec_target");
            InstallTree("tree_1",
                new List<Recording> { zombie, stray },
                new List<BranchPoint>());
            var orphanRel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_orphan",
                OldRecordingId = "rec_x",
                NewRecordingId = "rec_y",
            };
            var orphanTomb = new LedgerTombstone
            {
                TombstoneId = "tomb_orphan",
                ActionId = "act_missing",
                RetiringRecordingId = "rec_stray",
            };
            var zombieRp = Rp("rp_z", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_dead");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { zombieRp },
                supersedes: new List<RecordingSupersedeRelation> { orphanRel },
                tombstones: new List<LedgerTombstone> { orphanTomb });

            LoadTimeSweep.Run();

            // Single [LoadSweep] summary with all counters.
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]") &&
                l.Contains("Marker valid=False") &&
                l.Contains("discarded=1") &&
                l.Contains("orphanSupersedes=1") &&
                l.Contains("orphanTombstones=1") &&
                l.Contains("strayFields=1") &&
                l.Contains("discardedRps=1"));
        }

        [Fact]
        public void BumpStateVersions_InvalidatesCaches_AfterSweep()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", MergeState.Immutable) },
                new List<BranchPoint>());
            var scenario = InstallScenario();

            int beforeSupersede = scenario.SupersedeStateVersion;
            int beforeTombstone = scenario.TombstoneStateVersion;

            LoadTimeSweep.Run();

            Assert.NotEqual(beforeSupersede, scenario.SupersedeStateVersion);
            Assert.NotEqual(beforeTombstone, scenario.TombstoneStateVersion);
        }

        // ---------- Self-supersede cleanup (bug/rewind-self-supersede) ----

        /// <summary>
        /// Regression: saves written before the caller-side guard shipped can
        /// contain self-supersede rows (old==new). The load-time sweep must
        /// remove them and leave healthy rows alone.
        /// </summary>
        [Fact]
        public void SelfSupersedeRow_RemovedAtLoadTime()
        {
            // Two recordings so the healthy row is not also an orphan.
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_live", MergeState.Immutable),
                    Rec("rec_origin", MergeState.Immutable),
                },
                new List<BranchPoint>());
            var selfRow = new RecordingSupersedeRelation
            {
                RelationId = "rsr_self",
                OldRecordingId = "rec_live",
                NewRecordingId = "rec_live", // cycle
            };
            var healthyRow = new RecordingSupersedeRelation
            {
                RelationId = "rsr_healthy",
                OldRecordingId = "rec_origin",
                NewRecordingId = "rec_live",
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation> { selfRow, healthyRow });

            LoadTimeSweep.Run();

            // Only the healthy row remains.
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal("rsr_healthy", scenario.RecordingSupersedes[0].RelationId);

            // Cleanup count logged in the dedicated line.
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("Cleaned 1 self-supersede row"));

            // Summary log includes selfSupersedes=1.
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("selfSupersedes=1"));

            // Per-row WARN logged on removal.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Self-supersede row rel=rsr_self")
                && l.Contains("removing"));
        }

        // ---------- Internal helpers --------------------------------------

        private static Recording FindRecording(string recordingId)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }
    }
}
