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

        // ComputeCorrectedAltitude removed (#309): terrain-relative correction buried
        // vessels recorded on mesh objects (Island Airfield, launchpad, KSC buildings)
        // because body.TerrainAltitude() is PQS-only and blind to placed colliders.
        // Replaced by "trust recorded altitude + underground safety floor" semantics
        // in VesselSpawner.ClampAltitudeForLanded and ParsekFlight.ApplyLandedGhostClearance.

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

        #region ClampAltitude

        [Fact]
        public void ClampAltitude_AboveTerrain_NoChange()
        {
            // Ghost at 75m, terrain at 70m — already above terrain + 0.5m
            double result = TerrainCorrector.ClampAltitude(75.0, 70.0);
            Assert.Equal(75.0, result);
        }

        [Fact]
        public void ClampAltitude_BelowTerrain_ClampsUp()
        {
            // Ghost at 65m, terrain at 70m — below terrain
            double result = TerrainCorrector.ClampAltitude(65.0, 70.0);
            Assert.Equal(70.5, result);
        }

        [Fact]
        public void ClampAltitude_AtTerrain_ClampsUp()
        {
            // Ghost exactly at terrain height — needs clearance
            double result = TerrainCorrector.ClampAltitude(70.0, 70.0);
            Assert.Equal(70.5, result);
        }

        [Fact]
        public void ClampAltitude_NegativeTerrain_Ocean_NoChange()
        {
            // Ghost at sea level (0m), ocean floor at -500m — already above
            double result = TerrainCorrector.ClampAltitude(0.0, -500.0);
            Assert.Equal(0.0, result);
        }

        [Fact]
        public void ClampAltitude_DeepUnderground_ClampsUp()
        {
            // Ghost at -8444m (orbit reconstruction underground), terrain at 70m
            double result = TerrainCorrector.ClampAltitude(-8444.0, 70.0);
            Assert.Equal(70.5, result);
        }

        [Fact]
        public void ClampAltitude_CustomClearance()
        {
            double result = TerrainCorrector.ClampAltitude(70.0, 70.0, 1.0);
            Assert.Equal(71.0, result);
        }

        [Fact]
        public void ClampAltitude_LongRangeWatchRepro_UsesDistanceAwareClearance()
        {
            // Repro from logs/2026-04-13_2136: watched landed ghost #19 was in
            // Visual zone at 18.621 km with alt=283.8 and PQS terrain=283.1.
            // The distance-based clearance at that range is ~2.42 m, so the
            // visual floor should be ~285.52 m instead of the old 283.6 m
            // (terrain + 0.5 m) last-frame floor.
            double clearance = ParsekFlight.ComputeTerrainClearance(18621.0);
            double result = TerrainCorrector.ClampAltitude(283.8, 283.1, clearance);
            Assert.Equal(283.1 + clearance, result, 3);
        }

        [Fact]
        public void ResolveNaNFallbackLandedGhostClearance_BelowLegacyFloor_UsesLegacyMinimum()
        {
            double clearance = ParsekFlight.ResolveNaNFallbackLandedGhostClearanceMeters(2.42);
            Assert.Equal(VesselSpawner.LandedGhostClearanceMeters, clearance, 3);
        }

        [Fact]
        public void ResolveNaNFallbackLandedGhostClearance_AboveLegacyFloor_UsesDistanceAwareFloor()
        {
            double clearance = ParsekFlight.ResolveNaNFallbackLandedGhostClearanceMeters(5.0);
            Assert.Equal(5.0, clearance, 3);
        }

        [Fact]
        public void ResolveImmediateLandedGhostClearanceFallbackReason_AllLive_ReturnsNull()
        {
            string reason = ParsekFlight.ResolveImmediateLandedGhostClearanceFallbackReason(
                hasBody: true, hasActiveVessel: true);
            Assert.Null(reason);
        }

        [Fact]
        public void ResolveImmediateLandedGhostClearanceFallbackReason_NoBody_ReturnsNoBody()
        {
            string reason = ParsekFlight.ResolveImmediateLandedGhostClearanceFallbackReason(
                hasBody: false, hasActiveVessel: true);
            Assert.Equal("no-body", reason);
        }

        [Fact]
        public void ResolveImmediateLandedGhostClearanceFallbackReason_NoActiveVessel_ReturnsNoActiveVessel()
        {
            string reason = ParsekFlight.ResolveImmediateLandedGhostClearanceFallbackReason(
                hasBody: true, hasActiveVessel: false);
            Assert.Equal("no-active-vessel", reason);
        }

        [Fact]
        public void ResolveImmediateLandedGhostClearanceFallbackReason_NeitherLive_ReturnsCombined()
        {
            string reason = ParsekFlight.ResolveImmediateLandedGhostClearanceFallbackReason(
                hasBody: false, hasActiveVessel: false);
            Assert.Equal("no-body-and-no-active-vessel", reason);
        }

        [Fact]
        public void ImmediateLandedGhostClearanceFallbackMeters_MatchesLegacyFloor()
        {
            // #373: Pin the fallback floor at 0.5 m so a future refactor that
            // silently changes this constant immediately breaks the test.
            Assert.Equal(0.5, ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters);
        }

        [Fact]
        public void ShouldApplyImmediateSurfacePositionClearance_NaNTerrain_False()
        {
            Assert.False(ParsekFlight.ShouldApplyImmediateSurfacePositionClearance(double.NaN));
        }

        [Fact]
        public void ShouldApplyImmediateSurfacePositionClearance_CapturedTerrain_True()
        {
            Assert.True(ParsekFlight.ShouldApplyImmediateSurfacePositionClearance(283.1));
        }

        [Fact]
        public void ClampAltitude_JustAboveClearance_NoChange()
        {
            // Ghost at 70.6m, terrain at 70m, clearance 0.5m — just above threshold
            double result = TerrainCorrector.ClampAltitude(70.6, 70.0);
            Assert.Equal(70.6, result);
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
