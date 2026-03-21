using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TrackSectionSourceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TrackSectionSourceTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region Enum value tests

        [Fact]
        public void TrackSectionSource_Active_IsZero()
        {
            Assert.Equal(0, (int)TrackSectionSource.Active);
        }

        [Fact]
        public void TrackSectionSource_Background_IsOne()
        {
            Assert.Equal(1, (int)TrackSectionSource.Background);
        }

        [Fact]
        public void TrackSectionSource_Checkpoint_IsTwo()
        {
            Assert.Equal(2, (int)TrackSectionSource.Checkpoint);
        }

        [Fact]
        public void TrackSectionSource_ValuesAreContiguous()
        {
            var values = Enum.GetValues(typeof(TrackSectionSource)).Cast<int>().OrderBy(v => v).ToList();
            Assert.Equal(3, values.Count);
            for (int i = 0; i < values.Count; i++)
                Assert.Equal(i, values[i]);
        }

        #endregion

        #region Helpers

        private static TrackSection MakeSection(
            SegmentEnvironment env = SegmentEnvironment.Atmospheric,
            ReferenceFrame refFrame = ReferenceFrame.Absolute,
            double startUT = 17000.0,
            double endUT = 17100.0,
            float sampleRate = 10.0f,
            TrackSectionSource source = TrackSectionSource.Active,
            float boundaryDiscontinuity = 0f)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = refFrame,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = sampleRate,
                source = source,
                boundaryDiscontinuityMeters = boundaryDiscontinuity,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };
        }

        #endregion

        #region Round-trip: source=Background

        [Fact]
        public void RoundTrip_SourceBackground_PreservedCorrectly()
        {
            var section = MakeSection(source: TrackSectionSource.Background);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.Equal(TrackSectionSource.Background, loaded[0].source);
        }

        #endregion

        #region Round-trip: source=Checkpoint

        [Fact]
        public void RoundTrip_SourceCheckpoint_PreservedCorrectly()
        {
            var section = MakeSection(source: TrackSectionSource.Checkpoint);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.Equal(TrackSectionSource.Checkpoint, loaded[0].source);
        }

        #endregion

        #region Sparse: source=Active NOT written

        [Fact]
        public void Serialize_SourceActive_NotWrittenToConfigNode()
        {
            var section = MakeSection(source: TrackSectionSource.Active);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            var tsNode = parent.GetNodes("TRACK_SECTION")[0];
            Assert.Null(tsNode.GetValue("src"));
        }

        #endregion

        #region Sparse: source=Background IS written

        [Fact]
        public void Serialize_SourceBackground_WrittenToConfigNode()
        {
            var section = MakeSection(source: TrackSectionSource.Background);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            var tsNode = parent.GetNodes("TRACK_SECTION")[0];
            Assert.Equal("1", tsNode.GetValue("src"));
        }

        #endregion

        #region Round-trip: boundaryDiscontinuityMeters

        [Fact]
        public void RoundTrip_BoundaryDiscontinuity_PreservedCorrectly()
        {
            var section = MakeSection(boundaryDiscontinuity: 5.3f);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.Equal(5.3f, loaded[0].boundaryDiscontinuityMeters);
        }

        #endregion

        #region Sparse: boundaryDiscontinuityMeters=0 NOT written

        [Fact]
        public void Serialize_BoundaryDiscontinuityZero_NotWrittenToConfigNode()
        {
            var section = MakeSection(boundaryDiscontinuity: 0f);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            var tsNode = parent.GetNodes("TRACK_SECTION")[0];
            Assert.Null(tsNode.GetValue("bdisc"));
        }

        #endregion

        #region Sparse: boundaryDiscontinuityMeters > 0 IS written

        [Fact]
        public void Serialize_BoundaryDiscontinuityPositive_WrittenToConfigNode()
        {
            var section = MakeSection(boundaryDiscontinuity: 12.5f);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            var tsNode = parent.GetNodes("TRACK_SECTION")[0];
            Assert.NotNull(tsNode.GetValue("bdisc"));
            float parsed;
            Assert.True(float.TryParse(tsNode.GetValue("bdisc"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsed));
            Assert.Equal(12.5f, parsed);
        }

        #endregion

        #region Backward compat: old TRACK_SECTION without src or bdisc

        [Fact]
        public void BackwardCompat_MissingSrcAndBdisc_DefaultsToActiveAndZero()
        {
            var parent = new ConfigNode("TEST");
            var tsNode = parent.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");
            tsNode.AddValue("ref", "0");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17100");
            tsNode.AddValue("sampleRate", "10");
            // No "src" or "bdisc" keys — simulates old format

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.Equal(TrackSectionSource.Active, loaded[0].source);
            Assert.Equal(0f, loaded[0].boundaryDiscontinuityMeters);
        }

        #endregion

        #region Log assertion: serialization logs source when non-Active

        [Fact]
        public void Serialize_NonActiveSource_LogsSourceValue()
        {
            var section = MakeSection(source: TrackSectionSource.Background);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("source=Background") && l.Contains("non-default"));
        }

        [Fact]
        public void Serialize_ActiveSource_DoesNotLogSourceValue()
        {
            var section = MakeSection(source: TrackSectionSource.Active);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            Assert.DoesNotContain(logLines, l =>
                l.Contains("source=Active") && l.Contains("non-default"));
        }

        #endregion

        #region Log assertion: deserialization logs source when non-Active

        [Fact]
        public void Deserialize_NonActiveSource_LogsSourceValue()
        {
            var parent = new ConfigNode("TEST");
            var tsNode = parent.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");
            tsNode.AddValue("ref", "0");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17100");
            tsNode.AddValue("sampleRate", "10");
            tsNode.AddValue("src", "2"); // Checkpoint

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.Equal(TrackSectionSource.Checkpoint, loaded[0].source);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("source=Checkpoint"));
        }

        #endregion

        #region Combined: section with all new fields

        [Fact]
        public void RoundTrip_AllNewFields_PreservedTogether()
        {
            var section = MakeSection(
                source: TrackSectionSource.Checkpoint,
                boundaryDiscontinuity: 42.7f);

            var parent = new ConfigNode("TEST");
            RecordingStore.SerializeTrackSections(parent, new List<TrackSection> { section });

            var loaded = new List<TrackSection>();
            RecordingStore.DeserializeTrackSections(parent, loaded);

            Assert.Single(loaded);
            Assert.Equal(TrackSectionSource.Checkpoint, loaded[0].source);
            Assert.Equal(42.7f, loaded[0].boundaryDiscontinuityMeters);
        }

        #endregion

        #region ToString includes new fields

        [Fact]
        public void ToString_IncludesSourceAndBdisc()
        {
            var section = MakeSection(
                source: TrackSectionSource.Background,
                boundaryDiscontinuity: 3.14f);

            string str = section.ToString();
            Assert.Contains("src=Background", str);
            Assert.Contains("bdisc=3.14", str);
        }

        #endregion
    }
}
