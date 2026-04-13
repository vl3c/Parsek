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

        // --- Guard evaluation order: already-fired checked before terminal state ---

        [Fact]
        public void ShouldTriggerExplosion_AlreadyFired_SkipsBeforeCheckingTerminalState()
        {
            // Even with Destroyed state, already-fired should be checked first
            bool result = GhostPlaybackLogic.ShouldTriggerExplosion(
                explosionAlreadyFired: true,
                terminalState: TerminalState.Destroyed,
                ghostExists: true,
                vesselName: "V",
                recIdx: 0);

            Assert.False(result);
        }

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
        }

        [Fact]
        public void ApplyDestroyedFallback_NotDestroyed_NullTerminal_NoChange()
        {
            var rec = new Recording();
            rec.TerminalStateValue = null;

            bool result = ParsekFlight.ApplyDestroyedFallback(false, rec);

            Assert.False(result);
            Assert.Null(rec.TerminalStateValue);
        }

        [Fact]
        public void ApplyDestroyedFallback_NotDestroyed_LandedTerminal_NoChange()
        {
            var rec = new Recording();
            rec.TerminalStateValue = TerminalState.Landed;

            bool result = ParsekFlight.ApplyDestroyedFallback(false, rec);

            Assert.False(result);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
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

        [Theory]
        [InlineData(50f, true)]
        [InlineData(100f, true)]
        [InlineData(1000f, true)]
        public void ShouldTriggerExplosion_PassesButWarpSuppresses_LogsSuppression(float warpRate, bool expectedSuppressed)
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
    }
}
