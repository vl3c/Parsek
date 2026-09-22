using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GHOST-MAP-ENSURE-ORBIT-RENDERERS-TEARDOWN-NRE: the orbit-renderer repair stands down
    /// while the application quits or while RemoveAllGhostVessels is destroying every ghost.
    /// </summary>
    [Collection("Sequential")]
    public class GhostMapEnsureOrbitRenderersSkipTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostMapEnsureOrbitRenderersSkipTests()
        {
            GhostMapPresence.ResetForTesting();
            ParsekProcess.ResetApplicationQuittingForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekProcess.ResetApplicationQuittingForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
        }

        [Fact]
        public void ResolveSkipReason_NeitherCondition_ReturnsNull()
        {
            Assert.Null(GhostMapPresence.ResolveEnsureOrbitRenderersSkipReason(false, false));
        }

        [Fact]
        public void ResolveSkipReason_Quitting_ReturnsApplicationQuitting()
        {
            Assert.Equal(GhostMapPresence.EnsureOrbitRenderersSkipApplicationQuitting,
                GhostMapPresence.ResolveEnsureOrbitRenderersSkipReason(true, false));
        }

        [Fact]
        public void ResolveSkipReason_RemoveAllInProgress_ReturnsRemoveAll()
        {
            Assert.Equal(GhostMapPresence.EnsureOrbitRenderersSkipRemoveAllInProgress,
                GhostMapPresence.ResolveEnsureOrbitRenderersSkipReason(false, true));
        }

        [Fact]
        public void ResolveSkipReason_BothConditions_QuittingWins()
        {
            Assert.Equal(GhostMapPresence.EnsureOrbitRenderersSkipApplicationQuitting,
                GhostMapPresence.ResolveEnsureOrbitRenderersSkipReason(true, true));
        }

        [Fact]
        public void MarkApplicationQuitting_LatchesAndLogsOnce()
        {
            Assert.False(ParsekProcess.IsApplicationQuitting);

            ParsekProcess.MarkApplicationQuitting("test");
            ParsekProcess.MarkApplicationQuitting("test-again");

            Assert.True(ParsekProcess.IsApplicationQuitting);
            int latchLines = logLines.FindAll(l =>
                l.Contains("[Init]") && l.Contains("Application quitting latched")).Count;
            Assert.Equal(1, latchLines);
            Assert.Contains(logLines, l => l.Contains("Application quitting latched source=test "));
        }

        [Fact]
        public void EnsureGhostOrbitRenderers_WhileQuitting_SkipsBeforeTouchingMapViewAndLogs()
        {
            ParsekProcess.MarkApplicationQuitting("test");

            // Would read MapView.fetch (a live-Unity static) if the guard did not return first.
            int fixedCount = GhostMapPresence.EnsureGhostOrbitRenderers();

            Assert.Equal(0, fixedCount);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("EnsureGhostOrbitRenderers: skipped repair reason=application-quitting trackedGhosts=0"));
        }

        [Fact]
        public void EnsureGhostOrbitRenderers_RepeatedSkips_AreRateLimited()
        {
            ParsekProcess.MarkApplicationQuitting("test");

            for (int i = 0; i < 5; i++)
                GhostMapPresence.EnsureGhostOrbitRenderers();

            int skipLines = logLines.FindAll(l =>
                l.Contains("EnsureGhostOrbitRenderers: skipped repair")).Count;
            Assert.Equal(1, skipLines);
        }

        [Fact]
        public void RemoveAllInProgress_DefaultsFalseAndResets()
        {
            Assert.False(GhostMapPresence.IsRemoveAllGhostVesselsInProgress);
        }
    }
}
