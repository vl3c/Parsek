using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #601: Re-Fly load preserves post-RP merge tree mutations.
    ///
    /// <para>
    /// The Rewind Point's frozen <c>.sfs</c> snapshots the recording tree at the
    /// moment the RP was authored. If <c>RecordingOptimizer.SplitAtSection</c>
    /// (or any other tree-shape mutation) ran AFTER RP creation but BEFORE the
    /// player invoked Re-Fly, the in-memory <c>RecordingStore.CommittedTrees</c>
    /// has post-mutation recording IDs (and updated BranchPoint parent refs)
    /// that the loaded RP <c>.sfs</c> does NOT know about. Without the splice
    /// helper, those orphaned-but-on-disk recordings vanish from the active
    /// tree on Re-Fly load.
    /// </para>
    ///
    /// <para>
    /// Pins the contract that
    /// <see cref="ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree"/>
    /// (called inside <c>TryRestoreActiveTreeNode</c> right before
    /// <c>RemoveCommittedTreeById</c>) deep-clones the missing recordings AND
    /// any committed-tree-only / parent-id-mutated BranchPoints into the loaded
    /// tree, marks them <c>FilesDirty</c>, and emits a structured
    /// <c>[Scenario][INFO]</c> log line reporting the splice counts.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class Bug601ReFlyPostMergeSplitPreservationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug601ReFlyPostMergeSplitPreservationTests()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        // ============================================================
        // The user's playtest case — RP frozen pre-merge, merge ran
        // SplitAtSection, Re-Fly invoked. The committed tree has the
        // post-split halves; the loaded tree doesn't. The splice must
        // pull them in so the live tree shape survives the load.
        // ============================================================

        [Fact]
        public void Splice_TreeWithMissingPostSplitRecordings_PullsThemFromCommitted()
        {
            // Committed tree (in-memory): the post-merge state — capsule
            // recording was split into atmo (rec_capsule_atmo) + exo
            // (rec_capsule_exo); booster recording was split into atmo
            // (rec_booster_atmo) + exo (rec_booster_exo).
            var committed = MakeBaseTree("tree_kerbalx");
            AddRecording(committed, "rec_capsule_atmo", "Kerbal X", utStart: 6, utEnd: 222, points: 229);
            AddRecording(committed, "rec_capsule_exo", "Kerbal X", utStart: 222, utEnd: 4618, points: 52);
            AddRecording(committed, "rec_booster_atmo", "Kerbal X Probe", utStart: 159, utEnd: 222, points: 21);
            AddRecording(committed, "rec_booster_exo", "Kerbal X Probe", utStart: 219, utEnd: 235, points: 6);
            committed.ActiveRecordingId = "rec_capsule_atmo";
            // BranchPoint linking the booster split chain: parent points
            // at the post-split exo half (the merge updated this from
            // rec_booster_atmo -> rec_booster_exo). The loaded tree's
            // BP version still references rec_booster_atmo.
            committed.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_booster_split",
                UT = 222.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_booster_exo" },
                ChildRecordingIds = new List<string> { "rec_capsule_exo" },
            });
            RecordingStore.AddCommittedTreeForTesting(committed);

            // Loaded tree (from the frozen RP .sfs): the pre-merge
            // shape — capsule recording was a single rec, booster was
            // a single rec, and the BP parent points at the original
            // un-split id.
            var loaded = MakeBaseTree("tree_kerbalx");
            AddRecording(loaded, "rec_capsule_atmo", "Kerbal X", utStart: 6, utEnd: 4618, points: 281);
            AddRecording(loaded, "rec_booster_atmo", "Kerbal X Probe", utStart: 159, utEnd: 235, points: 27);
            loaded.ActiveRecordingId = "rec_capsule_atmo";
            loaded.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_booster_split",
                UT = 222.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_booster_atmo" },
                ChildRecordingIds = new List<string> { "rec_capsule_atmo" },
            });

            int spliced = ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree(loaded);

            Assert.Equal(2, spliced);
            Assert.Equal(4, loaded.Recordings.Count);
            Assert.True(loaded.Recordings.ContainsKey("rec_capsule_exo"));
            Assert.True(loaded.Recordings.ContainsKey("rec_booster_exo"));

            // Spliced recordings must be deep clones, not aliased — mutating
            // the committed copy must not affect the loaded one (and vice
            // versa). This protects against the pending-tree path racing
            // with the splice on a future scene change.
            var splicedCapsuleExo = loaded.Recordings["rec_capsule_exo"];
            Assert.NotSame(committed.Recordings["rec_capsule_exo"], splicedCapsuleExo);

            // FilesDirty must be true so the next OnSave rewrites the .sfs
            // (and advances the .prec sidecar epoch in lockstep — without
            // this, the spliced recording would surface as a stale-sidecar
            // warning on the next quickload).
            Assert.True(splicedCapsuleExo.FilesDirty);
            Assert.True(loaded.Recordings["rec_booster_exo"].FilesDirty);

            // BranchPoint parent ids must follow the post-merge truth.
            BranchPoint bp = loaded.BranchPoints.Find(b => b.Id == "bp_booster_split");
            Assert.NotNull(bp);
            Assert.Single(bp.ParentRecordingIds);
            Assert.Equal("rec_booster_exo", bp.ParentRecordingIds[0]);
        }

        [Fact]
        public void Splice_TreeAlreadyMatches_NoSplice_LogsAlreadyInSync()
        {
            // Sanity case: RP was created post-merge so the loaded tree
            // already matches the committed tree. The helper must be a
            // pure no-op and emit the "already in sync" verbose line.
            var committed = MakeBaseTree("tree_already_synced");
            AddRecording(committed, "rec_a", "Vessel A", utStart: 10, utEnd: 100, points: 50);
            AddRecording(committed, "rec_b", "Vessel A", utStart: 100, utEnd: 200, points: 50);
            committed.ActiveRecordingId = "rec_a";
            RecordingStore.AddCommittedTreeForTesting(committed);

            var loaded = MakeBaseTree("tree_already_synced");
            AddRecording(loaded, "rec_a", "Vessel A", utStart: 10, utEnd: 100, points: 50);
            AddRecording(loaded, "rec_b", "Vessel A", utStart: 100, utEnd: 200, points: 50);
            loaded.ActiveRecordingId = "rec_a";

            int spliced = ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree(loaded);

            Assert.Equal(0, spliced);
            Assert.Equal(2, loaded.Recordings.Count);
            // The "already in sync" decision is auditable.
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("SpliceMissingCommittedRecordings")
                && l.Contains("already in sync"));
            // Critically NO recording should have been marked dirty by the
            // no-op pass (the existing FilesDirty=false on the loaded
            // recordings is preserved).
            Assert.False(loaded.Recordings["rec_a"].FilesDirty);
            Assert.False(loaded.Recordings["rec_b"].FilesDirty);
        }

        [Fact]
        public void Splice_NoCommittedTreeForId_GracefulNoOp()
        {
            // Edge case: the loaded tree has no in-memory committed
            // counterpart (e.g. mod just installed, or the RP belongs to
            // a tree that was discarded mid-session and only the .sfs
            // survives). The helper must return 0 without throwing.
            var loaded = MakeBaseTree("tree_no_committed");
            AddRecording(loaded, "rec_solo", "Solo Vessel", utStart: 0, utEnd: 50, points: 25);
            loaded.ActiveRecordingId = "rec_solo";

            int spliced = ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree(loaded);

            Assert.Equal(0, spliced);
            Assert.Single(loaded.Recordings);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("SpliceMissingCommittedRecordings")
                && l.Contains("no in-memory")
                && l.Contains("committed counterpart"));
        }

        [Fact]
        public void Splice_BranchPointOnlyInCommitted_ClonedIntoLoaded()
        {
            // After RP creation the merge added a brand-new BranchPoint
            // (e.g. a Breakup BP for a debris event recorded post-RP).
            // The loaded tree doesn't know about it; the splice must
            // copy it in so the live tree's chain linkage survives.
            var committed = MakeBaseTree("tree_new_bp");
            AddRecording(committed, "rec_root", "Root", utStart: 0, utEnd: 200, points: 100);
            committed.ActiveRecordingId = "rec_root";
            committed.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_post_rp_breakup",
                UT = 150.0,
                Type = BranchPointType.Breakup,
                BreakupCause = "STRUCTURAL_FAILURE",
                ParentRecordingIds = new List<string> { "rec_root" },
                ChildRecordingIds = new List<string> { "rec_root" },
                DebrisCount = 4,
            });
            RecordingStore.AddCommittedTreeForTesting(committed);

            var loaded = MakeBaseTree("tree_new_bp");
            AddRecording(loaded, "rec_root", "Root", utStart: 0, utEnd: 200, points: 100);
            loaded.ActiveRecordingId = "rec_root";
            // Loaded tree has NO BranchPoints (RP was authored before the
            // breakup happened).

            int spliced = ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree(loaded);

            Assert.Equal(0, spliced);
            Assert.Single(loaded.BranchPoints);
            BranchPoint clonedBp = loaded.BranchPoints[0];
            Assert.Equal("bp_post_rp_breakup", clonedBp.Id);
            Assert.Equal(BranchPointType.Breakup, clonedBp.Type);
            Assert.Equal("STRUCTURAL_FAILURE", clonedBp.BreakupCause);
            Assert.Equal(4, clonedBp.DebrisCount);
            // Deep-cloned: mutating the committed BP must not affect the
            // loaded one. The BranchPoint's ParentRecordingIds list is the
            // most-mutated field, so check that one explicitly.
            Assert.NotSame(committed.BranchPoints[0].ParentRecordingIds, clonedBp.ParentRecordingIds);
        }

        [Fact]
        public void Splice_StructuredLogShape_MatchesContract()
        {
            // Pin the structured INFO log line shape so observability
            // tooling and post-playtest log scans can parse it. The line
            // shipped in PR #602 is:
            // "SpliceMissingCommittedRecordings: tree '<name>' (id=<id>) "
            // "loadedBefore=<n> committed=<n> after=<n> "
            // "splicedRecordings=<n> refreshedRecordings=<n> "
            // "splicedBranchPoints=<n> updatedBranchPoints=<n> "
            // "source=committed-tree-in-memory"
            // PR #575 P1 review follow-up: `refreshedRecordings` is the
            // count of same-ID recordings whose structural fields were
            // refreshed from the committed tree (the post-merge truth)
            // because SplitAtSection mutated them in place after the RP
            // .sfs was authored. In this scenario rec_a's loaded copy
            // (UT 0-200, 100 pts) diverges from the committed truncated
            // first half (UT 0-100, 50 pts), so it gets refreshed.
            var committed = MakeBaseTree("tree_log_shape");
            AddRecording(committed, "rec_a", "A", utStart: 0, utEnd: 100, points: 50);
            AddRecording(committed, "rec_b", "A", utStart: 100, utEnd: 200, points: 50);
            committed.ActiveRecordingId = "rec_a";
            RecordingStore.AddCommittedTreeForTesting(committed);

            var loaded = MakeBaseTree("tree_log_shape");
            AddRecording(loaded, "rec_a", "A", utStart: 0, utEnd: 200, points: 100);
            // Active recording is intentionally a DIFFERENT id from the
            // diverged same-ID, so the active-recording skip path doesn't
            // suppress the refresh in this log-shape pin.
            loaded.ActiveRecordingId = null;

            ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree(loaded);

            string entry = logLines.Find(l =>
                l.Contains("[Scenario]")
                && l.Contains("[INFO]")
                && l.Contains("SpliceMissingCommittedRecordings"));
            Assert.NotNull(entry);
            Assert.Contains("loadedBefore=1", entry);
            Assert.Contains("committed=2", entry);
            Assert.Contains("after=2", entry);
            Assert.Contains("splicedRecordings=1", entry);
            Assert.Contains("refreshedRecordings=1", entry);
            Assert.Contains("source=committed-tree-in-memory", entry);
        }

        // ============================================================
        // PR #575 P1 review follow-up — same-ID recording refresh.
        //
        // The reviewer pointed out that SplitAtSection mutates the
        // *original* recording in place (truncates trajectory, moves
        // terminal payload to the new second half, reassigns
        // ChildBranchPointId to the second half) before adding the
        // second half to the tree. The previous splice loop skipped
        // any committed-tree recording whose id matched a loaded-tree
        // recording — so a restored pre-split tree kept the stale
        // pre-split first half AND its old child-link, while the BP
        // loop already overwrote the parent BP's ParentRecordingIds
        // to name the new second half. That left the tree internally
        // inconsistent (the old first half claimed to parent the BP
        // via ChildBranchPointId, but the BP's ParentRecordingIds
        // listed the new second half).
        // ============================================================

        [Fact]
        public void Splice_TreeWithStaleFirstHalfAfterSplit_RefreshesFirstHalfFromCommitted()
        {
            // Committed (post-merge): rec_R is the truncated first half
            // (UT 0-100, 50 pts) with ChildBranchPointId pointing at the
            // post-split chain BP, and a NEW recording rec_R_exo carries
            // the moved terminal payload (UT 100-200).
            var committed = MakeBaseTree("tree_split_refresh");
            AddRecording(committed, "rec_R", "Vessel R", utStart: 0, utEnd: 100, points: 50);
            AddRecording(committed, "rec_R_exo", "Vessel R", utStart: 100, utEnd: 200, points: 50);
            committed.Recordings["rec_R"].ChildBranchPointId = "bp_split_new";
            committed.Recordings["rec_R"].TerminalStateValue = null;
            committed.Recordings["rec_R"].TerminalOrbitBody = null;
            committed.Recordings["rec_R_exo"].TerminalStateValue = TerminalState.SubOrbital;
            committed.Recordings["rec_R_exo"].TerminalOrbitBody = "Kerbin";
            committed.ActiveRecordingId = "rec_R_exo";
            committed.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_split_new",
                UT = 100.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_R_exo" },
                ChildRecordingIds = new List<string> { },
            });
            RecordingStore.AddCommittedTreeForTesting(committed);

            // Loaded (pre-split RP snapshot): rec_R holds the FULL pre-
            // split trajectory (UT 0-200, 100 pts), the original child
            // link to the old BP, and the terminal payload that the
            // committed copy has moved to rec_R_exo.
            var loaded = MakeBaseTree("tree_split_refresh");
            AddRecording(loaded, "rec_R", "Vessel R", utStart: 0, utEnd: 200, points: 100);
            loaded.Recordings["rec_R"].ChildBranchPointId = "bp_pre_split_old";
            loaded.Recordings["rec_R"].TerminalStateValue = TerminalState.SubOrbital;
            loaded.Recordings["rec_R"].TerminalOrbitBody = "Kerbin";
            loaded.ActiveRecordingId = null;

            int spliced = ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree(loaded);

            // rec_R_exo is the new id → spliced count is 1.
            Assert.Equal(1, spliced);
            Assert.Equal(2, loaded.Recordings.Count);

            // The same-ID rec_R must now match the committed truncated
            // first half on every split-relevant structural field.
            Recording loadedR = loaded.Recordings["rec_R"];
            Assert.Equal(50, loadedR.Points.Count);
            Assert.Equal(100.0, loadedR.Points[loadedR.Points.Count - 1].ut);
            Assert.Equal("bp_split_new", loadedR.ChildBranchPointId);
            Assert.Null(loadedR.TerminalStateValue);
            Assert.Null(loadedR.TerminalOrbitBody);
            // FilesDirty must be true so the next OnSave rewrites the
            // .sfs + .prec with the refreshed shape and a fresh epoch.
            Assert.True(loadedR.FilesDirty);

            // Identity fields must NOT have been clobbered by the refresh.
            Assert.Equal("rec_R", loadedR.RecordingId);
            Assert.Equal("tree_split_refresh", loadedR.TreeId);

            // Structured log line reports refreshedRecordings=1 and the
            // refreshed id list contains rec_R.
            string entry = logLines.Find(l =>
                l.Contains("[Scenario]")
                && l.Contains("[INFO]")
                && l.Contains("SpliceMissingCommittedRecordings"));
            Assert.NotNull(entry);
            Assert.Contains("refreshedRecordings=1", entry);
            string verbose = logLines.Find(l =>
                l.Contains("[Scenario]")
                && l.Contains("refreshedIds=[rec_R]"));
            Assert.NotNull(verbose);
        }

        [Fact]
        public void Splice_RefreshDoesNotClobberActiveRecording()
        {
            // Same divergence as the previous test, but rec_R is the
            // ACTIVE recording — the recorder is live-updating its
            // in-memory state during Re-Fly. The refresh path must
            // skip rec_R entirely so the recorder's mutations survive.
            var committed = MakeBaseTree("tree_active_skip");
            AddRecording(committed, "rec_R", "Vessel R", utStart: 0, utEnd: 100, points: 50);
            committed.Recordings["rec_R"].ChildBranchPointId = "bp_split_new";
            committed.ActiveRecordingId = "rec_R";
            RecordingStore.AddCommittedTreeForTesting(committed);

            var loaded = MakeBaseTree("tree_active_skip");
            AddRecording(loaded, "rec_R", "Vessel R", utStart: 0, utEnd: 200, points: 100);
            loaded.Recordings["rec_R"].ChildBranchPointId = "bp_pre_split_old";
            loaded.ActiveRecordingId = "rec_R";

            ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree(
                loaded, activeRecordingId: "rec_R");

            // rec_R's in-memory state must be UNTOUCHED.
            Recording loadedR = loaded.Recordings["rec_R"];
            Assert.Equal(100, loadedR.Points.Count);
            Assert.Equal("bp_pre_split_old", loadedR.ChildBranchPointId);
            Assert.False(loadedR.FilesDirty);
        }

        // ============================================================
        // Helpers
        // ============================================================

        private static RecordingTree MakeBaseTree(string id)
        {
            return new RecordingTree
            {
                Id = id,
                TreeName = id,
                RootRecordingId = null,
            };
        }

        private static void AddRecording(
            RecordingTree tree, string recId, string vesselName,
            double utStart, double utEnd, int points)
        {
            var rec = new Recording
            {
                RecordingId = recId,
                VesselName = vesselName,
                TreeId = tree.Id,
                ExplicitStartUT = utStart,
                ExplicitEndUT = utEnd,
                FilesDirty = false,
                SidecarEpoch = 5,
            };
            for (int i = 0; i < points; i++)
            {
                double t = points <= 1
                    ? utStart
                    : utStart + ((utEnd - utStart) * i / (points - 1));
                rec.Points.Add(new TrajectoryPoint { ut = t });
            }
            tree.AddOrReplaceRecording(rec);
            if (string.IsNullOrEmpty(tree.RootRecordingId))
                tree.RootRecordingId = recId;
        }
    }
}
