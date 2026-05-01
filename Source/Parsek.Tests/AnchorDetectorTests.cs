using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure logic tests for AnchorDetector — no log capture.
    /// This class runs in parallel (no Collection attribute). It does NOT
    /// touch ParsekLog.SuppressLogging to avoid racing with Sequential tests.
    /// AnchorDetector methods log internally but we don't assert on logs here.
    /// </summary>
    public class AnchorDetectorTests
    {
        private static RecordingAnchorCandidate RecordingCandidate(
            string recordingId,
            double x,
            AnchorCandidateSource source = AnchorCandidateSource.Ghost,
            uint diagnosticPid = 0u,
            int ghostIndex = 0,
            bool isSealed = false,
            bool isSameReplayPoint = false,
            bool isSameVesselLineage = false)
        {
            return new RecordingAnchorCandidate(
                recordingId,
                new Vector3d(x, 0, 0),
                Quaternion.identity,
                source,
                diagnosticPid,
                ghostIndex,
                isSealed,
                isSameReplayPoint,
                isSameVesselLineage);
        }

        #region FindNearestAnchor -- No candidates

        [Fact]
        public void FindNearestAnchor_EmptyVesselList_ReturnsZeroPidAndMaxDistance()
        {
            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                new List<(uint, Vector3d)>(),
                null);

            Assert.Equal(0u, result.anchorPid);
            Assert.Equal(double.MaxValue, result.distance);
        }

        [Fact]
        public void FindNearestAnchor_NoVessels_ReturnsZeroPidAndMaxDistance()
        {
            // Alias test: explicit "no vessels" wording
            var result = AnchorDetector.FindNearestAnchor(
                42u,
                new Vector3d(100, 200, 300),
                new List<(uint, Vector3d)>(),
                new HashSet<uint>());

            Assert.Equal(0u, result.anchorPid);
            Assert.Equal(double.MaxValue, result.distance);
        }

        #endregion

        #region FindNearestAnchor -- Single vessel

        [Fact]
        public void FindNearestAnchor_SingleVesselWithinRange_ReturnsItsPidAndDistance()
        {
            var vessels = new List<(uint, Vector3d)>
            {
                (10u, new Vector3d(1000, 0, 0))
            };

            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                vessels,
                null);

            Assert.Equal(10u, result.anchorPid);
            Assert.Equal(1000.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestAnchor_SingleVesselOutOfRange_StillReturnsIt()
        {
            // FindNearestAnchor returns the nearest vessel regardless of range --
            // the caller (ShouldUseRelativeFrame) decides whether it's close enough
            var vessels = new List<(uint, Vector3d)>
            {
                (20u, new Vector3d(50000, 0, 0))
            };

            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                vessels,
                null);

            Assert.Equal(20u, result.anchorPid);
            Assert.Equal(50000.0, result.distance, 5);
        }

        #endregion

        #region FindNearestAnchor -- Multiple vessels

        [Fact]
        public void FindNearestAnchor_MultipleVessels_ReturnsNearest()
        {
            var vessels = new List<(uint, Vector3d)>
            {
                (10u, new Vector3d(3000, 0, 0)),  // 3000m
                (20u, new Vector3d(500, 0, 0)),    // 500m -- nearest
                (30u, new Vector3d(1500, 0, 0))    // 1500m
            };

            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                vessels,
                null);

            Assert.Equal(20u, result.anchorPid);
            Assert.Equal(500.0, result.distance, 5);
        }

        #endregion

        #region FindNearestAnchor -- Focused vessel excluded

        [Fact]
        public void FindNearestAnchor_FocusedVesselExcludedFromCandidates()
        {
            var vessels = new List<(uint, Vector3d)>
            {
                (1u, new Vector3d(0, 0, 0)),       // focused vessel itself -- must be skipped
                (20u, new Vector3d(2000, 0, 0))     // 2000m away
            };

            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                vessels,
                null);

            Assert.Equal(20u, result.anchorPid);
            Assert.Equal(2000.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestAnchor_OnlyFocusedVessel_ReturnsZero()
        {
            var vessels = new List<(uint, Vector3d)>
            {
                (1u, new Vector3d(0, 0, 0))
            };

            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                vessels,
                null);

            Assert.Equal(0u, result.anchorPid);
            Assert.Equal(double.MaxValue, result.distance);
        }

        #endregion

        #region FindNearestAnchor -- Tree vessel exclusion

        [Fact]
        public void FindNearestAnchor_TreeVesselsExcluded()
        {
            var vessels = new List<(uint, Vector3d)>
            {
                (10u, new Vector3d(100, 0, 0)),    // 100m but in tree
                (20u, new Vector3d(500, 0, 0)),    // 500m but in tree
                (30u, new Vector3d(2000, 0, 0))    // 2000m, NOT in tree -- only candidate
            };

            var treePids = new HashSet<uint> { 10u, 20u };

            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                vessels,
                treePids);

            Assert.Equal(30u, result.anchorPid);
            Assert.Equal(2000.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestAnchor_AllVesselsInTree_ReturnsZero()
        {
            var vessels = new List<(uint, Vector3d)>
            {
                (10u, new Vector3d(100, 0, 0)),
                (20u, new Vector3d(500, 0, 0))
            };

            var treePids = new HashSet<uint> { 10u, 20u };

            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                vessels,
                treePids);

            Assert.Equal(0u, result.anchorPid);
            Assert.Equal(double.MaxValue, result.distance);
        }

        [Fact]
        public void FindNearestAnchor_NullTreePids_NoExclusion()
        {
            var vessels = new List<(uint, Vector3d)>
            {
                (10u, new Vector3d(100, 0, 0)),
                (20u, new Vector3d(500, 0, 0))
            };

            var result = AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                vessels,
                null);

            Assert.Equal(10u, result.anchorPid);
            Assert.Equal(100.0, result.distance, 5);
        }

        #endregion

        #region FindNearestRecordingAnchor

        [Fact]
        public void TryCreateRecordingAnchorCandidate_ComputesPriorityMetadata()
        {
            var focus = new Recording
            {
                RecordingId = "focus",
                VesselPersistentId = 42u,
                ChainId = "chain-a",
                ParentBranchPointId = "rp-1",
                VesselName = "Upper Stage"
            };
            var anchor = new Recording
            {
                RecordingId = "anchor",
                VesselPersistentId = 42u,
                ChainId = "chain-a",
                ChildBranchPointId = "rp-1",
                VesselName = "Upper Stage",
                MergeState = MergeState.Immutable
            };

            bool created = AnchorDetector.TryCreateRecordingAnchorCandidate(
                focus,
                anchor,
                new Vector3d(1, 2, 3),
                Quaternion.identity,
                AnchorCandidateSource.Ghost,
                123u,
                7,
                out RecordingAnchorCandidate candidate);

            Assert.True(created);
            Assert.Equal("anchor", candidate.RecordingId);
            Assert.True(candidate.IsSealed);
            Assert.True(candidate.IsSameReplayPoint);
            Assert.True(candidate.IsSameVesselLineage);
            Assert.Equal(2, AnchorDetector.RecordingAnchorAffinityRank(candidate));
        }

        [Fact]
        public void TryCreateRecordingAnchorCandidate_RejectsLoopAnchoredCandidate()
        {
            var focus = new Recording { RecordingId = "focus" };
            var anchor = new Recording
            {
                RecordingId = "anchor",
                LoopAnchorVesselId = 99u
            };

            bool created = AnchorDetector.TryCreateRecordingAnchorCandidate(
                focus,
                anchor,
                Vector3d.zero,
                Quaternion.identity,
                AnchorCandidateSource.Live,
                99u,
                -1,
                out RecordingAnchorCandidate candidate);

            Assert.False(created);
            Assert.Null(candidate.RecordingId);
        }

        [Fact]
        public void TryCreateRecordingAnchorCandidate_RejectsFocusedRecording()
        {
            var focus = new Recording { RecordingId = "same" };
            var anchor = new Recording { RecordingId = "same" };

            bool created = AnchorDetector.TryCreateRecordingAnchorCandidate(
                focus,
                anchor,
                Vector3d.zero,
                Quaternion.identity,
                AnchorCandidateSource.Live,
                0u,
                -1,
                out _);

            Assert.False(created);
        }

        [Fact]
        public void FindNearestRecordingAnchor_EmptyCandidates_ReturnsNotFound()
        {
            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                new List<RecordingAnchorCandidate>());

            Assert.False(result.found);
            Assert.Equal(double.MaxValue, result.distance);
            Assert.Null(result.candidate.RecordingId);
        }

        [Fact]
        public void FindNearestRecordingAnchor_SealedCandidateBeatsNearerUnsealedCandidate()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("near-unsealed", 50.0, isSealed: false),
                RecordingCandidate("far-sealed", 500.0, isSealed: true)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("far-sealed", result.candidate.RecordingId);
            Assert.Equal(500.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_RangeFilterRunsBeforePriority()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("near-unsealed", 1000.0),
                RecordingCandidate("far-sealed", 3000.0, isSealed: true)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates,
                AnchorDetector.RelativeFrameRangeLimit(currentlyRelative: false));

            Assert.True(result.found);
            Assert.Equal("near-unsealed", result.candidate.RecordingId);
            Assert.Equal(1000.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_SameReplayPointBeatsSlightlyNearerGenericCandidate()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("generic", 80.0),
                RecordingCandidate("same-rp", 125.0, isSameReplayPoint: true)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("same-rp", result.candidate.RecordingId);
            Assert.Equal(125.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_SameVesselLineageBeatsSlightlyNearerGenericCandidate()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("generic", 80.0),
                RecordingCandidate("same-vessel", 125.0, isSameVesselLineage: true)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("same-vessel", result.candidate.RecordingId);
            Assert.Equal(125.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_DistanceBreaksTieInsideSamePriorityClass()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("far", 300.0, isSealed: true, isSameReplayPoint: true),
                RecordingCandidate("near", 100.0, isSealed: true, isSameReplayPoint: true)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("near", result.candidate.RecordingId);
            Assert.Equal(100.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_RecordingIdTieBreaksEqualRankAndDistance()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("beta", -100.0),
                RecordingCandidate("alpha", 100.0)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("alpha", result.candidate.RecordingId);
            Assert.Equal(100.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_SkipsFocusedRecordingId()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("focus", 10.0),
                RecordingCandidate("other", 200.0)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("other", result.candidate.RecordingId);
            Assert.Equal(200.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_SkipsSameNonzeroDiagnosticPidAsFocusedVessel()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("self-pid", 10.0, diagnosticPid: 42u),
                RecordingCandidate("other", 200.0, diagnosticPid: 99u)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                42u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("other", result.candidate.RecordingId);
            Assert.Equal(200.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_SkipsNullAndEmptyRecordingIds()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate(null, 10.0),
                RecordingCandidate(string.Empty, 20.0),
                RecordingCandidate("valid", 200.0)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("valid", result.candidate.RecordingId);
            Assert.Equal(200.0, result.distance, 5);
        }

        [Fact]
        public void FindNearestRecordingAnchor_SourceTieBreakIsDeterministic()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("same", 100.0, source: AnchorCandidateSource.Ghost, ghostIndex: 0),
                RecordingCandidate("same", 100.0, source: AnchorCandidateSource.Live, ghostIndex: -1)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("same", result.candidate.RecordingId);
            Assert.Equal(AnchorCandidateSource.Live, result.candidate.Source);
        }

        [Fact]
        public void FindNearestRecordingAnchor_GhostIndexTieBreakIsDeterministicAndIgnoresPid()
        {
            var candidates = new List<RecordingAnchorCandidate>
            {
                RecordingCandidate("same", 100.0, ghostIndex: 4, diagnosticPid: 10u),
                RecordingCandidate("same", 100.0, ghostIndex: 2, diagnosticPid: 999u)
            };

            var result = AnchorDetector.FindNearestRecordingAnchor(
                "focus",
                1u,
                new Vector3d(0, 0, 0),
                candidates);

            Assert.True(result.found);
            Assert.Equal("same", result.candidate.RecordingId);
            Assert.Equal(2, result.candidate.GhostIndex);
            Assert.Equal(999u, result.candidate.DiagnosticPid);
        }

        #endregion

        #region ShouldUseRelativeFrame -- Entry (not currently relative)

        [Fact]
        public void ShouldUseRelativeFrame_NotRelative_InsideEntry_ReturnsTrue()
        {
            Assert.True(AnchorDetector.ShouldUseRelativeFrame(2000.0, false));
        }

        [Fact]
        public void ShouldUseRelativeFrame_NotRelative_AtEntryBoundary_ReturnsFalse()
        {
            // 2300m is the boundary -- not strictly less than, so returns false
            Assert.False(AnchorDetector.ShouldUseRelativeFrame(2300.0, false));
        }

        [Fact]
        public void ShouldUseRelativeFrame_NotRelative_BeyondEntry_ReturnsFalse()
        {
            Assert.False(AnchorDetector.ShouldUseRelativeFrame(3000.0, false));
        }

        [Fact]
        public void ShouldUseRelativeFrame_NotRelative_JustInsideEntry_ReturnsTrue()
        {
            Assert.True(AnchorDetector.ShouldUseRelativeFrame(2299.9, false));
        }

        #endregion

        #region ShouldUseRelativeFrame -- Exit (currently relative)

        [Fact]
        public void ShouldUseRelativeFrame_Relative_InsideExit_ReturnsTrue()
        {
            Assert.True(AnchorDetector.ShouldUseRelativeFrame(2000.0, true));
        }

        [Fact]
        public void ShouldUseRelativeFrame_Relative_AtExitBoundary_ReturnsFalse()
        {
            // 2500m is the exit boundary -- not strictly less than, so returns false
            Assert.False(AnchorDetector.ShouldUseRelativeFrame(2500.0, true));
        }

        [Fact]
        public void ShouldUseRelativeFrame_Relative_BeyondExit_ReturnsFalse()
        {
            Assert.False(AnchorDetector.ShouldUseRelativeFrame(3000.0, true));
        }

        [Fact]
        public void ShouldUseRelativeFrame_Relative_JustInsideExit_ReturnsTrue()
        {
            Assert.True(AnchorDetector.ShouldUseRelativeFrame(2499.9, true));
        }

        #endregion

        #region ShouldUseRelativeFrame -- Hysteresis gap (2300-2500m)

        [Fact]
        public void ShouldUseRelativeFrame_HysteresisGap_NotRelative_ReturnsFalse()
        {
            // 2400m is between entry (2300) and exit (2500) thresholds:
            // If not currently relative, 2400 >= 2300 so we don't enter
            Assert.False(AnchorDetector.ShouldUseRelativeFrame(2400.0, false));
        }

        [Fact]
        public void ShouldUseRelativeFrame_HysteresisGap_AlreadyRelative_ReturnsTrue()
        {
            // 2400m is between entry (2300) and exit (2500) thresholds:
            // If already relative, 2400 < 2500 so we stay relative
            Assert.True(AnchorDetector.ShouldUseRelativeFrame(2400.0, true));
        }

        #endregion

        #region IsInDockingApproach

        [Fact]
        public void IsInDockingApproach_InsideRange_ReturnsTrue()
        {
            Assert.True(AnchorDetector.IsInDockingApproach(100.0));
        }

        [Fact]
        public void IsInDockingApproach_AtBoundary_ReturnsFalse()
        {
            Assert.False(AnchorDetector.IsInDockingApproach(200.0));
        }

        [Fact]
        public void IsInDockingApproach_BeyondRange_ReturnsFalse()
        {
            Assert.False(AnchorDetector.IsInDockingApproach(500.0));
        }

        [Fact]
        public void IsInDockingApproach_ZeroDistance_ReturnsTrue()
        {
            Assert.True(AnchorDetector.IsInDockingApproach(0.0));
        }

        #endregion

        #region Constants consistency

        [Fact]
        public void Constants_EntryLessThanExit()
        {
            Assert.True(AnchorDetector.RelativeEntryDistance < AnchorDetector.RelativeExitDistance);
        }

        [Fact]
        public void Constants_DockingLessThanEntry()
        {
            Assert.True(AnchorDetector.DockingApproachDistance < AnchorDetector.RelativeEntryDistance);
        }

        [Fact]
        public void Constants_HysteresisGapIs200m()
        {
            double gap = AnchorDetector.RelativeExitDistance - AnchorDetector.RelativeEntryDistance;
            Assert.Equal(200.0, gap, 5);
        }

        [Fact]
        public void Constants_EntryMatchesPhysicsBubble()
        {
            // Entry distance should match ProximityRateSelector's physics bubble
            Assert.Equal(ProximityRateSelector.PhysicsBubble, AnchorDetector.RelativeEntryDistance);
        }

        [Fact]
        public void Constants_DockingMatchesProximityDockingRange()
        {
            // Docking approach distance should match ProximityRateSelector's docking range
            Assert.Equal(ProximityRateSelector.DockingRange, AnchorDetector.DockingApproachDistance);
        }

        #endregion
    }

    [Collection("Sequential")]
    public class AnchorDetectorLoggingTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AnchorDetectorLoggingTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        [Fact]
        public void FindNearestAnchor_LogsWhenAnchorFound()
        {
            logLines.Clear();
            var vessels = new List<(uint, Vector3d)>
            {
                (10u, new Vector3d(1000, 0, 0))
            };

            AnchorDetector.FindNearestAnchor(1u, new Vector3d(0, 0, 0), vessels, null);

            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]") &&
                l.Contains("anchorPid=10") &&
                l.Contains("1000.0m"));
        }

        [Fact]
        public void FindNearestAnchor_NoLogWhenNoAnchor()
        {
            logLines.Clear();
            AnchorDetector.FindNearestAnchor(
                1u,
                new Vector3d(0, 0, 0),
                new List<(uint, Vector3d)>(),
                null);

            Assert.DoesNotContain(logLines, l => l.Contains("[Anchor]"));
        }

        [Fact]
        public void ShouldUseRelativeFrame_LogsRelativeEntry()
        {
            logLines.Clear();
            AnchorDetector.ShouldUseRelativeFrame(2000.0, false);

            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]") &&
                l.Contains("RELATIVE entry"));
        }

        [Fact]
        public void ShouldUseRelativeFrame_LogsRelativeExit()
        {
            logLines.Clear();
            AnchorDetector.ShouldUseRelativeFrame(3000.0, true);

            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]") &&
                l.Contains("RELATIVE exit"));
        }

        [Fact]
        public void ShouldUseRelativeFrame_NoLogWhenStayingAbsolute()
        {
            logLines.Clear();
            AnchorDetector.ShouldUseRelativeFrame(5000.0, false);

            Assert.DoesNotContain(logLines, l => l.Contains("RELATIVE"));
        }

        [Fact]
        public void ShouldUseRelativeFrame_NoLogWhenStayingRelative()
        {
            logLines.Clear();
            AnchorDetector.ShouldUseRelativeFrame(1000.0, true);

            Assert.DoesNotContain(logLines, l => l.Contains("RELATIVE entry") || l.Contains("RELATIVE exit"));
        }

        [Fact]
        public void IsInDockingApproach_LogsWhenDetected()
        {
            logLines.Clear();
            AnchorDetector.IsInDockingApproach(100.0);

            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]") &&
                l.Contains("Docking approach detected"));
        }

        [Fact]
        public void IsInDockingApproach_NoLogWhenOutOfRange()
        {
            logLines.Clear();
            AnchorDetector.IsInDockingApproach(500.0);

            Assert.DoesNotContain(logLines, l => l.Contains("Docking approach"));
        }
    }
}
