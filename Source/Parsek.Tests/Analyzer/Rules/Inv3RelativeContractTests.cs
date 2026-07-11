using System.Collections.Generic;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV3 RELATIVE contract. Fixtures via DebrisFrameContractRecordingFixture +
    // TestBodyRegistry (plan section: INV3 fixture strategy). Body resolution is
    // injected as TestBodyRegistry.CreateBody, which builds an in-memory body
    // without mutating FlightGlobals statics, so no [Collection("Sequential")] is
    // needed. Each test names the regression it guards.
    public class Inv3RelativeContractTests
    {
        private static AnalyzerModel ModelWith(params Recording[] recs)
        {
            return new AnalyzerModel
            {
                SaveName = "inv3",
                Recordings = recs.ToList(),
                BodyResolver = name => TestBodyRegistry.CreateBody(name),
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv3RelativeContract().Evaluate(model).ToList();
        }

        // Guards (the documented RELATIVE-misread bug): a parent-anchored debris
        // fixture whose RELATIVE frames carry out-of-range values (metre offsets,
        // > 90 / > 180) with a valid anchorRecordingId -> zero INV3 findings.
        // Fails if the rule reads the offset fields as body-fixed lat/lon and
        // false-alarms, which is the exact production hazard INV3 exists to catch.
        [Fact]
        public void RelativeMetreOffsets_WithAnchor_NotFlagged()
        {
            var rec = new Recording
            {
                RecordingId = "rec-rel",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 0,
                        endUT = 10,
                        anchorRecordingId = "parent-rec",
                        frames = new List<TrajectoryPoint>
                        {
                            // Anchor-local metre offsets, deliberately out of any
                            // lat/lon range. Must NOT be flagged.
                            new TrajectoryPoint { ut = 0, latitude = 5000.0, longitude = -8000.0, altitude = 320.0, bodyName = "Kerbin" },
                            new TrajectoryPoint { ut = 10, latitude = 5200.0, longitude = -8100.0, altitude = 340.0, bodyName = "Kerbin" },
                        },
                    },
                },
            };

            Assert.Empty(Run(ModelWith(rec)));
        }

        // Guards: a real parent-anchored debris fixture (loop-anchored parent
        // variant: parent RELATIVE via anchorVesselId, debris RELATIVE via
        // anchorRecordingId) -> zero INV3 findings. Fails if either anchor form is
        // rejected or the RELATIVE frames are range-checked.
        [Fact]
        public void DebrisFrameContractFixture_LoopAnchored_Clean()
        {
            DebrisFrameContractRecordingFixture.Fixture fx =
                DebrisFrameContractRecordingFixture.Create(loopAnchoredParent: true);

            List<Finding> findings = Run(ModelWith(fx.Parent, fx.Debris));

            Assert.Empty(findings);
        }

        // Guards: a RELATIVE section with NEITHER anchorRecordingId NOR
        // anchorVesselId -> FAIL. Fails if an unanchored relative section passes,
        // which would make offset resolution silently unresolvable at playback.
        [Fact]
        public void RelativeWithNoAnchor_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-noanchor",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 0,
                        endUT = 10,
                        anchorRecordingId = null,
                        anchorVesselId = 0,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 0, latitude = 1.0, bodyName = "Kerbin" },
                        },
                    },
                },
            };

            List<Finding> findings = Run(ModelWith(rec));

            Finding fail = Assert.Single(findings);
            Assert.Equal(Inv3RelativeContract.RelativeRuleId, fail.RuleId);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Contains("error=no-anchor", fail.Message);
        }

        // Guards: an ABSOLUTE section with an out-of-range longitude (185) -> WARN
        // (INV3-ABSOLUTE-RANGE), NOT FAIL. Fails if a corrupt absolute longitude
        // is silently accepted, or if it is escalated to FAIL before KSP
        // normalization is cited.
        [Fact]
        public void AbsoluteOutOfRange_Warns_NotFails()
        {
            var rec = new Recording
            {
                RecordingId = "rec-abs",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 0,
                        endUT = 10,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 0, latitude = 10.0, longitude = 185.0, bodyName = "Kerbin" },
                        },
                    },
                },
            };

            List<Finding> findings = Run(ModelWith(rec));

            Finding warn = Assert.Single(findings);
            Assert.Equal(Inv3RelativeContract.AbsoluteRangeRuleId, warn.RuleId);
            Assert.Equal(VerdictLevel.Warn, warn.Level);
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
        }

        // Guards: an in-range ABSOLUTE section -> zero findings. Fails if the
        // range bounds are wrong (e.g. lon capped at 90 instead of 180).
        [Fact]
        public void AbsoluteInRange_NoFindings()
        {
            var rec = new Recording
            {
                RecordingId = "rec-abs-ok",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 0,
                        endUT = 10,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 0, latitude = -89.0, longitude = 179.0, bodyName = "Kerbin" },
                            new TrajectoryPoint { ut = 10, latitude = 45.0, longitude = -179.0, bodyName = "Kerbin" },
                        },
                    },
                },
            };

            Assert.Empty(Run(ModelWith(rec)));
        }
    }
}
