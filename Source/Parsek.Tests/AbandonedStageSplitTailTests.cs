using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Flight asr-final3 (lanes ASR-1 / ASR-5): the probe core's predicted fall survived the
    /// scene-exit commit and the tree merge, then the optimizer's atmosphere / space split
    /// dropped it. The space section's frames open with the atmosphere section's closing
    /// sample (UT 168.4, before the section's own start 173.75), so the second half's flat
    /// list (cut at the split UT) is one sample short of its sections, the tail-preserving
    /// sync refused, and the plain rebuild kept only the section-owned orbit segments.
    /// </summary>
    [Collection("Sequential")]
    public class AbandonedStageSplitTailTests : IDisposable
    {
        private const double TerminalUT = 428.601;
        private readonly List<string> logLines = new List<string>();

        public AbandonedStageSplitTailTests()
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

        private static TrajectoryPoint Pt(double ut, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = -0.09,
                longitude = -73.3,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = new Vector3(-1000f, 2f, -30f),
            };
        }

        private static OrbitSegment Conic(double startUT, double endUT, bool predicted, double mna)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = "Kerbin",
                inclination = 0.2,
                eccentricity = 0.8933,
                semiMajorAxis = 367283.7,
                longitudeOfAscendingNode = 10.0,
                argumentOfPeriapsis = 20.0,
                meanAnomalyAtEpoch = mna,
                epoch = startUT,
                isPredicted = predicted,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero
            };
        }

        private static TrackSection Absolute(SegmentEnvironment env, double startUT, double endUT,
            List<TrajectoryPoint> frames)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = startUT,
                endUT = endUT,
                frames = frames,
                sampleRateHz = 4f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
        }

        private static TrackSection Checkpoint(OrbitSegment seg)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = seg.startUT,
                endUT = seg.endUT,
                checkpoints = new List<OrbitSegment> { seg },
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
        }

        /// <summary>
        /// The probe as the commit merge left it (asr-final3 ASR-1, rec 174159fb): an
        /// atmosphere section ending its frames at 168.4, a space section starting at 173.75
        /// whose first frame is that same 168.4 sample, two on-rails checkpoints to the exit
        /// UT 279.935 and the finalizer's predicted fall to the impact UT, plus the predicted
        /// ending fields.
        /// </summary>
        private static Recording ProbeAsMerged(string id, bool spaceOpensBeforeItsStart = true)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Kerbal X Probe",
                TreeId = "asr-final3-tree",
                VesselPersistentId = 1811797276u,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            var atmo = new List<TrajectoryPoint>();
            for (double ut = 132.52; ut < 168.0; ut += 2.0)
                atmo.Add(Pt(ut, 39760.0 + (ut - 132.52) * 700.0));
            atmo.Add(Pt(168.4, 64000.0));

            var space = new List<TrajectoryPoint>();
            if (spaceOpensBeforeItsStart)
                space.Add(atmo[atmo.Count - 1]);
            space.Add(Pt(173.745719604507, 67000.0));
            for (double ut = 174.175; ut < 176.6; ut += 0.24)
                space.Add(Pt(ut, 67200.0 + (ut - 174.175) * 400.0));

            OrbitSegment cp1 = Conic(217.335177, 279.015177, predicted: false, mna: 3.0);
            OrbitSegment cp2 = Conic(279.015177, 279.935177, predicted: false, mna: 3.1);
            OrbitSegment fall = Conic(279.935177, TerminalUT, predicted: true, mna: 3.1);

            rec.TrackSections.Add(Absolute(SegmentEnvironment.Atmospheric, 132.52, 173.745719604507, atmo));
            rec.TrackSections.Add(Absolute(SegmentEnvironment.ExoBallistic, 173.745719604507, 217.335177, space));
            rec.TrackSections.Add(Checkpoint(cp1));
            rec.TrackSections.Add(Checkpoint(cp2));

            rec.Points.AddRange(atmo);
            rec.Points.AddRange(spaceOpensBeforeItsStart ? space.Skip(1) : space);
            rec.OrbitSegments.Add(cp1);
            rec.OrbitSegments.Add(cp2);
            rec.OrbitSegments.Add(fall);

            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true, quietEnd: true);
            rec.StockHeldVesselUT = 250.0;
            // Not a shape the stamp seam writes with a Destroyed ending (it keeps the list
            // only for Recovered); set directly so the split's plain copy is observable.
            rec.RecoveredCrewNames = new List<string> { "Jebediah Kerman" };
            rec.ExplicitEndUT = TerminalUT;
            return rec;
        }

        private void AssertSecondHalfCarriesTheFall(Recording first, Recording second)
        {
            Assert.NotNull(second);
            OrbitSegment last = second.OrbitSegments[second.OrbitSegments.Count - 1];
            Assert.True(last.isPredicted);
            Assert.Equal(TerminalUT, last.endUT, 6);
            Assert.Equal(TerminalUT, second.EndUT, 3);
            Assert.Equal(TerminalUT, second.ExplicitEndUT, 3);
            Assert.Equal(TerminalState.Destroyed, second.TerminalStateValue);
            Assert.True(second.TerminalPredicted);
            Assert.True(second.QuietDestroyedEnd);
            Assert.Equal(250.0, second.StockHeldVesselUT);
            Assert.Equal(new[] { "Jebediah Kerman" }, second.RecoveredCrewNames);
            Assert.False(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(second));

            Assert.DoesNotContain(first.OrbitSegments, s => s.isPredicted);
            Assert.Null(first.TerminalStateValue);
            Assert.False(first.TerminalPredicted);
            Assert.False(first.QuietDestroyedEnd);
            Assert.Null(first.RecoveredCrewNames);
            Assert.True(first.EndUT < 174.0);
        }

        [Fact]
        public void SplitAtSection_SpaceSectionOpensWithThePreviousSample_SecondHalfKeepsThePredictedFall()
        {
            Recording rec = ProbeAsMerged("asr1-split-tail");
            Assert.True(TrajectoryTextSidecarCodec.FlatTrajectoryExtendsTrackSectionPayload(
                rec, rec.TrackSections, allowRelativeSections: true));

            Recording second = RecordingOptimizer.SplitAtSection(rec, 1);

            AssertSecondHalfCarriesTheFall(rec, second);
            Assert.Equal(1, second.OrbitSegments.Count(s => s.isPredicted));
            Assert.Equal(3, second.OrbitSegments.Count);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("SplitAtSection: split asr1-split-tail")
                && l.Contains("track-sections-preserved-predicted-orbit-tail:1"));
        }

        /// <summary>
        /// The latent case named in WP15: the second half's flat list opens with an extra
        /// sample its first section does not hold. Here an equal-UT body seam: the atmosphere
        /// section closes on its own sample at the split UT and the space section opens on a
        /// different sample at the same UT, so the flat list holds both and the cut at the
        /// split UT hands the second half the atmosphere one first.
        /// </summary>
        [Fact]
        public void SplitAtSection_SecondHalfOpensWithAnExtraSeamSample_SecondHalfKeepsThePredictedFall()
        {
            Recording rec = ProbeAsMerged("asr1-split-tail-seam", spaceOpensBeforeItsStart: false);
            TrackSection atmo = rec.TrackSections[0];
            TrajectoryPoint seam = Pt(173.745719604507, 66990.0);
            atmo.frames.Add(seam);
            rec.TrackSections[0] = atmo;
            rec.Points.Insert(atmo.frames.Count - 1, seam);
            Assert.Equal(seam.altitude, rec.Points[atmo.frames.Count - 1].altitude);
            Assert.Equal(67000.0, rec.Points[atmo.frames.Count].altitude);

            Recording second = RecordingOptimizer.SplitAtSection(rec, 1);

            AssertSecondHalfCarriesTheFall(rec, second);
            Assert.Contains(logLines, l => l.Contains("SplitAtSection: split asr1-split-tail-seam")
                && l.Contains("track-sections-preserved-predicted-orbit-tail:1"));
        }

        /// <summary>Control: when the flat list matches the sections up to the tail, the
        /// existing flat-tail path keeps the fall (the shape the WP15 end-to-end test pins).</summary>
        [Fact]
        public void SplitAtSection_FlatListMatchesSections_KeepsThePredictedFallThroughTheFlatTailPath()
        {
            Recording rec = ProbeAsMerged("asr1-split-tail-clean", spaceOpensBeforeItsStart: false);
            TrackSection atmo = rec.TrackSections[0];
            // The space section's first frame closes the atmosphere section too, so the
            // flat list (which dedupes the shared sample) matches the sections exactly.
            atmo.frames.Add(rec.TrackSections[1].frames[0]);
            rec.TrackSections[0] = atmo;

            Recording second = RecordingOptimizer.SplitAtSection(rec, 1);

            AssertSecondHalfCarriesTheFall(rec, second);
            Assert.Contains(logLines, l => l.Contains("SplitAtSection: split asr1-split-tail-clean")
                && l.Contains("track-sections-preserved-flat-tail:1"));
        }

        /// <summary>A recording with no predicted segment still rebuilds from its sections.</summary>
        [Fact]
        public void SplitAtSection_NoPredictedSegment_RebuildsFromSections()
        {
            Recording rec = ProbeAsMerged("asr1-split-no-tail");
            rec.OrbitSegments.RemoveAt(rec.OrbitSegments.Count - 1);

            Recording second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.DoesNotContain(second.OrbitSegments, s => s.isPredicted);
            Assert.Equal(2, second.OrbitSegments.Count);
            Assert.Contains(logLines, l => l.Contains("SplitAtSection: split asr1-split-no-tail")
                && l.Contains("flatSync=track-sections/track-sections)"));
        }

        /// <summary>The merge pass mirrors the split: the absorbed later half's recovered crew
        /// list moves onto the target with the Recovered terminal it inherits (the stamp seam
        /// clears the list unless it is handed over).</summary>
        [Fact]
        public void MergeInto_CarriesRecoveredCrewWithTheRecoveredTerminal()
        {
            Recording rec = ProbeAsMerged("asr1-merge-crew");
            Recording second = RecordingOptimizer.SplitAtSection(rec, 1);
            second.RecordingId = "asr1-merge-crew-b";
            second.StampTerminalState(TerminalState.Recovered, "test",
                recoveredCrew: new[] { "Jebediah Kerman" });

            RecordingOptimizer.MergeInto(rec, second);

            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.Equal(new[] { "Jebediah Kerman" }, rec.RecoveredCrewNames);
        }
    }
}
