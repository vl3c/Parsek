using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless pins for <see cref="RecordingBuilder.WithLoopAnchorVesselId"/> and the
    /// <c>relative-loop</c> injection preset's recording (<see cref="RelativeLoopAnchorFixture"/>),
    /// plus the exact production log literals the RL-1 harness lane requires, built here from
    /// the real producers so a reworded line reds this suite before a flight.
    /// </summary>
    [Collection("Sequential")]
    public class RelativeLoopAnchorFixtureTests : IDisposable
    {
        private const double SaveUT = 25.25999999999955; // pad-runway-pair's FLIGHTSTATE UT
        private readonly List<string> logLines = new List<string>();

        public RelativeLoopAnchorFixtureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording LoadFixtureRecording()
        {
            RecordingBuilder builder = RelativeLoopAnchorFixture.BuildRecording(SaveUT);
            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(builder.BuildV3Metadata(), rec);
            RecordingStore.DeserializeTrajectoryFrom(builder.BuildTrajectoryNode(), rec);
            return rec;
        }

        [Fact]
        public void WithLoopAnchorVesselId_emits_the_production_keys_in_both_metadata_shapes()
        {
            var builder = new RecordingBuilder("anchored")
                .AddPoint(10, 0, 0, 0)
                .AddPoint(20, 0, 0, 0)
                .WithLoopPlayback(true, 10.0)
                .WithLoopAnchorVesselId(4242u, "Mun");

            foreach (ConfigNode node in new[] { builder.BuildV3Metadata(), builder.Build() })
            {
                Assert.Equal("4242", node.GetValue("loopAnchorPid"));
                Assert.Equal("Mun", node.GetValue("loopAnchorBodyName"));
            }

            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(builder.BuildV3Metadata(), rec);
            Assert.Equal(4242u, rec.LoopAnchorVesselId);
            Assert.Equal("Mun", rec.LoopAnchorBodyName);
        }

        [Fact]
        public void An_unanchored_builder_writes_no_loop_anchor_keys()
        {
            var builder = new RecordingBuilder("plain")
                .AddPoint(10, 0, 0, 0)
                .AddPoint(20, 0, 0, 0);

            foreach (ConfigNode node in new[] { builder.BuildV3Metadata(), builder.Build() })
            {
                Assert.Null(node.GetValue("loopAnchorPid"));
                Assert.Null(node.GetValue("loopAnchorBodyName"));
            }
        }

        [Fact]
        public void The_tree_writer_carries_the_loop_anchor_into_the_serialized_tree()
        {
            RecordingTree tree = ScenarioWriter.MaterializeTree(
                new[] { RelativeLoopAnchorFixture.BuildRecording(SaveUT) }, null);
            Recording rec = tree.Recordings[RelativeLoopAnchorFixture.RecordingId];
            Assert.Equal(RelativeLoopAnchorFixture.AnchorPid, rec.LoopAnchorVesselId);
            Assert.Equal(RelativeLoopAnchorFixture.AnchorBodyName, rec.LoopAnchorBodyName);

            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);
            ConfigNode recNode = treeNode.GetNode("RECORDING");
            Assert.NotNull(recNode);
            Assert.Equal(
                RelativeLoopAnchorFixture.AnchorPid.ToString(CultureInfo.InvariantCulture),
                recNode.GetValue("loopAnchorPid"));
        }

        [Fact]
        public void The_fixture_is_a_loop_anchored_relative_loop_the_engine_will_play()
        {
            Recording rec = LoadFixtureRecording();

            Assert.Equal(RelativeLoopAnchorFixture.RecordingId, rec.RecordingId);
            Assert.True(rec.LoopPlayback);
            Assert.Equal(RelativeLoopAnchorFixture.AnchorPid, rec.LoopAnchorVesselId);
            Assert.Equal("Kerbin", rec.LoopAnchorBodyName);
            Assert.Equal(RelativeLoopAnchorFixture.LoopSeconds, rec.LoopIntervalSeconds);
            // Survives the load-time sanitizer (a loop anchor makes the relative track viewable).
            Assert.True(Recording.IsLoopableRecording(rec));
            // The two engine gates that route it through the live-PID loop positioner.
            Assert.True(GhostPlaybackEngine.ShouldLoopPlayback(rec));
            Assert.True(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
            // Period == duration: the single-ghost path, not the overlap path.
            Assert.False(GhostPlaybackLogic.IsOverlapLoop(
                rec.LoopIntervalSeconds, rec.EndUT - rec.StartUT));
            // Nothing to spawn: a ghost-only recording (snapshots ride the sidecars, so read
            // them off the builder rather than the metadata-loaded recording).
            RecordingBuilder builder = RelativeLoopAnchorFixture.BuildRecording(SaveUT);
            Assert.Null(builder.GetVesselSnapshot());
            Assert.NotNull(builder.GetGhostVisualSnapshot());
            Assert.Null(rec.ParentAnchorRecordingId);
            Assert.False(rec.IsDebris);
        }

        [Fact]
        public void The_fixture_sections_hold_a_zero_offset_then_a_twenty_metre_offset()
        {
            Recording rec = LoadFixtureRecording();

            Assert.Equal(2, rec.TrackSections.Count);
            double t0 = RelativeLoopAnchorFixture.StartUTFor(SaveUT);
            Assert.Equal(SaveUT - RelativeLoopAnchorFixture.EndBeforeSaveSeconds,
                t0 + RelativeLoopAnchorFixture.LoopSeconds, 9);
            for (int i = 0; i < 2; i++)
            {
                TrackSection s = rec.TrackSections[i];
                Assert.Equal(ReferenceFrame.Relative, s.referenceFrame);
                Assert.Equal(SegmentEnvironment.Atmospheric, s.environment);
                Assert.Equal(RelativeLoopAnchorFixture.AnchorPid, s.anchorVesselId);
                Assert.True(string.IsNullOrEmpty(s.anchorRecordingId),
                    "the live-PID loop contract carries no recorded anchor");
                double expectedDy = i == 0 ? 0.0 : RelativeLoopAnchorFixture.SectionBOffsetY;
                Assert.Equal(3, s.frames.Count);
                foreach (TrajectoryPoint p in s.frames)
                {
                    Assert.Equal(0.0, p.latitude);
                    Assert.Equal(expectedDy, p.longitude);
                    Assert.Equal(0.0, p.altitude);
                }
            }
            Assert.Equal(t0, rec.TrackSections[0].startUT);
            Assert.Equal(rec.TrackSections[0].endUT, rec.TrackSections[1].startUT);
            Assert.Equal(t0 + RelativeLoopAnchorFixture.LoopSeconds, rec.TrackSections[1].endUT);
            Assert.Equal(6, rec.Points.Count);
        }

        [Fact]
        public void The_fixture_sidecars_write_through_the_production_writer()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "parsek-rl1-" + Guid.NewGuid().ToString("N"));
            try
            {
                var writer = new ScenarioWriter().WithV3Format();
                RelativeLoopAnchorFixture.PopulateWriter(writer, SaveUT);
                writer.WriteSidecarFiles(dir);
                string rec = System.IO.Path.Combine(dir, "Parsek", "Recordings",
                    RelativeLoopAnchorFixture.RecordingId);
                Assert.True(System.IO.File.Exists(rec + ".prec"));
                Assert.True(System.IO.File.Exists(rec + "_ghost.craft"));
            }
            finally
            {
                if (System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void StartUTFor_never_goes_below_one()
        {
            Assert.Equal(1.0, RelativeLoopAnchorFixture.StartUTFor(5.0));
            Assert.Equal(78.0, RelativeLoopAnchorFixture.StartUTFor(100.0));
        }

        // --- the RL-1 required literals, rendered by their production producers ---

        [Fact]
        public void The_spawn_policy_line_RL1_requires_is_the_production_line()
        {
            Recording rec = LoadFixtureRecording();
            Assert.True(GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, true, "Kerbin", rec.LoopAnchorBodyName));
            Assert.Contains(logLines, l => l.Contains("[Loop]")
                && l.Contains("ShouldSpawnLoopedGhost: rec 'Relative Loop Anchor Probe' "
                    + "anchor pid=95298807 valid, returning true"));
        }

        [Fact]
        public void A_zero_offset_places_the_ghost_exactly_on_the_anchor_for_any_anchor_rotation()
        {
            // The premise of RL-1's co-location regex (anchorPos=X ... output=X): with the
            // section-A offset, the production resolver returns the anchor position bit for bit,
            // so the two formatted vectors are the same string whatever the live rotation is.
            var anchorPos = new Vector3d(-1234.567891, 88.123456, 4321.987654);
            Quaternion[] rotations =
            {
                Quaternion.identity,
                new Quaternion(0.1830127f, 0.5f, 0.1830127f, 0.8290376f),
                new Quaternion(-0.7071068f, 0f, 0f, 0.7071068f),
            };
            foreach (Quaternion rot in rotations)
            {
                Vector3d output = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPos, rot, 0.0, 0.0, 0.0);
                Assert.Equal(
                    GhostRenderTrace.FormatVector3d(anchorPos),
                    GhostRenderTrace.FormatVector3d(output));
            }
            Assert.Equal("(0.00,0.00,0.00)", GhostRenderTrace.FormatVector3d(new Vector3d(0, 0, 0)));
        }
    }
}
