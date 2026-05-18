using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase A.4 coverage for <see cref="SwitchSegmentBuilder"/>: the pure
    /// tree-mutation helper that creates switch/Fly continuation segments
    /// plus the terminal-leaf parent resolver. These tests are tree-shape
    /// only — no live <see cref="Vessel"/>, no background recorder, no
    /// Harmony, no consume site. Phase C wires the helper into FLIGHT.
    ///
    /// Tests map to plan §"Segment Creation", §"Parent Selection Risk", and
    /// §"Behavior by Entry Path → Goal 5" in
    /// <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>.
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentBuilderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------- helpers ----------

        private static TrajectoryPoint MakeBoundary(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 100.0,
                bodyName = "Kerbin",
            };
        }

        private static Recording MakeRecording(
            string recordingId, string treeId, uint pid,
            string vesselName = "Test", double startUT = 0.0)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                TreeId = treeId,
                VesselPersistentId = pid,
                VesselName = vesselName,
                ExplicitStartUT = startUT,
            };
            rec.Points.Add(MakeBoundary(startUT));
            return rec;
        }

        private static RecordingTree MakeTree(string treeId = "tree-1")
        {
            return new RecordingTree
            {
                Id = treeId,
                TreeName = "Test Tree",
                BranchPoints = new List<BranchPoint>(),
            };
        }

        // ---------- creation tests ----------

        // Fails if: attaching under a terminal-leaf parent does not register
        // the new recording, does not wire ChildBranchPointId/ParentBranchPointId,
        // does not update ActiveRecordingId, or mutates the parent's payload.
        [Fact]
        public void CreateSwitchContinuationSegment_UnderTerminalLeaf_AttachesAndSetsActive()
        {
            var tree = MakeTree("tree-A");
            var parent = MakeRecording("parent-1", tree.Id, 999u,
                vesselName: "Parent", startUT: 100.0);
            tree.AddOrReplaceRecording(parent);
            tree.RootRecordingId = parent.RecordingId;
            tree.ActiveRecordingId = parent.RecordingId;

            // Snapshot pre-state of parent for unchanged-payload assertion.
            double preParentExplicitStartUT = parent.ExplicitStartUT;
            int prePointCount = parent.Points.Count;
            string preChildBranchPointId = parent.ChildBranchPointId;
            string preParentBranchPointId = parent.ParentBranchPointId;

            Guid intentId = Guid.NewGuid();
            Guid sessionId = Guid.NewGuid();
            string newRecId = Guid.NewGuid().ToString("N");
            string newBpId = Guid.NewGuid().ToString("N");
            double switchUT = 150.5;

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parent.RecordingId, focusedVesselPersistentId: 999u,
                focusedVesselName: "Parent",
                switchUT: switchUT,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: intentId, sessionId: sessionId,
                newRecordingId: newRecId, newBranchPointId: newBpId,
                initialBoundaryPointFactory: MakeBoundary,
                sourceVesselPersistentId: 888u);

            Assert.True(result.Created, "Expected Created=true; failureReason=" + (result.FailureReason ?? "<null>"));
            Assert.Equal(newRecId, result.NewRecordingId);
            Assert.Equal(newBpId, result.NewBranchPointId);
            Assert.Equal(parent.RecordingId, result.ParentRecordingId);
            Assert.Equal(BranchPointType.VesselSwitchContinuation, result.BranchType);
            Assert.Null(result.FailureReason);

            // Branch point registered + wired.
            Assert.Single(tree.BranchPoints);
            BranchPoint bp = tree.BranchPoints[0];
            Assert.Equal(newBpId, bp.Id);
            Assert.Equal(BranchPointType.VesselSwitchContinuation, bp.Type);
            Assert.Equal(switchUT, bp.UT);
            Assert.Single(bp.ParentRecordingIds);
            Assert.Equal(parent.RecordingId, bp.ParentRecordingIds[0]);
            Assert.Single(bp.ChildRecordingIds);
            Assert.Equal(newRecId, bp.ChildRecordingIds[0]);

            // Parent now points to the new branch point.
            Assert.Equal(newBpId, parent.ChildBranchPointId);

            // New recording registered + wired.
            Assert.True(tree.Recordings.ContainsKey(newRecId));
            Recording newRec = tree.Recordings[newRecId];
            Assert.Equal(newBpId, newRec.ParentBranchPointId);
            Assert.Equal(newRecId, tree.ActiveRecordingId);

            // SwitchSegmentSessionId stamped; CreatingSessionId untouched.
            Assert.Equal(sessionId.ToString("D", CultureInfo.InvariantCulture),
                newRec.SwitchSegmentSessionId);
            Assert.Null(newRec.CreatingSessionId);

            // Parent payload unchanged (no new points, ExplicitStartUT
            // unchanged, parent never gained a ParentBranchPointId from
            // this attach).
            Assert.Equal(prePointCount, parent.Points.Count);
            Assert.Equal(preParentExplicitStartUT, parent.ExplicitStartUT);
            Assert.Equal(preParentBranchPointId, parent.ParentBranchPointId);
            Assert.NotEqual(preChildBranchPointId, parent.ChildBranchPointId);

            // Initial boundary sample is present and non-empty.
            Assert.Single(newRec.Points);
            Assert.Equal(switchUT, newRec.Points[0].ut);

            // Diagnostic log written with all the required fields.
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("created")
                && l.Contains("intentId=" + intentId.ToString("D", CultureInfo.InvariantCulture))
                && l.Contains("sessionId=" + sessionId.ToString("D", CultureInfo.InvariantCulture))
                && l.Contains("segmentRecId=" + newRecId)
                && l.Contains("branchPointId=" + newBpId)
                && l.Contains("focusedPid=999")
                && l.Contains("sourcePid=888")
                && l.Contains("reason=TrackingStationFly"));
        }

        // Fails if: standalone (parent==null) leaks a branch point, fails to
        // add the recording, or does not set ActiveRecordingId.
        [Fact]
        public void CreateSwitchContinuationSegment_StandaloneTreeNoParent_AddsAsRoot()
        {
            var tree = MakeTree("tree-Standalone");
            Assert.Empty(tree.Recordings);
            Assert.Empty(tree.BranchPoints);

            Guid intentId = Guid.NewGuid();
            Guid sessionId = Guid.NewGuid();
            string newRecId = Guid.NewGuid().ToString("N");

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parentRecordingIdOrNull: null,
                focusedVesselPersistentId: 5555u,
                focusedVesselName: "Newcomer",
                switchUT: 42.0,
                entryReason: SwitchSegmentEntryReason.MapSwitchTo,
                intentId: intentId, sessionId: sessionId,
                newRecordingId: newRecId,
                newBranchPointId: "ignored-when-standalone",
                initialBoundaryPointFactory: MakeBoundary);

            Assert.True(result.Created, "Expected Created=true; failureReason=" + (result.FailureReason ?? "<null>"));
            Assert.Null(result.ParentRecordingId);
            Assert.Null(result.NewBranchPointId);
            Assert.Empty(tree.BranchPoints);
            Assert.Single(tree.Recordings);
            Assert.Equal(newRecId, tree.ActiveRecordingId);

            Recording newRec = tree.Recordings[newRecId];
            Assert.Null(newRec.ParentBranchPointId);
            Assert.Equal(sessionId.ToString("D", CultureInfo.InvariantCulture),
                newRec.SwitchSegmentSessionId);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("created")
                && l.Contains("parentRecId=<standalone>"));
        }

        // Fails if: a non-terminal parent (ChildBranchPointId != null) is
        // overwritten — plan §"Parent Selection Risk" item 1 forbids this.
        [Fact]
        public void CreateSwitchContinuationSegment_NonTerminalParent_RefusesWithFailureReason()
        {
            var tree = MakeTree("tree-NonTerminal");
            var parent = MakeRecording("parent-2", tree.Id, 999u, "Parent", 100.0);
            string preexistingChildBpId = "preexisting-bp";
            parent.ChildBranchPointId = preexistingChildBpId;
            tree.AddOrReplaceRecording(parent);
            tree.ActiveRecordingId = parent.RecordingId;

            int preBranchPointCount = tree.BranchPoints.Count;
            int preRecordingCount = tree.Recordings.Count;
            string preActiveRecordingId = tree.ActiveRecordingId;

            string newRecId = Guid.NewGuid().ToString("N");
            string newBpId = Guid.NewGuid().ToString("N");

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parent.RecordingId, 999u, "Parent",
                switchUT: 150.0,
                entryReason: SwitchSegmentEntryReason.KscMarkerFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: newRecId, newBranchPointId: newBpId,
                initialBoundaryPointFactory: MakeBoundary);

            Assert.False(result.Created);
            Assert.Equal("parent-not-terminal-leaf", result.FailureReason);

            // Existing parent ChildBranchPointId untouched.
            Assert.Equal(preexistingChildBpId, parent.ChildBranchPointId);
            // Tree shape unchanged.
            Assert.Equal(preBranchPointCount, tree.BranchPoints.Count);
            Assert.Equal(preRecordingCount, tree.Recordings.Count);
            Assert.Equal(preActiveRecordingId, tree.ActiveRecordingId);
            Assert.False(tree.Recordings.ContainsKey(newRecId));

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("refused")
                && l.Contains("failureReason=parent-not-terminal-leaf"));
        }

        // Fails if: a bogus parent ID either crashes the helper or partially
        // mutates the tree before refusing.
        [Fact]
        public void CreateSwitchContinuationSegment_ParentNotInTree_RefusesWithFailureReason()
        {
            var tree = MakeTree("tree-Missing");
            // No recordings in tree; parent-not-in-tree is the only case
            // for parentRecordingIdOrNull != null when tree.Recordings is empty.

            int preBranchPointCount = tree.BranchPoints.Count;
            int preRecordingCount = tree.Recordings.Count;

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parentRecordingIdOrNull: "does-not-exist",
                focusedVesselPersistentId: 1u,
                focusedVesselName: "Nobody",
                switchUT: 0.0,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: Guid.NewGuid().ToString("N"),
                newBranchPointId: Guid.NewGuid().ToString("N"),
                initialBoundaryPointFactory: MakeBoundary);

            Assert.False(result.Created);
            Assert.Equal("parent-not-in-tree", result.FailureReason);
            Assert.Equal(preBranchPointCount, tree.BranchPoints.Count);
            Assert.Equal(preRecordingCount, tree.Recordings.Count);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("refused")
                && l.Contains("failureReason=parent-not-in-tree"));
        }

        // Fails if: a multi-recording chain that walks via ChildBranchPointId
        // to a unique PID-matching terminal leaf does not resolve to that leaf.
        [Fact]
        public void ResolveSwitchContinuationParent_UniqueTerminalLeaf_Found()
        {
            // Chain: rec1 (PID=42) -[VesselSwitchContinuation]-> rec2 (PID=42, terminal)
            var tree = MakeTree("tree-Resolve1");
            var rec1 = MakeRecording("rec1", tree.Id, 42u, "Vessel", 10.0);
            var rec2 = MakeRecording("rec2", tree.Id, 42u, "Vessel", 50.0);
            var bp = new BranchPoint
            {
                Id = "bp-1",
                Type = BranchPointType.VesselSwitchContinuation,
                UT = 50.0,
                ParentRecordingIds = new List<string> { rec1.RecordingId },
                ChildRecordingIds = new List<string> { rec2.RecordingId },
            };
            rec1.ChildBranchPointId = bp.Id;
            rec2.ParentBranchPointId = bp.Id;
            tree.AddOrReplaceRecording(rec1);
            tree.AddOrReplaceRecording(rec2);
            tree.BranchPoints.Add(bp);

            SwitchContinuationParentResolution res =
                SwitchSegmentBuilder.ResolveSwitchContinuationParent(tree, 42u);

            Assert.Equal(SwitchContinuationParentStatus.UniqueTerminalLeafFound,
                res.Status);
            Assert.Equal(rec2.RecordingId, res.TerminalLeafRecordingId);
            Assert.Single(res.CandidateRecordingIds);
            Assert.Equal(rec2.RecordingId, res.CandidateRecordingIds[0]);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegmentResolver]")
                && l.Contains("unique-terminal-leaf")
                && l.Contains("leafRecId=" + rec2.RecordingId));
        }

        // Fails if: two distinct PID-matching terminal leaves do not produce
        // AmbiguousStartStandalone with both candidates listed. Plan test #16
        // ambiguous case + plan §"Goal 5" standalone fallback.
        [Fact]
        public void ResolveSwitchContinuationParent_AmbiguousMultipleLeaves_ReturnsAmbiguous()
        {
            // Two independent chains both ending in PID=77 terminal leaves.
            // (Pathological — should never occur in a healthy live tree —
            //  but the resolver must refuse to guess.)
            var tree = MakeTree("tree-Ambig");
            var leafA = MakeRecording("leafA", tree.Id, 77u, "VesselA", 10.0);
            var leafB = MakeRecording("leafB", tree.Id, 77u, "VesselB", 20.0);
            tree.AddOrReplaceRecording(leafA);
            tree.AddOrReplaceRecording(leafB);

            SwitchContinuationParentResolution res =
                SwitchSegmentBuilder.ResolveSwitchContinuationParent(tree, 77u);

            Assert.Equal(SwitchContinuationParentStatus.AmbiguousStartStandalone,
                res.Status);
            Assert.Null(res.TerminalLeafRecordingId);
            Assert.Equal(2, res.CandidateRecordingIds.Count);
            Assert.Contains(leafA.RecordingId, res.CandidateRecordingIds);
            Assert.Contains(leafB.RecordingId, res.CandidateRecordingIds);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegmentResolver]")
                && l.Contains("ambiguous-parent-start-standalone")
                && l.Contains("candidateCount=2"));
        }

        // Fails if: a tree with no PID-matching recordings does not return
        // NoMatchUseStandalone. Plan §"Goal 5" unrelated-vessel branch.
        [Fact]
        public void ResolveSwitchContinuationParent_NoMatch_ReturnsStandalone()
        {
            var tree = MakeTree("tree-NoMatch");
            tree.AddOrReplaceRecording(MakeRecording("rec1", tree.Id, 11u, "Other", 0.0));

            SwitchContinuationParentResolution res =
                SwitchSegmentBuilder.ResolveSwitchContinuationParent(tree, 99u);

            Assert.Equal(SwitchContinuationParentStatus.NoMatchUseStandalone,
                res.Status);
            Assert.Null(res.TerminalLeafRecordingId);
            Assert.Empty(res.CandidateRecordingIds);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegmentResolver]")
                && l.Contains("no-match-use-standalone")
                && l.Contains("focusedPid=99"));
        }

        // Fails if: the resolver's ambiguity log does not appear, or the
        // helper does not succeed at standalone when the caller honors
        // AmbiguousStartStandalone by passing parentRecordingIdOrNull = null.
        [Fact]
        public void CreateSwitchContinuationSegment_AmbiguousResolver_CallerStartsStandalone_LogsAmbiguityReason()
        {
            // Build ambiguous tree first.
            var tree = MakeTree("tree-AmbigStandalone");
            var leafA = MakeRecording("leafA", tree.Id, 700u, "VesselA", 5.0);
            var leafB = MakeRecording("leafB", tree.Id, 700u, "VesselB", 7.0);
            tree.AddOrReplaceRecording(leafA);
            tree.AddOrReplaceRecording(leafB);

            // Step 1: ask the resolver — it should report ambiguity and log
            // ambiguous-parent-start-standalone.
            SwitchContinuationParentResolution res =
                SwitchSegmentBuilder.ResolveSwitchContinuationParent(tree, 700u);
            Assert.Equal(SwitchContinuationParentStatus.AmbiguousStartStandalone,
                res.Status);

            // Step 2: caller honors ambiguity by going standalone.
            string newRecId = Guid.NewGuid().ToString("N");
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parentRecordingIdOrNull: null,
                focusedVesselPersistentId: 700u,
                focusedVesselName: "VesselA",
                switchUT: 99.0,
                entryReason: SwitchSegmentEntryReason.MapSwitchTo,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: newRecId,
                newBranchPointId: "ignored",
                initialBoundaryPointFactory: MakeBoundary);

            Assert.True(creation.Created);
            Assert.Null(creation.ParentRecordingId);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegmentResolver]")
                && l.Contains("ambiguous-parent-start-standalone")
                && l.Contains("candidateCount=2"));
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("created")
                && l.Contains("parentRecId=<standalone>"));
        }

        // Fails if: attaching a continuation on one leaf mutates unrelated
        // recordings (touches their Points, StartUT, EndUT, ChildBranchPointId,
        // or ParentBranchPointId).
        [Fact]
        public void CreateSwitchContinuationSegment_DoesNotMutateOtherRecordings()
        {
            var tree = MakeTree("tree-Untouched");
            // Three recordings; only one is the parent we attach under.
            var parent = MakeRecording("parent", tree.Id, 1u, "Parent", 100.0);
            var unrelated1 = MakeRecording("unrelated1", tree.Id, 2u, "U1", 200.0);
            var unrelated2 = MakeRecording("unrelated2", tree.Id, 3u, "U2", 300.0);
            unrelated2.ExplicitEndUT = 400.0; // make sure EndUT can drift if buggy.

            tree.AddOrReplaceRecording(parent);
            tree.AddOrReplaceRecording(unrelated1);
            tree.AddOrReplaceRecording(unrelated2);

            // Snapshot fields we care about for each unrelated recording.
            double pre_u1_StartUT = unrelated1.StartUT;
            double pre_u1_EndUT = unrelated1.EndUT;
            string pre_u1_ChildBp = unrelated1.ChildBranchPointId;
            string pre_u1_ParentBp = unrelated1.ParentBranchPointId;
            int pre_u1_PointCount = unrelated1.Points.Count;

            double pre_u2_StartUT = unrelated2.StartUT;
            double pre_u2_EndUT = unrelated2.EndUT;
            string pre_u2_ChildBp = unrelated2.ChildBranchPointId;
            string pre_u2_ParentBp = unrelated2.ParentBranchPointId;
            int pre_u2_PointCount = unrelated2.Points.Count;

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parent.RecordingId, 1u, "Parent", 150.0,
                SwitchSegmentEntryReason.TrackingStationFly,
                Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
                MakeBoundary);

            Assert.True(result.Created);

            // Unrelated recordings unchanged.
            Assert.Equal(pre_u1_StartUT, unrelated1.StartUT);
            Assert.Equal(pre_u1_EndUT, unrelated1.EndUT);
            Assert.Equal(pre_u1_ChildBp, unrelated1.ChildBranchPointId);
            Assert.Equal(pre_u1_ParentBp, unrelated1.ParentBranchPointId);
            Assert.Equal(pre_u1_PointCount, unrelated1.Points.Count);

            Assert.Equal(pre_u2_StartUT, unrelated2.StartUT);
            Assert.Equal(pre_u2_EndUT, unrelated2.EndUT);
            Assert.Equal(pre_u2_ChildBp, unrelated2.ChildBranchPointId);
            Assert.Equal(pre_u2_ParentBp, unrelated2.ParentBranchPointId);
            Assert.Equal(pre_u2_PointCount, unrelated2.Points.Count);
        }

        // Fails if: the new recording does not pin the current format/schema
        // version constants. Phase A.1 bumped these to 1; future format bumps
        // re-trip this guard if anyone forgets to thread them through here.
        [Fact]
        public void CreateSwitchContinuationSegment_NewRecording_StampsFormatVersionAndGeneration()
        {
            var tree = MakeTree("tree-Fmt");
            string newRecId = Guid.NewGuid().ToString("N");

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parentRecordingIdOrNull: null,
                focusedVesselPersistentId: 1u, focusedVesselName: "V",
                switchUT: 0.0,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: newRecId, newBranchPointId: "ignored",
                initialBoundaryPointFactory: MakeBoundary);

            Assert.True(result.Created);
            Recording newRec = tree.Recordings[newRecId];
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion,
                newRec.RecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingSchemaGeneration,
                newRec.RecordingSchemaGeneration);
            // Concrete current values (format version 1; schema generation 2
            // after the controlled-child parent-anchor contract extension). Pin
            // numeric values so the test re-trips if anyone moves them.
            Assert.Equal(1, newRec.RecordingFormatVersion);
            Assert.Equal(2, newRec.RecordingSchemaGeneration);
        }

        // Fails if: a null tree is silently dereferenced (NullReferenceException)
        // or partially mutated before refusing. The helper must reject with
        // FailureReason="tree-null" so the live-side wrapper has a definite
        // refusal reason to surface in the diagnostic log.
        [Fact]
        public void CreateSwitchContinuationSegment_NullTree_RefusesWithFailureReason()
        {
            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree: null,
                parentRecordingIdOrNull: null,
                focusedVesselPersistentId: 1u,
                focusedVesselName: "V",
                switchUT: 0.0,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: Guid.NewGuid().ToString("N"),
                newBranchPointId: Guid.NewGuid().ToString("N"),
                initialBoundaryPointFactory: MakeBoundary);

            Assert.False(result.Created);
            Assert.Equal("tree-null", result.FailureReason);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("refused")
                && l.Contains("failureReason=tree-null"));
        }

        // Fails if: a null/empty newRecordingId is accepted and a recording
        // with empty id is inserted into the tree, or the helper crashes
        // instead of cleanly refusing with FailureReason="new-recording-id-missing".
        [Fact]
        public void CreateSwitchContinuationSegment_MissingNewRecordingId_RefusesWithFailureReason()
        {
            var tree = MakeTree("tree-MissingRecId");
            int preBranchPointCount = tree.BranchPoints.Count;
            int preRecordingCount = tree.Recordings.Count;
            string preActiveRecordingId = tree.ActiveRecordingId;

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parentRecordingIdOrNull: null,
                focusedVesselPersistentId: 1u,
                focusedVesselName: "V",
                switchUT: 0.0,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: null,
                newBranchPointId: Guid.NewGuid().ToString("N"),
                initialBoundaryPointFactory: MakeBoundary);

            Assert.False(result.Created);
            Assert.Equal("new-recording-id-missing", result.FailureReason);

            // Tree shape unchanged.
            Assert.Equal(preBranchPointCount, tree.BranchPoints.Count);
            Assert.Equal(preRecordingCount, tree.Recordings.Count);
            Assert.Equal(preActiveRecordingId, tree.ActiveRecordingId);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("refused")
                && l.Contains("failureReason=new-recording-id-missing"));
        }

        // Fails if: a null initialBoundaryPointFactory is invoked anyway
        // (NullReferenceException) or the helper inserts the new recording
        // without a boundary sample. The helper must reject with
        // FailureReason="boundary-factory-missing" before any mutation.
        [Fact]
        public void CreateSwitchContinuationSegment_MissingBoundaryFactory_RefusesWithFailureReason()
        {
            var tree = MakeTree("tree-MissingFactory");
            int preBranchPointCount = tree.BranchPoints.Count;
            int preRecordingCount = tree.Recordings.Count;
            string preActiveRecordingId = tree.ActiveRecordingId;

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parentRecordingIdOrNull: null,
                focusedVesselPersistentId: 1u,
                focusedVesselName: "V",
                switchUT: 0.0,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: Guid.NewGuid().ToString("N"),
                newBranchPointId: Guid.NewGuid().ToString("N"),
                initialBoundaryPointFactory: null);

            Assert.False(result.Created);
            Assert.Equal("boundary-factory-missing", result.FailureReason);

            Assert.Equal(preBranchPointCount, tree.BranchPoints.Count);
            Assert.Equal(preRecordingCount, tree.Recordings.Count);
            Assert.Equal(preActiveRecordingId, tree.ActiveRecordingId);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("refused")
                && l.Contains("failureReason=boundary-factory-missing"));
        }

        // Fails if: an attached creation (parent != null) is allowed to
        // proceed without a fresh newBranchPointId, which would later make
        // tree-walk lookups by branch-point id fail. The helper must reject
        // with FailureReason="new-branch-point-id-missing". Note: the guard
        // is scoped to the parent-attached path; standalone creation
        // (parent == null) skips this check because no branch point is
        // created.
        [Fact]
        public void CreateSwitchContinuationSegment_MissingBranchPointIdWithParent_RefusesWithFailureReason()
        {
            var tree = MakeTree("tree-MissingBpId");
            var parent = MakeRecording("parent-bp", tree.Id, 5u, "Parent", 50.0);
            tree.AddOrReplaceRecording(parent);
            tree.RootRecordingId = parent.RecordingId;
            tree.ActiveRecordingId = parent.RecordingId;

            int preBranchPointCount = tree.BranchPoints.Count;
            int preRecordingCount = tree.Recordings.Count;
            string preActiveRecordingId = tree.ActiveRecordingId;
            string preParentChildBp = parent.ChildBranchPointId;

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parent.RecordingId,
                focusedVesselPersistentId: 5u,
                focusedVesselName: "Parent",
                switchUT: 75.0,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: Guid.NewGuid().ToString("N"),
                newBranchPointId: null,
                initialBoundaryPointFactory: MakeBoundary);

            Assert.False(result.Created);
            Assert.Equal("new-branch-point-id-missing", result.FailureReason);

            // Tree shape unchanged + parent left intact.
            Assert.Equal(preBranchPointCount, tree.BranchPoints.Count);
            Assert.Equal(preRecordingCount, tree.Recordings.Count);
            Assert.Equal(preActiveRecordingId, tree.ActiveRecordingId);
            Assert.Equal(preParentChildBp, parent.ChildBranchPointId);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("refused")
                && l.Contains("failureReason=new-branch-point-id-missing"));
        }

        // Fails if: WalkToTerminalLeaves still adds a recording whose
        // ChildBranchPointId references a missing/empty branch point. The
        // creator rejects any non-empty ChildBranchPointId with
        // `parent-not-terminal-leaf`, so resolver and creator must agree —
        // dangling references signal tree corruption and must surface as a
        // Warn log, not be papered over.
        [Fact]
        public void ResolveSwitchContinuationParent_DanglingChildBranchPoint_LogsWarnAndSkips()
        {
            var tree = MakeTree("tree-Dangling");
            var rec = MakeRecording("rec-dangling", tree.Id, 42u, "Vessel", 10.0);
            const string missingBpId = "nonexistent-bp-id";
            rec.ChildBranchPointId = missingBpId;
            tree.AddOrReplaceRecording(rec);
            // Intentionally do NOT add a BranchPoint with this id.

            SwitchContinuationParentResolution res =
                SwitchSegmentBuilder.ResolveSwitchContinuationParent(tree, 42u);

            // Resolver must NOT add the dangling-ref recording to candidates,
            // and (with no other matches) returns NoMatchUseStandalone.
            Assert.Equal(SwitchContinuationParentStatus.NoMatchUseStandalone,
                res.Status);
            Assert.Null(res.TerminalLeafRecordingId);
            Assert.Empty(res.CandidateRecordingIds);

            // Warn line with the diagnostic payload must be emitted so the
            // tree-corruption signal surfaces in KSP.log.
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegmentResolver]")
                && l.Contains("dangling-childbranchpoint")
                && l.Contains("recordingId=" + rec.RecordingId)
                && l.Contains("missingBpId=" + missingBpId));
        }
    }
}
