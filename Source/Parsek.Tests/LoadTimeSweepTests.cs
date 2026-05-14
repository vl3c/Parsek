using System;
using System.Collections.Generic;
using System.Linq;
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
            GroupHierarchyStore.ResetGroupsForTesting();
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
            GroupHierarchyStore.ResetGroupsForTesting();
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
            double invokedUt = 500.0,
            string supersedeTargetId = null)
        {
            return new ReFlySessionMarker
            {
                SessionId = sessId,
                TreeId = treeId,
                ActiveReFlyRecordingId = activeId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = supersedeTargetId,
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
            List<RecordingRewindRetirement> retirements = null,
            List<LedgerTombstone> tombstones = null,
            ReFlySessionMarker marker = null,
            MergeJournal journal = null)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = rps ?? new List<RewindPoint>(),
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = retirements ?? new List<RecordingRewindRetirement>(),
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
        public void MarkerValid_TreeExistsOnlyAsPendingTree_Preserved()
        {
            var tree = new RecordingTree
            {
                Id = "tree_pending",
                TreeName = "Pending Tree",
                RootRecordingId = "rec_origin",
                ActiveRecordingId = "rec_active",
                BranchPoints = new List<BranchPoint> { Bp("bp_1", "rp_1") },
            };
            var active = Rec("rec_active", MergeState.NotCommitted, sessionId: "sess_1",
                supersedeTarget: "rec_origin", treeId: "tree_pending");
            var origin = Rec("rec_origin", MergeState.CommittedProvisional,
                treeId: "tree_pending");
            var priorTip = Rec("rec_prior_tip", MergeState.CommittedProvisional,
                treeId: "tree_pending");
            tree.AddOrReplaceRecording(active);
            tree.AddOrReplaceRecording(origin);
            tree.AddOrReplaceRecording(priorTip);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            var rp = Rp("rp_1", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_1", slots: new[] { Slot(0, "rec_origin") });
            var marker = Marker("sess_1", "tree_pending", "rec_active", "rec_origin", "rp_1",
                supersedeTargetId: "rec_prior_tip");
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal("rec_prior_tip",
                scenario.ActiveReFlySessionMarker.SupersedeTargetId);
            Assert.Single(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("Marker valid sess=sess_1"));
        }

        [Fact]
        public void MarkerValid_SkipsGroupHierarchyPrune()
        {
            GroupHierarchyStore.groupParents["Kerbal X / Debris"] = "Kerbal X";

            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active_fork", MergeState.NotCommitted),
                    Rec("rec_origin", MergeState.Immutable),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_1", slots: new[] { Slot(0, "rec_origin") });
            var marker = Marker("sess_1", "tree_1", "rec_active_fork", "rec_origin", "rp_1",
                invokedUt: 500.0);
            marker.InPlaceContinuation = true;
            InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.True(GroupHierarchyStore.TryGetGroupParent("Kerbal X / Debris", out var parent));
            Assert.Equal("Kerbal X", parent);
            Assert.Contains(logLines, l =>
                l.Contains("[GroupHierarchy]")
                && l.Contains("Skipping group hierarchy prune reason=load-time-sweep while Re-Fly session is active"));
        }

        [Fact]
        public void MarkerInvalid_RunsGroupHierarchyPrune()
        {
            GroupHierarchyStore.groupParents["Kerbal X / Debris"] = "Kerbal X";

            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_origin", MergeState.Immutable),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_1", slots: new[] { Slot(0, "rec_origin") });
            var marker = Marker(null, "tree_1", "rec_origin", "rec_origin", "rp_1",
                invokedUt: 500.0);
            InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();

            Assert.False(GroupHierarchyStore.HasGroupParent("Kerbal X / Debris"));
            Assert.Contains(logLines, l =>
                l.Contains("[GroupHierarchy]")
                && l.Contains("Pruned stale group hierarchy")
                && l.Contains("reason=load-time-sweep"));
        }

        [Fact]
        public void SaveInto_ValidMarkerSkipsGroupHierarchyPrune()
        {
            GroupHierarchyStore.groupParents["Kerbal X / Debris"] = "Kerbal X";

            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active_fork", MergeState.NotCommitted),
                    Rec("rec_origin", MergeState.Immutable),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: true,
                creatingSessionId: "sess_1", slots: new[] { Slot(0, "rec_origin") });
            var marker = Marker("sess_1", "tree_1", "rec_active_fork", "rec_origin", "rp_1",
                invokedUt: 500.0);
            marker.InPlaceContinuation = true;
            InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            var node = new ConfigNode("PARSEK");
            GroupHierarchyStore.SaveInto(node);

            Assert.True(GroupHierarchyStore.TryGetGroupParent("Kerbal X / Debris", out var parent));
            Assert.Equal("Kerbal X", parent);
            Assert.NotNull(node.GetNode("GROUP_HIERARCHY"));
            Assert.Contains(logLines, l =>
                l.Contains("[GroupHierarchy]")
                && l.Contains("Skipping group hierarchy prune reason=save while Re-Fly session is active"));
        }

        [Fact]
        public void SaveInto_InvalidMarkerRunsGroupHierarchyPrune()
        {
            GroupHierarchyStore.groupParents["Kerbal X / Debris"] = "Kerbal X";

            var marker = Marker(null, "tree_missing", "rec_origin", "rec_origin", "rp_1",
                invokedUt: 500.0);
            InstallScenario(
                rps: new List<RewindPoint>(),
                marker: marker);

            var node = new ConfigNode("PARSEK");
            GroupHierarchyStore.SaveInto(node);

            Assert.False(GroupHierarchyStore.HasGroupParent("Kerbal X / Debris"));
            Assert.Null(node.GetNode("GROUP_HIERARCHY"));
            Assert.Contains(logLines, l =>
                l.Contains("[GroupHierarchy]")
                && l.Contains("Pruned stale group hierarchy")
                && l.Contains("reason=save"));
        }

        [Fact]
        public void MarkerValidator_InvalidSupersedeTarget_ClearsFieldAndLogsWarning()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active", MergeState.NotCommitted, sessionId: "sess_1"),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_1", "rec_active", "rec_origin", "rp_1",
                supersedeTargetId: "rec_missing_target");
            InstallScenario(rps: new List<RewindPoint> { rp }, marker: marker);

            var result = MarkerValidator.Validate(marker);

            Assert.True(result.Valid);
            Assert.Null(marker.SupersedeTargetId);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Marker invalid field=SupersedeTargetId; clearing")
                && l.Contains("rec_missing_target"));
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
        public void MarkerValid_InPlaceContinuation_ForkProvisional_Preserved()
        {
            // Issue #734 fork model: AtomicMarkerWrite always creates a
            // fresh NotCommitted provisional for the active attempt -- the
            // pre-fork "active == origin reuses the committed Recording"
            // pattern is gone, and so is the relax-set that accepted
            // CommittedProvisional / Immutable for the active recording.
            // The validator only accepts NotCommitted now; this test pins
            // that the in-place fork (a fresh NotCommitted active beside a
            // surviving committed origin) survives load-time validation.
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active_fork", MergeState.NotCommitted),
                    Rec("rec_origin", MergeState.CommittedProvisional),
                },
                new List<BranchPoint> { Bp("bp_1", "rp_1") });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            var marker = Marker("sess_1", "tree_1",
                activeId: "rec_active_fork", originId: "rec_origin", rpId: "rp_1");
            marker.InPlaceContinuation = true;
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
        public void Reaper_PreservesEligibleRpReferencedByActiveMarker()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_active_fork", MergeState.NotCommitted),
                    Rec("rec_origin", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                slots: new[] { Slot(0, "rec_origin") });
            var marker = Marker("sess_1", "tree_1", "rec_active_fork", "rec_origin", "rp_1");
            marker.InPlaceContinuation = true;
            RewindPointReaper.DeleteQuicksaveForTesting = id =>
            {
                deletedRpIds.Add(id);
                return true;
            };
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                marker: marker);

            LoadTimeSweep.Run();
            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", scenario.RewindPoints[0].RewindPointId);
            Assert.Empty(deletedRpIds);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("keeping marker rp=rp_1"));
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

            // One-sided relation survives per §3.5 invariant 7 because it can
            // still suppress the old recording.
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Orphan relation=rsr_orphan"));
        }

        [Fact]
        public void FullyOrphanSupersede_RemovedAtLoadTime()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_present", MergeState.Immutable) },
                new List<BranchPoint>());
            var rel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_fully_orphan",
                OldRecordingId = "rec_vanished_old",
                NewRecordingId = "rec_vanished_new",
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation> { rel });

            LoadTimeSweep.Run();

            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Fully orphaned relation=rsr_fully_orphan")
                && l.Contains("removing"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Orphan relation=rsr_fully_orphan"));
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("removedFullyOrphanSupersedes=1"));
        }

        [Fact]
        public void OrphanRewindRetirement_RemovedAtLoadTime()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_restored", MergeState.Immutable) },
                new List<BranchPoint>());
            var retirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_orphan",
                RecordingId = "rec_vanished_fork",
                RestoredRecordingId = "rec_restored",
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { retirement });

            LoadTimeSweep.Run();

            Assert.Empty(scenario.RecordingRewindRetirements);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Orphan rewind-retirement=rrt_orphan")
                && l.Contains("removing"));
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("removedOrphanRewindRetirements=1"));
        }

        [Fact]
        public void LegacyOldSideRetirement_RemovedWhenPriorTipIsNonImmutable()
        {
            // Pre-fix Pass-2 wrote a RewoundOutOldSideReason row for every
            // priorTip in the rewound subtree regardless of the dropped
            // supersede's fork MergeState. Under fix-tree-rewind-supersede-old-side
            // the new Pass-2 gate only writes such rows when the dropped
            // relation's fork is Immutable (non-self-rewound). For everyone
            // else (CommittedProvisional / NotCommitted forks, plus orphan
            // forks) the priorTip stays visible.
            //
            // Saves authored under the pre-fix code have stale rows pointing at
            // live non-Immutable priorTips. The sweep removes them so the user's
            // ghost is no longer permanently hidden. Direct mirror of
            // logs/2026-05-13_2335_kerbal-x-booster-ghost-missing.
            InstallTree("tree-prefix-bug",
                new List<Recording>
                {
                    Rec("kerbal-x-probe", MergeState.CommittedProvisional)
                },
                new List<BranchPoint>());
            var staleOldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt_stale_oldside",
                RecordingId = "kerbal-x-probe",
                RestoredRecordingId = null,
                SourceSupersedeRelationId = null,
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { staleOldSide });

            LoadTimeSweep.Run();

            Assert.Empty(scenario.RecordingRewindRetirements);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Removing legacy rewind-retirement=rrt_stale_oldside")
                && l.Contains("recording=kerbal-x-probe")
                && l.Contains("reason=fork-non-immutable-priortip-pre-fix"));
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("rewind-retirement row(s) RewoundOutOldSide on non-Immutable priorTip"));
        }

        [Fact]
        public void LegacyOldSideRetirement_KeepsImmutablePriorTipRow()
        {
            // Negative case: an old-side retirement targeting an *Immutable*
            // priorTip is preserved (existing isIntentionalOldSide carve-out).
            // The Immutable target case is reserved for stacked-canon supersede
            // shapes; the legacy non-Immutable sweep must NOT touch it.
            InstallTree("tree-immutable-priortip",
                new List<Recording>
                {
                    Rec("rec_imm_priorTip", MergeState.Immutable)
                },
                new List<BranchPoint>());
            var oldSideOnImmutable = new RecordingRewindRetirement
            {
                RetirementId = "rrt_oldside_on_imm",
                RecordingId = "rec_imm_priorTip",
                RestoredRecordingId = null,
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { oldSideOnImmutable });

            LoadTimeSweep.Run();

            Assert.Single(scenario.RecordingRewindRetirements);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Removing legacy rewind-retirement=rrt_oldside_on_imm"));
        }

        [Fact]
        public void LegacyOldSideSweep_DeferredAndDurableForMultiOldSideToImmutableForkShape()
        {
            // Pre-canon-forks saves can carry the multi-old-side-to-one-Immutable
            // -fork shape PR #807 addressed: multiple priorTips (P1, P2, P3)
            // each superseded by the same canon fork F (Orbiting → Immutable).
            // The buggy pre-fix code dropped all relations and wrote per-priorTip
            // RewoundOutOldSideReason rows. The existing legacy-Immutable sweep
            // can reconstruct ONLY ONE priorTip→canon relation per fork
            // retirement (the one named in F's RestoredRecordingId metadata),
            // so the other priorTips were kept hidden by their own old-side
            // rows. If the new non-Immutable old-side sweep removed those rows,
            // P2 and P3 would re-appear as "Destroyed" outcomes in the
            // recordings table — the exact regression PR #807 fixed.
            //
            // The guard must be DURABLE across loads. The fork retirement is a
            // one-shot signal: the per-row loop removes it and the save
            // persists without it, so a guard keyed on the fork retirement
            // alone would defer on load 1 then wrongly sweep on load 2. The
            // second-pass guard keys on (a) "an Immutable fork retirement was
            // removed THIS load" — covers load 1 — OR (b) "a supersede relation
            // whose NewRecordingId is a live Immutable recording survives" —
            // the F→P1 relation reconstructed on load 1, persisted, and seen
            // again on load 2+. This test runs the sweep TWICE on the same
            // scenario object (a faithful load-1 / load-2 simulation, since the
            // first run mutates RecordingSupersedes and RecordingRewindRetirements
            // in place) and asserts P1/P2/P3 survive BOTH runs.
            // Pass treeId explicitly so all four recordings genuinely share
            // "tree-multi-old" in recordingIdToTreeId (Rec() otherwise defaults
            // to "tree_1", and AddRecordingWithTreeForTesting only sets TreeId
            // when null — so the InstallTree treeId arg would not actually
            // reach the recordings). The tree-scoped guard keys on rec.TreeId,
            // so this keeps the test faithful to its single-tree fan-in shape.
            InstallTree("tree-multi-old",
                new List<Recording>
                {
                    Rec("F", MergeState.Immutable, treeId: "tree-multi-old"),
                    Rec("P1", MergeState.CommittedProvisional, treeId: "tree-multi-old"),
                    Rec("P2", MergeState.CommittedProvisional, treeId: "tree-multi-old"),
                    Rec("P3", MergeState.CommittedProvisional, treeId: "tree-multi-old")
                },
                new List<BranchPoint>());
            var fRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_F",
                RecordingId = "F",
                // Legacy pre-fix writer recorded only one of the priorTips here.
                RestoredRecordingId = "P1",
                SourceSupersedeRelationId = "rsr_F_P1",
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var p1OldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt_P1",
                RecordingId = "P1",
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var p2OldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt_P2",
                RecordingId = "P2",
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var p3OldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt_P3",
                RecordingId = "P3",
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement>
                {
                    fRetirement, p1OldSide, p2OldSide, p3OldSide
                });

            // --- Load 1: deferImmediate signal (Immutable fork retirement removed). ---
            LoadTimeSweep.Run();

            // The existing legacy-Immutable sweep removes F's retirement and
            // restores F→P1 (P1 hidden by the surviving relation).
            Assert.DoesNotContain(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "F");
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal("P1", scenario.RecordingSupersedes[0].OldRecordingId);
            Assert.Equal("F", scenario.RecordingSupersedes[0].NewRecordingId);

            // The new non-Immutable old-side sweep is deferred: P1, P2, P3
            // old-side rows survive. P2 and P3 stay hidden via their own
            // retirements (the only remaining suppression mechanism for them).
            Assert.Contains(scenario.RecordingRewindRetirements, r => r.RecordingId == "P1");
            Assert.Contains(scenario.RecordingRewindRetirements, r => r.RecordingId == "P2");
            Assert.Contains(scenario.RecordingRewindRetirements, r => r.RecordingId == "P3");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Removing legacy rewind-retirement")
                && (l.Contains("recording=P1")
                    || l.Contains("recording=P2")
                    || l.Contains("recording=P3")));
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("Legacy non-Immutable old-side sweep deferred"));

            // --- Load 2: F retirement is already gone; the durable signal is
            //     the surviving F→P1 supersede relation. The sweep must STILL
            //     defer — this is the regression the durable guard prevents. ---
            int logCountAfterLoad1 = logLines.Count;
            LoadTimeSweep.Run();

            Assert.Contains(scenario.RecordingRewindRetirements, r => r.RecordingId == "P1");
            Assert.Contains(scenario.RecordingRewindRetirements, r => r.RecordingId == "P2");
            Assert.Contains(scenario.RecordingRewindRetirements, r => r.RecordingId == "P3");
            int load2Removals = logLines
                .Skip(logCountAfterLoad1)
                .Count(l => l.Contains("Removing legacy rewind-retirement")
                    && (l.Contains("recording=P1")
                        || l.Contains("recording=P2")
                        || l.Contains("recording=P3")));
            Assert.Equal(0, load2Removals);
            // The deferral log fires again on load 2 via the surviving-relation signal.
            Assert.Contains(logLines.Skip(logCountAfterLoad1), l =>
                l.Contains("[LoadSweep]")
                && l.Contains("Legacy non-Immutable old-side sweep deferred"));
        }

        [Fact]
        public void LegacyOldSideSweep_RecoversStaleRow_WhenUnrelatedTreeHasCanonReFly()
        {
            // The deferral guard is TREE-SCOPED. A save can carry BOTH:
            //   - tree-user:  the user's stale CommittedProvisional priorTip
            //     RewoundOutOldSideReason row (the bug being fixed); and
            //   - tree-other: a completely unrelated, healthy successful Re-Fly
            //     with a surviving Immutable supersede relation.
            // A GLOBAL guard would see the Immutable supersede in tree-other and
            // wrongly defer the sweep of the tree-user stale row — leaving the
            // user's Watch ghost hidden forever. The tree-scoped guard only
            // defers rows whose OWN tree carries the Immutable canon state, so
            // the tree-user stale row is still swept and the user recovers.
            InstallTree("tree-user",
                new List<Recording>
                {
                    // Rec() defaults treeId to "tree_1"; pass it explicitly so
                    // the two trees are genuinely distinct in recordingIdToTreeId.
                    Rec("kerbal-x-probe", MergeState.CommittedProvisional,
                        treeId: "tree-user")
                },
                new List<BranchPoint>());
            InstallTree("tree-other",
                new List<Recording>
                {
                    Rec("other-priorTip", MergeState.CommittedProvisional,
                        treeId: "tree-other"),
                    Rec("other-canon", MergeState.Immutable,
                        treeId: "tree-other")
                },
                new List<BranchPoint>());
            var staleOldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt_user_stale",
                RecordingId = "kerbal-x-probe",
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation>
                {
                    // Healthy canon supersede in the UNRELATED tree-other.
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_other",
                        OldRecordingId = "other-priorTip",
                        NewRecordingId = "other-canon"
                    }
                },
                retirements: new List<RecordingRewindRetirement> { staleOldSide });

            LoadTimeSweep.Run();

            // The tree-user stale row IS swept — its tree carries no Immutable
            // canon state, so the unrelated tree-other supersede does not block it.
            Assert.DoesNotContain(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "kerbal-x-probe");
            Assert.Contains(logLines, l =>
                l.Contains("Removing legacy rewind-retirement=rrt_user_stale")
                && l.Contains("recording=kerbal-x-probe"));
            // The unrelated healthy supersede is untouched.
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal("other-canon", scenario.RecordingSupersedes[0].NewRecordingId);
        }

        [Fact]
        public void LegacyOldSideSweep_SameTreeMixedShape_DefersStaleRowConservatively()
        {
            // ACCEPTED LIMITATION (pinned here on purpose). A single recording
            // tree can hold multiple independent rewound-out Re-Fly slots. This
            // tree carries BOTH, in the SAME tree:
            //   - a healthy Immutable canon supersede (canonOld -> canonFork),
            //     unrelated to the stale row; and
            //   - a genuinely-stale CommittedProvisional priorTip
            //     RewoundOutOldSideReason row ("stale-probe") from a different
            //     slot.
            // The tree-scoped guard cannot distinguish them: RewoundOutOldSideReason
            // rows carry RestoredRecordingId == null / SourceSupersedeRelationId
            // == null by PR #807 design, so a row has no link to its fork, and
            // there is no recorded provenance finer than the tree. The guard
            // therefore defers BOTH — "stale-probe" is over-deferred.
            //
            // This is the correct conservative trade, NOT a regression:
            //   - Deferring leaves "stale-probe" exactly in its pre-PR-848
            //     state (hidden — PR #807 hid it on purpose at the time). A
            //     missed cleanup, not a corruption.
            //   - Sweeping it would risk re-exposing genuine multi-old-Immutable
            //     victims in trees we can't tell apart from this one — the
            //     double-materialization rendering bug PR #776/#777/#807 fixed.
            // PR #848 improves every single-shape tree (incl. the user's repro)
            // without regressing this rare same-tree-mixed shape.
            InstallTree("tree-mixed",
                new List<Recording>
                {
                    Rec("canonOld", MergeState.CommittedProvisional,
                        treeId: "tree-mixed"),
                    Rec("canonFork", MergeState.Immutable,
                        treeId: "tree-mixed"),
                    Rec("stale-probe", MergeState.CommittedProvisional,
                        treeId: "tree-mixed")
                },
                new List<BranchPoint>());
            var staleOldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt_stale_probe",
                RecordingId = "stale-probe",
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation>
                {
                    // Healthy Immutable canon supersede, SAME tree, unrelated
                    // to stale-probe.
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_canon",
                        OldRecordingId = "canonOld",
                        NewRecordingId = "canonFork"
                    }
                },
                retirements: new List<RecordingRewindRetirement> { staleOldSide });

            LoadTimeSweep.Run();

            // The stale row is DEFERRED (conservatively retained) because its
            // tree carries Immutable canon state we cannot prove it independent
            // of. This is the documented accepted limitation.
            Assert.Contains(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "stale-probe");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Removing legacy rewind-retirement=rrt_stale_probe"));
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("Legacy non-Immutable old-side sweep deferred"));
            // The healthy canon supersede is untouched.
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal("canonFork", scenario.RecordingSupersedes[0].NewRecordingId);
        }

        [Fact]
        public void LegacyOldSideSweep_DefersViaRemovedForkSignal_WhenReconstructionFails()
        {
            // Proves treesWithRemovedImmutableForkRetirement is LOAD-BEARING on
            // its own — not just redundant with treesWithSurvivingImmutableSupersede.
            //
            // The legacy multi-old shape, but F's DefaultReason retirement has
            // a null RestoredRecordingId, so TryRestoreLegacyImmutableSupersede
            // returns MissingMetadata: F's retirement is still removed, but NO
            // F->priorTip supersede relation is reconstructed. On load 1 the
            // only deferral signal available is treesWithRemovedImmutableForkRetirement
            // (the per-row loop just removed an Immutable fork retirement from
            // this tree). Without that signal the stale-looking P2 row would be
            // swept and a genuine multi-old-Immutable victim re-exposed.
            //
            // (Load 2 of this same save has neither signal — the documented
            // residual gap — but load 1 is where the protection must hold,
            // and it does via this set.)
            InstallTree("tree-recon-fail",
                new List<Recording>
                {
                    Rec("F", MergeState.Immutable, treeId: "tree-recon-fail"),
                    Rec("P2", MergeState.CommittedProvisional, treeId: "tree-recon-fail")
                },
                new List<BranchPoint>());
            var fRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_F_recon_fail",
                RecordingId = "F",
                // null RestoredRecordingId -> TryRestoreLegacyImmutableSupersede
                // returns MissingMetadata -> no relation reconstructed.
                RestoredRecordingId = null,
                SourceSupersedeRelationId = null,
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var p2OldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt_P2_recon_fail",
                RecordingId = "P2",
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement>
                {
                    fRetirement, p2OldSide
                });

            LoadTimeSweep.Run();

            // F's retirement was removed but no relation reconstructed
            // (MissingMetadata) -> treesWithSurvivingImmutableSupersede is empty.
            Assert.DoesNotContain(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "F");
            Assert.Empty(scenario.RecordingSupersedes);

            // P2's row is STILL deferred — solely via treesWithRemovedImmutableForkRetirement.
            Assert.Contains(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "P2");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Removing legacy rewind-retirement=rrt_P2_recon_fail"));
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("Legacy non-Immutable old-side sweep deferred"));
        }

        [Fact]
        public void LegacyOldSideRetirement_SweepIsIdempotent()
        {
            // First load removes the stale row; subsequent loads have nothing
            // to do. Verify by running the sweep twice and confirming the
            // second run produces no removal log line.
            InstallTree("tree-prefix-bug",
                new List<Recording>
                {
                    Rec("priorTip", MergeState.CommittedProvisional)
                },
                new List<BranchPoint>());
            var staleOldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt_stale",
                RecordingId = "priorTip",
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { staleOldSide });

            LoadTimeSweep.Run();
            int logCountAfterFirstSweep = logLines.Count;
            Assert.Empty(scenario.RecordingRewindRetirements);

            LoadTimeSweep.Run();
            Assert.Empty(scenario.RecordingRewindRetirements);
            // Second sweep produces no further "Removing legacy" line.
            int newRemovalLines = logLines
                .Skip(logCountAfterFirstSweep)
                .Count(l => l.Contains("Removing legacy rewind-retirement"));
            Assert.Equal(0, newRemovalLines);
        }

        [Fact]
        public void RetirementPointingAtImmutable_RemovedAndSupersedeRestoredAtLoadTime()
        {
            // Defensive cleanup for legacy pre-fix saves: the buggy rewind
            // path dropped the supersede relation AND wrote a retirement.
            // Just removing the retirement here would leave the canon
            // visible AND the priorTip un-superseded → double-materialization.
            // The sweep must reconstruct the priorTip → canon relation from
            // the retirement metadata so the priorTip stays superseded.
            InstallTree("tree_canon",
                new List<Recording>
                {
                    Rec("rec_priorTip", MergeState.Immutable),
                    Rec("rec_canon", MergeState.Immutable)
                },
                new List<BranchPoint>());
            var legacyRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_legacy_canon",
                RecordingId = "rec_canon",
                RestoredRecordingId = "rec_priorTip",
                SourceSupersedeRelationId = "rsr_legacy_known",
                CreatedUT = 286.9,
                Reason = RecordingRewindRetirement.DefaultReason
            };
            // No supersede relation in the scenario (the buggy code dropped
            // it). LoadTimeSweep must reconstruct it.
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { legacyRetirement });

            LoadTimeSweep.Run();

            Assert.Empty(scenario.RecordingRewindRetirements);
            // Supersede relation was reconstructed.
            var restored = Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal("rec_priorTip", restored.OldRecordingId);
            Assert.Equal("rec_canon", restored.NewRecordingId);
            // RelationId carried over from retirement metadata when set.
            Assert.Equal("rsr_legacy_known", restored.RelationId);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Removing rewind-retirement=rrt_legacy_canon")
                && l.Contains("Immutable canon recording=rec_canon")
                && l.Contains("restored supersede relation"));
        }

        [Fact]
        public void RetirementPointingAtImmutable_OrphanWithoutRestoredId_RemovedButNoRelationCreated()
        {
            // Edge case: legacy retirement carries no RestoredRecordingId
            // (orphan write or partial pre-fix data). We can't reconstruct
            // the supersede relation. Remove the retirement and warn so the
            // user can investigate; the canon will render alongside the
            // priorTip until the user reseals manually.
            InstallTree("tree_canon",
                new List<Recording>
                {
                    Rec("rec_canon", MergeState.Immutable)
                },
                new List<BranchPoint>());
            var orphanRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_orphan_imm",
                RecordingId = "rec_canon",
                RestoredRecordingId = null, // missing metadata
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { orphanRetirement });

            LoadTimeSweep.Run();

            Assert.Empty(scenario.RecordingRewindRetirements);
            // No supersede relation reconstructed (no metadata to do so).
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Removing rewind-retirement=rrt_orphan_imm")
                && l.Contains("supersede relation cannot be reconstructed"));
        }

        [Fact]
        public void RetirementPointingAtImmutable_RestoredRecordingMissing_DoesNotInjectOrphanRelation()
        {
            // Edge case: legacy retirement carries a non-empty
            // RestoredRecordingId, but the priorTip recording itself was
            // purged out-of-band (manual delete, tree discard, earlier
            // load-time sweep) between the buggy save and the current load.
            // Without verifying the priorTip exists, the legacy cleanup
            // would synthesize a one-sided orphan supersede pointing at a
            // missing OldRecordingId. SweepOrphanSupersedes already ran
            // earlier in the same sweep, so the new orphan would survive
            // until next load — a spurious entry that would silently
            // suppress... nothing (no recording with that id exists), but
            // would still pollute the supersede list and confuse audit
            // tools.
            //
            // Fix: TryRestoreLegacyImmutableSupersede now checks the live
            // recording set and returns RestoredRecordingMissing when the
            // priorTip is gone. The retirement is still removed (the
            // canon recording becomes visible) but no relation is
            // injected. Audit log makes the missing-priorTip case
            // distinguishable from the orphan-no-id case.
            InstallTree("tree_canon_only",
                new List<Recording>
                {
                    Rec("rec_canon", MergeState.Immutable)
                    // rec_priorTip intentionally NOT installed
                },
                new List<BranchPoint>());
            var orphanedPriorTipRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_priortip_gone",
                RecordingId = "rec_canon",
                RestoredRecordingId = "rec_priorTip", // recording no longer exists
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { orphanedPriorTipRetirement });

            LoadTimeSweep.Run();

            // Retirement removed (canon becomes visible).
            Assert.Empty(scenario.RecordingRewindRetirements);
            // CRITICAL: no synthesized orphan relation pointing at a missing priorTip.
            Assert.Empty(scenario.RecordingSupersedes);
            // Distinct log line for this outcome — distinguishable from the
            // orphan-no-RestoredRecordingId case and the standard restore.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Removing rewind-retirement=rrt_priortip_gone")
                && l.Contains("RestoredRecordingId no longer exists in committed store"));
        }

        [Fact]
        public void RetirementPointingAtImmutable_DemotedCanonReason_NotSwept()
        {
            // The post-fix Pass-2 demotion intentionally retires Immutable
            // forks whose priorTip is itself being retired in the same
            // rewind batch (the canon collapses to preserve the
            // no-double-materialization invariant). The retirement is tagged
            // with RecordingRewindRetirement.DemotedCanonReason. LoadTimeSweep
            // must NOT treat such a retirement as legacy bad state — doing
            // so would remove the retirement and reconstruct the priorTip →
            // canon supersede, making the demoted canon visible again and
            // silently undoing the Pass-2 fix on every save/load cycle.
            //
            // Repro shape: A → B(Provisional) → C(Immutable). After the
            // user's parent Rewind, in-memory state is A→B and B→C dropped,
            // B retired (DefaultReason), C retired (DemotedCanonReason).
            // Save → load → sweep: B's retirement stays (B is Provisional,
            // not Immutable, untouched). C's retirement is Immutable but
            // carries DemotedCanonReason — must also stay.
            InstallTree("tree_mixed",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    Rec("rec_b", MergeState.CommittedProvisional),
                    Rec("rec_c", MergeState.Immutable)
                },
                new List<BranchPoint>());
            var bRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_b",
                RecordingId = "rec_b",
                RestoredRecordingId = "rec_a",
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var cDemotedRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_c_demoted",
                RecordingId = "rec_c",
                RestoredRecordingId = "rec_b",
                Reason = RecordingRewindRetirement.DemotedCanonReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement>
                {
                    bRetirement, cDemotedRetirement
                });

            LoadTimeSweep.Run();

            // Both retirements survive: B stays retired (its Provisional
            // recording is not subject to the Immutable cleanup), C stays
            // retired because its DemotedCanonReason tag flags it as
            // intentional.
            Assert.Equal(2, scenario.RecordingRewindRetirements.Count);
            Assert.Contains(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "rec_b");
            Assert.Contains(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "rec_c");
            // No supersede was reconstructed — neither cleanup path fired.
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Removing rewind-retirement=rrt_c_demoted"));
        }

        [Fact]
        public void RetirementPointingAtImmutable_SelfRewoundCanonReason_NotSwept()
        {
            // Counterpart to the demoted-canon case: a retirement pointing
            // at an Immutable recording with SelfRewoundCanonReason is the
            // result of the user explicitly self-rewinding the canon. The
            // sweep must NOT remove this retirement or reconstruct the
            // priorTip → canon relation — doing so would silently undo the
            // user's self-rewind on every load.
            InstallTree("tree_self_rewound",
                new List<Recording>
                {
                    Rec("rec_priorTip", MergeState.Immutable),
                    Rec("rec_canon", MergeState.Immutable)
                },
                new List<BranchPoint>());
            var selfRewoundRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_self_rewound",
                RecordingId = "rec_canon",
                RestoredRecordingId = "rec_priorTip",
                Reason = RecordingRewindRetirement.SelfRewoundCanonReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { selfRewoundRetirement });

            LoadTimeSweep.Run();

            // Retirement survives unchanged — canon stays retired.
            Assert.Single(scenario.RecordingRewindRetirements);
            // No supersede relation reconstructed (would un-do the
            // user's self-rewind).
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Removing rewind-retirement=rrt_self_rewound"));
        }

        [Fact]
        public void RetirementPointingAtImmutable_DefaultReason_SweptAsLegacy()
        {
            // Counterpart to the test above: a retirement pointing at an
            // Immutable recording WITHOUT the DemotedCanonReason tag is
            // pre-fix legacy bad state. The sweep removes it and reconstructs
            // the dropped supersede relation.
            InstallTree("tree_legacy",
                new List<Recording>
                {
                    Rec("rec_priorTip", MergeState.Immutable),
                    Rec("rec_canon", MergeState.Immutable)
                },
                new List<BranchPoint>());
            var legacyRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_legacy",
                RecordingId = "rec_canon",
                RestoredRecordingId = "rec_priorTip",
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var scenario = InstallScenario(
                retirements: new List<RecordingRewindRetirement> { legacyRetirement });

            LoadTimeSweep.Run();

            Assert.Empty(scenario.RecordingRewindRetirements);
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Removing rewind-retirement=rrt_legacy")
                && l.Contains("restored supersede relation"));
        }

        [Fact]
        public void RetirementPointingAtImmutable_SupersedeAlreadyPresent_RelationNotDuplicated()
        {
            // Idempotency: if a supersede relation matching the retirement's
            // priorTip → canon pair is already in the scenario (e.g. user
            // re-flew between rewinds), the legacy cleanup must not add a
            // duplicate.
            InstallTree("tree_canon",
                new List<Recording>
                {
                    Rec("rec_priorTip", MergeState.Immutable),
                    Rec("rec_canon", MergeState.Immutable)
                },
                new List<BranchPoint>());
            var existingRel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_already_there",
                OldRecordingId = "rec_priorTip",
                NewRecordingId = "rec_canon",
                UT = 100.0
            };
            var legacyRetirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_legacy_canon",
                RecordingId = "rec_canon",
                RestoredRecordingId = "rec_priorTip",
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation> { existingRel },
                retirements: new List<RecordingRewindRetirement> { legacyRetirement });

            LoadTimeSweep.Run();

            Assert.Empty(scenario.RecordingRewindRetirements);
            // Supersede list still has exactly one entry — the original.
            var only = Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal("rsr_already_there", only.RelationId);
        }

        [Fact]
        public void SupersedeEndpointsInPendingTree_NotRemovedAsFullyOrphaned()
        {
            var tree = new RecordingTree
            {
                Id = "tree_pending_supersede",
                TreeName = "Pending Supersede",
            };
            var oldRec = Rec("rec_pending_old", MergeState.CommittedProvisional,
                treeId: tree.Id);
            var newRec = Rec("rec_pending_new", MergeState.CommittedProvisional,
                treeId: tree.Id);
            tree.AddOrReplaceRecording(oldRec);
            tree.AddOrReplaceRecording(newRec);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);

            var rel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_pending_tree",
                OldRecordingId = oldRec.RecordingId,
                NewRecordingId = newRec.RecordingId,
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation> { rel });

            LoadTimeSweep.Run();

            Assert.Single(scenario.RecordingSupersedes);
            Assert.Same(rel, scenario.RecordingSupersedes[0]);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Fully orphaned relation=rsr_pending_tree"));
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("removedFullyOrphanSupersedes=0"));
        }

        [Fact]
        public void SupersedeFullyOrphanedByZombieDiscard_RemovedSameLoad()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_zombie_old", MergeState.NotCommitted, sessionId: "sess_dead"),
                    Rec("rec_zombie_new", MergeState.NotCommitted, sessionId: "sess_dead"),
                },
                new List<BranchPoint>());
            var rel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_zombie_pair",
                OldRecordingId = "rec_zombie_old",
                NewRecordingId = "rec_zombie_new",
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation> { rel });

            LoadTimeSweep.Run();

            Assert.Null(FindRecording("rec_zombie_old"));
            Assert.Null(FindRecording("rec_zombie_new"));
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Fully orphaned relation=rsr_zombie_pair")
                && l.Contains("removing"));
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]")
                && l.Contains("discarded=2")
                && l.Contains("removedFullyOrphanSupersedes=1"));
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
            var retainedOrphanRel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_orphan",
                OldRecordingId = "rec_stray",
                NewRecordingId = "rec_y",
            };
            var fullyOrphanRel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_fully_orphan",
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
                supersedes: new List<RecordingSupersedeRelation> { retainedOrphanRel, fullyOrphanRel },
                tombstones: new List<LedgerTombstone> { orphanTomb });

            LoadTimeSweep.Run();

            // Single [LoadSweep] summary with all counters.
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep]") &&
                l.Contains("Marker valid=False") &&
                l.Contains("discarded=1") &&
                l.Contains("orphanSupersedes=1") &&
                l.Contains("removedFullyOrphanSupersedes=1") &&
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
