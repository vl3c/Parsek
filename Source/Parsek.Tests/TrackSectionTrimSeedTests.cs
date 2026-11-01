using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// B13 (overnight run 2026-11-01): a serialization checkpoint opens a section at the
    /// save UT seeded with the previous sample, up to one sampling gap earlier. A tail trim
    /// or a split that cut every later frame re-derived the section's end from that seed,
    /// so the committed recording carried a section ending before it started
    /// (23135.27 -> 23132.69, analyzer INV1-UT-MONOTONIC).
    /// </summary>
    [Collection("Sequential")]
    public class TrackSectionTrimSeedTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TrackSectionTrimSeedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static TrajectoryPoint P(double ut)
        {
            return new TrajectoryPoint { ut = ut, latitude = 1.0, longitude = 2.0, altitude = 100.0, bodyName = "Mun" };
        }

        private static TrackSection Section(double startUT, double endUT, params double[] frameUTs)
        {
            var frames = new List<TrajectoryPoint>();
            foreach (double ut in frameUTs) frames.Add(P(ut));
            return new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                frames = frames
            };
        }

        // catches: the B13 inversion. Seeded at 23132.69, opened at 23135.27, the next
        // sample at 23138.3 is cut away: only the pre-start seed is left.
        [Fact]
        public void Trim_OnlyThePreStartSeedLeft_DropsTheSection()
        {
            var sec = Section(23135.27, 23140.0, 23132.69, 23138.3, 23139.9);
            Assert.False(RecordingOptimizer.TryTrimTrackSectionPayload(ref sec, 23136.0));
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("dropped a section with only pre-start seed frames left")
                && l.Contains("startUT=23135.27") && l.Contains("keptEndUT=23132.69"));
        }

        // A frame inside the section's own window keeps it, ending on that frame.
        [Fact]
        public void Trim_AFrameInsideTheWindowKept_EndsOnIt()
        {
            var sec = Section(23135.27, 23140.0, 23132.69, 23135.9, 23138.3);
            Assert.True(RecordingOptimizer.TryTrimTrackSectionPayload(ref sec, 23136.0));
            Assert.Equal(23135.9, sec.endUT);
            Assert.True(sec.endUT >= sec.startUT);
            Assert.Equal(2, sec.frames.Count);
        }

        // Boundary: a seam frame stamped AT the start (section-start seam) is the
        // section's own sample, so the zero-length section stays.
        [Fact]
        public void Trim_SeamFrameAtTheStart_KeepsAZeroLengthSection()
        {
            var sec = Section(100.0, 110.0, 100.0, 105.0);
            Assert.True(RecordingOptimizer.TryTrimTrackSectionPayload(ref sec, 102.0));
            Assert.Equal(100.0, sec.endUT);
            Assert.Equal(100.0, sec.startUT);
        }

        // End to end: the B13 shape through the commit-time tail trim. Section A samples
        // every 3 s to 132.69; a save at 135.27 checkpoints it and opens section B seeded
        // with 132.69; the next sample is 138.3. The last interesting event (125.9) puts
        // the trim at 135.9, between the save and the next sample.
        [Fact]
        public void TrimBoringTail_CheckpointSeedOnlySectionLeft_NoSectionEndsBeforeItStarts()
        {
            const double lat = -0.5, lon = 23.0, alt = 612.0;
            var rec = new Recording
            {
                RecordingId = "b13seedtrimtest0000000000000000a",
                VesselName = "B13 lander",
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Mun", latitude = lat, longitude = lon, altitude = alt,
                    rotation = Quaternion.identity, situation = SurfaceSituation.Landed
                },
                ExplicitEndUT = 300.0
            };
            System.Func<double, TrajectoryPoint> at = ut => new TrajectoryPoint
            {
                ut = ut, bodyName = "Mun", latitude = lat, longitude = lon, altitude = alt,
                rotation = Quaternion.identity
            };
            var a = new List<TrajectoryPoint>();
            for (double t = 99.69; t <= 132.70; t += 3.0) a.Add(at(t));
            var b = new List<TrajectoryPoint> { at(a[a.Count - 1].ut) };
            for (double t = 138.3; t <= 300.0; t += 3.0) b.Add(at(t));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary, referenceFrame = ReferenceFrame.Absolute,
                startUT = 99.69, endUT = 135.27, frames = a
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary, referenceFrame = ReferenceFrame.Absolute,
                startUT = 135.27, endUT = b[b.Count - 1].ut, frames = b
            });
            rec.Points.AddRange(a);
            for (int i = 1; i < b.Count; i++) rec.Points.Add(b[i]);
            rec.PartEvents.Add(new PartEvent
            {
                ut = 125.9, partPersistentId = 100000u, partName = "landingLeg",
                eventType = PartEventType.DeployableExtended
            });

            RecordingOptimizer.TrimBoringTail(rec, new List<Recording> { rec });

            foreach (var sec in rec.TrackSections)
                Assert.True(sec.endUT >= sec.startUT,
                    "section " + sec.startUT.ToString("R") + " -> " + sec.endUT.ToString("R") + " is inverted");
            Assert.True(rec.EndUT >= rec.StartUT);
        }

        // Unchanged path: nothing left at all still drops the section.
        [Fact]
        public void Trim_NoFrameLeft_DropsTheSection()
        {
            var sec = Section(100.0, 110.0, 104.0, 108.0);
            Assert.False(RecordingOptimizer.TryTrimTrackSectionPayload(ref sec, 102.0));
        }
    }
}
