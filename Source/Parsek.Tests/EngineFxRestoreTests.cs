using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers the engine-FX restart that pairs with StopAllEngineFx so a ghost crossing the
    /// FX-LOD range and returning (canonical case: a looping aircraft) gets its event-driven
    /// plume FX back instead of staying dark until the next recorded throttle change.
    ///
    /// The decision is tested here as a pure predicate
    /// (<see cref="Parsek.GhostPlaybackEngine.ShouldRestartEngineFxAfterSuppression"/>). The
    /// renderer / particle-system application in GhostPlaybackLogic.RestoreActiveEngineFx
    /// touches Unity ECalls (ParticleSystem.Play) that cannot be JIT-compiled in the xUnit
    /// host, so its visual effect is covered by manual / in-game playtest, not here. The
    /// no-throttled-engine and null cases below confirm the function is a safe no-op without
    /// reaching that Unity path.
    /// </summary>
    [Collection("Sequential")]
    public class EngineFxRestoreTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EngineFxRestoreTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Theory]
        [InlineData(false, false, false)] // unsuppressed and stayed unsuppressed: nothing to restart
        [InlineData(false, true, true)]   // suppressed -> unsuppressed: restart engine FX
        [InlineData(true, true, false)]   // still suppressed
        [InlineData(true, false, false)]  // just became suppressed
        public void ShouldRestartEngineFxAfterSuppression_FiresOnlyOnUnsuppressTransition(
            bool suppressVisualFx, bool wasVisualFxSuppressed, bool expected)
        {
            bool shouldRestart = GhostPlaybackEngine.ShouldRestartEngineFxAfterSuppression(
                suppressVisualFx, wasVisualFxSuppressed);

            Assert.Equal(expected, shouldRestart);
        }

        [Fact]
        public void RestoreActiveEngineFx_NoThrottledEngines_DoesNotLogOrThrow()
        {
            // currentPower == 0 means a shut-down engine; CollectDeferredEnginePowerRestores
            // filters it out so the Unity application path is never reached.
            var state = new GhostPlaybackState
            {
                engineInfos = new Dictionary<ulong, EngineGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(1u, 0)] = new EngineGhostInfo
                    {
                        partPersistentId = 1u,
                        moduleIndex = 0,
                        currentPower = 0f
                    }
                }
            };

            GhostPlaybackLogic.RestoreActiveEngineFx(state);

            Assert.DoesNotContain(logLines, l => l.Contains("RestoreActiveEngineFx"));
        }

        [Fact]
        public void RestoreActiveEngineFx_NullEngineInfos_IsNoOp()
        {
            var state = new GhostPlaybackState { engineInfos = null };

            GhostPlaybackLogic.RestoreActiveEngineFx(state);

            Assert.DoesNotContain(logLines, l => l.Contains("RestoreActiveEngineFx"));
        }
    }
}
