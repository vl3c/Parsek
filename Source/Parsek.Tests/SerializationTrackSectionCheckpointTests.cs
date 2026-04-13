using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SerializationTrackSectionCheckpointTests : System.IDisposable
    {
        public SerializationTrackSectionCheckpointTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
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
        public void CheckpointOpenTrackSectionForSerialization_ClosesOpenSectionAndStartsContinuation()
        {
            var recorder = new FlightRecorder();

            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 100.0);
            recorder.CheckpointOpenTrackSectionForSerialization(120.0);
            recorder.CloseCurrentTrackSection(140.0);

            Assert.Equal(2, recorder.TrackSections.Count);
            Assert.Equal(100.0, recorder.TrackSections[0].startUT);
            Assert.Equal(120.0, recorder.TrackSections[0].endUT);
            Assert.Equal(120.0, recorder.TrackSections[1].startUT);
            Assert.Equal(140.0, recorder.TrackSections[1].endUT);
            Assert.Equal(recorder.TrackSections[0].endUT, recorder.TrackSections[1].startUT);
            Assert.Equal(SegmentEnvironment.Atmospheric, recorder.TrackSections[1].environment);
            Assert.Equal(ReferenceFrame.Absolute, recorder.TrackSections[1].referenceFrame);
            Assert.Equal(TrackSectionSource.Active, recorder.TrackSections[1].source);
        }

        [Fact]
        public void CheckpointOpenTrackSectionForSerialization_PreservesRelativeAnchor()
        {
            var recorder = new FlightRecorder();

            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Relative, 200.0);
            SetCurrentTrackSectionAnchor(recorder, 123456789u);

            recorder.CheckpointOpenTrackSectionForSerialization(220.0);
            recorder.CloseCurrentTrackSection(240.0);

            Assert.Equal(2, recorder.TrackSections.Count);
            Assert.Equal(ReferenceFrame.Relative, recorder.TrackSections[0].referenceFrame);
            Assert.Equal(ReferenceFrame.Relative, recorder.TrackSections[1].referenceFrame);
            Assert.Equal(123456789u, recorder.TrackSections[0].anchorVesselId);
            Assert.Equal(123456789u, recorder.TrackSections[1].anchorVesselId);
        }

        [Fact]
        public void CheckpointOpenTrackSectionForSerialization_PreservesCheckpointSourceWithoutSeedingFrames()
        {
            var recorder = new FlightRecorder();

            recorder.StartNewTrackSection(
                SegmentEnvironment.ExoBallistic,
                ReferenceFrame.OrbitalCheckpoint,
                300.0,
                TrackSectionSource.Checkpoint);

            recorder.CheckpointOpenTrackSectionForSerialization(330.0);
            recorder.CloseCurrentTrackSection(360.0);

            Assert.Equal(2, recorder.TrackSections.Count);
            Assert.Equal(TrackSectionSource.Checkpoint, recorder.TrackSections[0].source);
            Assert.Equal(TrackSectionSource.Checkpoint, recorder.TrackSections[1].source);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, recorder.TrackSections[1].referenceFrame);
            Assert.Empty(recorder.TrackSections[1].frames);
        }

        private static void SetCurrentTrackSectionAnchor(FlightRecorder recorder, uint anchorPid)
        {
            var field = typeof(FlightRecorder).GetField(
                "currentTrackSection",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);

            var section = (TrackSection)field.GetValue(recorder);
            section.anchorVesselId = anchorPid;
            field.SetValue(recorder, section);
        }
    }
}
