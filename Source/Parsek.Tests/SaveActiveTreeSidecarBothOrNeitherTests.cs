using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression coverage for the <c>SaveActiveTreeIfAny</c> sidecar-orphan bug
    /// (BUG-C latent secondary, 2026-06-07 career playtest).
    ///
    /// <para><b>The bug.</b> <c>SaveActiveTreeIfAny</c> used to write each recording's
    /// sidecars inline while iterating the active tree, and early-return — skipping the
    /// WHOLE <c>RECORDING_TREE</c> node — as soon as any one recording turned out to be
    /// a committed-restore overlap. A legitimately-new marker-owned switch-segment
    /// recording earlier in the (unspecified) dictionary iteration order therefore had
    /// its sidecar flushed to disk and then lost the metadata that referenced it:
    /// a sidecar file with no tree node naming it, i.e. an orphan.</para>
    ///
    /// <para><b>The invariant.</b> Either the active-tree node AND its sidecars are
    /// persisted, or neither is. The fix defers every sidecar write until after the
    /// skip decision: <c>PlanActiveTreeSidecarSaves</c> classifies every recording
    /// first (both skip predicates are pure functions of recording state), and
    /// <c>SaveActiveTreeIfAny</c> abandons every deferred write when the plan reports a
    /// skip.</para>
    ///
    /// <para>The full <c>Scenario.OnSave</c> path is not drivable from xUnit (it needs
    /// a live FLIGHT scene and <c>ParsekFlight.Instance</c>), which is why the existing
    /// coverage for this method is source-text-gated. The classify pass was extracted
    /// as <c>internal static</c> precisely so the decision half IS drivable; the
    /// ordering half stays a source gate.</para>
    /// </summary>
    [Collection("Sequential")]
    public class SaveActiveTreeSidecarBothOrNeitherTests : IDisposable
    {
        private const string AttemptTreeId = "tree_attempt";
        private const string LiveTreeId = "tree_attempt_live";
        private const string OverlapRecordingId = "rec_overlap";
        private const string SegmentRecordingId = "rec_segment_owned";

        private readonly List<string> logLines = new List<string>();

        public SaveActiveTreeSidecarBothOrNeitherTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            // The marker-owned bypass line is Verbose; pin the level rather than
            // relying on whatever ParsekSettings a sibling test left loaded.
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekScenario.SetInstanceForTesting(null);
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // The exact shape from the todo entry
        // -----------------------------------------------------------------

        // Fails if: the classify pass reports the tree as writable while a
        // committed-restore overlap is present, or stops reporting the
        // legitimately-new switch-segment recording as a write candidate.
        //
        // This is the both-or-neither statement in one assertion pair: the
        // marker-owned segment IS a write candidate (its sidecar would have been
        // written by the old inline loop), yet the plan says the tree node cannot be
        // written. The caller must therefore write NEITHER. Before the fix, the
        // segment's sidecar had already been flushed by the time the overlap was
        // reached, orphaning it.
        [Fact]
        public void OverlapPlusMarkerOwnedSegment_PlanDefersEverySidecarWrite()
        {
            RecordingTree activeTree = BuildOverlapPlusSegmentActiveTree(
                out Recording overlap, out Recording segment);

            logLines.Clear();
            ParsekScenario.ActiveTreeSidecarSavePlan plan =
                ParsekScenario.PlanActiveTreeSidecarSaves(activeTree);

            // The tree node will NOT be written...
            Assert.False(plan.AllRecordingsWritable);
            Assert.Equal(1, plan.SkippedCommittedRestoreOverlapCount);
            Assert.Equal(0, plan.SkippedDegradedCount);

            // ...but the new switch-segment recording was still a write candidate,
            // so the caller is the one that has to abandon it.
            Recording candidate = Assert.Single(plan.WriteCandidates);
            Assert.Equal(SegmentRecordingId, candidate.RecordingId);
            Assert.Same(segment, candidate);
            Assert.True(plan.WriteCandidateWasDirty[0]);

            Assert.Equal(2, plan.RecordingCount);
            Assert.Equal(2, plan.DirtyCount);

            // Both recordings are still dirty: the classify pass writes nothing.
            Assert.True(overlap.FilesDirty);
            Assert.True(segment.FilesDirty);

            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("skipped dirty sidecar save for committed-restore overlap") &&
                l.Contains("'" + OverlapRecordingId + "'"));
        }

        // Fails if: the classify pass is order-dependent. The orphan was created by
        // dictionary iteration order (segment first, overlap second); the plan must
        // reach the same verdict whichever order the recordings are walked in, so
        // both orders are asserted against the same expectations.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OverlapPlusMarkerOwnedSegment_VerdictIsIndependentOfIterationOrder(
            bool segmentFirst)
        {
            RecordingTree activeTree = BuildOverlapPlusSegmentActiveTree(
                out _, out _, segmentFirst: segmentFirst);
            ParsekScenario.ActiveTreeSidecarSavePlan plan =
                ParsekScenario.PlanActiveTreeSidecarSaves(activeTree);

            Assert.False(plan.AllRecordingsWritable);
            Assert.Equal(1, plan.SkippedCommittedRestoreOverlapCount);
            Assert.Equal(2, plan.RecordingCount);
            Assert.Equal(2, plan.DirtyCount);
            Recording candidate = Assert.Single(plan.WriteCandidates);
            Assert.Equal(SegmentRecordingId, candidate.RecordingId);
        }

        // Fails if: a marker-owned recording that ALSO shares an id with the armed
        // committed-restore attempt gets skipped. The narrowing (segment-scoped
        // switch/Fly Phase D) must keep the marker-owned bypass, otherwise a new
        // segment's data never becomes durable.
        [Fact]
        public void MarkerOwnedRecordingSharingOverlapId_StaysAWriteCandidate()
        {
            ParsekScenario scenario = MakeScenarioWithSession(out SwitchSegmentSession session);

            var shared = MakeRecording(OverlapRecordingId, AttemptTreeId,
                switchSegmentSessionId: ToSessionString(session.SessionId));
            var attemptTree = MakeTreeWithRecordings(AttemptTreeId, shared);
            RecordingStore.AddCommittedTreeForTesting(attemptTree);
            RecordingStore.AddCommittedInternal(shared);
            RecordingStore.ArmCommittedTreeRestoreAttempt(attemptTree, "test-arm");
            Assert.NotNull(scenario.ActiveSwitchSegmentSession);

            var activeTree = MakeTreeWithRecordings(LiveTreeId, shared);

            logLines.Clear();
            ParsekScenario.ActiveTreeSidecarSavePlan plan =
                ParsekScenario.PlanActiveTreeSidecarSaves(activeTree);

            Assert.True(plan.AllRecordingsWritable);
            Assert.Equal(0, plan.SkippedCommittedRestoreOverlapCount);
            Assert.Same(shared, Assert.Single(plan.WriteCandidates));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("reason=marker-owned-switch-segment"));
        }

        // Fails if: an ordinary active tree (no restore attempt, no session) reports
        // anything other than "write everything". The classify pass must not become a
        // new way to lose a plain in-flight save.
        [Fact]
        public void OrdinaryActiveTree_PlanWritesEveryRecording()
        {
            var first = MakeRecording("rec_a", LiveTreeId);
            var second = MakeRecording("rec_b", LiveTreeId);
            second.FilesDirty = false;
            var activeTree = MakeTreeWithRecordings(LiveTreeId, first, second);

            ParsekScenario.ActiveTreeSidecarSavePlan plan =
                ParsekScenario.PlanActiveTreeSidecarSaves(activeTree);

            Assert.True(plan.AllRecordingsWritable);
            Assert.Equal(2, plan.WriteCandidates.Count);
            Assert.Equal(2, plan.RecordingCount);
            Assert.Equal(1, plan.DirtyCount);
            Assert.Equal(0, plan.SkippedCommittedRestoreOverlapCount);
            Assert.Equal(0, plan.SkippedDegradedCount);
        }

        // Fails if: the OTHER skip (a hydration-failed recording with an empty
        // trajectory payload, which must not overwrite a good sidecar with nothing)
        // stops defeating the whole-tree write. It orphans sidecars exactly the same
        // way, so it obeys the same both-or-neither rule.
        [Fact]
        public void DegradedEmptySidecarRecording_PlanDefersEverySidecarWrite()
        {
            var healthy = MakeRecording("rec_healthy", LiveTreeId);
            var degraded = MakeRecording("rec_degraded", LiveTreeId);
            degraded.SidecarLoadFailed = true;
            // Deliberately NOT one of the snapshot-hydration reasons: those are
            // repaired elsewhere and do not trigger the empty-overwrite skip.
            degraded.SidecarLoadFailureReason = "trajectory-missing";
            var activeTree = MakeTreeWithRecordings(LiveTreeId, healthy, degraded);

            logLines.Clear();
            ParsekScenario.ActiveTreeSidecarSavePlan plan =
                ParsekScenario.PlanActiveTreeSidecarSaves(activeTree);

            Assert.False(plan.AllRecordingsWritable);
            Assert.Equal(1, plan.SkippedDegradedCount);
            Assert.Same(healthy, Assert.Single(plan.WriteCandidates));
            // The degraded recording is skipped BEFORE the recording counter, matching
            // the pre-fix accounting for this branch.
            Assert.Equal(1, plan.RecordingCount);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("skipped empty sidecar overwrite for hydration-failed") &&
                l.Contains("reason=trajectory-missing"));
        }

        // Fails if: a null / empty tree throws instead of returning a writable,
        // empty plan. SaveActiveTreeIfAny already guards for a null tree, but the
        // planner must not depend on that.
        [Fact]
        public void NullOrEmptyTree_ReturnsEmptyWritablePlan()
        {
            ParsekScenario.ActiveTreeSidecarSavePlan nullPlan =
                ParsekScenario.PlanActiveTreeSidecarSaves(null);
            Assert.True(nullPlan.AllRecordingsWritable);
            Assert.Empty(nullPlan.WriteCandidates);
            Assert.Equal(0, nullPlan.RecordingCount);

            ParsekScenario.ActiveTreeSidecarSavePlan emptyPlan =
                ParsekScenario.PlanActiveTreeSidecarSaves(MakeTreeWithRecordings(LiveTreeId));
            Assert.True(emptyPlan.AllRecordingsWritable);
            Assert.Empty(emptyPlan.WriteCandidates);
        }

        // -----------------------------------------------------------------
        // Ordering gate — source text
        //
        // The decision half is unit-tested above; what a unit test cannot reach is
        // that SaveActiveTreeIfAny actually CLASSIFIES BEFORE IT WRITES. That
        // ordering is the whole fix, so it is pinned by reading the source, the same
        // way the sibling gates for this method do.
        // -----------------------------------------------------------------

        // Fails if: SaveActiveTreeIfAny writes a sidecar before it knows whether the
        // tree node will be written — i.e. the orphan is reintroduced.
        [Fact]
        public void SaveActiveTreeIfAny_ClassifiesBeforeAnySidecarWrite()
        {
            string methodSrc = ReadSaveActiveTreeIfAnyBody();

            int planIdx = methodSrc.IndexOf(
                "PlanActiveTreeSidecarSaves(activeTree)", StringComparison.Ordinal);
            int bothOrNeitherIdx = methodSrc.IndexOf(
                "outcome=both-or-neither", StringComparison.Ordinal);
            int writeIdx = methodSrc.IndexOf(
                "EnsureRecordingFilesCurrentForSave(writeCandidate, \"active tree\")",
                StringComparison.Ordinal);

            Assert.True(planIdx >= 0,
                "SaveActiveTreeIfAny no longer runs the classify pass");
            Assert.True(bothOrNeitherIdx >= 0,
                "SaveActiveTreeIfAny no longer logs the grep-stable both-or-neither skip outcome");
            Assert.True(writeIdx >= 0,
                "SaveActiveTreeIfAny no longer writes active-tree sidecars from the deferred plan");

            Assert.True(planIdx < bothOrNeitherIdx,
                "the classify pass must run before the skip decision is logged");
            Assert.True(bothOrNeitherIdx < writeIdx,
                "the skip decision (and its early return) must precede every sidecar write");
        }

        // Fails if: a second, un-deferred sidecar write sneaks back into the method.
        // Exactly one active-tree write site may exist, and it must be the deferred
        // one that reads from the plan.
        [Fact]
        public void SaveActiveTreeIfAny_HasExactlyOneDeferredSidecarWriteSite()
        {
            string methodSrc = ReadSaveActiveTreeIfAnyBody();

            int total = CountOccurrences(methodSrc, "EnsureRecordingFilesCurrentForSave(");
            int deferred = CountOccurrences(methodSrc,
                "EnsureRecordingFilesCurrentForSave(writeCandidate, \"active tree\")");

            Assert.Equal(1, total);
            Assert.Equal(1, deferred);
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// The todo entry's shape: one committed-restore-overlap recording (dirty, id
        /// belongs to the armed attempt tree, NOT marker-owned) plus one
        /// legitimately-new marker-owned switch-segment recording (dirty), both in the
        /// live active tree.
        /// </summary>
        private RecordingTree BuildOverlapPlusSegmentActiveTree(
            out Recording overlap, out Recording segment, bool segmentFirst = false)
        {
            MakeScenarioWithSession(out SwitchSegmentSession session);

            overlap = MakeRecording(OverlapRecordingId, AttemptTreeId);
            var attemptTree = MakeTreeWithRecordings(AttemptTreeId, overlap);
            RecordingStore.AddCommittedTreeForTesting(attemptTree);
            RecordingStore.AddCommittedInternal(overlap);
            RecordingStore.ArmCommittedTreeRestoreAttempt(attemptTree, "test-arm");

            segment = MakeRecording(SegmentRecordingId, AttemptTreeId,
                switchSegmentSessionId: ToSessionString(session.SessionId));
            RecordingStore.AddCommittedInternal(segment);

            return segmentFirst
                ? MakeTreeWithRecordings(LiveTreeId, segment, overlap)
                : MakeTreeWithRecordings(LiveTreeId, overlap, segment);
        }

        private static ParsekScenario MakeScenarioWithSession(out SwitchSegmentSession session)
        {
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);
            session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.TrackingStationFly,
                TreeId = AttemptTreeId,
                ActiveSegmentRecordingId = SegmentRecordingId,
                SwitchUT = 250.0,
            };
            scenario.ArmSwitchSegmentSession(session);
            return scenario;
        }

        private static string ToSessionString(Guid sessionId)
            => sessionId.ToString("D", CultureInfo.InvariantCulture);

        private static Recording MakeRecording(
            string recordingId, string treeId, string switchSegmentSessionId = null)
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = treeId,
                VesselName = "Test Recording",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
                SwitchSegmentSessionId = switchSegmentSessionId,
                FilesDirty = true,
            };
        }

        private static RecordingTree MakeTreeWithRecordings(
            string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : null,
                ActiveRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : null,
            };
            foreach (var rec in recordings)
                tree.AddOrReplaceRecording(rec);
            return tree;
        }

        /// <summary>
        /// Returns the source text of <c>SaveActiveTreeIfAny</c>, bounded by its
        /// signature and its terminal log line, so the gates below cannot match text
        /// belonging to a neighbouring method.
        /// </summary>
        private static string ReadSaveActiveTreeIfAnyBody()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot, "Source", "Parsek", "ParsekScenario.cs");
            Assert.True(File.Exists(scenarioPath), $"ParsekScenario.cs not found at {scenarioPath}");

            string source = File.ReadAllText(scenarioPath);
            int start = source.IndexOf(
                "private static void SaveActiveTreeIfAny(ConfigNode node)", StringComparison.Ordinal);
            Assert.True(start >= 0, "SaveActiveTreeIfAny method signature not found");

            int end = source.IndexOf("OnSave: wrote ACTIVE tree", start, StringComparison.Ordinal);
            Assert.True(end > start, "SaveActiveTreeIfAny terminal log line not found");

            return source.Substring(start, end - start);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int idx = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (idx >= 0)
            {
                count++;
                idx = haystack.IndexOf(needle, idx + needle.Length, StringComparison.Ordinal);
            }
            return count;
        }
    }
}
