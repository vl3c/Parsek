using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The vessel-switch stop (<c>FlightRecorder.HandleVesselSwitchDuringRecording</c>, the
    /// ChainToVessel decision: an EVA recording whose active vessel became a non-EVA vessel,
    /// confirmed board or not) builds <c>CaptureAtStop</c> from the recorder's CLOSED
    /// TrackSections plus the flat Points. The flat list carries every sample of the still
    /// OPEN section, so a stop that does not close it hands the board merge (and the
    /// unconfirmed-stop flush) a recording whose last section is missing. The codec then
    /// writes only what the sections hold (a Relative section's body-fixed shadows) or, when
    /// no section is left at all, the open Relative section's anchor-local metres as flat
    /// lat/lon.
    /// </summary>
    [Collection("Sequential")]
    public class VesselSwitchStopOpenSectionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public VesselSwitchStopOpenSectionTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private const double BoardUT = 130.0;

        private static TrajectoryPoint P(double ut, double lat, double lon, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 0f, 0f),
            };
        }

        /// <summary>
        /// A recorder in the state the switch stop finds it in: an EVA kerbal walked away from
        /// its pod in an Absolute section (closed when the anchor came into range), then walked
        /// to the hatch in a Relative section that is still OPEN. Every sample is in the flat
        /// list too (the recorder dual-writes); the Relative samples hold anchor-local metres
        /// there and body-fixed lat/lon in the section's <c>bodyFixedFrames</c>.
        /// </summary>
        private static FlightRecorder EvaRecorderWithOpenRelativeSection(bool withClosedAbsolute)
        {
            var recorder = new FlightRecorder();
            if (withClosedAbsolute)
                AddAbsoluteSection(recorder, 100.0, close: true);
            AddRelativeSection(recorder, 115.0, close: false);
            return recorder;
        }

        /// <summary>
        /// The mirror shape: the kerbal left the pod in a Relative section (closed when the
        /// anchor went out of range) and reached the other vessel's hatch in an Absolute section
        /// that is still OPEN.
        /// </summary>
        private static FlightRecorder EvaRecorderWithOpenAbsoluteSection()
        {
            var recorder = new FlightRecorder();
            AddRelativeSection(recorder, 85.0, close: true);
            AddAbsoluteSection(recorder, 115.0, close: false);
            return recorder;
        }

        private static void AddAbsoluteSection(FlightRecorder recorder, double startUT, bool close)
        {
            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceMobile, ReferenceFrame.Absolute, startUT);
            for (int i = 0; i < 4; i++)
            {
                TrajectoryPoint pt = P(startUT + i * 5.0, -0.0972, -74.5577 + i * 0.0001, 70.0);
                recorder.CurrentTrackSectionForTesting.frames.Add(pt);
                recorder.Recording.Add(pt);
            }
            if (close)
                recorder.CloseCurrentTrackSection(startUT + 15.0);
        }

        private static void AddRelativeSection(FlightRecorder recorder, double startUT, bool close)
        {
            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceMobile, ReferenceFrame.Relative, startUT);
            for (int i = 0; i < 4; i++)
            {
                double ut = startUT + i * 5.0;
                TrajectoryPoint anchorLocal = P(ut, 12.0 - i * 3.0, -8.0 + i * 2.0, 1.5);
                TrajectoryPoint bodyFixed = P(ut, -0.0972, -74.5574 + i * 0.00002, 70.0);
                recorder.CurrentTrackSectionForTesting.frames.Add(anchorLocal);
                recorder.CurrentTrackSectionForTesting.bodyFixedFrames.Add(bodyFixed);
                recorder.Recording.Add(anchorLocal);
            }
            if (close)
                recorder.CloseCurrentTrackSection(startUT + 15.0);
        }

        /// <summary>
        /// What <c>BuildCaptureRecording</c> copies off the recorder (flat lists + a deep copy
        /// of the closed sections), appended onto the EVA leg the way
        /// <c>FlushStoppedRecorderIntoMergeParent</c> does on a confirmed board.
        /// </summary>
        private static Recording BoardMergeParentFrom(FlightRecorder recorder)
        {
            var capture = new Recording
            {
                RecordingId = "eva-capture",
                Points = new List<TrajectoryPoint>(recorder.Recording),
                TrackSections = Recording.DeepCopyTrackSections(recorder.TrackSections),
            };
            var evaLeg = new Recording { RecordingId = "eva-leg", VesselName = "Bill Kerman" };
            ParsekFlight.AppendCapturedDataToRecording(evaLeg, capture, BoardUT);
            return evaLeg;
        }

        private static Recording RoundTrip(Recording rec)
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);
            var restored = new Recording { RecordingId = rec.RecordingId };
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);
            return restored;
        }

        // ---------- the defect shape (no close before the capture) ----------

        [Fact]
        public void Witness_OpenRelativeLeftOut_TheWalkToTheHatchIsSavedAsAnchorMetres()
        {
            FlightRecorder recorder = EvaRecorderWithOpenRelativeSection(withClosedAbsolute: true);
            Recording leg = BoardMergeParentFrom(recorder);

            // In memory the flat list reaches the hatch, the sections stop at 115.
            Assert.Equal(8, leg.Points.Count);
            Assert.Single(leg.TrackSections);
            Assert.False(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(leg));

            Recording restored = RoundTrip(leg);
            // Saved and loaded, the walk to the hatch is flat points no section covers, whose
            // lat/lon are anchor-local metres (3 m along an anchor axis reads as 3 degrees).
            Assert.Single(restored.TrackSections);
            TrajectoryPoint last = restored.Points[restored.Points.Count - 1];
            Assert.Equal(BoardUT, last.ut);
            Assert.Equal(3.0, last.latitude);
        }

        [Fact]
        public void Witness_OpenAbsoluteLeftOut_TheWalkToTheHatchHasNoSection()
        {
            FlightRecorder recorder = EvaRecorderWithOpenAbsoluteSection();
            Recording leg = BoardMergeParentFrom(recorder);

            Assert.Equal(8, leg.Points.Count);
            Assert.Single(leg.TrackSections);

            Recording restored = RoundTrip(leg);
            // The codec keeps the Absolute tail as flat points past the last section (its
            // lat/lon are real), but no section covers the last 30 s of the leg.
            Assert.Single(restored.TrackSections);
            Assert.Equal(100.0, restored.TrackSections[0].endUT);
            Assert.Equal(BoardUT, restored.Points[restored.Points.Count - 1].ut);
        }

        [Fact]
        public void Witness_WholeEvaInOneOpenRelativeSection_SavesAnchorMetresAsLatLon()
        {
            FlightRecorder recorder = EvaRecorderWithOpenRelativeSection(withClosedAbsolute: false);
            Recording leg = BoardMergeParentFrom(recorder);

            Assert.Empty(leg.TrackSections);
            Recording restored = RoundTrip(leg);
            Assert.Empty(restored.TrackSections);
            // The flat list now reads anchor-local metres (12 m along an anchor axis) as a
            // body-fixed latitude of 12 degrees.
            Assert.Equal(12.0, restored.Points[0].latitude);
        }

        // ---------- the fix ----------

        [Fact]
        public void SwitchStop_ClosesTheOpenRelativeSection_TheCaptureCarriesIt()
        {
            FlightRecorder recorder = EvaRecorderWithOpenRelativeSection(withClosedAbsolute: true);

            recorder.CloseOpenTrackSectionForVesselSwitchStop(BoardUT);

            Assert.Equal(2, recorder.TrackSections.Count);
            TrackSection last = recorder.TrackSections[1];
            Assert.Equal(ReferenceFrame.Relative, last.referenceFrame);
            Assert.Equal(4, last.frames.Count);
            Assert.Equal(4, last.bodyFixedFrames.Count);
            Assert.Equal(BoardUT, last.endUT);

            Recording leg = BoardMergeParentFrom(recorder);
            Assert.Equal(2, leg.TrackSections.Count);
            Assert.True(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(leg));

            Recording restored = RoundTrip(leg);
            Assert.Equal(2, restored.TrackSections.Count);
            Assert.Equal(ReferenceFrame.Relative, restored.TrackSections[1].referenceFrame);
            Assert.Equal(4, restored.TrackSections[1].bodyFixedFrames.Count);
            Assert.Equal(130.0, restored.TrackSections[1].frames[3].ut);
            // No flat point outside a section: the flat list is rebuilt from the sections.
            Assert.Equal(130.0, restored.Points[restored.Points.Count - 1].ut);

            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Vessel-switch stop: closed the open TrackSection")
                && l.Contains("ref=Relative") && l.Contains("frames=4"));
        }

        [Fact]
        public void SwitchStop_WholeEvaInOneRelativeSection_KeepsItsSection()
        {
            FlightRecorder recorder = EvaRecorderWithOpenRelativeSection(withClosedAbsolute: false);

            recorder.CloseOpenTrackSectionForVesselSwitchStop(BoardUT);

            Recording leg = BoardMergeParentFrom(recorder);
            Assert.Single(leg.TrackSections);
            Recording restored = RoundTrip(leg);
            Assert.Single(restored.TrackSections);
            Assert.Equal(ReferenceFrame.Relative, restored.TrackSections[0].referenceFrame);
            Assert.Equal(4, restored.TrackSections[0].bodyFixedFrames.Count);
        }

        [Fact]
        public void SwitchStop_ClosesTheOpenAbsoluteSection_TheWalkSurvivesASave()
        {
            FlightRecorder recorder = EvaRecorderWithOpenAbsoluteSection();

            recorder.CloseOpenTrackSectionForVesselSwitchStop(BoardUT);

            Recording leg = BoardMergeParentFrom(recorder);
            Assert.Equal(2, leg.TrackSections.Count);
            Assert.True(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(leg));
            Recording restored = RoundTrip(leg);
            Assert.Equal(2, restored.TrackSections.Count);
            Assert.Equal(ReferenceFrame.Absolute, restored.TrackSections[1].referenceFrame);
            Assert.Equal(130.0, restored.TrackSections[1].frames[3].ut);
            Assert.Equal(-0.0972, restored.TrackSections[1].frames[3].latitude);
        }

        [Fact]
        public void SwitchStop_NoOpenSection_ChangesNothingAndSaysSo()
        {
            var recorder = new FlightRecorder();
            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceMobile, ReferenceFrame.Absolute, 100.0);
            recorder.CurrentTrackSectionForTesting.frames.Add(P(100.0, 0.0, 0.0, 70.0));
            recorder.CloseCurrentTrackSection(101.0);

            recorder.CloseOpenTrackSectionForVesselSwitchStop(BoardUT);

            Assert.Single(recorder.TrackSections);
            Assert.Equal(101.0, recorder.TrackSections[0].endUT);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Vessel-switch stop: no open TrackSection to close"));
        }

        // ---------- the wiring ----------

        [Fact]
        public void HandleVesselSwitchDuringRecording_ClosesTheSectionBeforeBuildingTheCapture()
        {
            string body = ReadMethodBody(
                "Recording/FlightRecorder.cs", "private bool HandleVesselSwitchDuringRecording(");
            int close = body.IndexOf("CloseOpenTrackSectionForVesselSwitchStop(", StringComparison.Ordinal);
            int capture = body.IndexOf("BuildCaptureRecording(", StringComparison.Ordinal);
            Assert.True(capture >= 0, "BuildCaptureRecording call not found in the switch stop");
            Assert.True(close >= 0, "the switch stop does not close its open TrackSection");
            Assert.True(close < capture, "the switch stop closes its open TrackSection after building the capture");
        }

        private static string ReadMethodBody(string fileName, string signature)
        {
            string dir = System.IO.Path.GetDirectoryName(
                new Uri(typeof(VesselSwitchStopOpenSectionTests).Assembly.CodeBase).LocalPath);
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                dir, "..", "..", "..", "..", "..", "Source", "Parsek", fileName));
            Assert.True(System.IO.File.Exists(path), $"source not found: {path}");
            string source = StripLineComments(System.IO.File.ReadAllText(path));
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"method not found: {signature}");
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced braces in " + fileName);
        }

        private static string StripLineComments(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line).Append('\n');
            }
            return sb.ToString();
        }
    }
}
