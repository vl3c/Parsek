using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 7 (design doc §13.1, §18 Phase 7) tests for the render-time
    /// effective-altitude helper <c>ParsekFlight.ResolvePhase7EffectiveAltitude</c>.
    /// The recorder-side population happens inside KSP's vessel sampling loop
    /// and is exercised by the in-game test
    /// <c>Pipeline_Terrain_RoverClearance_StaysConstant</c>; the xUnit tests
    /// here pin the pure render-time math.
    /// </summary>
    [Collection("Sequential")]
    public class FlightRecorderTerrainTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody fakeKerbin;

        public FlightRecorderTerrainTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            TerrainCacheBuckets.ResetForTesting();
            // Stub Unity Time.frameCount (xUnit cannot bind the ECall).
            TerrainCacheBuckets.FrameCountResolverForTesting = () => 1L;
            fakeKerbin = TestBodyRegistry.CreateBody("Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
        }

        public void Dispose()
        {
            TerrainCacheBuckets.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        // ----- NaN clearance ⇒ legacy fall-through -----

        [Fact]
        public void ResolvePhase7EffectiveAltitude_NaNClearance_ReturnsRecordedAltitude()
        {
            // Resolver should never be called when clearance is NaN.
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return 50.0;
            };

            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin, 0.0, 0.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: double.NaN);

            Assert.Equal(100.0, effective);
            Assert.Equal(0, resolverCalls);
        }

        // ----- finite clearance ⇒ effective = current_terrain + clearance -----

        [Fact]
        public void ResolvePhase7EffectiveAltitude_FiniteClearance_AppliesTerrainPlusClearance()
        {
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 75.0;

            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin, 0.0, 0.0,
                recordedAltitude: 80.0,         // would have been used in legacy path
                recordedGroundClearance: 1.5);  // 1.5 m above current terrain

            // current_terrain (75.0) + clearance (1.5) = 76.5
            Assert.Equal(76.5, effective);
        }

        [Fact]
        public void ResolvePhase7EffectiveAltitude_TerrainShifted_KeepsConstantClearance()
        {
            // Recording-time terrain was 75 m at this lat/lon; rover sat 1.5 m
            // above. Playback session terrain regenerates 4 m higher (79 m),
            // but the rover should still appear 1.5 m above terrain.
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 79.0;

            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin, 0.0, 0.0,
                recordedAltitude: 76.5,         // recording-time root altitude
                recordedGroundClearance: 1.5);  // recording-time clearance

            // Effective altitude = 79 + 1.5 = 80.5 — rover lifted 4 m so it
            // sits on the new terrain at the same clearance.
            Assert.Equal(80.5, effective);
            // Visual contract: clearance is preserved despite terrain shift.
            double effectiveClearance = effective - 79.0;
            Assert.Equal(1.5, effectiveClearance);
        }

        // ----- resolver returns NaN ⇒ fall back to recorded altitude -----

        [Fact]
        public void ResolvePhase7EffectiveAltitude_ResolverNaN_FallsBackToRecordedAltitude()
        {
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => double.NaN;

            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin, 0.0, 0.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: 2.0);

            Assert.Equal(100.0, effective);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("ResolvePhase7EffectiveAltitude")
                && l.Contains("falling back to recorded altitude"));
        }

        // ----- null body short-circuit -----

        [Fact]
        public void ResolvePhase7EffectiveAltitude_NullBody_ReturnsRecordedAltitude()
        {
            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                null, 0.0, 0.0,
                recordedAltitude: 50.0,
                recordedGroundClearance: 1.0);
            Assert.Equal(50.0, effective);
        }

        // ----- clearance computation contract (pure math, mirrors recorder side) -----

        [Theory]
        [InlineData(100.0, 50.0, 50.0)]    // 100 m alt over 50 m terrain → 50 m clearance
        [InlineData(76.0, 75.0, 1.0)]      // rover slightly above terrain
        [InlineData(75.0, 75.0, 0.0)]      // sitting flat on terrain (valid 0 clearance)
        [InlineData(70.0, 75.0, -5.0)]     // recorded below terrain (mesh embedded)
        public void RecordedClearance_EqualsAltitudeMinusTerrain(
            double altitude, double terrainHeight, double expectedClearance)
        {
            // The recorder computes clearance as (altitude - terrainHeight).
            // Verify the symmetry: terrain_at_play + clearance reproduces
            // altitude when the play-time terrain matches the recording-time
            // terrain.
            double clearance = altitude - terrainHeight;
            Assert.Equal(expectedClearance, clearance);

            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => terrainHeight;
            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin, 0.0, 0.0,
                recordedAltitude: altitude,
                recordedGroundClearance: clearance);

            Assert.Equal(altitude, effective);
        }

        // ----- equator vs pole resolution still works (just the lat doesn't matter for the math) -----

        [Theory]
        [InlineData(0.0, 0.0)]       // equator
        [InlineData(89.999, 0.0)]    // near north pole
        [InlineData(-89.999, 180.0)] // near south pole, antimeridian
        public void ResolvePhase7EffectiveAltitude_AnyLatLon_AppliesFormula(double lat, double lon)
        {
            TerrainCacheBuckets.TerrainResolverForTesting = (name, plat, plon) => 200.0;

            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin, lat, lon,
                recordedAltitude: 250.0,
                recordedGroundClearance: 3.0);

            Assert.Equal(203.0, effective);
        }
    }
}
