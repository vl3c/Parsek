using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for TerrainCorrector pure math and ShouldCorrectTerrain decision,
    /// plus serialization round-trip for the TerrainHeightAtEnd field.
    /// </summary>
    [Collection("Sequential")]
    public class TerrainCorrectorTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TerrainCorrectorTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
        }

        #region ComputeCorrectedAltitude

        [Fact]
        public void ComputeCorrectedAltitude_TerrainHigher_AltitudeIncreases()
        {
            // recordedAlt=100, recordedTerrain=90, currentTerrain=100
            // Expected: 100 + (100-90) = 110
            // Guards: vessel clips into ground when terrain rises
            double result = TerrainCorrector.ComputeCorrectedAltitude(100.0, 100.0, 90.0);
            Assert.Equal(110.0, result);

            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("corrected=110"));
        }

        [Fact]
        public void ComputeCorrectedAltitude_TerrainLower_AltitudeDecreases()
        {
            // recordedAlt=100, recordedTerrain=90, currentTerrain=80
            // Expected: 80 + (100-90) = 90
            // Guards: vessel floats when terrain drops
            double result = TerrainCorrector.ComputeCorrectedAltitude(80.0, 100.0, 90.0);
            Assert.Equal(90.0, result);

            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("corrected=90"));
        }

        [Fact]
        public void ComputeCorrectedAltitude_TerrainUnchanged_AltitudeSame()
        {
            // recordedAlt=100, recordedTerrain=90, currentTerrain=90
            // Expected: 90 + (100-90) = 100
            // Guards: unnecessary correction
            double result = TerrainCorrector.ComputeCorrectedAltitude(90.0, 100.0, 90.0);
            Assert.Equal(100.0, result);

            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("corrected=100"));
        }

        #endregion

        #region ShouldCorrectTerrain

        [Fact]
        public void ShouldCorrectTerrain_Landed_ValidHeight_True()
        {
            bool result = TerrainCorrector.ShouldCorrectTerrain(TerminalState.Landed, 90.0);
            Assert.True(result);

            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("returning true"));
        }

        [Fact]
        public void ShouldCorrectTerrain_Splashed_ValidHeight_True()
        {
            bool result = TerrainCorrector.ShouldCorrectTerrain(TerminalState.Splashed, -5.0);
            Assert.True(result);

            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("returning true"));
        }

        [Fact]
        public void ShouldCorrectTerrain_Orbiting_False()
        {
            bool result = TerrainCorrector.ShouldCorrectTerrain(TerminalState.Orbiting, 90.0);
            Assert.False(result);

            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("not surface"));
        }

        [Fact]
        public void ShouldCorrectTerrain_NaNHeight_False()
        {
            bool result = TerrainCorrector.ShouldCorrectTerrain(TerminalState.Landed, double.NaN);
            Assert.False(result);

            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("NaN"));
        }

        [Fact]
        public void ShouldCorrectTerrain_NullTerminal_False()
        {
            bool result = TerrainCorrector.ShouldCorrectTerrain(null, 90.0);
            Assert.False(result);

            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("no terminal state"));
        }

        [Fact]
        public void ShouldCorrectTerrain_Destroyed_False()
        {
            bool result = TerrainCorrector.ShouldCorrectTerrain(TerminalState.Destroyed, 90.0);
            Assert.False(result);
        }

        [Fact]
        public void ShouldCorrectTerrain_SubOrbital_False()
        {
            bool result = TerrainCorrector.ShouldCorrectTerrain(TerminalState.SubOrbital, 90.0);
            Assert.False(result);
        }

        #endregion

        #region Serialization round-trip

        [Fact]
        public void TerrainHeight_SerializationRoundTrip()
        {
            // Build Recording with TerrainHeightAtEnd=123.456
            var rec = new Recording();
            rec.RecordingId = "terrain_roundtrip_test";
            rec.TerrainHeightAtEnd = 123.456;
            rec.TerminalStateValue = TerminalState.Landed;

            // Save to ConfigNode via RecordingTree serialization
            var recNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(recNode, rec);

            // Verify serialized value
            string thtVal = recNode.GetValue("terrainHeightAtEnd");
            Assert.NotNull(thtVal);

            // Load back
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(recNode, loaded);

            // Verify value preserved
            Assert.Equal(123.456, loaded.TerrainHeightAtEnd);
        }

        [Fact]
        public void TerrainHeight_NaN_NotSerialized()
        {
            // Recording with NaN TerrainHeightAtEnd (default)
            var rec = new Recording();
            rec.RecordingId = "terrain_nan_test";
            Assert.True(double.IsNaN(rec.TerrainHeightAtEnd));

            // Save to ConfigNode
            var recNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(recNode, rec);

            // Verify no "terrainHeightAtEnd" key in the node
            string thtVal = recNode.GetValue("terrainHeightAtEnd");
            Assert.Null(thtVal);

            // Load back — should remain NaN
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(recNode, loaded);
            Assert.True(double.IsNaN(loaded.TerrainHeightAtEnd));
        }

        [Fact]
        public void TerrainHeight_DefaultIsNaN()
        {
            var rec = new Recording();
            Assert.True(double.IsNaN(rec.TerrainHeightAtEnd));
        }

        [Fact]
        public void TerrainHeight_ApplyPersistenceArtifacts_Copies()
        {
            var source = new Recording();
            source.TerrainHeightAtEnd = 456.789;

            var target = new Recording();
            Assert.True(double.IsNaN(target.TerrainHeightAtEnd));

            target.ApplyPersistenceArtifactsFrom(source);
            Assert.Equal(456.789, target.TerrainHeightAtEnd);
        }

        [Fact]
        public void TerrainHeight_ApplyPersistenceArtifacts_CopiesNaN()
        {
            var source = new Recording();
            // source.TerrainHeightAtEnd is NaN by default

            var target = new Recording();
            target.TerrainHeightAtEnd = 100.0;

            target.ApplyPersistenceArtifactsFrom(source);
            Assert.True(double.IsNaN(target.TerrainHeightAtEnd));
        }

        [Fact]
        public void TerrainHeight_NegativeValue_RoundTrips()
        {
            // Negative terrain heights occur for ocean floors on bodies with oceans
            var rec = new Recording();
            rec.RecordingId = "terrain_negative_test";
            rec.TerrainHeightAtEnd = -42.5;

            var recNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(recNode, rec);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(recNode, loaded);
            Assert.Equal(-42.5, loaded.TerrainHeightAtEnd);
        }

        #endregion
    }
}
