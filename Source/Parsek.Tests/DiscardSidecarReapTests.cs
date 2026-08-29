using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the gameplay-path sidecar reap (<see cref="DiscardSidecarReap"/>) that
    /// closes the OPEN sibling of the S0.5 discard-residue leak.
    ///
    /// <para><b>The bug.</b> <c>StartRecording</c>'s quickload-resume OnSave writes the
    /// ACTIVE tree's <c>.prec</c> / <c>.pann</c> / <c>_ghost.craft</c> sidecars to
    /// <c>saves/&lt;save&gt;/Parsek/Recordings/</c>, but
    /// <c>ParsekFlight.AutoDiscardActiveTreeCore</c> is in-memory-only BY DESIGN. Every
    /// normal-gameplay discard that routes through it (scene-exit no-op auto-discard,
    /// pre-switch Discard Case A / Case B, the idle-on-pad fast path, the no-session
    /// committed-resume revert) therefore left those files behind, and with the store
    /// otherwise empty the sweeper's zero-known safety guard (correctly) refuses to
    /// clean them - permanently.</para>
    ///
    /// <para><b>Fails on origin/main.</b> Every cell here either calls
    /// <c>DiscardSidecarReap</c>, which does not exist on main, or source-gates the
    /// reap wiring in <c>ParsekFlight.AutoDiscardActiveTreeCore</c>, which on main has
    /// no capture and no reap call.</para>
    ///
    /// <para><b>Why the file cells drive the reap rather than ParsekFlight.</b>
    /// <c>AutoDiscardActiveTreeCore</c> is a private instance method on a live
    /// MonoBehaviour and its teardown touches <c>FlightGlobals</c> /
    /// <c>PhysicsFramePatch</c>; it cannot be constructed headless. The cells below
    /// drive the production reap entry point with the reason string each gameplay
    /// discard path actually passes, and the source gates pin that each of those paths
    /// reaches it. That split mirrors the existing convention in
    /// <c>SceneExitInterceptorTests</c>.</para>
    /// </summary>
    [Collection("Sequential")]
    public class DiscardSidecarReapTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> cleanupRoots = new List<string>();

        public DiscardSidecarReapTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            DiscardSidecarReap.ResetForTesting();
        }

        public void Dispose()
        {
            DiscardSidecarReap.ResetForTesting();
            for (int i = 0; i < cleanupRoots.Count; i++)
            {
                try
                {
                    if (Directory.Exists(cleanupRoots[i]))
                        Directory.Delete(cleanupRoots[i], true);
                }
                catch { }
            }
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- fixture: a real recordings directory with real staged sidecars -----

        /// <summary>
        /// The seven sidecar suffixes <c>RecordingStore.DeleteRecordingFiles</c> unlinks
        /// under <c>Parsek/Recordings/</c>. Its EIGHTH limb - the per-recording
        /// quickload-resume save at <c>Parsek/Saves/&lt;RewindSaveFileName&gt;.sfs</c> -
        /// lives in a different directory and is staged separately (see
        /// <see cref="StageRewindSave"/>). NOT the Rewind-to-Separation quicksaves under
        /// <c>Parsek/RewindPoints/</c>: those use a separate path builder
        /// (<c>BuildRewindPointRelativePath</c>) and their own reaper, and this reap
        /// never touches them.
        /// </summary>
        private static readonly string[] StagedSuffixes =
        {
            ".prec", ".pann", "_vessel.craft", "_ghost.craft",
            ".prec.txt", "_vessel.craft.txt", "_ghost.craft.txt",
        };

        /// <summary>Save root of the most recent <see cref="CreateRecordingsDir"/>.</summary>
        private string lastSaveRoot;

        private string CreateRecordingsDir(string label)
        {
            string root = Path.Combine(
                Path.GetTempPath(), "parsek-discard-reap-" + label + "-" + Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(root, "Parsek", "Recordings");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(root, "Parsek", "Saves"));
            Directory.CreateDirectory(Path.Combine(root, "Parsek", "RewindPoints"));
            cleanupRoots.Add(root);
            lastSaveRoot = root;
            return dir;
        }

        private static void StageSidecars(string dir, string recordingId)
        {
            for (int i = 0; i < StagedSuffixes.Length; i++)
                File.WriteAllText(Path.Combine(dir, recordingId + StagedSuffixes[i]), "x");
        }

        /// <summary>Stages the per-recording quickload-resume save limb.</summary>
        private void StageRewindSave(string rewindSaveFileName)
            => File.WriteAllText(RewindSavePath(rewindSaveFileName), "sfs");

        private string RewindSavePath(string rewindSaveFileName)
            => Path.Combine(lastSaveRoot, "Parsek", "Saves", rewindSaveFileName + ".sfs");

        /// <summary>A Rewind-to-Separation quicksave, which the reap must NEVER touch.</summary>
        private string StageRewindPointQuicksave(string rewindPointId)
        {
            string p = Path.Combine(lastSaveRoot, "Parsek", "RewindPoints", rewindPointId + ".sfs");
            File.WriteAllText(p, "rp");
            return p;
        }

        private static int CountSidecars(string dir, string recordingId)
        {
            int n = 0;
            for (int i = 0; i < StagedSuffixes.Length; i++)
            {
                if (File.Exists(Path.Combine(dir, recordingId + StagedSuffixes[i])))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// Stands in for <c>RecordingStore.DeleteRecordingFiles</c>, which resolves
        /// paths through <c>KSPUtil.ApplicationRootPath</c> and throws outside KSP.
        /// Deletes exactly the recording's footprint from the staged save - the seven
        /// sidecars AND the quickload-resume save when the recording carries one - so
        /// these cells observe real filesystem effects, not a mock's call log. Returns
        /// the production contract: true when nothing was left behind.
        /// </summary>
        private Func<Recording, bool> StagedDirDeleter(string dir)
        {
            string saveRoot = lastSaveRoot;
            return rec =>
            {
                bool allDeleted = true;
                for (int i = 0; i < StagedSuffixes.Length; i++)
                {
                    string p = Path.Combine(dir, rec.RecordingId + StagedSuffixes[i]);
                    if (File.Exists(p))
                        File.Delete(p);
                    allDeleted &= !File.Exists(p);
                }
                if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
                {
                    string sfs = Path.Combine(
                        saveRoot, "Parsek", "Saves", rec.RewindSaveFileName + ".sfs");
                    if (File.Exists(sfs))
                        File.Delete(sfs);
                    allDeleted &= !File.Exists(sfs);
                }
                return allDeleted;
            };
        }

        private static Recording Rec(string id)
            => new Recording { RecordingId = id, VesselName = "V-" + id };

        // ================= per-discard-path repro cells =================
        //
        // Each cell stages the quickload-resume sidecars for a live active tree, drives
        // the production reap DIRECTLY with the exact `reason` literal that gameplay
        // path passes into AutoDiscardActiveTreeCore, and asserts the files are gone.
        //
        // SCOPE, honestly stated: the reason string is the only thing these cells share
        // with the real call site - they do not drive ParsekFlight (see the class
        // remarks for why it cannot be constructed headless), so they prove the reap
        // does the right thing under each path's inputs, NOT that each path reaches it.
        // The latter rests on the source gates at the bottom of this file, which pin
        // that the single shared core every one of these paths funnels through captures
        // before the teardown, reaps after it, and that no caller opts out.

        [Theory]
        // Scene-exit no-op switch-segment auto-discard
        // (SceneExitInterceptor.TryAutoDiscardNoOpSwitchSegment ->
        //  ParsekFlight.AutoDiscardNoOpStandaloneSwitchSegment).
        [InlineData("scene-exit no-op switch-segment auto-discard dest=SPACECENTER")]
        // Pre-switch Discard, Case A (MapFocusObjectOnSelectPatch.DiscardPriorAndSwitchTo ->
        //  ParsekFlight.DiscardActiveSwitchSegmentAttemptRevertingLiveClone).
        [InlineData("pre-switch-dialog discard")]
        // Pre-switch Discard, Case B / C
        // (MapFocusObjectOnSelectPatch.DiscardActiveRecordingAndSwitchTo).
        [InlineData("pre-switch-dialog discard (no-session)")]
        // Idle-on-pad scene-exit fast path (SceneExitInterceptor.TryAutoDiscardIdleActiveTree
        //  -> ParsekFlight.AutoDiscardIdleActiveTree).
        [InlineData("scene-exit idle-on-pad auto-discard dest=SPACECENTER")]
        // No-session committed-resume revert
        // (ParsekFlight.AutoDiscardNoOpNoSessionCommittedResume).
        [InlineData("scene-exit no-op no-session committed-resume revert dest=TRACKSTATION")]
        public void GameplayDiscardPath_ReapsQuickloadResumeSidecars_WhenStoreEmptiesToZero(
            string discardReason)
        {
            string dir = CreateRecordingsDir("path");
            StageSidecars(dir, "rec-live-a");
            StageSidecars(dir, "rec-live-b");
            Assert.Equal(StagedSuffixes.Length, CountSidecars(dir, "rec-live-a"));

            DiscardSidecarReap.DeleteRecordingFilesForTesting = StagedDirDeleter(dir);

            // The discard-to-empty case: RecordingStore was reset, so the REAL
            // BuildKnownRecordingIds() returns zero ids - exactly the shape where the
            // sweeper's zero-known guard refuses to clean up.
            var captured = DiscardSidecarReap.CaptureTreeRecordings(
                TreeWith("tree-live", Rec("rec-live-a"), Rec("rec-live-b")));

            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                captured, skipReason: null, context: discardReason);

            Assert.Null(outcome.Skipped);
            Assert.Equal(0, outcome.KnownAfterDiscard);
            Assert.Equal(2, outcome.TreeRecordings);
            Assert.Equal(2, outcome.Reaped);
            Assert.Equal(0, outcome.Failed);
            Assert.Equal(0, CountSidecars(dir, "rec-live-a"));
            Assert.Equal(0, CountSidecars(dir, "rec-live-b"));
            Assert.Contains(logLines,
                l => l.Contains("[DiscardReap]")
                     && l.Contains("discard sidecar-reap:")
                     && l.Contains("context='" + discardReason + "'")
                     && l.Contains("treeRecordings=2")
                     && l.Contains("knownAfterDiscard=0")
                     && l.Contains("reaped=2")
                     && l.Contains("failed=0"));
        }

        private static RecordingTree TreeWith(string treeId, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = treeId };
            for (int i = 0; i < recs.Length; i++)
                tree.Recordings[recs[i].RecordingId] = recs[i];
            return tree;
        }

        // ================= the known-ids guard (semantics preserved exactly) =========

        [Fact]
        public void Reap_PreservesIdsTheStoreStillOwns_CommittedRestoreCloneCase()
        {
            // The committed-restore clone shares its committed original's recording id.
            // The original's files are live mission data: the guard must preserve them
            // while still reaping the clone-only ids.
            string dir = CreateRecordingsDir("preserve");
            StageSidecars(dir, "rec-committed");
            StageSidecars(dir, "rec-clone-only");

            RecordingStore.AddCommittedInternal(Rec("rec-committed"));
            DiscardSidecarReap.DeleteRecordingFilesForTesting = StagedDirDeleter(dir);

            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                new List<Recording> { Rec("rec-committed"), Rec("rec-clone-only") },
                skipReason: null,
                context: "pre-switch-dialog discard");

            Assert.Equal(1, outcome.Reaped);
            Assert.Equal(1, outcome.PreservedKnown);
            Assert.Equal(StagedSuffixes.Length, CountSidecars(dir, "rec-committed"));
            Assert.Equal(0, CountSidecars(dir, "rec-clone-only"));
        }

        [Fact]
        public void Reap_PreservesIdsOwnedOnlyByACommittedTree()
        {
            // The cell above seeds the flat CommittedRecordings list; the REAL
            // committed-restore-clone shape keeps the original inside a committed TREE.
            // BuildKnownRecordingIds unions three surfaces, and if its committedTrees
            // loop were ever dropped, the flat-list cell above would stay green while
            // the very path it is named for started hard-deleting the original's files.
            // This variant seeds ONLY the tree surface, so that change reds here.
            string dir = CreateRecordingsDir("preserve-tree");
            StageSidecars(dir, "rec-in-committed-tree");
            StageSidecars(dir, "rec-clone-only");

            var committedTree = TreeWith("tree-committed", Rec("rec-in-committed-tree"));
            RecordingStore.AddCommittedTreeInternal(committedTree);
            Assert.Empty(RecordingStore.CommittedRecordings);   // tree surface only
            Assert.Contains("rec-in-committed-tree", RecordingStore.BuildKnownRecordingIds());

            DiscardSidecarReap.DeleteRecordingFilesForTesting = StagedDirDeleter(dir);

            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                new List<Recording> { Rec("rec-in-committed-tree"), Rec("rec-clone-only") },
                skipReason: null,
                context: "scene-exit no-op switch-segment auto-discard dest=SPACECENTER");

            Assert.Equal(1, outcome.Reaped);
            Assert.Equal(1, outcome.PreservedKnown);
            Assert.Equal(StagedSuffixes.Length, CountSidecars(dir, "rec-in-committed-tree"));
            Assert.Equal(0, CountSidecars(dir, "rec-clone-only"));
        }

        [Fact]
        public void SelectReapRecordings_FailsClosed_OnNullKnownSet()
        {
            // Null known set = "we could not establish what the store owns". A deletion
            // guard must delete nothing there, not everything.
            Assert.Empty(DiscardSidecarReap.SelectReapRecordings(
                new List<Recording> { Rec("rec-a") }, null));
            Assert.Empty(DiscardSidecarReap.SelectReapRecordings(null, new HashSet<string>()));
        }

        [Fact]
        public void SelectReapRecordings_SkipsNullAndIdlessEntries()
        {
            var reap = DiscardSidecarReap.SelectReapRecordings(
                new List<Recording> { null, new Recording { RecordingId = null }, Rec("rec-a") },
                new HashSet<string>(StringComparer.Ordinal));
            Assert.Single(reap);
            Assert.Equal("rec-a", reap[0].RecordingId);
        }

        [Fact]
        public void Reap_RefusesInvalidRecordingIds()
        {
            // RecordingPaths.ValidateRecordingId semantics: a traversal-shaped id never
            // reaches the filesystem, even though it is absent from the known set.
            //
            // BOTH ids are traversal-shaped ON PURPOSE. An invalid-FILENAME-CHAR id
            // ("bad|id") is NOT portable: Path.GetInvalidFileNameChars() is only
            // {'\0','/'} under Mono/Linux, so such an id VALIDATES on the required
            // ubuntu CI run and this cell would red there. Same precedent as
            // InGameTestSidecarReaperTests, which also uses two traversal ids; see
            // PreParsekBackupTests / RecordingPathsLoggingTests for the runtime-probe
            // variants of the same trap.
            string dir = CreateRecordingsDir("invalid");
            int deleteCalls = 0;
            DiscardSidecarReap.DeleteRecordingFilesForTesting = _ => { deleteCalls++; return true; };

            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                new List<Recording> { Rec("../escape"), Rec("..\\escape2") },
                skipReason: null,
                context: "scene-exit idle-on-pad auto-discard dest=SPACECENTER");

            Assert.Equal(0, deleteCalls);
            Assert.Equal(0, outcome.Reaped);
            Assert.Equal(2, outcome.InvalidId);
            Assert.True(Directory.Exists(dir));
        }

        // ================= the load-shape skip gate (mandatory) =====================

        [Theory]
        [InlineData(true, false, false, "refly-marker-active")]
        [InlineData(false, true, false, "merge-journal-active")]
        [InlineData(false, false, true, "restoring-active-tree")]
        // Precedence is fixed: Re-Fly wins over journal wins over restore.
        [InlineData(true, true, true, "refly-marker-active")]
        [InlineData(false, true, true, "merge-journal-active")]
        public void SkipReason_NamesTheLoadShapeThatForbidsReaping(
            bool reFly, bool journal, bool restoring, string expected)
        {
            Assert.Equal(expected, DiscardSidecarReap.SkipReason(reFly, journal, restoring));
        }

        [Fact]
        public void SkipReason_NullWhenNoProtectedLoadShapeIsActive()
        {
            Assert.Null(DiscardSidecarReap.SkipReason(false, false, false));
        }

        [Fact]
        public void Reap_DeletesNothing_WhenTheLoadShapeGateIsSet()
        {
            // In a Re-Fly / merge-journal / restore shape the restored active tree holds
            // the ONLY copy of the original mission's recordings, so their absence from
            // the known set is expected and reaping would destroy committed data.
            string dir = CreateRecordingsDir("skip");
            StageSidecars(dir, "rec-restored");
            DiscardSidecarReap.DeleteRecordingFilesForTesting = StagedDirDeleter(dir);

            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                new List<Recording> { Rec("rec-restored") },
                skipReason: "refly-marker-active",
                context: "pre-switch-dialog discard (no-session)");

            Assert.Equal("refly-marker-active", outcome.Skipped);
            Assert.Equal(0, outcome.Reaped);
            Assert.Equal(StagedSuffixes.Length, CountSidecars(dir, "rec-restored"));
            Assert.Contains(logLines,
                l => l.Contains("[DiscardReap]")
                     && l.Contains("skipped")
                     && l.Contains("reason=refly-marker-active"));
        }

        // ================= failure safety (degrade to status quo) ===================

        [Fact]
        public void Reap_LockedFile_CountsAsFailed_AndTheBatchContinues()
        {
            // This is the PRODUCTION degrade path. RecordingStore.DeleteRecordingFiles
            // swallows each per-file exception (logging its own Warn) and reports the
            // outcome through its RETURN VALUE: false means at least one file is still
            // on disk. The reap must count that as failed rather than assume success,
            // or `failed=` in the batch summary would be a lie.
            string dir = CreateRecordingsDir("locked");
            StageSidecars(dir, "rec-locked");
            StageSidecars(dir, "rec-free");

            var realDelete = StagedDirDeleter(dir);
            DiscardSidecarReap.DeleteRecordingFilesForTesting = rec =>
                rec.RecordingId == "rec-locked"
                    ? false            // swallowed-lock: files left behind
                    : realDelete(rec);

            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                new List<Recording> { Rec("rec-locked"), Rec("rec-free") },
                skipReason: null,
                context: "scene-exit no-op switch-segment auto-discard dest=SPACECENTER");

            Assert.Equal(1, outcome.Reaped);
            Assert.Equal(1, outcome.Failed);
            // The later recording still got reaped: one failure does not stop the batch.
            Assert.Equal(0, CountSidecars(dir, "rec-free"));
            Assert.Equal(StagedSuffixes.Length, CountSidecars(dir, "rec-locked"));
            Assert.Contains(logLines,
                l => l.Contains("[DiscardReap]")
                     && l.Contains("discard sidecar-reap failed")
                     && l.Contains("id=rec-locked")
                     && l.Contains("reason=file-not-unlinked")
                     && l.Contains("orphan retained, discard continues"));
            Assert.Contains(logLines,
                l => l.Contains("discard sidecar-reap:")
                     && l.Contains("reaped=1")
                     && l.Contains("failed=1"));
        }

        [Fact]
        public void DeleteRecordingFiles_ReportsFailure_SoTheReapCanCountTruthfully()
        {
            // Pins the signal the cell above consumes. Without a save context the
            // production delete resolves no paths and unlinks nothing - that is
            // "nothing left behind", so it must report success; a null / invalid-id
            // recording is unusable and must report failure.
            Assert.False(RecordingStore.DeleteRecordingFiles(null));
            Assert.False(RecordingStore.DeleteRecordingFiles(new Recording { RecordingId = "../nope" }));
            Assert.True(RecordingStore.DeleteRecordingFiles(Rec("rec-no-save-context")));
        }

        [Fact]
        public void Reap_DeleteThrows_IsStillContainedAndCounted()
        {
            // Defence in depth. The production delete does not throw today (it swallows
            // per-file), but the reap must contain one if that ever changes - two of the
            // callers run inside Harmony prefixes where an escape aborts a scene
            // transition or a menu click.
            string dir = CreateRecordingsDir("throw");
            StageSidecars(dir, "rec-throws");
            DiscardSidecarReap.DeleteRecordingFilesForTesting =
                _ => throw new IOException("The process cannot access the file");

            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                new List<Recording> { Rec("rec-throws") },
                skipReason: null,
                context: "pre-switch-dialog discard");

            Assert.Equal(0, outcome.Reaped);
            Assert.Equal(1, outcome.Failed);
            Assert.Equal(StagedSuffixes.Length, CountSidecars(dir, "rec-throws"));
            Assert.Contains(logLines,
                l => l.Contains("discard sidecar-reap failed") && l.Contains("IOException"));
        }

        [Fact]
        public void Reap_AlsoUnlinksTheQuickloadResumeSave_ButNeverARewindPointQuicksave()
        {
            // DeleteRecordingFiles' eighth limb: the per-recording quickload-resume save
            // at Parsek/Saves/<RewindSaveFileName>.sfs. The Rewind-to-Separation
            // quicksaves under Parsek/RewindPoints/ use a different path builder and
            // belong to RewindPointReaper - this reap must leave them strictly alone.
            string dir = CreateRecordingsDir("rewindsave");
            StageSidecars(dir, "rec-with-save");
            StageRewindSave("resume-rec-with-save");
            string rpQuicksave = StageRewindPointQuicksave("rp-untouched");

            DiscardSidecarReap.DeleteRecordingFilesForTesting = StagedDirDeleter(dir);

            var rec = Rec("rec-with-save");
            rec.RewindSaveFileName = "resume-rec-with-save";
            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                new List<Recording> { rec },
                skipReason: null,
                context: "pre-switch-dialog discard (no-session)");

            Assert.Equal(1, outcome.Reaped);
            Assert.Equal(0, CountSidecars(dir, "rec-with-save"));
            Assert.False(File.Exists(RewindSavePath("resume-rec-with-save")));
            Assert.True(File.Exists(rpQuicksave));
        }

        [Fact]
        public void Reap_NullCapture_IsAQuietNoOp()
        {
            var outcome = DiscardSidecarReap.ReapDiscardedTreeSidecars(
                null, skipReason: null, context: "idle-on-pad auto-discard");
            Assert.Equal(0, outcome.TreeRecordings);
            Assert.Equal(0, outcome.Reaped);
        }

        [Fact]
        public void CaptureTreeRecordings_SnapshotsBeforeTeardown_AndSurvivesTheTreeBeingDropped()
        {
            // The capture must be a snapshot, not a live view: the teardown nulls
            // activeTree between the capture and the reap.
            var tree = TreeWith("tree-x", Rec("rec-1"), Rec("rec-2"));
            var captured = DiscardSidecarReap.CaptureTreeRecordings(tree);
            tree.Recordings.Clear();
            Assert.Equal(2, captured.Count);
            Assert.Empty(DiscardSidecarReap.CaptureTreeRecordings(null));
        }

        // ================= the reap is explicit-id, never a sweep ===================

        [Fact]
        public void Reap_NeverEnumeratesTheRecordingsDirectory()
        {
            // Hard contract: the fix lives at the discard sites and deletes only ids
            // that were IN the discarded set. If this class ever grows a directory
            // walk it becomes a second, unguarded sweeper next to CleanOrphanFiles.
            string source = ReadProjectSource("DiscardSidecarReap.cs");
            Assert.DoesNotContain("Directory.GetFiles", source);
            Assert.DoesNotContain("Directory.EnumerateFiles", source);
            Assert.DoesNotContain("CollectSidecarIdsOnDisk", source);
            Assert.DoesNotContain("ResolveRecordingsDirectoryForCurrentSave", source);
        }

        [Fact]
        public void SweeperZeroKnownGuard_StaysUntouchedByThisFix()
        {
            // Pin: the sweeper's zero-known safety guard is deliberately NOT relaxed.
            // (Its behavior is asserted end-to-end by
            // OrphanCleanupSafetyGuardTests.CleanOrphanFiles_RefusesDeletion_*; this
            // cell pins that the guard text still exists after the discard-site fix.)
            string source = ReadProjectSource("RecordingStore.OrphanCleanup.cs");
            Assert.Matches(new Regex(
                @"knownIds\.Count\s*==\s*0", RegexOptions.Multiline), source);
        }

        [Fact]
        public void PendingAndScopedDiscardPaths_KeepOwningTheirOwnSidecarDeletes()
        {
            // Inventory pin: the whole-pending-tree discard and the scoped
            // switch-segment discard already delete sidecars at the store level, so
            // this fix deliberately adds nothing there. If either loses its delete the
            // gap reopens somewhere this fix does not cover.
            string source = ReadProjectSource("RecordingStore.cs");
            int discardPending = source.IndexOf("public static void DiscardPendingTree(",
                StringComparison.Ordinal);
            Assert.True(discardPending > 0, "DiscardPendingTree not found");
            Assert.Contains("DeleteRecordingFiles(rec)",
                source.Substring(discardPending, Math.Min(12000, source.Length - discardPending)));

            int scoped = source.IndexOf(
                "internal static SwitchSegmentDiscardDisposition TryDiscardActiveSwitchSegmentAttempt(",
                StringComparison.Ordinal);
            Assert.True(scoped > 0, "TryDiscardActiveSwitchSegmentAttempt not found");
            Assert.Contains("DeleteRecordingFiles(rec)",
                source.Substring(scoped, Math.Min(12000, source.Length - scoped)));
        }

        // ================= source gates: the gameplay paths reach the reap ==========
        //
        // AutoDiscardActiveTreeCore is a private instance method on a live MonoBehaviour
        // whose teardown touches FlightGlobals / PhysicsFramePatch; it cannot be driven
        // headless. Same convention as SceneExitInterceptorTests' gates on this method.

        private static string ReadProjectSource(params string[] relativeParts)
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            var parts = new List<string> { projectRoot, "Source", "Parsek" };
            parts.AddRange(relativeParts);
            string path = Path.Combine(parts.ToArray());
            Assert.True(File.Exists(path), $"source not found at {path}");
            return File.ReadAllText(path);
        }

        private static string ReadAutoDiscardCoreBody()
        {
            string source = ReadProjectSource("ParsekFlight.cs");
            int coreStart = source.IndexOf("private void AutoDiscardActiveTreeCore(",
                StringComparison.Ordinal);
            Assert.True(coreStart > 0, "AutoDiscardActiveTreeCore not found");
            int coreEnd = source.IndexOf("\n        private ", coreStart + 1, StringComparison.Ordinal);
            if (coreEnd < 0)
                coreEnd = Math.Min(coreStart + 8000, source.Length);
            return source.Substring(coreStart, coreEnd - coreStart);
        }

        // Fails if: the shared discard core stops capturing the tree's recordings BEFORE
        // the teardown nulls activeTree. A capture taken after the null reaps nothing and
        // the leak silently returns.
        [Fact]
        public void AutoDiscardActiveTreeCore_CapturesRecordingsBeforeNullingActiveTree()
        {
            string body = ReadAutoDiscardCoreBody();
            int capture = body.IndexOf("DiscardSidecarReap.CaptureTreeRecordings(activeTree)",
                StringComparison.Ordinal);
            int nulling = body.IndexOf("activeTree = null;", StringComparison.Ordinal);
            Assert.True(capture > 0, "reap capture not found in AutoDiscardActiveTreeCore");
            Assert.True(nulling > 0, "activeTree teardown not found in AutoDiscardActiveTreeCore");
            Assert.True(capture < nulling,
                "the reap capture must precede the activeTree teardown");
        }

        // Fails if: the core stops reaping, or reaps BEFORE the teardown (which would
        // read a known-id set that still owns the ids and preserve everything).
        [Fact]
        public void AutoDiscardActiveTreeCore_ReapsAfterTheTeardown()
        {
            string body = ReadAutoDiscardCoreBody();
            int nulling = body.IndexOf("activeTree = null;", StringComparison.Ordinal);
            int reap = body.IndexOf("DiscardSidecarReap.ReapDiscardedTreeSidecars(",
                StringComparison.Ordinal);
            Assert.True(reap > 0, "reap call not found in AutoDiscardActiveTreeCore");
            Assert.True(nulling < reap, "the reap must run after the activeTree teardown");
        }

        // Fails if: the core stops sampling the mandatory load-shape gate. Without it a
        // gameplay discard during a Re-Fly / merge-journal / restore shape would delete
        // the only copy of the original mission's committed recordings.
        [Fact]
        public void AutoDiscardActiveTreeCore_SamplesTheLoadShapeSkipGate()
        {
            string body = ReadAutoDiscardCoreBody();
            Assert.Matches(new Regex(
                @"DiscardSidecarReap\.SkipReason\([\s\S]{0,600}?ActiveReFlySessionMarker[\s\S]{0,600}?" +
                @"ActiveMergeJournal[\s\S]{0,600}?restoringActiveTree",
                RegexOptions.Multiline),
                body);
        }

        // Fails if: the reap escapes the discard. A throw out of the core would abort a
        // scene transition or a map click (both callers run inside Harmony prefixes).
        [Fact]
        public void AutoDiscardActiveTreeCore_WrapsTheReapSoItCannotAbortTheDiscard()
        {
            string body = ReadAutoDiscardCoreBody();
            Assert.Matches(new Regex(
                @"try\s*\{\s*DiscardSidecarReap\.ReapDiscardedTreeSidecars\([\s\S]{0,300}?\}\s*catch",
                RegexOptions.Multiline),
                body);
        }

        // Fails if: the PRE-teardown half stops being wrapped. Same Harmony-prefix
        // exposure as the reap itself (ParsekFlight's own precedent for this is
        // TryEvaluateActiveSwitchSegmentNoOp), and this half must additionally fail
        // CLOSED - a capture that threw must reap nothing rather than reap blind.
        [Fact]
        public void AutoDiscardActiveTreeCore_WrapsTheCaptureAndFailsClosed()
        {
            string body = ReadAutoDiscardCoreBody();
            Assert.Matches(new Regex(
                @"try\s*\{[\s\S]{0,900}?DiscardSidecarReap\.CaptureTreeRecordings\(activeTree\)" +
                @"[\s\S]{0,900}?\}\s*catch[\s\S]{0,400}?reapCapturedRecordings\s*=\s*null;",
                RegexOptions.Multiline),
                body);
        }

        // Fails if: any caller ANYWHERE in the mod opts out of the reap other than the
        // M-A2 DiscardTree test-command verb (which owns its own reap, and whose summary
        // line the S0.5 harness spec pins verbatim).
        //
        // The scan is repo-wide ON PURPOSE. AutoDiscardActiveTreeWithMessage is
        // `internal` and Patches/MapFocusObjectOnSelectPatch.cs already calls it, so a
        // future caller there - or anywhere else under Source/Parsek - passing false
        // would reintroduce the leak with every other gate in this file still green.
        [Fact]
        public void OnlyTheTestCommandVerbOptsOutOfTheReap()
        {
            var optOutFiles = new List<string>();
            foreach (string path in EnumerateModSources())
            {
                if (Regex.IsMatch(File.ReadAllText(path), @"reapSidecars:\s*false"))
                    optOutFiles.Add(Path.GetFileName(path));
            }
            Assert.Equal(new[] { "ParsekTestCommandAddon.cs" }, optOutFiles.ToArray());

            string flight = ReadProjectSource("ParsekFlight.cs");
            // Exactly two ParsekFlight entry points arm it explicitly
            // (AutoDiscardIdleActiveTree, AutoDiscardNoOpStandaloneSwitchSegment); the
            // third (AutoDiscardActiveTreeWithMessage) forwards its own parameter, which
            // DEFAULTS to true so every existing and future caller stays covered.
            //
            // INTENT of the exact count: a third correct `reapSidecars: true` call site
            // reds this cell BY DESIGN. That is the point - a new entry point into the
            // shared discard core is exactly the change that needs a human to confirm it
            // belongs in the reaping set and to add it to the inventory table in
            // docs/dev/todo-and-known-bugs.md. Bump the number deliberately, never
            // reflexively.
            Assert.Equal(2, Regex.Matches(flight, @"reapSidecars:\s*true").Count);
            Assert.Contains("bool reapSidecars = true", flight);
            Assert.Contains("reapSidecars: reapSidecars", flight);
        }

        private static IEnumerable<string> EnumerateModSources()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string modRoot = Path.Combine(projectRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(modRoot), $"mod source root not found at {modRoot}");
            return Directory.GetFiles(modRoot, "*.cs", SearchOption.AllDirectories);
        }

        // Fails if: the shared pure deciders diverge again. TestCommandRecordingVerbs'
        // two S0.5 helpers must stay thin aliases over DiscardSidecarReap so the
        // harness path and the gameplay paths cannot drift apart.
        [Fact]
        public void TestCommandVerbs_DelegateToTheSharedDeciders()
        {
            string source = ReadProjectSource("TestCommands", "TestCommandRecordingVerbs.cs");
            Assert.Contains("DiscardSidecarReap.SelectReapRecordings(", source);
            Assert.Contains("DiscardSidecarReap.SkipReason(", source);
        }
    }
}
