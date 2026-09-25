using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards for the per-engine ignition witness (`[GhostPartEvents] engine-fx start`)
    /// and the build-time FX source classification it reports. The applier's aggregate
    /// line names one representative pid per batch, so a lane could not show that a
    /// specific EFFECTS-node engine (the GS-6 Ant) started its plume on replay; this
    /// line is the per-pid, source-tagged witness D7 `engine-fx-effects` is claimed on.
    ///
    /// Not covered here: the call site inside SetEngineEmissionWithOutcome (its body
    /// touches ParticleSystem ECalls and cannot JIT headless). The edge predicate, the
    /// grammar, the gate and the rate-limit key it calls are all driven below.
    /// </summary>
    [Collection("Sequential")]
    public class GhostEngineFxStartLogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostEngineFxStartLogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
        }

        private static EngineGhostInfo AntInfo(uint pid = 900501233u) => new EngineGhostInfo
        {
            partPersistentId = pid,
            moduleIndex = 0,
            partName = "microEngine.v2",
            fxSource = EngineFxSource.EffectsNode,
            effectsNodeSystemCount = 1,
            supplementSystemCount = 1,
            legacySystemCount = 0,
        };

        [Theory]
        [InlineData(0f, 0.25f, true)]
        [InlineData(0f, 0.01f, true)]
        [InlineData(-1f, 0.5f, true)]
        [InlineData(0.25f, 0.5f, false)]  // a throttle change is not an ignition
        [InlineData(0.5f, 0f, false)]     // a shutdown is not an ignition
        [InlineData(0f, 0f, false)]       // a repeated shutdown seed is not an ignition
        public void IsIgnitionEdge_OnlyZeroToPositive(float previous, float next, bool expected)
        {
            Assert.Equal(expected, GhostEngineFxStartLog.IsIgnitionEdge(previous, next));
        }

        [Theory]
        [InlineData((int)EngineFxSource.EffectsNode, "effects-node")]
        [InlineData((int)EngineFxSource.Legacy, "legacy")]
        [InlineData((int)EngineFxSource.Supplement, "supplement")]
        [InlineData((int)EngineFxSource.None, "none")]
        public void SourceToken_ClosedVocabulary(int source, string token)
        {
            Assert.Equal(token, GhostEngineFxStartLog.SourceToken((EngineFxSource)source));
        }

        [Fact]
        public void FormatLine_Grammar_IsInvariantUnderCommaDecimalCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = GhostEngineFxStartLog.FormatLine(
                    "microEngine.v2", 900501233u, 0, EngineFxSource.EffectsNode,
                    1, 1, 0, 1, 2, 1, 0.25f);
                Assert.Equal(
                    "engine-fx start part='microEngine.v2' pid=900501233 midx=0 " +
                    "source=effects-node effectsSystems=1 supplementSystems=1 legacySystems=0 " +
                    "effectsPlaying=1 playing=2 emitters=1 power=0.250",
                    line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void FormatLine_NullPartName_RendersPlaceholder()
        {
            string line = GhostEngineFxStartLog.FormatLine(
                null, 1u, 2, EngineFxSource.Legacy, 0, 0, 3, 0, 3, 0, 1f);
            Assert.StartsWith("engine-fx start part='?' pid=1 midx=2 source=legacy ", line);
        }

        [Fact]
        public void Emit_WritesOneTaggedLine_WithTheInfosBuildCounts()
        {
            GhostEngineFxStartLog.Emit(AntInfo(), effectsPlaying: 1, playing: 2, power: 0.25f);

            string line = Assert.Single(logLines);
            Assert.Contains("[GhostPartEvents]", line);
            Assert.Contains("engine-fx start part='microEngine.v2' pid=900501233 midx=0 " +
                            "source=effects-node effectsSystems=1 supplementSystems=1 " +
                            "legacySystems=0 effectsPlaying=1 playing=2 emitters=0 power=0.250",
                line);
        }

        [Fact]
        public void Emit_SamePidInsideTheWindow_IsRateLimited_OtherPidStillEmits()
        {
            GhostEngineFxStartLog.Emit(AntInfo(), 1, 2, 0.25f);
            GhostEngineFxStartLog.Emit(AntInfo(), 1, 2, 0.25f);
            Assert.Single(logLines, l => l.Contains("engine-fx start") && l.Contains("pid=900501233"));

            GhostEngineFxStartLog.Emit(AntInfo(pid: 42u), 1, 2, 0.25f);
            Assert.Single(logLines, l => l.Contains("engine-fx start") && l.Contains("pid=42 "));
        }

        [Fact]
        public void Emit_VerboseOff_WritesNothing()
        {
            ParsekLog.VerboseOverrideForTesting = false;
            GhostEngineFxStartLog.Emit(AntInfo(), 1, 2, 0.25f);
            Assert.DoesNotContain(logLines, l => l.Contains("engine-fx start"));
        }

        [Fact]
        public void Emit_NullInfo_IsANoOp()
        {
            GhostEngineFxStartLog.Emit(null, 1, 1, 1f);
            Assert.Empty(logLines);
        }

        [Fact]
        public void RateLimitKey_IsPerPidAndModule()
        {
            Assert.Equal("engine-fx-start-7-1", GhostEngineFxStartLog.RateLimitKey(7u, 1));
            Assert.NotEqual(GhostEngineFxStartLog.RateLimitKey(7u, 0),
                            GhostEngineFxStartLog.RateLimitKey(7u, 1));
        }

        [Theory]
        [InlineData(1, 1, 0, (int)EngineFxSource.EffectsNode)]  // the Ant: EFFECTS + Twitch supplement
        [InlineData(2, 0, 0, (int)EngineFxSource.EffectsNode)]
        [InlineData(0, 0, 3, (int)EngineFxSource.Legacy)]       // an LV-T45: fx_* children
        [InlineData(0, 2, 0, (int)EngineFxSource.Supplement)]   // Kickback / Rhino forced plumes
        [InlineData(0, 0, 0, (int)EngineFxSource.None)]
        public void ClassifyEngineFxSource_EffectsWinsOverSupplements(
            int effects, int supplement, int legacy, int expected)
        {
            Assert.Equal((EngineFxSource)expected,
                EngineFxBuilder.ClassifyEngineFxSource(effects, supplement, legacy));
        }

        [Theory]
        [InlineData("fallback", true)]
        [InlineData("waterfall-lastresort", true)]
        [InlineData("pristine-legacy", true)]
        [InlineData("pristine-legacy-flame-fallback", true)]
        [InlineData("running", false)]
        [InlineData("running_closed", false)]
        [InlineData("power", false)]
        [InlineData("?", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("Fallback", false)]  // group names are matched exactly, never case-folded
        public void IsSupplementFxGroup_OnlyTheFourSynthesizedGroups(string groupName, bool expected)
        {
            Assert.Equal(expected, EngineFxBuilder.IsSupplementFxGroup(groupName));
        }
    }
}
