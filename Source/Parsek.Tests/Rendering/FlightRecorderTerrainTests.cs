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
            RecordingStore.ResetForTesting();
            ParsekFlight.InvalidateTailLiftPlanCache();
            TerrainCacheBuckets.ResetForTesting();
            fakeKerbin = TestBodyRegistry.CreateBody("Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekFlight.InvalidateTailLiftPlanCache();
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
                recordedGroundClearance: double.NaN,
                referenceFrame: ReferenceFrame.Absolute);

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
                recordedGroundClearance: 1.5,   // 1.5 m above current terrain
                referenceFrame: ReferenceFrame.Absolute);

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
                recordedGroundClearance: 1.5,   // recording-time clearance
                referenceFrame: ReferenceFrame.Absolute);

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
                recordedGroundClearance: 2.0,
                referenceFrame: ReferenceFrame.Absolute);

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
                recordedGroundClearance: 1.0,
                referenceFrame: ReferenceFrame.Absolute);
            Assert.Equal(50.0, effective);
        }

        // ----- P2-1 safety gate: non-Absolute frames must skip terrain correction -----

        [Fact]
        public void ResolvePhase7EffectiveAltitude_RelativeFrame_FiniteClearance_ReturnsRecordedAltitude()
        {
            // Recorder never writes finite clearance on a Relative-frame
            // point today, but if a future codec / merge / optimizer ever
            // does, the renderer must NOT interpret the metre-scale
            // anchor-local "latitude" / "longitude" / "altitude" fields as
            // degrees + altitude — that would project a point deep inside
            // the planet (CLAUDE.md "Rotation / world frame" notes). The
            // helper short-circuits to the recorded altitude regardless.
            // Resolver must not be called on this path.
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return 999.0;
            };

            // Synthetic "Relative-frame" point: lat=120 m anchor-local x,
            // lon=-50 m anchor-local y, alt=2 m anchor-local z. If the helper
            // mistakenly applied terrain correction it would treat lat=120 as
            // 120° latitude and try to look up terrain at an invalid lat.
            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin,
                latitude: 120.0,
                longitude: -50.0,
                recordedAltitude: 2.0,
                recordedGroundClearance: 1.5,
                referenceFrame: ReferenceFrame.Relative);

            Assert.Equal(2.0, effective);
            Assert.Equal(0, resolverCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("non-Absolute frame=Relative"));
        }

        [Fact]
        public void ResolvePhase7EffectiveAltitude_OrbitalCheckpointFrame_FiniteClearance_ReturnsRecordedAltitude()
        {
            // OrbitalCheckpoint sections have no per-point lat/lon/alt
            // payload at all (they're driven by Keplerian orbit segments).
            // Any TrajectoryPoint flagged with this frame should never
            // reach terrain correction.
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return 50.0;
            };

            double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin, 0.0, 0.0,
                recordedAltitude: 78000.0,
                recordedGroundClearance: 5.0,
                referenceFrame: ReferenceFrame.OrbitalCheckpoint);

            Assert.Equal(78000.0, effective);
            Assert.Equal(0, resolverCalls);
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
                recordedGroundClearance: clearance,
                referenceFrame: ReferenceFrame.Absolute);

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
                recordedGroundClearance: 3.0,
                referenceFrame: ReferenceFrame.Absolute);

            Assert.Equal(203.0, effective);
        }

        [Fact]
        public void ResolveEffectiveAltitudeWithTailLift_NaNClearance_AddsRampLift()
        {
            Recording rec = CreateTailLiftRecording("tail_lift_active");
            RecordingStore.AddCommittedInternal(rec);
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 112.0;

            double effective = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                fakeKerbin, 1.0, 2.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: double.NaN,
                referenceFrame: ReferenceFrame.Absolute,
                pointUT: 85.0,
                recordingId: rec.RecordingId);

            Assert.Equal(106.0, effective);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("TailLift active")
                && l.Contains(rec.RecordingId));
        }

        [Fact]
        public void ResolveEffectiveAltitudeWithTailLift_FiniteClearance_DoesNotAddRampLift()
        {
            Recording rec = CreateTailLiftRecording("tail_lift_finite_clearance");
            RecordingStore.AddCommittedInternal(rec);
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 112.0;

            double effective = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                fakeKerbin, 1.0, 2.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: 1.5,
                referenceFrame: ReferenceFrame.Absolute,
                pointUT: 85.0,
                recordingId: rec.RecordingId);

            Assert.Equal(113.5, effective);
            Assert.DoesNotContain(logLines, l => l.Contains("TailLift active"));
        }

        [Fact]
        public void ResolveEffectiveAltitudeWithTailLift_CurrentBodyNotTerminal_DoesNotCacheWrongPlan()
        {
            CelestialBody fakeMun = TestBodyRegistry.CreateBody(
                "Mun", radius: 200000.0, gravParameter: 6.5e10);
            Recording rec = CreateTailLiftRecording("tail_lift_terminal_mun", "Mun");
            RecordingStore.AddCommittedInternal(rec);
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
                string.Equals(name, "Mun", StringComparison.Ordinal) ? 112.0 : 300.0;

            double wrongBodyEffective = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                fakeKerbin, 1.0, 2.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: double.NaN,
                referenceFrame: ReferenceFrame.Absolute,
                pointUT: 85.0,
                recordingId: rec.RecordingId);

            double terminalBodyEffective = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                fakeMun, 1.0, 2.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: double.NaN,
                referenceFrame: ReferenceFrame.Absolute,
                pointUT: 85.0,
                recordingId: rec.RecordingId);

            Assert.Equal(100.0, wrongBodyEffective);
            Assert.Equal(106.0, terminalBodyEffective);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("reason=current-body-not-terminal")
                && l.Contains(rec.RecordingId));
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("TailLift active")
                && l.Contains(rec.RecordingId));
        }

        [Fact]
        public void ResolveEffectiveAltitudeWithTailLift_RelativeTerminalFrame_DoesNotApplyLift()
        {
            Recording rec = CreateTailLiftRecording("tail_lift_relative_terminal");
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 70.0,
                endUT = 100.0
            });
            RecordingStore.AddCommittedInternal(rec);
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) => 112.0;

            double effective = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                fakeKerbin, 120.0, -50.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: double.NaN,
                referenceFrame: ReferenceFrame.Absolute,
                pointUT: 85.0,
                recordingId: rec.RecordingId);

            Assert.Equal(100.0, effective);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("reason=terminal-non-absolute-frame")
                && l.Contains(rec.RecordingId));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("TailLift active") && l.Contains(rec.RecordingId));
        }

        [Fact]
        public void ResolveEffectiveAltitudeWithTailLift_TerrainNaN_RetriesLaterFiniteTerrain()
        {
            Recording rec = CreateTailLiftRecording("tail_lift_retry_after_nan");
            RecordingStore.AddCommittedInternal(rec);
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return resolverCalls == 1 ? double.NaN : 112.0;
            };

            double firstEffective = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                fakeKerbin, 1.0, 2.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: double.NaN,
                referenceFrame: ReferenceFrame.Absolute,
                pointUT: 85.0,
                recordingId: rec.RecordingId);

            double secondEffective = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                fakeKerbin, 1.0, 2.0,
                recordedAltitude: 100.0,
                recordedGroundClearance: double.NaN,
                referenceFrame: ReferenceFrame.Absolute,
                pointUT: 85.0,
                recordingId: rec.RecordingId);

            Assert.Equal(100.0, firstEffective);
            Assert.Equal(106.0, secondEffective);
            Assert.Equal(2, resolverCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("reason=current-terrain-nan")
                && l.Contains(rec.RecordingId));
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Terrain]")
                && l.Contains("TailLift active")
                && l.Contains(rec.RecordingId));
        }

        private static Recording CreateTailLiftRecording(
            string recordingId,
            string terminalBodyName = "Kerbin")
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = "TailLift Test",
                TerminalStateValue = TerminalState.Landed,
                TerrainHeightAtEnd = 100.0
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                bodyName = terminalBodyName,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 100.0,
                recordedGroundClearance = double.NaN
            });
            return rec;
        }
    }
}
