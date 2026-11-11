using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Re-Fly merge × optimizer split interaction. The optimizer's split pass
    /// runs from <see cref="CommittedListOptimizer.RunOptimizationPass"/>, which the
    /// merge dialog invokes during <c>CommitPendingTree</c> before
    /// <c>TryCommitReFlySupersede</c>. Without the deferral guard,
    /// <see cref="RecordingOptimizer.SplitAtSection"/> moves
    /// <see cref="Recording.TerminalStateValue"/> off the head and
    /// <see cref="SupersedeCommit.ValidateSupersedeTarget"/> rejects the head
    /// with "null TerminalState".
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyMergeOptimizerSplitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ReFlyMergeOptimizerSplitTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---------- Helpers ------------------------------------------------

        private static Recording MakeAtmoSurfaceRecording(string id, double startUT, double midUT, double endUT)
        {
            // Atmospheric -> SurfaceStationary boundary on Kerbin: matches the
            // log scenario from the failing session (rec_4eb2 atmo→surface
            // crossing at UT 361.86) and is splittable per
            // RecordingOptimizer.IsSplittableEnvOrBodyBoundary.
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                VesselPersistentId = 12345,
                TerminalStateValue = TerminalState.Landed,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 5000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = midUT - 0.01, altitude = 200, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = midUT, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 0, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = startUT,
                endUT = midUT,
                frames = new List<TrajectoryPoint>(),
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                startUT = midUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
            });
            return rec;
        }

        private static ParsekScenario InstallScenarioWithMarker(string activeReFlyRecordingId)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = string.IsNullOrEmpty(activeReFlyRecordingId)
                    ? null
                    : new ReFlySessionMarker
                    {
                        SessionId = "sess_test_1",
                        TreeId = "tree_test",
                        ActiveReFlyRecordingId = activeReFlyRecordingId,
                        OriginChildRecordingId = "origin_rec",
                        RewindPointId = "rp_test_1",
                        InvokedUT = 0.0,
                    },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        // ---------- Split deferral -----------------------------------------

        [Fact]
        public void SplitPass_ActiveReFlyMarker_DefersSplitForActiveProvisionalOnly()
        {
            // Tree with two split-eligible recordings: one is the active Re-Fly
            // provisional (must be deferred); the other is a sibling that must
            // still split this pass.
            InstallScenarioWithMarker("active_refly");

            var provisional = MakeAtmoSurfaceRecording("active_refly",
                startUT: 200, midUT: 250, endUT: 300);
            var sibling = MakeAtmoSurfaceRecording("sibling_normal",
                startUT: 1000, midUT: 1050, endUT: 1100);
            sibling.VesselPersistentId = 99999;

            RecordingStore.AddRecordingWithTreeForTesting(provisional);
            RecordingStore.AddRecordingWithTreeForTesting(sibling);

            CommittedListOptimizer.RunOptimizationPass();

            var recordings = RecordingStore.CommittedRecordings;

            // Provisional stays as a single recording with its terminal state
            // intact — the supersede commit's invariant will pass.
            var provisionalAfter = recordings.SingleOrDefault(r => r.RecordingId == "active_refly");
            Assert.NotNull(provisionalAfter);
            Assert.Equal(TerminalState.Landed, provisionalAfter.TerminalStateValue);

            // Sibling split into two chain segments — deferral must not block
            // unrelated optimizer work in the same pass.
            var siblingSegments = recordings.Where(r => r.VesselName == "sibling_normal").ToList();
            Assert.Equal(2, siblingSegments.Count);
            Assert.Equal(siblingSegments[0].ChainId, siblingSegments[1].ChainId);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("deferred split for active Re-Fly recording")
                && l.Contains("active_refly"));
        }

        [Fact]
        public void SplitPass_NoActiveMarker_SplitsRecordingThatWouldHaveBeenDeferred()
        {
            InstallScenarioWithMarker(null);

            var rec = MakeAtmoSurfaceRecording("would_have_been_deferred",
                startUT: 200, midUT: 250, endUT: 300);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            CommittedListOptimizer.RunOptimizationPass();

            var recordings = RecordingStore.CommittedRecordings;
            Assert.Equal(2, recordings.Count);
            Assert.Equal("would_have_been_deferred", recordings[0].RecordingId);
            Assert.NotEqual("would_have_been_deferred", recordings[1].RecordingId);
            // Original head loses TerminalState by design (moves to the second
            // half) — this is the very reason the deferral exists when a Re-Fly
            // marker is live.
            Assert.Null(recordings[0].TerminalStateValue);
            Assert.Equal(TerminalState.Landed, recordings[1].TerminalStateValue);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("deferred split for active Re-Fly recording"));
        }

        [Fact]
        public void SplitPass_MarkerActiveButDifferentRecordingId_DoesNotDefer()
        {
            // Re-Fly marker pointing at some other recording id must not
            // accidentally defer splits for unrelated provisional ids.
            InstallScenarioWithMarker("some_other_refly");

            var rec = MakeAtmoSurfaceRecording("normal_recording",
                startUT: 200, midUT: 250, endUT: 300);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            CommittedListOptimizer.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
        }

        // ---------- End-to-end merge invariant -----------------------------

        [Fact]
        public void RunOptimizationPass_LeavesProvisionalEligibleForAppendRelations()
        {
            // Reproduces the production failure mode (logs/2026-05-06_2035):
            // landed Re-Fly across atmo→surface, RunOptimizationPass invoked
            // from the merge dialog before TryCommitReFlySupersede. Without
            // the deferral guard, the optimizer split nulls the head's
            // TerminalStateValue and SupersedeCommit.ValidateSupersedeTarget
            // would later reject it with "null TerminalState".
            InstallScenarioWithMarker("active_refly");

            var provisional = MakeAtmoSurfaceRecording("active_refly",
                startUT: 200, midUT: 360, endUT: 371);
            provisional.CreatingSessionId = "sess_test_1";
            provisional.MergeState = MergeState.NotCommitted;
            provisional.SupersedeTargetId = "origin_rec";
            RecordingStore.AddRecordingWithTreeForTesting(provisional);

            CommittedListOptimizer.RunOptimizationPass();

            // Provisional must satisfy ValidateSupersedeTarget at the moment
            // TryCommitReFlySupersede looks it up: same id, terminal state
            // present, payload non-empty.
            string reason;
            bool valid = SupersedeCommit.ValidateSupersedeTarget(
                provisional, out reason);
            Assert.True(valid,
                $"ValidateSupersedeTarget rejected the provisional: {reason ?? "<none>"}");
            Assert.Equal(TerminalState.Landed, provisional.TerminalStateValue);
            Assert.Single(RecordingStore.CommittedRecordings);
        }
    }
}
