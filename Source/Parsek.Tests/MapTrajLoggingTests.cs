using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Log-assertion coverage for the MapTraj covering-segment diagnostic
    // (GhostMapPresence.LogMapCoveringSegmentChange). Verifies the body/covered/source
    // formatting, the GAP and StateVector branches, and - the regression guard for the
    // review fix - that a same-body segment->segment advance registers as a new on-change
    // line because the segment start UT is part of the on-change key.
    [Collection("Sequential")]
    public class MapTrajLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MapTrajLoggingTests()
        {
            ParsekLog.ResetTestOverrides();           // clears on-change + rate-limit state
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static OrbitSegment Seg(string body, double startUT, double endUT, double sma, double ecc)
        {
            return new OrbitSegment
            {
                bodyName = body,
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = sma,
                eccentricity = ecc,
            };
        }

        private int MapTrajCount(int member)
        {
            return logLines.FindAll(l =>
                l.Contains("[MapTraj]") && l.Contains("member=" + member)).Count;
        }

        [Fact]
        public void CoveredSegment_LogsBodyCoveredSourceAndSpan()
        {
            GhostMapPresence.LogMapCoveringSegmentChange(
                "FLIGHT", 30, 52569495.6,
                Seg("Kerbin", 52569494.7, 52569925.6, 671928, 0.088),
                segmentCoversEffUT: true, isStateVector: false, effectiveSegmentCount: 20);

            Assert.Contains(logLines, l =>
                l.Contains("[MapTraj]")
                && l.Contains("FLIGHT covering-segment CHANGED")
                && l.Contains("member=30")
                && l.Contains("body=Kerbin")
                && l.Contains("covered=True")
                && l.Contains("source=Segment")
                && l.Contains("sma=671928"));
        }

        [Fact]
        public void NoCoveringSegment_LogsGapAndUncovered()
        {
            GhostMapPresence.LogMapCoveringSegmentChange(
                "TS", 7, 1000.0, (OrbitSegment?)null,
                segmentCoversEffUT: false, isStateVector: false, effectiveSegmentCount: 0);

            Assert.Contains(logLines, l =>
                l.Contains("[MapTraj]")
                && l.Contains("TS covering-segment CHANGED")
                && l.Contains("body=GAP(no-segment)")
                && l.Contains("covered=False")
                && l.Contains("seg=n/a"));
        }

        [Fact]
        public void StateVectorSource_LogsStateVector()
        {
            GhostMapPresence.LogMapCoveringSegmentChange(
                "TS", 12, 2000.0, Seg("Kerbin", 1000, 3000, 700000, 0.0),
                segmentCoversEffUT: true, isStateVector: true, effectiveSegmentCount: 5);

            Assert.Contains(logLines, l => l.Contains("[MapTraj]") && l.Contains("source=StateVector"));
        }

        [Fact]
        public void SameBodySegmentAdvance_LogsAgain()
        {
            // Two consecutive same-body (Kerbin) covering segments differing only by their UT span.
            // The on-change key includes the segment start UT, so the advance must register as a new
            // line. Without that key component the second call would be suppressed and the
            // segment->segment transition would be invisible (the bug this diagnostic must catch).
            GhostMapPresence.LogMapCoveringSegmentChange(
                "FLIGHT", 30, 52569500,
                Seg("Kerbin", 52569494.7, 52569925.6, 671928, 0.088),
                segmentCoversEffUT: true, isStateVector: false, effectiveSegmentCount: 20);
            GhostMapPresence.LogMapCoveringSegmentChange(
                "FLIGHT", 30, 52569940,
                Seg("Kerbin", 52569937.8, 52569969.5, 671928, 0.088),
                segmentCoversEffUT: true, isStateVector: false, effectiveSegmentCount: 20);

            Assert.Equal(2, MapTrajCount(30));
            Assert.Contains(logLines, l => l.Contains("[52569494.7,52569925.6]"));
            Assert.Contains(logLines, l => l.Contains("[52569937.8,52569969.5]"));
        }

        [Fact]
        public void IdenticalCoveringSegmentRepeated_SuppressesSecond()
        {
            // Same covering segment twice in a row -> on-change coalesces the repeat (no per-frame churn).
            var seg = Seg("Kerbin", 1000, 2000, 700000, 0.0);
            GhostMapPresence.LogMapCoveringSegmentChange("FLIGHT", 99, 1500, seg, true, false, 3);
            GhostMapPresence.LogMapCoveringSegmentChange("FLIGHT", 99, 1600, seg, true, false, 3);

            Assert.Equal(1, MapTrajCount(99));
        }
    }
}
