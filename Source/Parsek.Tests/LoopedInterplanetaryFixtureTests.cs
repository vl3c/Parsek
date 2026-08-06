using System.Collections.Generic;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless pins for the looped-interplanetary corpus (Tier 1 of the
    /// arrival-validation lane) and for the pure cross-body seam enumerator
    /// the in-game SoiCrossingPlayback cells gate on. The fixture's segment
    /// chain is the REAL flown duna-direct geometry byte-pinned from the
    /// committed duna-direct-recorded fixture; these cells red on any drift
    /// in either the generator or the enumerator before a KSP flight can.
    /// </summary>
    public class LoopedInterplanetaryFixtureTests
    {
        private static Recording BuildRecording()
        {
            RecordingBuilder builder = LoopedInterplanetaryFixture.BuildRecording();
            var rec = new Recording();
            // BuildV3Metadata carries the schema-generation stamp the codec's
            // compatibility gate requires (plain Build() is the legacy inline
            // shape and loads only up to the gate).
            RecordingTreeRecordCodec.LoadRecordingFrom(builder.BuildV3Metadata(), rec);
            RecordingStore.DeserializeTrajectoryFrom(
                builder.BuildTrajectoryNode(), rec);
            return rec;
        }

        [Fact]
        public void The_fixture_round_trips_with_sixteen_segments_and_the_loop_flag()
        {
            Recording rec = BuildRecording();
            Assert.Equal(16, rec.OrbitSegments.Count);
            Assert.True(rec.LoopPlayback,
                "the corpus recording must be flagged for loop playback");
            // The flag alone is not enough: RecordingStore's load-time
            // sanitizer clears it on any recording the PRODUCTION predicate
            // calls non-loopable (measured on the S1.8 corpus's first flight:
            // a pure orbital coast without launch identity lost the flag in
            // game while this suite stayed green). Pin the predicate itself.
            Assert.True(Recording.IsLoopableRecording(rec),
                "the corpus recording must satisfy Recording.IsLoopableRecording "
                + "(launch identity: Prelaunch start + a named launch site)");
            Assert.Equal("Prelaunch", rec.StartSituation);
            Assert.Equal("Launch Pad", rec.LaunchSiteName);
            Assert.Equal(LoopedInterplanetaryFixture.RecordingId, rec.RecordingId);
        }

        [Fact]
        public void The_enumerator_finds_exactly_the_two_flown_soi_seams()
        {
            Recording rec = BuildRecording();
            var seams = TrajectoryMath.FindCrossBodySoiSeams(rec.OrbitSegments);
            Assert.Equal(2, seams.Count);
            Assert.Equal("Kerbin", rec.OrbitSegments[seams[0].beforeIndex].bodyName);
            Assert.Equal("Sun", rec.OrbitSegments[seams[0].afterIndex].bodyName);
            Assert.Equal(LoopedInterplanetaryFixture.KerbinToSunSeamUT,
                seams[0].seamUT, 6);
            Assert.Equal("Sun", rec.OrbitSegments[seams[1].beforeIndex].bodyName);
            Assert.Equal("Duna", rec.OrbitSegments[seams[1].afterIndex].bodyName);
            Assert.Equal(LoopedInterplanetaryFixture.SunToDunaSeamUT,
                seams[1].seamUT, 6);
        }

        [Fact]
        public void Burn_gaps_and_same_body_splits_are_not_seams()
        {
            // The flown chain carries several same-body adjacent splits and
            // burn gaps (correction rounds); only the two genuine SOI
            // handoffs may enumerate. A widened tolerance would start
            // admitting the burn gaps -- pin the boundary: the largest
            // non-seam adjacency in the flown chain is the ~2 s escape-leg
            // split, same-body so excluded regardless, and the smallest burn
            // gap is > 20 s, far above the 1 s default tolerance.
            Recording rec = BuildRecording();
            var wide = TrajectoryMath.FindCrossBodySoiSeams(
                rec.OrbitSegments, adjacencyToleranceSeconds: 10.0);
            Assert.Equal(2, wide.Count);
        }

        [Fact]
        public void Degenerate_inputs_enumerate_no_seams()
        {
            Assert.Empty(TrajectoryMath.FindCrossBodySoiSeams(null));
            Assert.Empty(TrajectoryMath.FindCrossBodySoiSeams(
                new List<OrbitSegment>()));
            Assert.Empty(TrajectoryMath.FindCrossBodySoiSeams(
                new List<OrbitSegment>
                {
                    new OrbitSegment { bodyName = "Kerbin", startUT = 0, endUT = 10 },
                    new OrbitSegment { bodyName = "Kerbin", startUT = 10, endUT = 20 },
                }));
        }

        [Fact]
        public void A_gap_wider_than_the_tolerance_is_not_a_seam()
        {
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = 0, endUT = 100 },
                new OrbitSegment { bodyName = "Sun", startUT = 130, endUT = 200 },
            };
            Assert.Empty(TrajectoryMath.FindCrossBodySoiSeams(segments));
            Assert.Single(TrajectoryMath.FindCrossBodySoiSeams(
                segments, adjacencyToleranceSeconds: 60.0));
        }

        [Fact]
        public void The_ingame_cells_and_the_fixture_agree_on_the_recording_id()
        {
            // The in-game cells find the corpus recording by id; a drifted
            // constant would make every cell self-skip forever (a silent
            // vacuity, the exact failure mode Skip-on-absence must not hide).
            Assert.Equal(
                LoopedInterplanetaryFixture.RecordingId,
                InGameTests.SoiCrossingPlaybackInGameTests.CorpusRecordingId);
        }
    }
}
