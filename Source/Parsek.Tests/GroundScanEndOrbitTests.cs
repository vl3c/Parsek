using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-22 (todo ABANDONED-STAGE-CREWED-SUBORBITAL-RESERVED): an abandoned
    /// stage whose low point sits among the body's peaks and whose ground scan finds no crash
    /// ends Orbiting where the scan stopped checking (about 170 passes, or the horizon when
    /// every pass up to it was read). It spawns there on its orbit with its crew aboard, and
    /// stock decides from then on. The spawn safety check lets this one ending through its
    /// periapsis floor; every other ending keeps the floor.
    /// </summary>
    [Collection("Sequential")]
    public class GroundScanEndOrbitTests : IDisposable
    {
        private const double MunGravParameter = 6.5138398e10;
        private const double MunRadius = 200000.0;
        private const double PeakAltitude = 7000.0;

        private readonly List<string> logLines = new List<string>();

        public GroundScanEndOrbitTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static double Magnitude(Vector3d v) => Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

        // Airless body, flat ground at the datum, peaks reported at 7 km.
        private static Dictionary<string, ExtrapolationBody> PeakyMun()
        {
            return new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = new ExtrapolationBody
                {
                    Name = "Mun",
                    GravitationalParameter = MunGravParameter,
                    Radius = MunRadius,
                    MaxTerrainAltitude = PeakAltitude,
                    TerrainAltitude = (double lat, double lon, out double altitude) =>
                    {
                        altitude = 0.0;
                        return true;
                    },
                },
            };
        }

        // A conic from apoapsis with its low point at 5 km (under the 7 km peaks).
        private static BallisticStateVector PeakGrazingStart(out double period)
        {
            double rp = MunRadius + 5000.0;
            double ra = MunRadius + 20000.0;
            double sma = 0.5 * (rp + ra);
            period = 2.0 * Math.PI * Math.Sqrt(sma * sma * sma / MunGravParameter);
            return new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Mun",
                position = new Vector3d(ra, 0.0, 0.0),
                velocity = new Vector3d(0.0, Math.Sqrt(MunGravParameter * (2.0 / ra - 1.0 / sma)), 0.0),
            };
        }

        // ------------------------------------------------------------------ extrapolator

        [Fact]
        public void ScanBudgetSpent_EndsOrbitingWhereTheScanStopped_AboveThePeaks()
        {
            Dictionary<string, ExtrapolationBody> bodies = PeakyMun();
            BallisticStateVector start = PeakGrazingStart(out double period);
            var limits = new ExtrapolationLimits { maxHorizonYears = 1.0, maxSoiTransitions = 4, soiSampleStep = 600.0 };
            double horizonUT = limits.maxHorizonYears * BallisticExtrapolator.SecondsPerYear;

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies, limits);

            Assert.True(result.endedAtGroundScanEnd);
            Assert.False(result.endedOnFinalConic);
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            // Days, not the year-long horizon: the scan budget covers some 170 passes.
            Assert.True(result.terminalUT < horizonUT / 10.0, "terminalUT=" + result.terminalUT);
            Assert.True(result.terminalUT > 100.0 * period, "terminalUT=" + result.terminalUT);
            Assert.Single(result.segments);
            Assert.Equal(result.terminalUT, result.segments[0].endUT, 6);
            // The ending sits between two passes under the peaks, so above every peak.
            Assert.True(Magnitude(result.terminalPosition) - MunRadius >= PeakAltitude,
                "endAlt=" + (Magnitude(result.terminalPosition) - MunRadius));
            Assert.Contains(logLines, l => l.Contains("Later-pass terrain scan budget spent"));
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("[Extrapolator]")
                && l.Contains("Terminal reason=ground-scan-end") && l.Contains("body=Mun")
                && l.Contains("floor=7000.0"));
            Assert.DoesNotContain(logLines, l => l.Contains("Terminal reason=horizon-cap"));
        }

        [Fact]
        public void FinalConicStopOff_StillRunsToTheHorizon()
        {
            Dictionary<string, ExtrapolationBody> bodies = PeakyMun();
            BallisticStateVector start = PeakGrazingStart(out _);
            var limits = new ExtrapolationLimits { maxHorizonYears = 0.002, maxSoiTransitions = 4, soiSampleStep = 60.0 };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                start, bodies, limits, stopOnFinalConic: false);

            Assert.False(result.endedAtGroundScanEnd);
            Assert.Contains(logLines, l => l.Contains("Terminal reason=horizon-cap"));
        }

        [Fact]
        public void LaterPassImpact_StillEndsDestroyed()
        {
            // The same conic over ground that rises to 6 km under its track: a pass meets it.
            Dictionary<string, ExtrapolationBody> bodies = PeakyMun();
            bodies["Mun"].TerrainAltitude = (double lat, double lon, out double altitude) =>
            {
                altitude = 6000.0;
                return true;
            };
            BallisticStateVector start = PeakGrazingStart(out _);
            var limits = new ExtrapolationLimits { maxHorizonYears = 1.0, maxSoiTransitions = 4, soiSampleStep = 600.0 };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies, limits);

            Assert.False(result.endedAtGroundScanEnd);
            Assert.Equal(TerminalState.Destroyed, result.terminalState);
        }

        // ------------------------------------------------------------------ pure helpers

        [Theory]
        // last pass + settle, the next stretch far away
        [InlineData(1000.0, 5000.0, 1e9, 60.0, 1060.0)]
        // the next stretch starts before the settle ends: stop at its start
        [InlineData(1000.0, 1030.0, 1e9, 60.0, 1030.0)]
        // a conic always under the peaks: the next stretch starts at once, keep the last pass
        [InlineData(1000.0, 1000.0, 1e9, 60.0, 1000.0)]
        [InlineData(1000.0, 990.0, 1e9, 60.0, 1000.0)]
        // never past the scan's end
        [InlineData(1000.0, 5000.0, 1020.0, 60.0, 1020.0)]
        // a negative settle reads as none
        [InlineData(1000.0, 5000.0, 1e9, -5.0, 1000.0)]
        public void ResolveGroundScanStopUT_Cases(
            double lastScannedUT, double nextStretchStartUT, double endUT, double settle, double expected)
        {
            Assert.Equal(expected,
                BallisticExtrapolator.ResolveGroundScanStopUT(lastScannedUT, nextStretchStartUT, endUT, settle), 9);
        }

        [Fact]
        public void IsGroundScanEndCandidate_EachGuard()
        {
            double inf = double.PositiveInfinity;
            Assert.True(BallisticExtrapolator.IsGroundScanEndCandidate(true, false, inf, 500.0, 100.0));
            // the stop disabled by the caller
            Assert.False(BallisticExtrapolator.IsGroundScanEndCandidate(false, false, inf, 500.0, 100.0));
            // the scan found an impact (or a deletion periapsis)
            Assert.False(BallisticExtrapolator.IsGroundScanEndCandidate(true, true, inf, 500.0, 100.0));
            // no later-pass scan ran
            Assert.False(BallisticExtrapolator.IsGroundScanEndCandidate(true, false, inf, double.NaN, 100.0));
            Assert.False(BallisticExtrapolator.IsGroundScanEndCandidate(true, false, inf, inf, 100.0));
            // the scan stop is not after the conic's start
            Assert.False(BallisticExtrapolator.IsGroundScanEndCandidate(true, false, inf, 100.0, 100.0));
            // the conic leaves the sphere first (the scan ran up to the exit)
            Assert.False(BallisticExtrapolator.IsGroundScanEndCandidate(true, false, 500.0, 500.0, 100.0));
            // a scan that stopped before the exit ends there
            Assert.True(BallisticExtrapolator.IsGroundScanEndCandidate(true, false, 900.0, 500.0, 100.0));
        }

        // ------------------------------------------------------------------ finalizer

        [Fact]
        public void PeriapsisFloor_KeepsTheGroundScanEndOrbiting_WithALog()
        {
            Dictionary<string, ExtrapolationBody> bodies = PeakyMun();
            BallisticStateVector start = PeakGrazingStart(out _);
            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                start, bodies,
                new ExtrapolationLimits { maxHorizonYears = 1.0, maxSoiTransitions = 4, soiSampleStep = 600.0 });

            Assert.Equal(TerminalState.Orbiting,
                IncompleteBallisticSceneExitFinalizer.ApplyPeriapsisFloorToExtrapolatedTerminal(
                    result.terminalState, result.segments, bodies, "rec-scan-end", groundScanEnd: true));
            Assert.Contains(logLines, l => l.Contains("extrapolated Orbiting for 'rec-scan-end' kept")
                && l.Contains("reason=ground-scan-end"));
            Assert.True(IncompleteBallisticSceneExitFinalizer.IsGroundScanEndOrbit(TerminalState.Orbiting, result));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsGroundScanEndOrbit(TerminalState.SubOrbital, result));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsGroundScanEndOrbit(
                TerminalState.Orbiting, default(ExtrapolationResult)));
        }

        // ------------------------------------------------------------------ recording marker

        [Fact]
        public void Stamp_KeepsTheMarkerOnlyWithAPredictedOrbitingEnding()
        {
            var rec = new Recording { RecordingId = "rec-mark" };
            rec.StampTerminalState(TerminalState.Orbiting, "test", predicted: true, groundScanEnd: true);
            Assert.True(rec.GroundScanEndOrbit);
            Assert.Contains(logLines, l => l.Contains("terminal ground-scan-end flag false->true")
                && l.Contains("rec=rec-mark"));

            rec.StampTerminalState(TerminalState.Orbiting, "test", predicted: false, groundScanEnd: true);
            Assert.False(rec.GroundScanEndOrbit);
            rec.StampTerminalState(TerminalState.SubOrbital, "test", predicted: true, groundScanEnd: true);
            Assert.False(rec.GroundScanEndOrbit);
            rec.StampTerminalState(TerminalState.Orbiting, "test", predicted: true, groundScanEnd: true);
            // any later stamp that does not carry it drops it (a takeover, a stock verdict)
            rec.StampTerminalState(TerminalState.Orbiting, "test", predicted: true);
            Assert.False(rec.GroundScanEndOrbit);
        }

        [Fact]
        public void Codec_RoundTripsTheMarkerSparse()
        {
            var rec = new Recording { RecordingId = "rec-codec" };
            rec.StampTerminalState(TerminalState.Orbiting, "test", predicted: true, groundScanEnd: true);
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);
            Assert.Equal("True", node.GetValue("groundScanEnd"));
            var loaded = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);
            Assert.True(loaded.GroundScanEndOrbit);
            Assert.True(loaded.TerminalPredicted);

            // a recording without it writes no key and reads false
            var plain = new Recording { RecordingId = "rec-plain" };
            plain.StampTerminalState(TerminalState.Orbiting, "test", predicted: true);
            var plainNode = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(plainNode, plain);
            Assert.Null(plainNode.GetValue("groundScanEnd"));

            // a stray key beside another ending reads false
            var strayNode = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(strayNode, plain);
            strayNode.SetValue("terminalState", ((int)TerminalState.SubOrbital).ToString());
            strayNode.AddValue("groundScanEnd", "True");
            var stray = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(strayNode, stray);
            Assert.False(stray.GroundScanEndOrbit);
        }

        // ------------------------------------------------------------------ cache

        [Fact]
        public void CacheApplier_StampsTheMarker_OnlyWithItsPredictedTail()
        {
            var cache = new RecordingFinalizationCache
            {
                GroundScanEnd = true,
                TerminalState = TerminalState.Orbiting,
            };
            var tail = new List<OrbitSegment> { new OrbitSegment { startUT = 0, endUT = 10, isPredicted = true } };
            Assert.True(RecordingFinalizationCacheApplier.IsGroundScanEndCache(cache, tail));
            Assert.False(RecordingFinalizationCacheApplier.IsGroundScanEndCache(cache, new List<OrbitSegment>()));
            cache.TerminalState = TerminalState.SubOrbital;
            Assert.False(RecordingFinalizationCacheApplier.IsGroundScanEndCache(cache, tail));
            cache.TerminalState = TerminalState.Orbiting;
            cache.GroundScanEnd = false;
            Assert.False(RecordingFinalizationCacheApplier.IsGroundScanEndCache(cache, tail));
        }

        [Fact]
        public void BackgroundEndScope_PulledBackGroundScanEnd_GetsTheFloorVerdictBack()
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = "rec-bg",
                Status = FinalizationCacheStatus.Fresh,
                GroundScanEnd = true,
                TerminalState = TerminalState.Orbiting,
                TerminalUT = 5000.0,
                TailStartsAtUT = 100.0,
                PredictedSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100.0, endUT = 5000.0, bodyName = "Mun", isPredicted = true },
                },
            };

            RecordingFinalizationCache kept = BackgroundRecorder.ScopeFinalizationCacheToBackgroundEndForTesting(
                cache, 200.0, retainPredictedTailPastEnd: true);
            Assert.True(kept.GroundScanEnd);
            Assert.Equal(TerminalState.Orbiting, kept.TerminalState);
            Assert.Equal(5000.0, kept.TerminalUT);

            RecordingFinalizationCache pulled = BackgroundRecorder.ScopeFinalizationCacheToBackgroundEndForTesting(
                cache, 200.0, retainPredictedTailPastEnd: false);
            Assert.False(pulled.GroundScanEnd);
            Assert.Equal(TerminalState.SubOrbital, pulled.TerminalState);
            Assert.Contains(logLines, l => l.Contains("ground-scan-end ending pulled back") && l.Contains("rec=rec-bg"));
        }

        // ------------------------------------------------------------------ spawn safety

        [Fact]
        public void SpawnSafety_GroundScanEndOrbit_SpawnsUnderTheFloor_OthersRefused()
        {
            // Mun-like: no atmosphere, peaks at 7 km, low point at 5 km, now at 7.5 km.
            TerminalOrbitSpawnSafetyDecision refused = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                false, 0.0, 7500.0, 5000.0, 20000.0, PeakAltitude, 0.0);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, refused.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude, refused.ReasonCode);

            TerminalOrbitSpawnSafetyDecision spawned = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                false, 0.0, 7500.0, 5000.0, 20000.0, PeakAltitude, 0.0, groundScanEndOrbit: true);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, spawned.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonGroundScanEndOrbit, spawned.ReasonCode);
            Assert.Equal(PeakAltitude, spawned.PeriapsisFloorAltitude);

            // An orbit that clears the floor reads the same with or without the marker.
            TerminalOrbitSpawnSafetyDecision clear = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                false, 0.0, 15000.0, 9000.0, 20000.0, PeakAltitude, 0.0, groundScanEndOrbit: true);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, clear.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonAboveSafeAltitude, clear.ReasonCode);

            // A non-finite periapsis is still refused.
            TerminalOrbitSpawnSafetyDecision nonFinite = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                false, 0.0, 7500.0, double.NaN, 20000.0, PeakAltitude, 0.0, groundScanEndOrbit: true);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, nonFinite.Action);
        }

        [Fact]
        public void SpawnSafety_GroundScanEndOrbit_InTheAtmosphereBand_WaitsForTheClimb()
        {
            // Duna-like: 50 km atmosphere, deletion line 6 km, peaks 8 km; low point at 7 km,
            // now at 20 km, apoapsis 80 km (above the 55 km safe altitude).
            TerminalOrbitSpawnSafetyDecision deferred = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                true, 50000.0, 20000.0, 7000.0, 80000.0, 8000.0, 6000.0, groundScanEndOrbit: true);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.DeferUntilSafe, deferred.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonCurrentAltitudeBelowSafeAltitude, deferred.ReasonCode);

            // An apoapsis that never leaves the band spawns now.
            TerminalOrbitSpawnSafetyDecision low = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                true, 50000.0, 20000.0, 7000.0, 30000.0, 8000.0, 6000.0, groundScanEndOrbit: true);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, low.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonGroundScanEndOrbit, low.ReasonCode);
        }
    }
}
