using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Quickload discard path: F5 → fly → F9 must discard any pending recording
    /// that was stashed during the F9 scene transition, because those points
    /// were recorded in the discarded "future" after the quicksave and do not
    /// belong in the timeline. Companion to <see cref="QuickloadResumeTests"/>,
    /// which covers the tree-mode resume-across-F9 path.
    ///
    /// Regression target (2026-04-09 playtest, bug #276): F5 → EVA 24 s → F9
    /// committed the EVA walk as an orphan recording. Revert detection in
    /// <see cref="ParsekScenario"/> OnLoad used two signals: saved epoch/count
    /// regression (which doesn't fire when F5 happens post-merge — both sides
    /// are equal), and a <c>vesselSwitchPending</c> frame-count staleness cap
    /// (which didn't trip because ~12 fps at loaded KSC advances only ~288
    /// frames in 24 s, slipping under the old 300-frame cap). Neither signal
    /// contradicted the stale vessel-switch flag and the stashed pending was
    /// then committed on the next OnSave.
    ///
    /// Fix: add an orthogonal UT-backwards signal. Stamp the pre-transition
    /// <c>Planetarium.GetUniversalTime()</c> in <c>ParsekFlight.OnSceneChangeRequested</c>,
    /// compare against the loaded UT in <c>OnLoad</c>. If UT regressed, discard
    /// <c>HasPending</c> / <c>HasPendingTree</c> (non-Limbo) that were stashed
    /// this transition. Limbo pending trees are the resume-across-F9 carrier
    /// and are preserved. Also tightens <c>VesselSwitchPendingMaxAgeFrames</c>
    /// from 300 to 60 as defense in depth.
    ///
    /// Tests split in two groups:
    ///   (1) <c>IsQuickloadOnLoad</c> — pure helper, no Unity.
    ///   (2) <c>DiscardStashedOnQuickload</c> — exercises
    ///       <see cref="RecordingStore"/> static state + log-sink assertions.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadDiscardTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public QuickloadDiscardTests()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RewindInvokeContext.ResetForTesting();
            TreeDiscardPurge.ResetTestOverrides();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RewindInvokeContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            TreeDiscardPurge.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateRecorder.PendingScienceSubjects.Clear();
        }

        // ----- IsQuickloadOnLoad pure helper -----

        [Fact]
        public void IsQuickloadOnLoad_Unset_ReturnsFalse()
        {
            // preChangeUT == -1.0 means no scene change stamped (e.g. first load).
            // Must NOT report as a quickload — loadedUT could be anything.
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: -1.0, currentUT: 0.0, epsilon: 0.1));
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: -1.0, currentUT: 500.0, epsilon: 0.1));
        }

        [Fact]
        public void IsQuickloadOnLoad_PreChangeUtZero_AllowsBackwardsDetection()
        {
            // Pin: preChangeUT == 0.0 is a LEGITIMATE value for fresh sandbox
            // saves that start at UT 0. The unset sentinel is -1.0, so the
            // helper uses `< 0.0` (strict) rather than `<= 0.0`. A scene
            // change at UT 0 followed by a quickload to UT 0 is not a
            // backwards jump (currentUT == preChangeUT, no regression),
            // and a forward UT advance correctly returns false.
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 0.0, currentUT: 0.0, epsilon: 0.1));
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 0.0, currentUT: 5.0, epsilon: 0.1));
            // Belt-and-braces: a scene change happens at UT 0 and somehow
            // OnLoad reads a *negative* UT — also flagged as quickload, since
            // the time strictly went backwards (this should never happen but
            // pinning the strict-less-than semantics protects future edits).
            Assert.True(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 0.0, currentUT: -1.0, epsilon: 0.1));
        }

        [Fact]
        public void IsQuickloadOnLoad_UtUnchanged_ReturnsFalse()
        {
            // Normal FLIGHT→FLIGHT scene change without time manipulation:
            // OnSceneChangeRequested and OnLoad see the same UT.
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 500.0, currentUT: 500.0, epsilon: 0.1));
        }

        [Fact]
        public void IsQuickloadOnLoad_UtWentForward_ReturnsFalse()
        {
            // Time advanced during scene load (e.g. long scene load takes
            // fractions of a second). Forward motion is never a quickload.
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 500.0, currentUT: 500.5, epsilon: 0.1));
        }

        [Fact]
        public void IsQuickloadOnLoad_UtWentBackwards_ReturnsTrue()
        {
            // Clear backwards jump — quickload / revert / rewind.
            Assert.True(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 500.0, currentUT: 480.0, epsilon: 0.1));
        }

        [Fact]
        public void IsQuickloadOnLoad_SubEpsilonNoise_ReturnsFalse()
        {
            // 0.05 s regression is smaller than the physics tick (~0.02 s) ×
            // safety factor. This can happen from rounding and must NOT trip.
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 500.0, currentUT: 499.95, epsilon: 0.1));
        }

        [Fact]
        public void IsQuickloadOnLoad_ExactEpsilonBoundary_ReturnsFalse()
        {
            // currentUT == preChangeUT - epsilon is the boundary.
            // The helper uses strict less-than (`<`), so this is NOT a quickload.
            // A future off-by-one here would flip the boundary silently.
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 500.0, currentUT: 499.9, epsilon: 0.1));
        }

        [Fact]
        public void IsQuickloadOnLoad_PlaytestSiford_ReturnsTrue()
        {
            // The real numbers from KSP.log:13107 → KSP.log:13194 in the
            // 2026-04-09 playtest: OnSceneChangeRequested captured UT=397.2,
            // OnLoad read UT=369.2 (28-second F9 quickload). This test is a
            // concrete named regression guard — if it flips, the specific
            // playtest bug is back.
            Assert.True(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 397.2, currentUT: 369.2, epsilon: 0.1));
        }

        [Fact]
        public void IsQuickloadOnLoad_NegativeEpsilon_ReturnsFalse()
        {
            // Defensive: caller passes a negative epsilon by mistake. Don't
            // treat any regression as a quickload — refuse and return false.
            Assert.False(ParsekScenario.IsQuickloadOnLoad(
                preChangeUT: 500.0, currentUT: 480.0, epsilon: -0.1));
        }

        // ----- DiscardStashedOnQuickload log + state assertions -----

        [Fact]
        public void DiscardStashedOnQuickload_WithPendingTree_DiscardsAndLogs()
        {
            // Seed a pending tree stashed "this transition"
            var tree = MakeTree("t_sa", "Siford Kerman", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.True(RecordingStore.PendingStashedThisTransition);
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 397.2, currentUT: 369.2);

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("Quickload detected")
                && l.Contains("397.20") && l.Contains("369.20"));
            Assert.Contains(logLines, l =>
                l.Contains("discarded pending tree") && l.Contains("Siford Kerman"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload discard complete") && l.Contains("tree=1"));
        }

        [Fact]
        public void DiscardStashedOnQuickload_WithPendingTreeFinalized_Discards()
        {
            // Non-Limbo pending tree stashed this transition: discard.
            var tree = MakeTree("t_a", "Kerbal X", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.True(RecordingStore.PendingStashedThisTransition);
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 400.0, currentUT: 370.0);

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(logLines, l =>
                l.Contains("discarded pending tree") && l.Contains("Kerbal X")
                && l.Contains("state != Limbo"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload discard complete") && l.Contains("tree=1"));
        }

        [Fact]
        public void DiscardStashedOnQuickload_WithActiveReFlySession_PreservesPendingTreeAndRewindPoint()
        {
            // Defense-in-depth for Re-Fly retry: an active session owns the
            // pending tree and RP quicksave, so even a direct call must refuse
            // before RecordingStore.DiscardPendingTree can invoke PurgeTree.
            var tree = MakeTree("t_refly", "Kerbal X", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_active_refly",
                    TreeId = tree.Id,
                    ActiveReFlyRecordingId = tree.RootRecordingId,
                    OriginChildRecordingId = tree.RootRecordingId,
                    RewindPointId = "rp_keep_refly",
                },
                RewindPoints = new List<RewindPoint>
                {
                    new RewindPoint
                    {
                        RewindPointId = "rp_keep_refly",
                        QuicksaveFilename = "rp_keep_refly.sfs",
                    },
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            TreeDiscardPurge.ResetCallCountForTesting();
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 400.0, currentUT: 370.0);

            Assert.True(RecordingStore.HasPendingTree);
            Assert.True(RecordingStore.PendingStashedThisTransition);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal(0, TreeDiscardPurge.PurgeTreeCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("refusing to run with active re-fly session")
                && l.Contains("sess_active_refly") && l.Contains("rp_keep_refly"));
            Assert.DoesNotContain(logLines, l => l.Contains("DiscardPendingTree abandon path"));
        }

        [Fact]
        public void DiscardStashedOnQuickload_WithPendingReFlyInvoke_PreservesPendingTreeAndRewindPoint()
        {
            // Captured Retry repro: before AtomicMarkerWrite recreates the
            // marker, the session dependency lives in RewindInvokeContext.
            var tree = MakeTree("t_refly_pending", "Kerbal X", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            RewindInvokeContext.Pending = true;
            RewindInvokeContext.SessionId = "sess_pending_refly";
            RewindInvokeContext.RewindPointId = "rp_pending_refly";
            TreeDiscardPurge.ResetCallCountForTesting();
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 16558.08, currentUT: 16466.39);

            Assert.True(RecordingStore.HasPendingTree);
            Assert.True(RecordingStore.PendingStashedThisTransition);
            Assert.Equal(0, TreeDiscardPurge.PurgeTreeCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("refusing to run with active re-fly session")
                && l.Contains("pending-invoke")
                && l.Contains("sess_pending_refly") && l.Contains("rp_pending_refly"));
            Assert.DoesNotContain(logLines, l => l.Contains("DiscardPendingTree abandon path"));
        }

        // #434: removed DiscardStashedOnQuickload_WithDeferredFlightResults_ClearsDiscardedFutureState.
        // FlightResultsPatch is gone — there's no deferred flight-results state to seed or assert on.
        // The broader DiscardStashedOnQuickload behaviour is still exercised by the adjacent tests.

        [Fact]
        public void DiscardStashedOnQuickload_WithPendingTreeLimbo_Preserves()
        {
            // Limbo pending tree is the quickload-resume carrier — must survive
            // the discard so RestoreActiveTreeFromPending can resume it in
            // OnFlightReady. This is the tree-mode F5/F9 case.
            var tree = MakeTree("t_b", "Mun Mission", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 400.0, currentUT: 370.0);

            Assert.True(RecordingStore.HasPendingTree); // preserved
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload discard complete") && l.Contains("tree=0"));
            // The preserve path must NOT log a "discarded pending tree" line.
            Assert.DoesNotContain(logLines, l => l.Contains("discarded pending tree"));
        }

        [Fact]
        public void DiscardPendingTreeAndAbandonDeferredFlightResults_DiscardsPendingTree()
        {
            // #434: FlightResultsPatch is gone; this helper now just forwards to
            // RecordingStore.DiscardPendingTree with a named reason for logging.
            var tree = MakeTree("t_cleanup", "Cleanup Carrier", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);

            ParsekScenario.DiscardPendingTreeAndAbandonDeferredFlightResults(
                "unit test abandoned pending tree");

            Assert.False(RecordingStore.HasPendingTree);
        }

        [Fact]
        public void DiscardStashedOnQuickload_WithStaleScienceSubjects_Clears()
        {
            GameStateRecorder.PendingScienceSubjects.Clear();
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "crewReport@KerbinSrfLandedLaunchPad",
                science = 1.5f,
            });
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "evaReport@KerbinSrfLandedLaunchPad",
                science = 0.3f,
            });
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 400.0, currentUT: 370.0);

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l =>
                l.Contains("cleared 2 stale pending science subject(s)"));
        }

        [Fact]
        public void DiscardStashedOnQuickload_NothingStashed_LogsOnlyHeader()
        {
            // Empty state: no pending tree, no science. The
            // discard call must still log the "Quickload detected" header (so
            // diagnostics always show why we took this path) and a final
            // "discard complete tree=0 science=0" line.
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 397.2, currentUT: 369.2);

            Assert.Contains(logLines, l =>
                l.Contains("Quickload detected") && l.Contains("397.20") && l.Contains("369.20"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload discard complete")
                && l.Contains("tree=0") && l.Contains("science=0"));
        }

        // ----- Defense in depth: tight frame cap -----

        [Fact]
        public void IsVesselSwitchFlagFresh_LowFpsEvaLeak_IsRejectedByTighterCap()
        {
            // Bug #276 narrative regression: a 24 s EVA walk at ~12 render fps
            // advances ~288 frames. With the OLD cap of 300 this slipped
            // through and mis-classified the stale EVA flag as fresh. With
            // the new cap of 60 it's rejected outright. Asserts the real
            // production constant (not a hand-rolled value).
            const int evaFramesAt12Fps = 288;
            bool result = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: true,
                pendingFrame: 5000,
                currentFrame: 5000 + evaFramesAt12Fps,
                maxAgeFrames: ParsekScenario.VesselSwitchPendingMaxAgeFrames);
            Assert.False(result);
        }

        [Fact]
        public void DiscardStashedOnQuickload_PendingTreeFinalized_AlwaysDiscarded()
        {
            // Finalized pending tree is always discarded on quickload.
            var tree = MakeTree("t_kx", "Kerbal X", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.True(RecordingStore.PendingStashedThisTransition);
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 350.0, currentUT: 295.0);

            Assert.False(RecordingStore.HasPendingTree); // discarded
            Assert.Contains(logLines, l =>
                l.Contains("discarded pending tree") && l.Contains("Kerbal X"));
        }

        // ----- Helpers -----

        private static RecordingTree MakeTree(string id, string name, int recordingCount)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = name,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            for (int i = 0; i < recordingCount; i++)
            {
                string recId = i == 0 ? "root_" + id : $"child_{id}_{i}";
                var rec = new Recording
                {
                    RecordingId = recId,
                    VesselName = $"{name} #{i}",
                    TreeId = id,
                    ExplicitStartUT = 100 + i * 10,
                    ExplicitEndUT = 110 + i * 10,
                };
                rec.Points.Add(new TrajectoryPoint { ut = rec.ExplicitStartUT });
                rec.Points.Add(new TrajectoryPoint { ut = rec.ExplicitEndUT });
                tree.Recordings[recId] = rec;
            }
            return tree;
        }
    }
}
