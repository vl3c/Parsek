using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV1 UT monotonicity. Pure in-memory AnalyzerModel fixtures (no loader,
    // no disk), each test names the regression it guards.
    public class Inv1UtMonotonicTests
    {
        private static TrajectoryPoint P(double ut)
        {
            return new TrajectoryPoint { ut = ut, bodyName = "Kerbin" };
        }

        private static TrackSection Section(double startUT, double endUT, params double[] frameUts)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                frames = frameUts.Select(P).ToList(),
                checkpoints = new List<OrbitSegment>(),
            };
        }

        private static List<Finding> Run(Recording rec)
        {
            var model = new AnalyzerModel
            {
                SaveName = "inv1",
                Recordings = new List<Recording> { rec },
            };
            return new Inv1UtMonotonic().Evaluate(model).ToList();
        }

        // Guards: a monotonic recording with an EQUAL-UT structural-event snapshot
        // (two adjacent samples share a UT) -> zero findings. Fails if the check
        // has an off-by-one that flags equal UT as a back-step, which would
        // false-FAIL every dock/undock snapshot.
        [Fact]
        public void MonotonicWithEqualUtSnapshot_NoFindings()
        {
            var rec = new Recording
            {
                RecordingId = "rec-ok",
                Points = new List<TrajectoryPoint> { P(0), P(1), P(1), P(2) },
                TrackSections = new List<TrackSection>
                {
                    Section(0, 2, 0, 1, 1, 2),
                },
            };

            Assert.Empty(Run(rec));
        }

        // Guards: a back-stepping UT in the flat Points list -> FAIL. Fails if a
        // regression makes the rule accept descending UT, letting a corrupt
        // sidecar through that breaks TrajectoryMath's binary-search sampling.
        [Fact]
        public void BackSteppingPoints_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-back",
                Points = new List<TrajectoryPoint> { P(0), P(5), P(3) },
                TrackSections = new List<TrackSection>(),
            };

            List<Finding> findings = Run(rec);

            Finding fail = Assert.Single(findings);
            Assert.Equal(Inv1UtMonotonic.RuleIdConst, fail.RuleId);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Contains("seq=Points", fail.Message);
        }

        // Guards: a back-stepping UT inside a TrackSection's frames -> FAIL, cited
        // to the offending section index. Fails if section frames are not checked.
        [Fact]
        public void BackSteppingSectionFrames_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-sec",
                Points = new List<TrajectoryPoint>(),
                TrackSections = new List<TrackSection>
                {
                    Section(0, 10, 0, 4, 2),
                },
            };

            List<Finding> findings = Run(rec);

            Finding fail = Assert.Single(findings);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Contains("seq=frames", fail.Message);
            Assert.Equal(0, fail.SectionIndex);
        }

        // Guards: a section whose startUT > endUT -> FAIL. Fails if the span
        // ordering check is dropped (an inverted span breaks section dispatch).
        [Fact]
        public void SectionStartAfterEnd_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-span",
                Points = new List<TrajectoryPoint>(),
                TrackSections = new List<TrackSection>
                {
                    Section(20, 10, 20, 10),
                },
            };

            List<Finding> findings = Run(rec);

            Assert.Contains(findings, f =>
                f.Level == VerdictLevel.Fail && f.Message.Contains("seq=SectionSpan"));
        }

        // Guards: a NaN point UT -> FAIL. A NaN never trips the strict back-step
        // check (NaN < x is always false), so without an explicit IsNaN check a
        // NaN-poisoned sidecar would analyze GREEN and then break TrajectoryMath's
        // binary-search sampler at playback.
        [Fact]
        public void NaNPointUt_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-nan-pt",
                Points = new List<TrajectoryPoint> { P(0), P(double.NaN), P(2) },
                TrackSections = new List<TrackSection>(),
            };

            List<Finding> findings = Run(rec);

            Finding fail = Assert.Single(findings);
            Assert.Equal(Inv1UtMonotonic.RuleIdConst, fail.RuleId);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Contains("nan", fail.Message);
            Assert.Contains("seq=Points", fail.Message);
        }

        // Guards: a NaN section startUT -> FAIL. NaN > endUT is always false, so the
        // ordering check silently passes; the explicit IsNaN branch reports it.
        [Fact]
        public void NaNSectionStartUt_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-nan-span",
                Points = new List<TrajectoryPoint>(),
                TrackSections = new List<TrackSection>
                {
                    Section(double.NaN, 10, 0, 5),
                },
            };

            List<Finding> findings = Run(rec);

            Assert.Contains(findings, f =>
                f.Level == VerdictLevel.Fail
                && f.Message.Contains("nan")
                && f.Message.Contains("seq=SectionSpan"));
        }

        // Guards: a back-stepping checkpoint startUT sequence -> FAIL. Fails if
        // orbital-checkpoint sections are not covered.
        [Fact]
        public void BackSteppingCheckpoints_Fails()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 0,
                endUT = 100,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 40, endUT = 80, bodyName = "Kerbin" },
                    new OrbitSegment { startUT = 30, endUT = 60, bodyName = "Kerbin" },
                },
            };
            var rec = new Recording
            {
                RecordingId = "rec-cp",
                Points = new List<TrajectoryPoint>(),
                TrackSections = new List<TrackSection> { section },
            };

            List<Finding> findings = Run(rec);

            Assert.Contains(findings, f =>
                f.Level == VerdictLevel.Fail && f.Message.Contains("seq=checkpoints"));
        }
    }
}
