using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for explosion FX trigger logic and RecordingBuilder terminal state support.
    /// Validates guard conditions, logging, and serialization roundtrip.
    /// </summary>
    [Collection("Sequential")]
    public class ExplosionFxTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ExplosionFxTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- ShouldTriggerExplosion guard condition tests ---

        [Fact]
        public void ShouldTriggerExplosion_Destroyed_GhostAlive_ReturnsTrue()
        {
            bool result = GhostPlaybackLogic.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: TerminalState.Destroyed,
                ghostExists: true,
                vesselName: "TestVessel",
                recIdx: 3);

            Assert.True(result);
        }

        [Fact]
        public void ShouldTriggerExplosion_AlreadyFired_ReturnsFalse()
        {
            bool result = GhostPlaybackLogic.ShouldTriggerExplosion(
                explosionAlreadyFired: true,
                terminalState: TerminalState.Destroyed,
                ghostExists: true,
                vesselName: "TestVessel",
                recIdx: 5);

            Assert.False(result);
        }

        [Fact]
        public void ShouldTriggerExplosion_NotDestroyed_Landed_ReturnsFalse()
        {
            bool result = GhostPlaybackLogic.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: TerminalState.Landed,
                ghostExists: true,
                vesselName: "Lander",
                recIdx: 1);

            Assert.False(result);
        }

        [Fact]
        public void ShouldTriggerExplosion_NullTerminalState_ReturnsFalse()
        {
            bool result = GhostPlaybackLogic.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: null,
                ghostExists: true,
                vesselName: "Legacy",
                recIdx: 0);

            Assert.False(result);
        }

        [Fact]
        public void ShouldTriggerExplosion_GhostNull_ReturnsFalse()
        {
            bool result = GhostPlaybackLogic.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: TerminalState.Destroyed,
                ghostExists: false,
                vesselName: "NoGhost",
                recIdx: 7);

            Assert.False(result);
        }

        [Theory]
        [InlineData(TerminalState.Orbiting)]
        [InlineData(TerminalState.Splashed)]
        [InlineData(TerminalState.SubOrbital)]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void ShouldTriggerExplosion_NonDestroyedStates_AllReturnFalse(TerminalState state)
        {
            bool result = GhostPlaybackLogic.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: state,
                ghostExists: true,
                vesselName: "Vessel",
                recIdx: 0);

            Assert.False(result);
        }

        [Fact]
        public void TryGetEarlyDestroyedDebrisExplosionUT_DestroyedDebris_ReturnsFirstDestroyedEvent()
        {
            var rec = new Recording
            {
                IsDebris = true,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 20.0
            };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 11.5,
                eventType = PartEventType.EngineIgnited,
                partName = "engine"
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 12.5,
                eventType = PartEventType.Destroyed,
                partName = "tank"
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 14.0,
                eventType = PartEventType.Destroyed,
                partName = "noseCone"
            });

            bool result = GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(rec, out double explosionUT);

            Assert.True(result);
            Assert.Equal(12.5, explosionUT, 3);
        }

        [Fact]
        public void TryGetEarlyDestroyedDebrisExplosionUT_PredictedTailPastLastPoint_FollowsTheTail()
        {
            // L4: debris that lost a part and then was extrapolated to the ground must not explode
            // at that first lost part and skip the rest of its tail.
            var rec = new Recording
            {
                IsDebris = true,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitEndUT = 300.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 10.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 20.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 20.0, endUT = 300.0, bodyName = "Kerbin",
                semiMajorAxis = 650000.0, eccentricity = 0.1, isPredicted = true
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 12.5,
                eventType = PartEventType.Destroyed,
                partName = "tank"
            });

            Assert.True(GhostPlaybackLogic.HasPredictedTailPastLastPoint(rec));
            Assert.False(GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(rec, out double explosionUT));
            Assert.True(double.IsNaN(explosionUT));
            Assert.False(GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(rec, 100.0));
            Assert.True(GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(rec, 300.0));
        }

        [Fact]
        public void HasPredictedTailPastLastPoint_RecordedSegmentOnly_False()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 10.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 20.0 });
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 20.0, endUT = 300.0, isPredicted = false });
            Assert.False(GhostPlaybackLogic.HasPredictedTailPastLastPoint(rec));
            rec.OrbitSegments[0] = new OrbitSegment { startUT = 5.0, endUT = 15.0, isPredicted = true };
            Assert.False(GhostPlaybackLogic.HasPredictedTailPastLastPoint(rec));
        }

        [Fact]
        public void TryGetEarlyDestroyedDebrisExplosionUT_OnlyEndDestroyedEvent_ReturnsFalse()
        {
            var rec = new Recording
            {
                IsDebris = true,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 20.0
            };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 20.0,
                eventType = PartEventType.Destroyed,
                partName = "tank"
            });

            bool result = GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(rec, out double explosionUT);

            Assert.False(result);
            Assert.True(double.IsNaN(explosionUT));
        }

        [Fact]
        public void TryGetEarlyDestroyedDebrisExplosionUT_NonDebris_ReturnsFalse()
        {
            var rec = new Recording
            {
                IsDebris = false,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 20.0
            };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 12.5,
                eventType = PartEventType.Destroyed,
                partName = "tank"
            });

            bool result = GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(rec, out double explosionUT);

            Assert.False(result);
            Assert.True(double.IsNaN(explosionUT));
        }

        [Fact]
        public void TryGetEarlyDestroyedDebrisExplosionUT_UnsortedEvents_ReturnsEarliestEligibleDestroyedEvent()
        {
            var rec = new Recording
            {
                IsDebris = true,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 20.0
            };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 19.9,
                eventType = PartEventType.Destroyed,
                partName = "late"
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 13.0,
                eventType = PartEventType.Destroyed,
                partName = "mid"
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 12.0,
                eventType = PartEventType.Destroyed,
                partName = "early"
            });

            bool result = GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(rec, out double explosionUT);

            Assert.True(result);
            Assert.Equal(12.0, explosionUT, 3);
        }

        [Fact]
        public void ShouldTriggerExplosionAtPlaybackUT_LoopEndpointBeforeDestroyedEnd_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 300.0,
                LoopStartUT = 100.0,
                LoopEndUT = 200.0
            };

            bool result = GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                rec, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(rec));

            Assert.False(result);
        }

        [Fact]
        public void ShouldTriggerExplosionAtPlaybackUT_LoopEndpointAfterEarlyDestroyedDebrisEvent_ReturnsTrue()
        {
            var rec = new Recording
            {
                IsDebris = true,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 300.0,
                LoopStartUT = 100.0,
                LoopEndUT = 200.0
            };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 180.0,
                eventType = PartEventType.Destroyed,
                partName = "tank"
            });

            bool result = GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                rec, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(rec));

            Assert.True(result);
        }

        // --- Guard evaluation order: already-fired checked before terminal state ---

        // --- ApplyDestroyedFallback tests ---

        [Fact]
        public void ApplyDestroyedFallback_WasDestroyed_NullTerminal_SetsDestroyed()
        {
            var rec = new Recording();
            rec.TerminalStateValue = null;

            bool result = ParsekFlight.ApplyDestroyedFallback(true, rec);

            Assert.True(result);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Contains(logLines, l => l.Contains("overriding TerminalState") && l.Contains("null"));
        }

        [Fact]
        public void ApplyDestroyedFallback_WasDestroyed_LandedTerminal_OverridesToDestroyed()
        {
            var rec = new Recording();
            rec.TerminalStateValue = TerminalState.Landed;

            bool result = ParsekFlight.ApplyDestroyedFallback(true, rec);

            Assert.True(result);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Contains(logLines, l => l.Contains("overriding TerminalState") && l.Contains("Landed"));
        }

        [Fact]
        public void ApplyDestroyedFallback_WasDestroyed_AlreadyDestroyed_NoChange()
        {
            var rec = new Recording();
            rec.TerminalStateValue = TerminalState.Destroyed;

            bool result = ParsekFlight.ApplyDestroyedFallback(true, rec);

            Assert.False(result);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            // "No change" covers the terminal only: VesselDestroyed is still set,
            // ahead of the early return, because SwitchSegmentNoOpClassifier reads it.
            Assert.True(rec.VesselDestroyed);
        }

        [Fact]
        public void ApplyDestroyedFallback_WasDestroyed_SetsVesselDestroyedFlag()
        {
            // The VesselDestroyed bool is set UNCONDITIONALLY, ahead of the
            // already-Destroyed early return: it is the field
            // SwitchSegmentNoOpClassifier gates on, so a destruction that emits
            // no onPartDie Destroyed PartEvent would otherwise leave the flag
            // false and let a destroyed resume auto-discard as a no-op coast.
            // The already-Destroyed row is the one the early return can hide.
            var alreadyDestroyed = new Recording();
            alreadyDestroyed.TerminalStateValue = TerminalState.Destroyed;
            alreadyDestroyed.VesselDestroyed = false;

            bool result = ParsekFlight.ApplyDestroyedFallback(true, alreadyDestroyed);

            Assert.False(result);
            Assert.True(alreadyDestroyed.VesselDestroyed);

            // Mirror row: the re-stamping arm sets it too.
            var landed = new Recording();
            landed.TerminalStateValue = TerminalState.Landed;
            landed.VesselDestroyed = false;

            Assert.True(ParsekFlight.ApplyDestroyedFallback(true, landed));
            Assert.True(landed.VesselDestroyed);

            // Negative row: no destruction, no flag.
            var untouched = new Recording();
            untouched.TerminalStateValue = TerminalState.Landed;
            untouched.VesselDestroyed = false;

            Assert.False(ParsekFlight.ApplyDestroyedFallback(false, untouched));
            Assert.False(untouched.VesselDestroyed);
        }

        [Fact]
        public void ApplyDestroyedFallback_NotDestroyed_NullTerminal_NoChange()
        {
            var rec = new Recording();
            rec.TerminalStateValue = null;

            bool result = ParsekFlight.ApplyDestroyedFallback(false, rec);

            Assert.False(result);
            Assert.Null(rec.TerminalStateValue);
            Assert.False(rec.VesselDestroyed);
        }

        [Fact]
        public void ApplyDestroyedFallback_NotDestroyed_LandedTerminal_NoChange()
        {
            var rec = new Recording();
            rec.TerminalStateValue = TerminalState.Landed;

            bool result = ParsekFlight.ApplyDestroyedFallback(false, rec);

            Assert.False(result);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.False(rec.VesselDestroyed);
        }

        // --- RecordingBuilder.WithTerminalState serialization ---

        [Fact]
        public void WithTerminalState_Build_EmitsTerminalStateValue()
        {
            var builder = new RecordingBuilder("Crasher");
            builder.WithTerminalState((int)TerminalState.Destroyed);
            builder.AddPoint(100, -0.09, -74.5, 70);

            var node = builder.Build();

            string val = node.GetValue("terminalState");
            Assert.NotNull(val);
            Assert.Equal("4", val);
        }

        [Fact]
        public void WithTerminalState_BuildV3Metadata_EmitsTerminalStateValue()
        {
            var builder = new RecordingBuilder("Crasher");
            builder.WithTerminalState((int)TerminalState.Destroyed);
            builder.AddPoint(100, -0.09, -74.5, 70);

            var node = builder.BuildV3Metadata();

            string val = node.GetValue("terminalState");
            Assert.NotNull(val);
            Assert.Equal("4", val);
        }

        [Fact]
        public void WithoutTerminalState_Build_OmitsTerminalStateKey()
        {
            var builder = new RecordingBuilder("Normal");
            builder.AddPoint(100, -0.09, -74.5, 70);

            var node = builder.Build();

            Assert.Null(node.GetValue("terminalState"));
        }

        [Fact]
        public void WithoutTerminalState_BuildV3Metadata_OmitsTerminalStateKey()
        {
            var builder = new RecordingBuilder("Normal");
            builder.AddPoint(100, -0.09, -74.5, 70);

            var node = builder.BuildV3Metadata();

            Assert.Null(node.GetValue("terminalState"));
        }

        [Theory]
        [InlineData(0, "0")]   // Orbiting
        [InlineData(1, "1")]   // Landed
        [InlineData(4, "4")]   // Destroyed
        [InlineData(5, "5")]   // Recovered
        public void WithTerminalState_AllValues_RoundtripCorrectly(int stateInt, string expected)
        {
            var builder = new RecordingBuilder("V");
            builder.WithTerminalState(stateInt);
            builder.AddPoint(100, 0, 0, 70);

            var buildNode = builder.Build();
            Assert.Equal(expected, buildNode.GetValue("terminalState"));

            var v3Node = builder.BuildV3Metadata();
            Assert.Equal(expected, v3Node.GetValue("terminalState"));
        }

        // --- KscPadDestroyed synthetic recording has terminal state ---

        [Fact]
        public void KscPadDestroyed_HasDestroyedTerminalState()
        {
            var node = SyntheticRecordingTests.KscPadDestroyed().Build();

            string val = node.GetValue("terminalState");
            Assert.NotNull(val);
            Assert.Equal("4", val); // TerminalState.Destroyed
        }

        [Fact]
        public void KscPadDestroyed_V3Metadata_HasDestroyedTerminalState()
        {
            var node = SyntheticRecordingTests.KscPadDestroyed().BuildV3Metadata();

            string val = node.GetValue("terminalState");
            Assert.NotNull(val);
            Assert.Equal("4", val);
        }

        // --- Warp suppression tests ---

        // Renamed: neither helper logs, so the old "_LogsSuppression" name promised an
        // assertion the cell never made. What the pair can prove is that every explosion
        // guard passes and the warp gate decides on its own, so both arms of that gate
        // are pinned here - the boundary rows (at and just above FxSuppress = 10x) make
        // the strict `>` and the constant the deciding terms rather than the rate range.
        [Theory]
        [InlineData(50f, true)]
        [InlineData(100f, true)]
        [InlineData(1000f, true)]
        [InlineData(10.01f, true)]
        [InlineData(10f, false)]
        [InlineData(5f, false)]
        [InlineData(1f, false)]
        public void ShouldTriggerExplosion_AllGuardsPass_WarpGateDecidesFxSuppression(float warpRate, bool expectedSuppressed)
        {
            // ShouldTriggerExplosion returns true (all guards pass)
            bool wouldFire = GhostPlaybackLogic.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: TerminalState.Destroyed,
                ghostExists: true,
                vesselName: "WarpTestVessel",
                recIdx: 7);

            Assert.True(wouldFire);

            // But ShouldSuppressVisualFx prevents the actual FX
            bool suppressed = GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate);
            Assert.Equal(expectedSuppressed, suppressed);
        }

        [Fact]
        public void ShouldSuppressVisualFx_At10x_DoesNotSuppress()
        {
            Assert.False(GhostPlaybackLogic.ShouldSuppressVisualFx(10f));
        }

        [Fact]
        public void ShouldSuppressVisualFx_Above10x_Suppresses()
        {
            Assert.True(GhostPlaybackLogic.ShouldSuppressVisualFx(50f));
        }

        // --- Owner ruling L2: the crash explosion plays at any warp when the ghost is in view ---

        [Theory]
        [InlineData(1f, 1.0e9, true)]        // low warp: always plays, as before
        [InlineData(10f, 1.0e9, true)]
        [InlineData(50f, 1000.0, true)]      // high warp, close: plays
        [InlineData(1000f, 49000.0, true)]
        [InlineData(1000f, 50000.0, false)]  // high warp, past the mesh cutoff: just ends
        [InlineData(100000f, 2.0e6, false)]
        public void ShouldPlayExplosionFx_HighWarp_PlaysOnlyInView(
            float warpRate, double viewDistance, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ShouldPlayExplosionFx(
                warpRate, isWatched: false, viewDistanceMeters: viewDistance,
                maxVisibleMeters: GhostPlaybackLogic.ExplosionInViewMaxMeters));
        }

        [Fact]
        public void ShouldPlayExplosionFx_HighWarp_WatchedGhost_Plays()
        {
            Assert.True(GhostPlaybackLogic.ShouldPlayExplosionFx(
                1000f, isWatched: true, viewDistanceMeters: double.NaN,
                maxVisibleMeters: GhostPlaybackLogic.ExplosionInViewMaxMeters));
        }

        [Fact]
        public void ShouldPlayExplosionFx_HighWarp_UnknownDistance_Ends()
        {
            Assert.False(GhostPlaybackLogic.ShouldPlayExplosionFx(
                1000f, isWatched: false, viewDistanceMeters: double.NaN,
                maxVisibleMeters: GhostPlaybackLogic.ExplosionInViewMaxMeters));
            Assert.False(GhostPlaybackLogic.ShouldPlayExplosionFx(
                1000f, isWatched: false, viewDistanceMeters: double.PositiveInfinity,
                maxVisibleMeters: GhostPlaybackLogic.ExplosionInViewMaxMeters));
        }

        [Fact]
        public void ExplosionInViewMaxMeters_IsTheMeshLodCutoff()
        {
            // An explosion is "in view" exactly where an unwatched ghost mesh still draws.
            Assert.Equal(DistanceThresholds.GhostFlight.LoopSimplifiedMeters,
                GhostPlaybackLogic.ExplosionInViewMaxMeters);
        }

        [Fact]
        public void ResolveExplosionViewDistance_PrefersTheRenderDistance()
        {
            var state = new GhostPlaybackState { lastDistance = 9000.0, lastRenderDistance = 1200.0 };
            Assert.Equal(1200.0, GhostPlaybackLogic.ResolveExplosionViewDistance(state));

            state.lastRenderDistance = double.NaN;
            Assert.Equal(9000.0, GhostPlaybackLogic.ResolveExplosionViewDistance(state));

            Assert.True(double.IsNaN(GhostPlaybackLogic.ResolveExplosionViewDistance(null)));
        }
    }
}
