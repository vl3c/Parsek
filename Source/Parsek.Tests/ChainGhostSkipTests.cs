using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ChainGhostSkipTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainGhostSkipTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetIsGhostedOverride();
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetIsGhostedOverride();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Chain-aware ghost skip: ghosted vessels must NOT be skipped
        // ================================================================

        [Fact]
        public void GhostedVessel_SkipBypassed()
        {
            // A vessel ghosted by the chain system should NOT be skipped,
            // even if the real vessel "exists" — because VesselGhoster has
            // despawned it, background recording data must produce a ghost GO.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => pid == 100);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 100, false);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[Ghoster]") && l.Contains("pid=100") &&
                l.Contains("ghosted by chain") && l.Contains("NOT skipping"));
        }

        [Fact]
        public void NonGhostedExternalVessel_RealVesselExists_SkipPreserved()
        {
            // Non-ghosted external vessel with a live real vessel — existing
            // behavior: skip the ghost (real vessel serves as its own visual).
            GhostPlaybackLogic.SetIsGhostedOverride(pid => false);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 200, false);

            Assert.True(result);
        }

        [Fact]
        public void NonGhostedExternalVessel_RealVesselMissing_NotSkipped()
        {
            // Non-ghosted external vessel whose real vessel is missing —
            // existing fallback: do NOT skip, a ghost is needed.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => false);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 300, false);

            Assert.False(result);
        }

        [Fact]
        public void ZeroPid_NoEffect()
        {
            // PID=0 triggers early return regardless of ghosted state.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => true);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 0, false);

            Assert.False(result);
        }

        [Fact]
        public void NullTreeId_NoEffect()
        {
            // Null treeId triggers early return regardless of ghosted state.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => true);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                null, 100, false);

            Assert.False(result);
        }

        [Fact]
        public void ActiveRecording_NeverSkipped()
        {
            // Active recording is the player's own vessel — always produce ghost.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => true);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 100, true);

            Assert.False(result);
        }

        [Fact]
        public void GhostedOverrideNull_FallsToRealVesselCheck()
        {
            // When no chain system is active (isGhostedOverride = null),
            // behavior is identical to pre-6b: falls through to RealVesselExists.
            GhostPlaybackLogic.ResetIsGhostedOverride();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 400, false);

            Assert.True(result);
        }
    }
}
