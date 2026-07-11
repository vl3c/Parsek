using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV2 no double-cover. Each test names the regression it guards.
    // Pure in-memory AnalyzerModel fixtures (no loader, no disk), so no shared
    // static state and no [Collection("Sequential")] is needed.
    public class Inv2NoDoubleCoverTests
    {
        private static TrackSection Section(double startUT, double endUT)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
            };
        }

        private static AnalyzerModel ModelWith(Recording rec)
        {
            return new AnalyzerModel
            {
                SaveName = "inv2",
                Recordings = new List<Recording> { rec },
            };
        }

        private static List<Finding> Run(Recording rec)
        {
            return new Inv2NoDoubleCover().Evaluate(ModelWith(rec)).ToList();
        }

        // Guards: an orbit-bridged gap between two authored sections is legitimate
        // and emits NO finding. A regression that reverted to a "no gaps allowed"
        // rule would false-alarm on every BG on-rails span that a coast bridges.
        [Fact]
        public void OrbitBridgedGap_NoFinding()
        {
            var rec = new Recording
            {
                RecordingId = "rec-bridged",
                TrackSections = new List<TrackSection>
                {
                    Section(0, 100),
                    Section(200, 300),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 200, bodyName = "Kerbin" },
                },
            };

            List<Finding> findings = Run(rec);

            Assert.Empty(findings);
        }

        // Guards: two sections overlapping in interior UT ([100,200] and [150,250])
        // -> FAIL. A regression where the overlap detector misses interior overlap
        // would let double-covered (ambiguous playback position) UT through.
        [Fact]
        public void InteriorOverlap_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-overlap",
                TrackSections = new List<TrackSection>
                {
                    Section(100, 200),
                    Section(150, 250),
                },
            };

            List<Finding> findings = Run(rec);

            Finding fail = Assert.Single(findings);
            Assert.Equal(Inv2NoDoubleCover.OverlapRuleId, fail.RuleId);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Equal("rec-overlap", fail.Target);
        }

        // Guards: containment (a section fully inside another) is also interior
        // overlap and must FAIL. The running-max sweep, not a naive
        // adjacent-pair check, is what catches this.
        [Fact]
        public void ContainedSection_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-contained",
                TrackSections = new List<TrackSection>
                {
                    Section(0, 500),
                    Section(100, 200),
                },
            };

            List<Finding> findings = Run(rec);

            Assert.Contains(findings, f =>
                f.RuleId == Inv2NoDoubleCover.OverlapRuleId && f.Level == VerdictLevel.Fail);
        }

        // Guards: an uncovered gap with no bridging OrbitSegment -> WARN
        // (INV2-UNCOVERED-SPAN), never FAIL. A regression escalating this to FAIL
        // would red-flag every legitimate on-rails BG gap.
        [Fact]
        public void UncoveredGap_Warns_NotFails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-gap",
                TrackSections = new List<TrackSection>
                {
                    Section(0, 100),
                    Section(200, 300),
                },
            };

            List<Finding> findings = Run(rec);

            Finding warn = Assert.Single(findings);
            Assert.Equal(Inv2NoDoubleCover.UncoveredRuleId, warn.RuleId);
            Assert.Equal(VerdictLevel.Warn, warn.Level);
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
        }

        // Guards: sections that touch exactly at a UT boundary (a.end == b.start)
        // are NOT an overlap and NOT a gap. A regression using a non-strict
        // comparison would false-FAIL every adjacent optimizer split.
        [Fact]
        public void TouchingSections_NoFinding()
        {
            var rec = new Recording
            {
                RecordingId = "rec-touch",
                TrackSections = new List<TrackSection>
                {
                    Section(0, 100),
                    Section(100, 200),
                },
            };

            List<Finding> findings = Run(rec);

            Assert.Empty(findings);
        }
    }
}
