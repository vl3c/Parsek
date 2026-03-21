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
            MilestoneStore.SuppressLogging = true;
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
