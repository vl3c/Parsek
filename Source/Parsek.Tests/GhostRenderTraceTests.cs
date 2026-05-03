using Xunit;

namespace Parsek.Tests
{
    public class GhostRenderTraceTests : System.IDisposable
    {
        public GhostRenderTraceTests()
        {
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = false;
            ParsekSettings.CurrentOverrideForTesting = null;
        }

        public void Dispose()
        {
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = false;
            ParsekSettings.CurrentOverrideForTesting = null;
        }

        [Fact]
        public void EvaluateGate_FirstSeenAndInitialWindow_EmitsThenCloses()
        {
            var first = GhostRenderTrace.EvaluateGateForTesting(
                currentUT: 100.0,
                firstSeenUT: 100.0,
                firstSeen: true,
                structuralWindow: false,
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: false,
                deltaMeters: 0.0,
                expectedDeltaMeters: 0.0);

            Assert.True(first.Emit);
            Assert.False(first.Important);
            Assert.Equal("first-seen", first.Reason);

            var within = GhostRenderTrace.EvaluateGateForTesting(
                currentUT: 103.0,
                firstSeenUT: 100.0,
                firstSeen: false,
                structuralWindow: false,
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: false,
                deltaMeters: 0.0,
                expectedDeltaMeters: 0.0);

            Assert.True(within.Emit);
            Assert.Equal("initial-window", within.Reason);

            var closed = GhostRenderTrace.EvaluateGateForTesting(
                currentUT: 106.0,
                firstSeenUT: 100.0,
                firstSeen: false,
                structuralWindow: false,
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: false,
                deltaMeters: 0.0,
                expectedDeltaMeters: 0.0);

            Assert.False(closed.Emit);
            Assert.Equal("closed", closed.Reason);
        }

        [Fact]
        public void EvaluateGate_LargeDelta_IsImportant()
        {
            var decision = GhostRenderTrace.EvaluateGateForTesting(
                currentUT: 50.0,
                firstSeenUT: 10.0,
                firstSeen: false,
                structuralWindow: false,
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: false,
                deltaMeters: 500.0,
                expectedDeltaMeters: 10.0);

            Assert.True(decision.Emit);
            Assert.True(decision.Important);
            Assert.Equal("large-delta", decision.Reason);
        }

        [Fact]
        public void FormatTracePrefix_UsesInvariantKeyValueFields()
        {
            string prefix = GhostRenderTrace.FormatTracePrefixForTesting(
                "abcdef0123456789",
                7,
                123.4567,
                120.25,
                "AfterUpdate");

            Assert.Contains("phase=AfterUpdate", prefix);
            Assert.Contains("rec=abcdef01", prefix);
            Assert.Contains("recId=abcdef0123456789", prefix);
            Assert.Contains("ghostIndex=7", prefix);
            Assert.Contains("currentUT=123.457", prefix);
            Assert.Contains("playbackUT=120.250", prefix);
        }

        [Fact]
        public void ShouldEmitPhase_DisabledByDefault_ReturnsFalseEvenForForce()
        {
            GhostRenderTrace.OpenDetailedWindow("rec-disabled", 100.0, 10.0, "test");

            Assert.False(GhostRenderTrace.ShouldEmitPhase(
                "rec-disabled",
                101.0,
                important: true,
                force: true));
        }

        [Fact]
        public void ShouldEmitPhase_EnabledBySettings_UsesDetailedWindow()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                ghostRenderTracing = true
            };

            GhostRenderTrace.OpenDetailedWindow("rec-enabled", 100.0, 2.0, "test");

            Assert.True(GhostRenderTrace.ShouldEmitPhase("rec-enabled", 101.0));
            Assert.False(GhostRenderTrace.ShouldEmitPhase("rec-enabled", 103.0));
        }

        [Fact]
        public void ShouldEmitPhase_ForceEnabledForTesting_UsesDetailedWindow()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            GhostRenderTrace.OpenDetailedWindow("rec-force", 50.0, 1.0, "test");

            Assert.True(GhostRenderTrace.ShouldEmitPhase("rec-force", 50.5));
            Assert.False(GhostRenderTrace.ShouldEmitPhase("rec-force", 52.0));
        }
    }
}
