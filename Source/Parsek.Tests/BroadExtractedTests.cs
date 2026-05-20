using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for the pure helpers extracted in the broad
    /// behavior-preserving extract-method pass:
    /// RecordingStore.ChooseSplitCandidateIndex / ShouldMoveChildBranchPointToSplitSecondHalf,
    /// RecordingTree.ClearRejectedRecordingReferences,
    /// GhostVisualBuilder.ComputeHeatColorRamp, and
    /// FlightRecorder.ClassifyAeroEventName.
    /// </summary>
    [Collection("Sequential")]
    public class BroadExtractedTests : IDisposable
    {
        public BroadExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region RecordingStore.ChooseSplitCandidateIndex

        [Fact]
        public void ChooseSplitCandidateIndex_NoDeferredId_ReturnsFirstCandidate()
        {
            var candidates = new List<(int, int)> { (2, 0), (5, 1) };
            var recordings = new List<Recording>
            {
                null, null, new Recording { RecordingId = "a" }, null, null,
                new Recording { RecordingId = "b" }
            };

            int chosen = RecordingStore.ChooseSplitCandidateIndex(
                candidates, recordings, deferredActiveReFlyId: null, out int deferredObserved);

            Assert.Equal(0, chosen);
            Assert.Equal(0, deferredObserved);
        }

        [Fact]
        public void ChooseSplitCandidateIndex_EmptyDeferredId_ReturnsFirstCandidate()
        {
            var candidates = new List<(int, int)> { (0, 0) };
            var recordings = new List<Recording> { new Recording { RecordingId = "a" } };

            int chosen = RecordingStore.ChooseSplitCandidateIndex(
                candidates, recordings, deferredActiveReFlyId: "", out int deferredObserved);

            Assert.Equal(0, chosen);
            Assert.Equal(0, deferredObserved);
        }

        [Fact]
        public void ChooseSplitCandidateIndex_DeferredIdSkipsThatCandidate()
        {
            // Candidate 0 points at the deferred recording; candidate 1 is eligible.
            var candidates = new List<(int, int)> { (0, 0), (1, 0) };
            var recordings = new List<Recording>
            {
                new Recording { RecordingId = "deferred" },
                new Recording { RecordingId = "other" }
            };

            int chosen = RecordingStore.ChooseSplitCandidateIndex(
                candidates, recordings, deferredActiveReFlyId: "deferred", out int deferredObserved);

            Assert.Equal(1, chosen);          // candidate-list index of the non-deferred candidate
            Assert.Equal(1, deferredObserved); // one deferred candidate was skipped
        }

        [Fact]
        public void ChooseSplitCandidateIndex_AllCandidatesDeferred_ReturnsMinusOne()
        {
            var candidates = new List<(int, int)> { (0, 0), (1, 0) };
            var recordings = new List<Recording>
            {
                new Recording { RecordingId = "deferred" },
                new Recording { RecordingId = "deferred" }
            };

            int chosen = RecordingStore.ChooseSplitCandidateIndex(
                candidates, recordings, deferredActiveReFlyId: "deferred", out int deferredObserved);

            Assert.Equal(-1, chosen);
            Assert.Equal(2, deferredObserved);
        }

        [Fact]
        public void ChooseSplitCandidateIndex_OutOfRangeCandidateIndex_Skipped()
        {
            // candidate 0 has an out-of-range index (continue); candidate 1 is in range,
            // not deferred -> chosen. Out-of-range candidates are not counted as deferred.
            var candidates = new List<(int, int)> { (99, 0), (1, 0) };
            var recordings = new List<Recording>
            {
                new Recording { RecordingId = "a" },
                new Recording { RecordingId = "c" }
            };

            int chosen = RecordingStore.ChooseSplitCandidateIndex(
                candidates, recordings, deferredActiveReFlyId: "deferred", out int deferredObserved);

            Assert.Equal(1, chosen);
            Assert.Equal(0, deferredObserved);
        }

        [Fact]
        public void ChooseSplitCandidateIndex_NullCandidateRecording_IsChosenNotDeferred()
        {
            // A candidate whose recording is null fails the deferred-match guard (which
            // short-circuits on null) and is therefore CHOSEN, not skipped or counted.
            var candidates = new List<(int, int)> { (0, 0), (1, 0) };
            var recordings = new List<Recording>
            {
                null,
                new Recording { RecordingId = "c" }
            };

            int chosen = RecordingStore.ChooseSplitCandidateIndex(
                candidates, recordings, deferredActiveReFlyId: "deferred", out int deferredObserved);

            Assert.Equal(0, chosen);
            Assert.Equal(0, deferredObserved);
        }

        #endregion

        #region RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf

        private static RecordingTree TreeWithBranchPoint(string treeId, string bpId, double bpUT)
        {
            var tree = new RecordingTree { Id = treeId };
            tree.BranchPoints.Add(new BranchPoint { Id = bpId, UT = bpUT });
            return tree;
        }

        [Fact]
        public void ShouldMove_NullTreeId_ReturnsFalse()
        {
            Assert.False(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                null, "bp", 100.0));
        }

        [Fact]
        public void ShouldMove_NullBranchPointId_ReturnsFalse()
        {
            Assert.False(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                "tree", null, 100.0));
        }

        [Fact]
        public void ShouldMove_NaNSecondStartUT_ReturnsFalse()
        {
            RecordingStore.AddCommittedTreeInternal(TreeWithBranchPoint("tree", "bp", 100.0));
            Assert.False(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                "tree", "bp", double.NaN));
        }

        [Fact]
        public void ShouldMove_InfinitySecondStartUT_ReturnsFalse()
        {
            RecordingStore.AddCommittedTreeInternal(TreeWithBranchPoint("tree", "bp", 100.0));
            Assert.False(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                "tree", "bp", double.PositiveInfinity));
        }

        [Fact]
        public void ShouldMove_BranchPointAfterSplitUT_ReturnsTrue()
        {
            // BP at UT 170, split's second half starts at UT 116 -> BP belongs to the
            // second half. (The Re-Fly atmo/exo-after-staging-branch case.)
            RecordingStore.AddCommittedTreeInternal(TreeWithBranchPoint("tree", "bp", 170.0));
            Assert.True(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                "tree", "bp", 116.0));
        }

        [Fact]
        public void ShouldMove_BranchPointBeforeSplitUT_ReturnsFalse()
        {
            // BP at UT 90, split's second half starts at UT 116 -> BP stays on the first half.
            RecordingStore.AddCommittedTreeInternal(TreeWithBranchPoint("tree", "bp", 90.0));
            Assert.False(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                "tree", "bp", 116.0));
        }

        [Fact]
        public void ShouldMove_BranchPointAtSplitUTWithinEpsilon_ReturnsTrue()
        {
            // bp.UT >= secondStartUT - eps (eps = 0.0001). Exactly equal counts as "second".
            RecordingStore.AddCommittedTreeInternal(TreeWithBranchPoint("tree", "bp", 116.0));
            Assert.True(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                "tree", "bp", 116.0));
        }

        [Fact]
        public void ShouldMove_UnknownTreeId_ReturnsFalse()
        {
            RecordingStore.AddCommittedTreeInternal(TreeWithBranchPoint("tree", "bp", 170.0));
            Assert.False(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                "other-tree", "bp", 116.0));
        }

        [Fact]
        public void ShouldMove_UnknownBranchPointInMatchedTree_ReturnsFalse()
        {
            RecordingStore.AddCommittedTreeInternal(TreeWithBranchPoint("tree", "bp", 170.0));
            Assert.False(RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                "tree", "missing-bp", 116.0));
        }

        #endregion

        #region RecordingTree.ClearRejectedRecordingReferences

        [Fact]
        public void ClearRejectedRefs_NullRecording_NoThrow()
        {
            var rejected = new HashSet<string>(StringComparer.Ordinal) { "x" };
            var removedBps = new HashSet<string>(StringComparer.Ordinal) { "bp" };
            // Should short-circuit silently on a null recording.
            RecordingTree.ClearRejectedRecordingReferences(null, rejected, removedBps);
        }

        [Fact]
        public void ClearRejectedRefs_NullRemovedBranchPoints_StillClearsRecordingRefs()
        {
            var rec = new Recording
            {
                ParentRecordingId = "rejected",
                DebrisParentRecordingId = "rejected",
                ParentBranchPointId = "bp",
                ChildBranchPointId = "bp"
            };
            var rejected = new HashSet<string>(StringComparer.Ordinal) { "rejected" };

            RecordingTree.ClearRejectedRecordingReferences(rec, rejected, removedBranchPointIds: null);

            // Recording-id refs cleared; the branch-point clears short-circuit on null set.
            Assert.Null(rec.ParentRecordingId);
            Assert.Null(rec.DebrisParentRecordingId);
            Assert.Equal("bp", rec.ParentBranchPointId);
            Assert.Equal("bp", rec.ChildBranchPointId);
        }

        [Fact]
        public void ClearRejectedRefs_EmptyRemovedBranchPoints_ShortCircuitsBranchPointClears()
        {
            var rec = new Recording
            {
                ParentBranchPointId = "bp",
                ChildBranchPointId = "bp"
            };
            var rejected = new HashSet<string>(StringComparer.Ordinal) { "rejected" };
            var removedBps = new HashSet<string>(StringComparer.Ordinal); // empty

            RecordingTree.ClearRejectedRecordingReferences(rec, rejected, removedBps);

            Assert.Equal("bp", rec.ParentBranchPointId);
            Assert.Equal("bp", rec.ChildBranchPointId);
        }

        [Fact]
        public void ClearRejectedRefs_NormalClear_ClearsAllFourFieldsIndependently()
        {
            var rec = new Recording
            {
                ParentRecordingId = "rejectedRec",
                DebrisParentRecordingId = "rejectedRec",
                ParentBranchPointId = "removedBp",
                ChildBranchPointId = "removedBp"
            };
            var rejected = new HashSet<string>(StringComparer.Ordinal) { "rejectedRec" };
            var removedBps = new HashSet<string>(StringComparer.Ordinal) { "removedBp" };

            RecordingTree.ClearRejectedRecordingReferences(rec, rejected, removedBps);

            Assert.Null(rec.ParentRecordingId);
            Assert.Null(rec.DebrisParentRecordingId);
            Assert.Null(rec.ParentBranchPointId);
            Assert.Null(rec.ChildBranchPointId);
        }

        [Fact]
        public void ClearRejectedRefs_NonRejectedRefs_Preserved()
        {
            var rec = new Recording
            {
                ParentRecordingId = "keepRec",
                DebrisParentRecordingId = "keepRec",
                ParentBranchPointId = "keepBp",
                ChildBranchPointId = "keepBp"
            };
            var rejected = new HashSet<string>(StringComparer.Ordinal) { "otherRec" };
            var removedBps = new HashSet<string>(StringComparer.Ordinal) { "otherBp" };

            RecordingTree.ClearRejectedRecordingReferences(rec, rejected, removedBps);

            Assert.Equal("keepRec", rec.ParentRecordingId);
            Assert.Equal("keepRec", rec.DebrisParentRecordingId);
            Assert.Equal("keepBp", rec.ParentBranchPointId);
            Assert.Equal("keepBp", rec.ChildBranchPointId);
        }

        #endregion

        #region GhostVisualBuilder.ComputeHeatColorRamp

        [Fact]
        public void ComputeHeatColorRamp_NoColorNoEmissive_HotEqualsColdEmissionBlack()
        {
            var cold = new Color(0.3f, 0.4f, 0.5f, 1f);

            var (hot, medium, hotEmission, mediumEmission) =
                GhostVisualBuilder.ComputeHeatColorRamp(cold, hasColorProperty: false, hasEmissiveProperty: false);

            // No color property -> hot color stays the cold color, and the medium midpoint
            // between cold and an identical hot is also the cold color.
            Assert.Equal(cold, hot);
            Assert.Equal(cold, medium);
            // No emissive property -> emission stays black at both stops.
            Assert.Equal(Color.black, hotEmission);
            Assert.Equal(Color.black, mediumEmission);
        }

        [Fact]
        public void ComputeHeatColorRamp_HasColor_HotLerpsTowardHeatTint()
        {
            var cold = Color.white;

            var (hot, medium, _, _) =
                GhostVisualBuilder.ComputeHeatColorRamp(cold, hasColorProperty: true, hasEmissiveProperty: false);

            // hot = Lerp(white, HeatTint(1, 0.45, 0.2), 0.45). White stays 1 on R;
            // G and B move toward the tint, so hot is distinct from cold and the medium
            // midpoint sits between them.
            Assert.NotEqual(cold, hot);
            Assert.True(hot.g < cold.g, "hot green should move toward the heat tint");
            Assert.True(hot.b < cold.b, "hot blue should move toward the heat tint");
            // medium = Lerp(cold, hot, 0.5) -> green strictly between cold and hot.
            Assert.True(medium.g < cold.g && medium.g > hot.g,
                $"medium green {medium.g} should lie between hot {hot.g} and cold {cold.g}");
        }

        [Fact]
        public void ComputeHeatColorRamp_HasEmissive_HotEmissionIsHeatEmissionColor()
        {
            var cold = Color.white;

            var (_, _, hotEmission, mediumEmission) =
                GhostVisualBuilder.ComputeHeatColorRamp(cold, hasColorProperty: true, hasEmissiveProperty: true);

            // hotEmission = HeatEmissionColor (1.5, 0.6, 0.15, 1).
            Assert.Equal(1.5f, hotEmission.r, 1e-4f);
            Assert.Equal(0.6f, hotEmission.g, 1e-4f);
            Assert.Equal(0.15f, hotEmission.b, 1e-4f);
            // mediumEmission = Lerp(black, hotEmission, 0.5) -> half of hotEmission per channel.
            Assert.Equal(hotEmission.r * 0.5f, mediumEmission.r, 1e-4f);
            Assert.Equal(hotEmission.g * 0.5f, mediumEmission.g, 1e-4f);
            Assert.Equal(hotEmission.b * 0.5f, mediumEmission.b, 1e-4f);
        }

        [Fact]
        public void ComputeHeatColorRamp_ColorOnly_EmissionStaysBlack()
        {
            var cold = new Color(0.2f, 0.2f, 0.2f, 1f);

            var (_, _, hotEmission, mediumEmission) =
                GhostVisualBuilder.ComputeHeatColorRamp(cold, hasColorProperty: true, hasEmissiveProperty: false);

            Assert.Equal(Color.black, hotEmission);
            Assert.Equal(Color.black, mediumEmission);
        }

        #endregion

        #region FlightRecorder.ClassifyAeroEventName

        [Theory]
        [InlineData("deploy", "", true, false)]
        [InlineData("", "extend ladder", true, false)]
        [InlineData("openshield", "", true, false)]
        [InlineData("toggle brake", "", true, false)]
        [InlineData("enable", "", true, false)]
        [InlineData("retract", "", false, true)]
        [InlineData("", "close bay", false, true)]
        [InlineData("stow", "", false, true)]
        [InlineData("disable", "", false, true)]
        [InlineData("toggle", "", false, false)]
        [InlineData("", "", false, false)]
        public void ClassifyAeroEventName_PinsKeywordClassification(
            string evtName, string guiName, bool expectDeploy, bool expectRetract)
        {
            FlightRecorder.ClassifyAeroEventName(
                evtName, guiName, out bool isDeploy, out bool isRetract);

            Assert.Equal(expectDeploy, isDeploy);
            Assert.Equal(expectRetract, isRetract);
        }

        [Fact]
        public void ClassifyAeroEventName_GuiNameAlsoMatched()
        {
            // The gui name participates in matching just like the event name.
            FlightRecorder.ClassifyAeroEventName(
                "toggle", "deploy airbrake", out bool isDeploy, out bool isRetract);

            Assert.True(isDeploy);    // deploy + brake both in gui name
            Assert.False(isRetract);
        }

        [Fact]
        public void ClassifyAeroEventName_DeployAndRetractKeywordsBothPresent_BothSet()
        {
            // Independent classification: an event naming both stays both true.
            FlightRecorder.ClassifyAeroEventName(
                "deploy", "retract", out bool isDeploy, out bool isRetract);

            Assert.True(isDeploy);
            Assert.True(isRetract);
        }

        #endregion
    }
}
